using System;
using System.Linq;
using DocsVision.Approval.WebClient.Models;
using DocsVision.Approval.WebClient.Services;
using DocsVision.BackOffice.CardLib.CardDefs;
using DocsVision.BackOffice.ObjectModel;
using DocsVision.BackOffice.ObjectModel.Services;
using DocsVision.Layout.WebClient.Services;
using DocsVision.Platform.ObjectModel;
using DocsVision.Platform.WebClient;
using DocsVision.Platform.WebClient.Operations;

namespace Agora.Web;

internal sealed class IncomingRouting : IIncomingRouting
{
    private static readonly Guid IncomingKind = new("9530f638-6669-48b8-867a-a135ce4232a6");
    private static readonly Guid Registered = new("ed171ce1-7014-45ac-9a30-223a6c8c62e6");
    private static readonly Guid Preparation = new("de445cf0-2482-48ae-80b3-49746540c4de");
    private static readonly Guid RegisterOperation = new("aa43e10d-2d81-4f9c-8eb0-6bedd02b216f");
    private static readonly Guid RouteFields = new("e8a5eeac-be18-4227-95bf-ff2c16f020a4");
    private static readonly Guid ReviewOperation = new("882f04d1-7521-4945-a54f-86a73e05e03b");
    private static readonly Guid DirectOperation = new("07e1e86c-0fc5-473b-a6e8-7738bf025f28");
    private static readonly Guid AgreementCreationMode = new("38c5329a-dd34-44ed-a5a5-000000000017");
    private static readonly Guid AgreementTemplate = new("38c5329a-dd34-44ed-a5a6-000000000015");
    private static readonly Guid AgreementKind = new("38c5329a-dd34-44ed-a5a5-000000000016");

    private readonly ICurrentObjectContextProvider contexts;
    private readonly ICardService cards;
    private readonly IApprovalCreationService approvalCreation;
    private readonly IApprovalManagementService approvalManagement;
    private readonly IOperationService operations;

    /// <summary>Подключает штатные сервисы создания и запуска согласования.</summary>
    public IncomingRouting(ICurrentObjectContextProvider contexts, ICardService cards,
        IApprovalCreationService approvalCreation, IApprovalManagementService approvalManagement, IOperationService operations)
    {
        this.contexts = contexts;
        this.cards = cards;
        this.approvalCreation = approvalCreation;
        this.approvalManagement = approvalManagement;
        this.operations = operations;
    }

    /// <summary>Проверяет основной файл под блокировкой и выполняет штатную регистрацию письма.</summary>
    public IncomingRoutingOutcome Register(Guid id, long timestamp)
    {
        var actor = contexts.GetOrCreateCurrentSessionContext();
        if (!actor.AdvancedCardManager.CardReadAccessCheck(id))
            return IncomingRoutingOutcome.Forbidden;
        var context = actor.ObjectContext;
        var letter = context.GetObject<Document>(id);
        if (letter?.SystemInfo?.CardKind == null || context.GetObjectRef(letter.SystemInfo.CardKind).Id != IncomingKind)
            return IncomingRoutingOutcome.NotFound;
        if (!context.LockObject(letter, false))
            return IncomingRoutingOutcome.Locked;
        try
        {
            cards.CheckTimestamp(actor, id, timestamp);
            context.RefreshObject(ref letter);
            if (letter.SystemInfo.State == null || context.GetObjectRef(letter.SystemInfo.State).Id != Preparation)
                return IncomingRoutingOutcome.Changed;
            var states = context.GetService<IStateService>();
            var operation = context.GetObject<StatesOperation>(RegisterOperation);
            if (!states.IsOperationAllowedFull(operation, letter))
                return IncomingRoutingOutcome.Forbidden;
            if (!letter.Files.Any(file => file.FileType == DocumentFileType.Main))
                return IncomingRoutingOutcome.MainFileMissing;
            var branch = states.FindLineBranchesByStartState(letter.SystemInfo.State)
                .SingleOrDefault(item => item.Operation != null && context.GetObjectRef(item.Operation).Id == RegisterOperation
                    && item.EndState != null && context.GetObjectRef(item.EndState).Id == Registered);
            if (branch == null)
                return IncomingRoutingOutcome.Disabled;
            operations.ChangeState(actor, letter, branch, new CardOperationArguments());
            return IncomingRoutingOutcome.Sent;
        }
        finally
        {
            context.UnlockObject(letter);
        }
    }

    /// <summary>Проверяет права отправителя и запускает маршрут Конструктора согласований для зарегистрированного письма.</summary>
    public IncomingRoutingOutcome Send(Guid id, long timestamp)
    {
        var actor = contexts.GetOrCreateCurrentSessionContext();
        if (!actor.AdvancedCardManager.CardReadAccessCheck(id))
            return IncomingRoutingOutcome.Forbidden;

        ObjectContext actorContext = actor.ObjectContext;
        var letter = actorContext.GetObject<Document>(id);
        if (letter?.SystemInfo?.CardKind == null || actorContext.GetObjectRef(letter.SystemInfo.CardKind).Id != IncomingKind)
            return IncomingRoutingOutcome.NotFound;
        if (letter.SystemInfo.State == null || actorContext.GetObjectRef(letter.SystemInfo.State).Id != Registered)
            return IncomingRoutingOutcome.RequiresRegistration;

        cards.CheckTimestamp(actor, id, timestamp);
        var fields = actor.Session.CardManager.GetCardData(id).Sections[RouteFields].Rows;
        bool requiresReview = fields.Count > 0 && fields[0].GetBoolean("RequiresReview") == true;
        var operation = actorContext.GetObject<StatesOperation>(requiresReview ? ReviewOperation : DirectOperation);
        if (!actorContext.GetService<IStateService>().IsOperationAllowedFull(operation, letter))
            return IncomingRoutingOutcome.Forbidden;

        using var pool = contexts.GetOrCreateExclusivePoolSessionContext();
        var session = pool.Context;
        ObjectContext serviceContext = session.ObjectContext;
        var serviceLetter = serviceContext.GetObject<Document>(id);
        if (serviceLetter == null || !serviceContext.LockObject(serviceLetter, false))
            return IncomingRoutingOutcome.Locked;

        try
        {
            cards.CheckTimestamp(session, id, timestamp);
            serviceContext.RefreshObject(ref serviceLetter);
            if (serviceContext.GetObjectRef(serviceLetter.SystemInfo.CardKind).Id != IncomingKind
                || serviceContext.GetObjectRef(serviceLetter.SystemInfo.State).Id != Registered)
                return IncomingRoutingOutcome.Changed;

            var mode = serviceContext.GetObject<KindsCardCreationSetting>(AgreementCreationMode);
            if (mode?.Template != AgreementTemplate)
                return IncomingRoutingOutcome.Disabled;

            var reconcileRows = session.Session.CardManager.GetCardData(id).Sections[CardDocument.Reconciliation.ID].Rows;
            Guid approvalId = reconcileRows.Count == 0 ? Guid.Empty
                : reconcileRows[0].GetGuid("Reconciliation").GetValueOrDefault();
            if (approvalId == Guid.Empty)
            {
                if (serviceLetter.MainInfo.Tasks == null)
                {
                    serviceLetter.MainInfo.Tasks = serviceContext.GetService<ITaskListService>().CreateTaskList();
                    serviceContext.AcceptChanges();
                }
                approvalId = approvalCreation.CreateReconciliation(session, new ReconcilationCreationModel
                {
                    DocumentId = id,
                    CreationSettingId = AgreementCreationMode
                });
                if (approvalId == Guid.Empty)
                    return IncomingRoutingOutcome.Disabled;
            }
            else
            {
                var existing = serviceContext.GetObject<Reconcile>(approvalId);
                if (existing?.SystemInfo?.CardKind == null
                    || serviceContext.GetObjectRef(existing.SystemInfo.CardKind).Id != AgreementKind
                    || existing.MainInfo.Document == null || serviceContext.GetObjectRef(existing.MainInfo.Document).Id != id)
                    return IncomingRoutingOutcome.Changed;
            }
            approvalManagement.StartReconciliation(session, id, approvalId);
            return IncomingRoutingOutcome.Sent;
        }
        finally
        {
            serviceContext.UnlockObject(serviceLetter);
        }
    }
}
