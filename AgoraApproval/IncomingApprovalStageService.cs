using System;
using System.Collections.Generic;
using System.Linq;
using DocsVision.ApprovalDesigner.Helpers;
using DocsVision.ApprovalDesigner.ObjectModel;
using DocsVision.ApprovalDesigner.ObjectModel.Services;
using DocsVision.BackOffice.ObjectModel;
using DocsVision.BackOffice.ObjectModel.Services;
using DocsVision.Platform.ObjectManager;
using DocsVision.Platform.ObjectModel;

namespace Agora.Approval;

public sealed class IncomingApprovalStageService : ApprovalStageService
{
    private static readonly Guid Incoming = new("9530f638-6669-48b8-867a-a135ce4232a6");
    private static readonly Guid ReviewKind = new("38c5329a-dd34-44ed-a5a6-000000000028");
    private static readonly Guid ExecutionKind = new("38c5329a-dd34-44ed-a5a6-000000000029");
    private static readonly Guid ReadingKind = new("38c5329a-dd34-44ed-a5a6-00000000002a");
    private static readonly Guid AcceptanceKind = new("38c5329a-dd34-44ed-a5a6-00000000002b");
    private static readonly Guid Details = new("e8a5eeac-be18-4227-95bf-ff2c16f020a4");
    private static readonly Guid Control = new("d5c373ad-8008-446e-b2d6-db96ace455c4");
    private static readonly Guid Receivers = new("b6dfaead-baaa-4024-908c-5dbd693d0fd3");
    private static readonly Guid Coexecutors = new("da2ad468-e4a9-444b-8135-848b245a3fb2");
    private static readonly Guid Readers = new("af798ae7-baac-486e-84ef-82c59dc00a7e");
    private static readonly Guid ResolutionPerformers = new("c6561de3-3b09-4a44-9d6c-0d30ab1d806c");
    private static readonly Guid ReviewDetails = new("870e1272-9858-409c-a85f-eb7b6151c102");
    private static readonly Guid ReviewPerformers = new("4dcbc2c0-0f5c-407e-8c5c-a2477cb2b550");
    private static readonly Guid ReviewReaders = new("8fc00166-4525-4b03-89f1-11e3c4a3f365");
    private static readonly Guid DefaultResolution = new("5112b7d1-acee-485a-9701-4ba0c234a3bc");
    private static readonly Guid WorkState = new("103aa9b4-4ce3-40ab-9cf2-001eb48a1b11");
    private static readonly Guid ArchiveState = new("3471aa78-fd91-4a18-961c-3de2225d2eea");

    /// <summary>Сохраняет состояние письма с рассмотрением между этапами исполнения и ознакомления.</summary>
    public override void ChangeDocumentState(ApprovalStage stage, Document document, DecisionSemantics semantics)
    {
        if (IsIncoming(document) && Existing(Read(document).Sections[Details])?.GetBoolean("RequiresReview") == true
            && (Context.GetObjectRef(stage.TaskSettings.Kind).Id != ReviewKind || semantics == DecisionSemantics.Positive))
        {
            return;
        }
        base.ChangeDocumentState(stage, document, semantics);
    }

    /// <summary>Подбирает участников этапа по реквизитам актуального входящего письма.</summary>
    public override List<StaffEmployee> ComputeApproversForStage(ApprovalStage stage, Reconcile reconcile, ApprovalPath path)
    {
        var document = reconcile.MainInfo.Document;
        if (!IsIncoming(document)) return base.ComputeApproversForStage(stage, reconcile, path);
        var letter = Read(document);
        Guid kind = Context.GetObjectRef(stage.TaskSettings.Kind).Id;
        if (kind == ReviewKind) return Employees(Identifiers(letter.Sections[Receivers], "ReceiverStaff"));
        if (kind == ReadingKind) return Employees(Identifiers(letter.Sections[Readers], "AcquaintancePersons"));
        if (kind == AcceptanceKind)
        {
            Guid? controller = Existing(letter.Sections[Control])?.GetGuid("DocumentInspectorId");
            return Employees(new[] { controller.GetValueOrDefault() });
        }

        bool reviewed = Existing(letter.Sections[Details])?.GetBoolean("RequiresReview") == true;
        var assignments = letter.Sections[reviewed ? ResolutionPerformers : Coexecutors];
        var performers = Employees(Identifiers(assignments, "EmployeeId"));
        if (!reviewed) performers.InsertRange(0, Employees(Identifiers(letter.Sections[Receivers], "ReceiverStaff")));
        var staff = Context.GetService<IStaffService>();
        foreach (Guid id in Identifiers(assignments, "UnitId").Distinct())
            performers.AddRange(staff.GetUnitEmployees(Context.GetObject<StaffUnit>(id), false));
        return performers.Where(employee => employee != null).Distinct().ToList();
    }

    /// <summary>Перед отправкой задания переносит срок и реквизиты письма в форму этапа.</summary>
    protected override void OnTaskCreated(ApprovalStage stage, ApprovalPath path, Reconcile reconcile, Task task)
    {
        var document = reconcile.MainInfo.Document;
        if (!IsIncoming(document)) return;
        var letter = Read(document);
        var details = Existing(letter.Sections[Details]);
        var deadline = Existing(letter.Sections[Control])?.GetDateTime("ControlDate");
        Guid kind = Context.GetObjectRef(task.SystemInfo.CardKind).Id;
        if (kind == ReviewKind)
        {
            var review = Read(task);
            Update(review, () =>
            {
                var fields = review.Sections[ReviewDetails].FirstRow;
                fields.SetDateTime("Deadline", deadline);
                fields.SetGuid("ControllerId", Existing(letter.Sections[Control])?.GetGuid("DocumentInspectorId"));
                fields.SetGuid("ResolutionTypeId", details?.GetGuid("ResolutionTypeId"));
                fields.SetString("ResolutionText", details?.GetString("ResolutionText"));
                CopyPeople(letter.Sections[Readers], "AcquaintancePersons", null, review.Sections[ReviewReaders], "EmployeeId", null);
                CopyPeople(letter.Sections[ResolutionPerformers], "EmployeeId", "UnitId", review.Sections[ReviewPerformers], "EmployeeId", "UnitId");
            });
            return;
        }
        if (deadline.HasValue) task.MainInfo.EndDate = deadline;
        if (kind == ExecutionKind && !string.IsNullOrWhiteSpace(details?.GetString("ResolutionText")))
            task.MainInfo.Content = details.GetString("ResolutionText");
        Context.SaveObject(task);
    }

    /// <summary>Сохраняет резолюцию до того, как конструктор вычислит участников следующих этапов.</summary>
    protected override void OnTaskCompleting(ApprovalStage stage, Reconcile reconcile, ApprovalStageCurrentApprover approver)
    {
        var task = approver.Task;
        var document = reconcile.MainInfo.Document;
        if (!IsIncoming(document) || Context.GetObjectRef(task.SystemInfo.CardKind).Id != ReviewKind
            || task.MainInfo.CompletionResult == null)
            return;
        var decision = DecisionsHelper.GetDecisionFromTaskResult(task.MainInfo.CompletionResult, stage).Semantics;
        var letter = Read(document);
        var review = Read(task);
        var fields = Existing(review.Sections[ReviewDetails]);
        var originalDeadline = Existing(letter.Sections[Control])?.GetDateTime("ControlDate");
        if (decision is DecisionSemantics.BranchA or DecisionSemantics.BranchB)
        {
            bool keep = decision == DecisionSemantics.BranchA;
            Update(review, () =>
            {
                var resolution = review.Sections[ReviewDetails].FirstRow;
                resolution.SetGuid("ResolutionTypeId", keep ? DefaultResolution : null);
                resolution.SetString("ResolutionText", keep ? "К исполнению" : null);
                resolution.SetDateTime("Deadline", keep ? originalDeadline : null);
                var performers = review.Sections[ReviewPerformers];
                Clear(performers);
                if (keep) performers.CreateRow().SetGuid("EmployeeId", Context.GetObjectRef(task.MainInfo.CompletedUser).Id);
            });
            fields = Existing(review.Sections[ReviewDetails]);
        }
        Update(letter, () =>
        {
            var target = letter.Sections[Details].FirstRow;
            var assignments = letter.Sections[ResolutionPerformers];
            target.SetGuid("ResolutionTypeId", fields?.GetGuid("ResolutionTypeId"));
            target.SetString("ResolutionText", fields?.GetString("ResolutionText"));
            CopyPeople(review.Sections[ReviewPerformers], "EmployeeId", "UnitId", assignments, "EmployeeId", "UnitId");
            var control = letter.Sections[Control].FirstRow;
            control.SetDateTime("ControlDate", fields?.GetDateTime("Deadline"));
            if (fields?.GetGuid("ControllerId") is Guid controller) control.SetGuid("DocumentInspectorId", controller);
            CopyPeople(review.Sections[ReviewReaders], "EmployeeId", null, letter.Sections[Readers], "AcquaintancePersons", null);
        });
        if (decision is DecisionSemantics.BranchA or DecisionSemantics.BranchB)
        {
            var state = Context.GetObject<StatesState>(decision == DecisionSemantics.BranchA ? WorkState : ArchiveState);
            Context.GetService<IApprovalDocumentService>().ChangeDocumentState(document, true, state);
            Context.SaveObject(document);
        }
    }

    /// <summary>Проверяет принадлежность документа производному виду Агора.</summary>
    private bool IsIncoming(Document document) =>
        document?.SystemInfo?.CardKind != null && Context.GetObjectRef(document.SystemInfo.CardKind).Id == Incoming;

    /// <summary>Возвращает данные задания или письма из сеанса обработки события.</summary>
    private CardData Read(BaseCard card) =>
        Context.GetService<UserSession>().CardManager.GetCardData(Context.GetObjectRef(card).Id);

    /// <summary>Преобразует ссылки на сотрудников в список получателей штатного этапа.</summary>
    private List<StaffEmployee> Employees(IEnumerable<Guid> ids) =>
        ids.Where(id => id != Guid.Empty).Distinct().Select(id => Context.GetObject<StaffEmployee>(id)).ToList();

    /// <summary>Читает непустые идентификаторы участников из секции карточки.</summary>
    private static IEnumerable<Guid> Identifiers(SectionData section, string field) =>
        section.Rows.Cast<RowData>().Select(row => row.GetGuid(field).GetValueOrDefault()).Where(id => id != Guid.Empty);

    /// <summary>Читает необязательную строку реквизитов.</summary>
    private static RowData Existing(SectionData section) => section.Rows.Count == 0 ? null : section.Rows[0];

    /// <summary>Копирует сотрудников и подразделения между списками участников письма и задания.</summary>
    private static void CopyPeople(SectionData from, string employee, string unit, SectionData to, string targetEmployee, string targetUnit)
    {
        Clear(to);
        foreach (Guid id in Identifiers(from, employee).Distinct()) to.CreateRow().SetGuid(targetEmployee, id);
        if (unit != null && targetUnit != null)
            foreach (Guid id in Identifiers(from, unit).Distinct()) to.CreateRow().SetGuid(targetUnit, id);
    }

    /// <summary>Удаляет прежние строки списка перед сохранением решения получателя.</summary>
    private static void Clear(SectionData section)
    {
        foreach (Guid id in section.Rows.Cast<RowData>().Select(row => row.Id).ToArray()) section.DeleteRow(id);
    }

    /// <summary>Сохраняет реквизиты одной карточки или отменяет группу изменений.</summary>
    private static void Update(CardData card, Action change)
    {
        card.BeginUpdate();
        try { change(); card.EndUpdate(); }
        catch { card.CancelUpdate(); throw; }
    }
}
