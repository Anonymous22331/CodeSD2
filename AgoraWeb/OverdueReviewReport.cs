using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agora.Web;

internal sealed class OverdueReviewReport : BackgroundService
{
    private const int ReportIntervalHours = 3;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MailTimeout = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory scopes;
    private readonly IConfiguration configuration;
    private readonly ILogger<OverdueReviewReport> logger;

    /// <summary>Подключает настройки рассылки и журнал Web-клиента.</summary>
    public OverdueReviewReport(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<OverdueReviewReport> logger)
    {
        this.scopes = scopes;
        this.configuration = configuration;
        this.logger = logger;
    }

    /// <summary>Запускает отчёт в текущем трёхчасовом окне и повторяет временные сбои.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = configuration.GetSection("AgoraOverdueReviewReport");
        if (!settings.GetValue<bool>("Enabled"))
        {
            return;
        }

        // Не блокируем запуск Web-клиента запросом к базе.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime now = DateTime.Now;
            DateTime slot = now.Date.AddHours(now.Hour / ReportIntervalHours * ReportIntervalHours);
            TimeSpan delay = slot.AddHours(ReportIntervalHours) - now;
            try
            {
                if (!await RunReportAsync(settings, now, slot, stoppingToken))
                {
                    delay = delay < RetryInterval ? delay : RetryInterval;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Не удалось сформировать отчёт о просроченных заданиях Агора.");
                delay = delay < RetryInterval ? delay : RetryInterval;
            }

            TimeSpan elapsed = DateTime.Now - now;
            await Task.Delay(delay > elapsed ? delay - elapsed : TimeSpan.Zero, stoppingToken);
        }
    }

    /// <summary>Формирует письмо на каждого исполнителя и фиксирует успешные отправки текущего окна.</summary>
    private async Task<bool> RunReportAsync(IConfiguration settings, DateTime now, DateTime slot, CancellationToken stoppingToken)
    {
        string statePath = settings["StatePath"] ?? throw new InvalidOperationException("Укажите путь журнала рассылки.");
        var state = File.Exists(statePath) ? JsonSerializer.Deserialize<ReportState>(File.ReadAllText(statePath)) : null;
        if (state?.Slot != slot)
        {
            state = new ReportState(slot, new HashSet<Guid>(), false);
        }
        if (state.Completed)
        {
            return true;
        }

        string smtpHost = settings["SmtpHost"];
        bool useLocalAddresses = settings.GetValue<bool>("LocalMailbox")
            && (smtpHost == "localhost" || IPAddress.TryParse(smtpHost, out var ip) && IPAddress.IsLoopback(ip));
        var baseUrl = new Uri(settings["WebClientUrl"], UriKind.Absolute);
        var from = new MailAddress(settings["From"], "Docsvision Агора");
        List<OverdueReviewRow> rows;
        using (var scope = scopes.CreateScope())
        {
            rows = scope.ServiceProvider.GetRequiredService<OverdueReviewTasks>().Read(now, useLocalAddresses);
        }

        bool completed = true;
        foreach (var report in rows.GroupBy(row => row.EmployeeId))
        {
            stoppingToken.ThrowIfCancellationRequested();
            if (state.Sent.Contains(report.Key))
            {
                continue;
            }

            using var message = new MailMessage
            {
                From = from, Subject = "Просроченные задания на рассмотрение",
                BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8, IsBodyHtml = true,
                Body = BuildTable(report, baseUrl)
            };
            message.To.Add(report.First().Email);
            using var smtp = new SmtpClient(smtpHost, settings.GetValue<int>("SmtpPort"))
            {
                EnableSsl = settings.GetValue<bool>("SmtpTls")
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(MailTimeout);
            try
            {
                await smtp.SendMailAsync(message, timeout.Token);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Отчёт по просроченным заданиям для исполнителя {EmployeeId} будет отправлен повторно.", report.Key);
                completed = false;
                continue;
            }
            state.Sent.Add(report.Key);
            SaveState(statePath, state);
        }

        if (completed)
        {
            SaveState(statePath, state with { Completed = true });
            logger.LogInformation("Отчёт о просроченных заданиях Агора: {Tasks} заданий, {Recipients} писем.",
                rows.Select(row => row.TaskId).Distinct().Count(), state.Sent.Count);
        }
        return completed;
    }

    /// <summary>Заменяет журнал целиком после записи, чтобы перезапуск не повредил его.</summary>
    private static void SaveState(string path, ReportState state)
    {
        string pending = path + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(state));
        File.Move(pending, path, true);
    }

    /// <summary>Создаёт таблицу с безопасно закодированными заголовками и ссылками Web-клиента.</summary>
    private static string BuildTable(IEnumerable<OverdueReviewRow> rows, Uri baseUrl)
    {
        var html = new StringBuilder("<table border=\"1\" cellpadding=\"6\"><thead><tr>" +
            "<th>Наименование задания</th><th>Ссылка на задание</th><th>Наименование документа</th></tr></thead><tbody>");
        foreach (var row in rows.DistinctBy(row => row.TaskId).OrderBy(row => row.Deadline).ThenBy(row => row.TaskId))
        {
            var url = new Uri(baseUrl, "#/CardView/" + row.TaskId.ToString("D", CultureInfo.InvariantCulture));
            html.Append("<tr><td>").Append(WebUtility.HtmlEncode(row.TaskName))
                .Append("</td><td><a href=\"").Append(WebUtility.HtmlEncode(url.AbsoluteUri))
                .Append("\">Открыть задание</a></td><td>").Append(WebUtility.HtmlEncode(row.DocumentName)).Append("</td></tr>");
        }
        return html.Append("</tbody></table>").ToString();
    }

    private sealed record ReportState(DateTime Slot, HashSet<Guid> Sent, bool Completed);
}
