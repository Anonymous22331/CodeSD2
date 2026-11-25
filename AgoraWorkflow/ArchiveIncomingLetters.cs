using System;
using System.Collections.Generic;
using System.Linq;
using DocsVision.BackOffice.CardLib.CardDefs;
using DocsVision.BackOffice.ObjectModel;
using DocsVision.BackOffice.ObjectModel.Services;
using DocsVision.Platform.ObjectManager;
using DocsVision.Platform.ObjectManager.Metadata;
using DocsVision.Platform.ObjectManager.SearchModel;
using DocsVision.Platform.ObjectModel;
using DocsVision.Workflow.Functions;
using DocsVision.Workflow.Objects;
using DocsVision.Workflow.Runtime;
using WorkItem = DocsVision.BackOffice.ObjectModel.Task;

namespace DVScriptHost;

internal sealed class DVScript
{
    private static readonly Guid Incoming = new("9530f638-6669-48b8-867a-a135ce4232a6");
    private static readonly Guid InWork = new("103aa9b4-4ce3-40ab-9cf2-001eb48a1b11");
    private static readonly Guid ArchiveBranch = new("b0c241e1-82ea-436f-92b3-3a2ba45fa2fe");
    /// <summary>Архивирует письма Агора в работе после завершения дерева заданий.</summary>
    public ExecResultEnum Execute(ProcessInfo process, PassState passInfo, ObjectContext context, UserSession session)
    {
        var query = session.CreateSearchQuery();
        var type = query.AttributiveSearch.CardTypeQueries.AddNew(CardDocument.ID);
        var fields = type.SectionQueries.AddNew(CardDocument.MainInfo.ID).ConditionGroup.Conditions;
        fields.AddNew("Kind", FieldType.RefId, ConditionOperation.Equals, Incoming);
        fields.AddNew("State", FieldType.RefId, ConditionOperation.Equals, InWork);

        foreach (CardData card in session.CardManager.FindCards(query.GetXml()))
        {
            ArchiveIfComplete(context, card.Id);
        }
        return ExecResultEnum.Done;
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
                || tasks == null || tasks.Tasks.Count + tasks.TaskGroups.Count == 0
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
