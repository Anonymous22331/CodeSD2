using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DocsVision.BackOffice.CardLib.CardDefs;
using DocsVision.BackOffice.ObjectModel;
using DocsVision.BackOffice.ObjectModel.Services;
using DocsVision.Platform.ObjectManager;
using DocsVision.Platform.ObjectManager.Metadata;
using DocsVision.Platform.ObjectManager.SearchModel;
using DocsVision.Platform.ObjectModel;
using DocsVision.Platform.WebClient.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AsyncTask = System.Threading.Tasks.Task;
using WorkItem = DocsVision.BackOffice.ObjectModel.Task;

namespace Agora.Web;

internal sealed class NightlyIncomingArchive(IServiceScopeFactory scopes, ILogger<NightlyIncomingArchive> logger)
    : BackgroundService
{
    /// <summary>Запускает архивирование в полночь по местному времени стенда.</summary>
    protected override async AsyncTask ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var midnight = new DateTimeOffset(now.LocalDateTime.Date.AddDays(1));
            await AsyncTask.Delay(midnight - now, stoppingToken);
            try
            {
                using var scope = scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<IncomingArchiveSweep>().Run(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "Не удалось выполнить ночную архивацию входящих писем Агора.");
            }
        }
    }
}

internal sealed class IncomingArchiveSweep(IExclusiveSessionPoolService sessions, ILogger<IncomingArchiveSweep> logger)
{
    private static readonly Guid Incoming = new("9530f638-6669-48b8-867a-a135ce4232a6");
    private static readonly Guid InWork = new("103aa9b4-4ce3-40ab-9cf2-001eb48a1b11");
    private static readonly Guid ArchiveBranch = new("b0c241e1-82ea-436f-92b3-3a2ba45fa2fe");
    private static readonly Guid AcceptanceKind = new("25e8363d-90a0-4edc-ada6-da2e1525a254");
    private static readonly Guid GroupKind = new("3d40ca84-6a12-479c-89f0-d64de1ed2ca5");
    private const string AcceptanceName = "Приёмка исполнения входящего письма (цикл ";
    private const string LegacyControlName = "Проконтролировать исполнение входящего письма";

    /// <summary>Архивирует письма Агора в работе после завершения дерева заданий.</summary>
    public void Run(CancellationToken stoppingToken)
    {
        using var holder = sessions.GetOrCreateExclusivePoolSessionContext();
        var session = holder.Context.Session;
        var context = holder.Context.ObjectContext;
        var query = session.CreateSearchQuery();
        var type = query.AttributiveSearch.CardTypeQueries.AddNew(CardDocument.ID);
        var fields = type.SectionQueries.AddNew(CardDocument.MainInfo.ID).ConditionGroup.Conditions;
        fields.AddNew("Kind", FieldType.RefId, ConditionOperation.Equals, Incoming);
        fields.AddNew("State", FieldType.RefId, ConditionOperation.Equals, InWork);

        foreach (CardData card in session.CardManager.FindCards(query.GetXml()))
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                ArchiveIfComplete(context, card.Id);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "Не удалось архивировать входящее письмо Агора {CardId}.", card.Id);
            }
        }
    }

    /// <summary>Блокирует письмо, повторно проверяет дерево заданий и применяет переход в архив.</summary>
    private static void ArchiveIfComplete(ObjectContext context, Guid cardId)
    {
        var document = context.GetObject<Document>(cardId);
        if (document == null || !context.LockObject(document, false))
        {
            return;
        }
        try
        {
            context.RefreshObject(ref document);
            var system = document.SystemInfo;
            var tasks = document.MainInfo.Tasks;
            if (system?.CardKind == null || context.GetObjectRef(system.CardKind).Id != Incoming
                || system.State == null || context.GetObjectRef(system.State).Id != InWork
                || tasks == null || !HasAcceptance(context, tasks)
                || !AllComplete(context, tasks, new HashSet<Guid>()))
            {
                return;
            }
            var branch = context.GetObject<StatesStateMachineBranch>(ArchiveBranch);
            context.GetService<IStateService>().ChangeState(document, branch, false, out _);
            context.AcceptChanges();
        }
        finally
        {
            context.UnlockObject(document);
        }
    }

    /// <summary>Проверяет положительную приёмку или завершение ранее выданной группы контроля.</summary>
    private static bool HasAcceptance(ObjectContext context, TaskList list)
    {
        return list.Tasks.Select(row => row.Task).Any(task =>
                task?.SystemInfo?.CardKind != null
                && context.GetObjectRef(task.SystemInfo.CardKind).Id == AcceptanceKind
                && task.MainInfo.Name.StartsWith(AcceptanceName, StringComparison.Ordinal)
                && task.MainInfo.CompletionResult?.Value == 1)
            || list.TaskGroups.Select(row => row.TaskGroup).Any(group =>
                group?.SystemInfo?.CardKind != null && context.GetObjectRef(group.SystemInfo.CardKind).Id == GroupKind
                && group.MainInfo.Name == LegacyControlName
                && group.SystemInfo.State?.BuiltInState == TaskGroup.CompletedState.Id);
    }

    /// <summary>Проверяет завершение заданий и групп, прерывая обход при циклической ссылке.</summary>
    private static bool AllComplete(ObjectContext context, TaskList list, HashSet<Guid> visited)
    {
        if (!visited.Add(context.GetObjectRef(list).Id))
        {
            return false;
        }
        return list.Tasks.All(row => row.Task?.SystemInfo?.State?.BuiltInState == WorkItem.CompletedState.Id
                && (row.Task.MainInfo.ChildTaskList == null || AllComplete(context, row.Task.MainInfo.ChildTaskList, visited)))
            && list.TaskGroups.All(row => row.TaskGroup?.SystemInfo?.State?.BuiltInState == TaskGroup.CompletedState.Id
                && (row.TaskGroup.MainInfo.TaskList == null || AllComplete(context, row.TaskGroup.MainInfo.TaskList, visited)));
    }
}
