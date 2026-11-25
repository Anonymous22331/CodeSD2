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
using DocsVision.Platform.WebClient.Services;
using Microsoft.Extensions.Logging;

namespace Agora.Web;

internal sealed class OverdueReviewTasks
{
    // Виды из справочника видов карточек решения Агора.
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

    private readonly IExclusiveSessionPoolService sessions;
    private readonly ILogger<OverdueReviewTasks> logger;

    /// <summary>Подключает системную сессию Docsvision для чтения заданий.</summary>
    public OverdueReviewTasks(IExclusiveSessionPoolService sessions, ILogger<OverdueReviewTasks> logger)
    {
        this.sessions = sessions;
        this.logger = logger;
    }

    /// <summary>Возвращает активные просроченные задания и фактических исполнителей.</summary>
    public List<OverdueReviewRow> Read(DateTime now, bool useLocalAddresses)
    {
        using var holder = sessions.GetOrCreateExclusivePoolSessionContext();
        var session = holder.Context.Session;
        var context = holder.Context.ObjectContext;
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
                logger.LogWarning("Не найдена карточка входящего письма для задания {TaskId}.", card.Id);
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
                    logger.LogWarning("У исполнителя {EmployeeId} задания {TaskId} нет корректного e-mail.", employeeId, card.Id);
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
}

internal sealed record OverdueReviewRow(Guid EmployeeId, string Email, Guid TaskId, string TaskName,
    string DocumentName, DateTime Deadline);
