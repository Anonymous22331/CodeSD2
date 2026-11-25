using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using DocsVision.BackOffice.CardLib.CardDefs;
using DocsVision.BackOffice.ObjectModel;
using DocsVision.Platform.ObjectManager;
using DocsVision.Platform.ObjectManager.Metadata;
using DocsVision.Platform.ObjectManager.SearchModel;
using DocsVision.Platform.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using DocsVision.Workflow.Functions;
using DocsVision.Workflow.Objects;
using DocsVision.Workflow.Runtime;

namespace DVScriptHost;

internal sealed class DVScript
{
    private const string StatePath = @"C:\ProgramData\Agora\OverdueReviewReport\state-workflow.json";

    /// <summary>Формирует отдельное письмо каждому текущему исполнителю за трёхчасовое окно.</summary>
    public ExecResultEnum Execute(ProcessInfo process, PassState passInfo, ObjectContext context, UserSession session)
    {
        var now = DateTime.Now;
        var slot = now.Date.AddHours(now.Hour / 3 * 3);
        var state = File.Exists(StatePath) ? JsonSerializer.Deserialize<ReportState>(File.ReadAllText(StatePath)) : null;
        if (state?.Slot != slot)
            state = new ReportState(slot, new HashSet<Guid>(), false);
        if (state.Completed)
            return ExecResultEnum.Done;

        using var settingsFile = JsonDocument.Parse(File.ReadAllText(@"C:\Program Files\Docsvision\WebClient\appsettings.json"),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var settings = settingsFile.RootElement.GetProperty("AgoraOverdueReviewReport");
        string host = settings.GetProperty("SmtpHost").GetString();
        int port = settings.GetProperty("SmtpPort").GetInt32();
        bool local = settings.TryGetProperty("LocalMailbox", out var localSetting) && localSetting.GetBoolean()
            && (host == "localhost" || host == "127.0.0.1" || host == "::1");
        bool tls = settings.TryGetProperty("SmtpTls", out var tlsSetting) && tlsSetting.GetBoolean();
        var from = new MailAddress(settings.GetProperty("From").GetString(), "Docsvision Агора");
        string baseUrl = settings.GetProperty("WebClientUrl").GetString().TrimEnd('/');
        var rows = Read(context, session, now, local);
        foreach (var group in rows.GroupBy(row => row.EmployeeId))
        {
            if (state.Sent.Contains(group.Key))
                continue;
            using var message = new MailMessage
            {
                From = from, Subject = "Просроченные задания на рассмотрение", BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8, IsBodyHtml = true, Body = BuildTable(group, baseUrl)
            };
            message.To.Add(group.First().Email);
            using var smtp = new SmtpClient(host, port) { EnableSsl = tls, Timeout = 30000 };
            try
            {
                smtp.Send(message);
            }
            catch (SmtpException)
            {
                passInfo.ExecuteTime = now.AddMinutes(5);
                return ExecResultEnum.KeepActive;
            }
            state.Sent.Add(group.Key);
            SaveState(StatePath, state);
        }
        SaveState(StatePath, state with { Completed = true });
        return ExecResultEnum.Done;
    }

    private static readonly HashSet<Guid> ReviewKinds = new()
    {
        new("166a40f6-62c4-4f4e-a090-839b6c5df995"),
        new("38c5329a-dd34-44ed-a5a6-000000000028")
    };
    private static readonly Guid IncomingKind = new("9530f638-6669-48b8-867a-a135ce4232a6");
    private static readonly HashSet<Guid> ActiveStates = new()
    {
        Task.StartedState.Id, Task.InWorkState.Id, Task.DeferredState.Id,
        Task.DelegatedState.Id, Task.ReturnFromTheDelegationState.Id
    };

    /// <summary>Возвращает активные просроченные задания и фактических исполнителей.</summary>
    private static List<OverdueReviewRow> Read(ObjectContext context, UserSession session, DateTime now, bool useLocalAddresses)
    {
        var query = session.CreateSearchQuery();
        var type = query.AttributiveSearch.CardTypeQueries.AddNew(CardTask.ID);
        var main = type.SectionQueries.AddNew(CardTask.MainInfo.ID).ConditionGroup;
        main.Conditions.AddNew(CardTask.MainInfo.EndDate, FieldType.DateTime, ConditionOperation.LessThan, now);
        main.Conditions.AddNew(CardTask.MainInfo.EndDateActual, FieldType.DateTime, ConditionOperation.IsNull, null);
        var kinds = main.ConditionGroups.AddNew();
        kinds.Operation = ConditionGroupOperation.Or;
        foreach (Guid kind in ReviewKinds)
            kinds.Conditions.AddNew(CardTask.MainInfo.Kind, FieldType.RefId, ConditionOperation.Equals, kind);

        var rows = new List<OverdueReviewRow>();
        foreach (CardData card in session.CardManager.FindCards(query.GetXml()))
        {
            var task = context.GetObject<Task>(card.Id);
            if (task?.SystemInfo?.CardKind == null || !ReviewKinds.Contains(context.GetObjectRef(task.SystemInfo.CardKind).Id)
                || task.SystemInfo.State == null || !ActiveStates.Contains(task.SystemInfo.State.BuiltInState)
                || task.MainInfo.EndDate is not DateTime deadline || deadline >= now || task.MainInfo.EndDateActual.HasValue)
            {
                continue;
            }

            var document = FindDocument(context, task);
            if (document == null)
            {
                continue;
            }

            foreach (var employee in task.CurrentPerformers.Select(performer => performer.Employee).Where(employee => employee != null))
            {
                Guid employeeId = context.GetObjectRef(employee).Id;
                string address = employee.Email?.Trim();
                if (string.IsNullOrEmpty(address) && useLocalAddresses)
                {
                    address = $"{employeeId:N}@agora.test";
                }
                if (!MailAddress.TryCreate(address, out var mailbox))
                {
                    continue;
                }

                rows.Add(new OverdueReviewRow(employeeId, mailbox.Address, card.Id, task.MainInfo.Name,
                    document.MainInfo.Name, deadline));
            }
        }
        return rows;
    }

    /// <summary>Находит исходное письмо среди родителей или ссылок карточки задания.</summary>
    private static Document FindDocument(ObjectContext context, Task task)
    {
        var parent = task.MainInfo.ParentDocument;
        if (IsIncomingLetter(context, parent))
        {
            return parent;
        }

        return task.MainInfo.ReferenceList?.References
            .Where(link => link.CardType == CardDocument.ID && link.Card != Guid.Empty)
            .Select(link => context.GetObject<Document>(link.Card))
            .FirstOrDefault(document => IsIncomingLetter(context, document));
    }

    /// <summary>Проверяет вид документа по справочнику видов.</summary>
    private static bool IsIncomingLetter(ObjectContext context, Document document)
    {
        return document?.SystemInfo?.CardKind != null && context.GetObjectRef(document.SystemInfo.CardKind).Id == IncomingKind;
    }
    /// <summary>Заменяет журнал целиком после записи, чтобы перезапуск не повредил его.</summary>
    private static void SaveState(string path, ReportState state)
    {
        string pending = path + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(state));
        File.Move(pending, path, true);
    }

    /// <summary>Создаёт таблицу с безопасно закодированными заголовками и ссылками Web-клиента.</summary>
    private static string BuildTable(IEnumerable<OverdueReviewRow> rows, string baseUrl)
    {
        var html = new StringBuilder("<table border=\"1\" cellpadding=\"6\"><thead><tr>" +
            "<th>Наименование задания</th><th>Ссылка на задание</th><th>Наименование документа</th></tr></thead><tbody>");
        foreach (var row in rows.DistinctBy(row => row.TaskId).OrderBy(row => row.Deadline).ThenBy(row => row.TaskId))
        {
            string url = baseUrl + "/#/CardView/" + row.TaskId.ToString("D", CultureInfo.InvariantCulture);
            html.Append("<tr><td>").Append(WebUtility.HtmlEncode(row.TaskName))
                .Append("</td><td><a href=\"").Append(WebUtility.HtmlEncode(url))
                .Append("\">Открыть задание</a></td><td>").Append(WebUtility.HtmlEncode(row.DocumentName)).Append("</td></tr>");
        }
        return html.Append("</tbody></table>").ToString();
    }

    private sealed record ReportState(DateTime Slot, HashSet<Guid> Sent, bool Completed);
}

internal sealed record OverdueReviewRow(Guid EmployeeId, string Email, Guid TaskId, string TaskName,
    string DocumentName, DateTime Deadline);
