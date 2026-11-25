using System;
using System.Collections.Generic;
using System.Linq;
using DocsVision.BackOffice.ObjectModel;
using DocsVision.BackOffice.ObjectModel.Services;
using DocsVision.Platform.ObjectManager;
using DocsVision.Platform.ObjectModel;
using DocsVision.Workflow.Functions;
using DocsVision.Workflow.Gates;
using DocsVision.Workflow.Objects;
using DocsVision.Workflow.Runtime;

namespace DVScriptHost
{
    internal sealed class DVScript
    {
        private static readonly Guid Main = new Guid("30eb9b87-822b-4753-9a50-a1825dca1b74");
        private static readonly Guid Details = new Guid("e8a5eeac-be18-4227-95bf-ff2c16f020a4");
        private static readonly Guid Control = new Guid("d5c373ad-8008-446e-b2d6-db96ace455c4");
        private static readonly Guid Readers = new Guid("af798ae7-baac-486e-84ef-82c59dc00a7e");
        private static readonly Guid Receivers = new Guid("b6dfaead-baaa-4024-908c-5dbd693d0fd3");
        private static readonly Guid Coexecutors = new Guid("da2ad468-e4a9-444b-8135-848b245a3fb2");
        private static readonly Guid ResolutionPerformers = new Guid("c6561de3-3b09-4a44-9d6c-0d30ab1d806c");
        private static readonly Guid ReviewDetails = new Guid("870e1272-9858-409c-a85f-eb7b6151c102");
        private static readonly Guid ReviewPerformers = new Guid("4dcbc2c0-0f5c-407e-8c5c-a2477cb2b550");
        private static readonly Guid ReviewReaders = new Guid("8fc00166-4525-4b03-89f1-11e3c4a3f365");
        private static readonly Guid DefaultResolution = new Guid("5112b7d1-acee-485a-9701-4ba0c234a3bc");
        private static readonly Guid ParallelExecution = new Guid("f19582fe-98cf-46a1-bc6d-842f42a0fd96");
        private static readonly Guid GroupKind = new Guid("3d40ca84-6a12-479c-89f0-d64de1ed2ca5");
        private static readonly Guid ExecutionKind = new Guid("e2c41216-4f4e-4a02-9fc4-774ce9c1da79");
        private static readonly Guid ReadingKind = new Guid("27a4021f-8fac-46cc-bb30-d88f2a4d4737");
        private static readonly Guid ControlKind = new Guid("25e8363d-90a0-4edc-ada6-da2e1525a254");
        private static readonly Guid ReviewState = new Guid("bdb6771a-7124-45f8-8526-e597e9febdfc");
        private static readonly Guid WorkState = new Guid("103aa9b4-4ce3-40ab-9cf2-001eb48a1b11");
        private static readonly Guid ArchiveState = new Guid("3471aa78-fd91-4a18-961c-3de2225d2eea");
        private static readonly Guid ReviewToWorkBranch = new Guid("c439a0b1-7d78-4c38-a651-9d1bcf975843");
        private static readonly Guid SkipBranch = new Guid("0a28736e-e313-47ef-bced-3021ece399a8");
        private static readonly Guid ArchiveWorkBranch = new Guid("b0c241e1-82ea-436f-92b3-3a2ba45fa2fe");
        private const string ReadingName = "Ознакомиться с входящим письмом";
        private const string ExecutionName = "Исполнить входящее письмо";
        private const string LegacyControlName = "Проконтролировать исполнение входящего письма";
        private const string AcceptanceName = "Приёмка исполнения входящего письма";
        private const int Accepted = 1;
        private const int Rejected = 2;

        private sealed class Recipient
        {
            public Guid Id;
            public bool IsUnit;
        }

        private sealed class Assignment
        {
            public int Decision;
            public Guid Author;
            public Guid RecipientId;
            public Guid Controller;
            public Guid? ResolutionType;
            public string Text;
            public DateTime? Deadline;
            public bool Alternative;
            public Recipient[] Performers;
            public Recipient[] Readers;
        }

        /// <summary>Сохраняет решение, формирует задания маршрута и переносит письмо в архив по завершении.</summary>
        public ExecResultEnum Execute(ProcessInfo process, PassState pass, ObjectContext context, UserSession session)
        {
            var input = (DVCard)process.GetVariableByName("Документ").Value;
            Guid cardId = Guid.Parse(input.ID);
            var document = context.GetObject<Document>(cardId);
            bool reviewing = (bool)process.GetVariableByName("Требует рассмотрения").Value;
            int decision = reviewing ? Convert.ToInt32(process.GetVariableByName("Решение по рассмотрению").Value) : 0;
            if (reviewing && (decision < 1 || decision > 3))
                throw new InvalidOperationException("В процессе отсутствует решение по рассмотрению.");
            if (InState(context, document, ArchiveState))
                return ExecResultEnum.Done;
            if (!InState(context, document, WorkState) &&
                !(reviewing && InState(context, document, ReviewState)))
                throw new InvalidOperationException("Состояние письма не соответствует выбранному маршруту.");

            CardData letter = session.CardManager.GetCardData(cardId);
            CardData review = null;
            if (reviewing)
            {
                var taskReference = (DVCard)process.GetVariableByName("Задание на рассмотрение").Value;
                review = session.CardManager.GetCardData(Guid.Parse(taskReference.ID));
            }
            Assignment assignment = ReadAssignment(context, letter, review, decision);
            if (decision == 1 && (assignment.Performers.Length == 0 || !assignment.ResolutionType.HasValue
                || string.IsNullOrWhiteSpace(assignment.Text)))
                throw new InvalidOperationException("Укажите тип и текст резолюции, а также исполнителей.");
            if (decision != 3 && assignment.Controller == Guid.Empty)
                throw new InvalidOperationException("Укажите контролёра.");
            if (decision != 3 && !assignment.Deadline.HasValue)
                throw new InvalidOperationException("Укажите срок исполнения письма.");

            if (reviewing && string.IsNullOrEmpty(pass.Data))
            {
                SaveLetterResolution(letter, assignment);
                if (decision == 2 || decision == 3)
                    SaveReviewResolution(review, assignment);
                pass.Data = "resolution-saved";
            }

            if (decision == 3)
            {
                Move(context, document, SkipBranch);
                return ExecResultEnum.Done;
            }

            if (InState(context, document, ReviewState))
                Move(context, document, ReviewToWorkBranch);

            TaskList list = EnsureTaskList(context, document);
            TaskGroup reading = EnsureGroup(context, document, assignment, ReadingKind, ReadingName,
                "Ознакомьтесь с письмом и приложенными файлами.", assignment.Readers);

            // Активные письма старой версии завершаются по уже выданным заданиям.
            TaskGroup legacyControl = FindGroup(list, context.GetObject<KindsCardKind>(GroupKind), LegacyControlName);
            TaskGroup legacyExecution = FindGroup(list, context.GetObject<KindsCardKind>(GroupKind), ExecutionName);
            Task legacyAlternative = FindTask(list, context.GetObject<KindsCardKind>(ExecutionKind), ExecutionName);
            if (legacyControl != null || legacyExecution != null || legacyAlternative != null)
            {
                if (legacyControl == null)
                    legacyControl = EnsureGroup(context, document, assignment, ControlKind, LegacyControlName,
                        "Проверьте выполнение заданий по письму и примите результат.",
                        new[] { new Recipient { Id = assignment.Controller } });
                if (!IsComplete(reading) || !IsComplete(legacyExecution) || !IsComplete(legacyAlternative)
                    || !IsComplete(legacyControl))
                    return Wait(pass);
            }
            else if (!AcceptExecution(context, document, assignment, reading))
            {
                return Wait(pass);
            }
            Move(context, document, ArchiveWorkBranch);
            return ExecResultEnum.Done;
        }

        /// <summary>Запускает исполнение и открывает приёмку только после исполнения и ознакомления.</summary>
        private static bool AcceptExecution(ObjectContext context, Document document, Assignment assignment, TaskGroup reading)
        {
            TaskList list = EnsureTaskList(context, document);
            var acceptanceKind = context.GetObject<KindsCardKind>(ControlKind);
            int cycle = 1;
            // Завершённая приёмка хранит решение. Повторное выполнение функции не создаёт дубликаты.
            while (true)
            {
                string acceptanceName = CycleName(AcceptanceName, cycle);
                Task acceptance = FindTask(list, acceptanceKind, acceptanceName);
                if (acceptance != null && IsComplete(acceptance))
                {
                    int? decision = acceptance.MainInfo.CompletionResult?.Value;
                    if (decision == Accepted)
                        return true;
                    if (decision != Rejected)
                        throw new InvalidOperationException("Приёмка завершена без решения «Принято» или «Отклонено».");
                    cycle++;
                    continue;
                }

                string executionName = CycleName(ExecutionName, cycle);
                bool executionComplete;
                if (assignment.Alternative)
                    executionComplete = IsComplete(EnsureAlternative(context, document, assignment, executionName));
                else
                    executionComplete = IsComplete(EnsureGroup(context, document, assignment, ExecutionKind,
                        executionName, assignment.Text, assignment.Performers));
                if (executionComplete && IsComplete(reading))
                    EnsureAcceptance(context, document, assignment, acceptanceName);
                return false;
            }
        }

        /// <summary>Формирует уникальное имя задания внутри последовательности повторных исполнений.</summary>
        private static string CycleName(string name, int cycle)
        {
            return name + " (цикл " + cycle.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>Планирует следующую проверку заданий без блокировки процесса.</summary>
        private static ExecResultEnum Wait(PassState pass)
        {
            pass.ExecuteTime = DateTime.Now.AddMinutes(1);
            return ExecResultEnum.KeepActive;
        }

        /// <summary>Проверяет завершение группы, если участникам было что назначить.</summary>
        private static bool IsComplete(TaskGroup group)
        {
            return group == null || group.SystemInfo.State.BuiltInState == TaskGroup.CompletedState.Id;
        }

        /// <summary>Проверяет завершение отдельного задания.</summary>
        private static bool IsComplete(Task task)
        {
            return task == null || task.SystemInfo.State.BuiltInState == Task.CompletedState.Id;
        }

        /// <summary>Собирает участников, срок и текст заданий по письму и фактическому решению о рассмотрении.</summary>
        private static Assignment ReadAssignment(ObjectContext context, CardData letter, CardData review, int decision)
        {
            RowData letterMain = letter.Sections[Main].FirstRow;
            RowData control = ExistingRow(letter.Sections[Control]);
            RowData details = ExistingRow(letter.Sections[Details]);
            Guid recipient = letter.Sections[Receivers].Rows[0].GetGuid("ReceiverStaff").GetValueOrDefault();
            Guid registrar = letterMain.GetGuid("Registrar").GetValueOrDefault();
            DateTime? deadline = control == null ? null : control.GetDateTime("ControlDate");
            Guid controller = control == null ? Guid.Empty : control.GetGuid("DocumentInspectorId").GetValueOrDefault();
            var result = new Assignment {
                Decision = decision,
                Deadline = deadline,
                Controller = controller == Guid.Empty ? registrar : controller,
                Author = decision == 0 ? (controller == Guid.Empty ? registrar : controller) : recipient,
                RecipientId = recipient,
                Text = details == null ? null : details.GetString("ResolutionText"),
                Readers = ReadPeople(letter.Sections[Readers], "AcquaintancePersons", null)
            };
            if (decision == 0)
            {
                result.Alternative = details == null || details.GetGuid("ExecutionTypeId") != ParallelExecution;
                result.Performers = DistinctPeople(new[] { new Recipient { Id = recipient } }
                    .Concat(ReadPeople(letter.Sections[Coexecutors], "EmployeeId", "UnitId")));
                return result;
            }

            var reviewedTask = context.GetObject<Task>(review.Id);
            if (reviewedTask.MainInfo.CompletedUser == null)
                throw new InvalidOperationException("Не удалось определить сотрудника, завершившего рассмотрение.");
            var reviewFields = ExistingRow(review.Sections[ReviewDetails]);
            result.Controller = reviewFields == null ? Guid.Empty : reviewFields.GetGuid("ControllerId").GetValueOrDefault();
            result.Readers = ReadPeople(review.Sections[ReviewReaders], "EmployeeId", null);
            if (decision == 1)
            {
                result.Deadline = reviewFields == null ? deadline : reviewFields.GetDateTime("Deadline") ?? deadline;
                result.ResolutionType = reviewFields == null ? null : reviewFields.GetGuid("ResolutionTypeId");
                result.Text = reviewFields == null ? null : reviewFields.GetString("ResolutionText");
                result.Performers = ReadPeople(review.Sections[ReviewPerformers], "EmployeeId", "UnitId");
            }
            else
            {
                result.ResolutionType = decision == 2 ? (Guid?)DefaultResolution : null;
                result.Text = decision == 2 ? "К исполнению" : null;
                result.Performers = decision == 2
                    ? new[] { new Recipient { Id = context.GetObjectRef(reviewedTask.MainInfo.CompletedUser).Id } }
                    : new Recipient[0];
                if (decision == 3)
                    result.Readers = new Recipient[0];
            }
            return result;
        }

        /// <summary>Заменяет реквизиты резолюции в карточке письма данными завершённого рассмотрения.</summary>
        private static void SaveLetterResolution(CardData card, Assignment assignment)
        {
            Update(card, () =>
            {
                RowData row = card.Sections[Details].FirstRow;
                row.SetGuid("ResolutionTypeId", assignment.ResolutionType);
                row.SetString("ResolutionText", assignment.Text);
                ReplacePeople(card.Sections[ResolutionPerformers], "EmployeeId", "UnitId", assignment.Performers);
                ReplacePeople(card.Sections[Readers], "AcquaintancePersons", null, assignment.Readers);
                if (assignment.Decision != 3)
                {
                    RowData control = card.Sections[Control].FirstRow;
                    control.SetDateTime("ControlDate", assignment.Deadline);
                    control.SetGuid("DocumentInspectorId", assignment.Controller);
                }
            });
        }

        /// <summary>Приводит сохранённые реквизиты задания в соответствие с решением об оставлении или завершении рассмотрения.</summary>
        private static void SaveReviewResolution(CardData card, Assignment assignment)
        {
            Update(card, () =>
            {
                RowData row = card.Sections[ReviewDetails].FirstRow;
                row.SetGuid("ResolutionTypeId", assignment.ResolutionType);
                row.SetString("ResolutionText", assignment.Text);
                row.SetDateTime("Deadline", assignment.Decision == 2 ? assignment.Deadline : null);
                ReplacePeople(card.Sections[ReviewPerformers], "EmployeeId", "UnitId", assignment.Performers);
            });
        }

        /// <summary>Находит уже поставленную группу или создаёт задания для указанных участников.</summary>
        private static TaskGroup EnsureGroup(ObjectContext context, Document document, Assignment assignment,
            Guid taskKindId, string name, string text, Recipient[] recipients)
        {
            if (recipients.Length == 0)
                return null;
            var groupKind = context.GetObject<KindsCardKind>(GroupKind);
            var groups = context.GetService<ITaskGroupService>();
            var states = context.GetService<IStateService>();
            TaskList list = EnsureTaskList(context, document);
            TaskGroup group = FindGroup(list, groupKind, name);
            if (group == null)
            {
                group = groups.CreateTaskGroup(groupKind);
                group.Description = name;
                group.MainInfo.Name = name;
                group.MainInfo.Content = text;
                group.MainInfo.Author = context.GetObject<StaffEmployee>(assignment.Author);
                group.MainInfo.ExecutionType = TaskGroupExecutionType.Parallel;
                group.MainInfo.StartDate = DateTime.Now;
                group.MainInfo.EndDate = assignment.Deadline;
                if (taskKindId == ExecutionKind)
                {
                    group.MainInfo.Controller = context.GetObject<StaffEmployee>(assignment.Controller);
                    group.MainInfo.ControlDate = assignment.Deadline;
                }
                foreach (Recipient recipient in recipients)
                {
                    if (recipient.IsUnit)
                        groups.AddSelectedPerformer(group, context.GetObject<StaffUnit>(recipient.Id));
                    else
                        groups.AddSelectedPerformer(group, context.GetObject<StaffEmployee>(recipient.Id));
                }
                context.GetService<ITaskListService>().AddTaskGroup(list, group, document);
                context.AcceptChanges();
            }
            StatesState performance = states.FindStateByBuiltIn(TaskGroup.PerformanceState, group);
            if (Equals(group.SystemInfo.State, performance) || groups.IsTaskGroupCompleted(group))
                return group;
            if (group.MainInfo.PerformanceProcess == Guid.Empty)
            {
                string errors;
                if (!groups.ValidateBeforeSendToPerformance(group, out errors))
                    throw new InvalidOperationException(errors);
                groups.SendToPerformance(group, context.GetObject<KindsCardKind>(taskKindId));
            }
            states.ChangeState(group, performance);
            context.AcceptChanges();
            return group;
        }

        /// <summary>Находит связанную группу Агора по имени этапа.</summary>
        private static TaskGroup FindGroup(TaskList list, KindsCardKind kind, string name)
        {
            return list.TaskGroups.Select(row => row.TaskGroup).FirstOrDefault(group => group != null
                && Equals(group.SystemInfo.CardKind, kind) && group.MainInfo.Name == name);
        }

        /// <summary>Находит связанное задание Агора заданного вида и цикла.</summary>
        private static Task FindTask(TaskList list, KindsCardKind kind, string name)
        {
            return list.Tasks.Select(row => row.Task).FirstOrDefault(task => task != null
                && Equals(task.SystemInfo.CardKind, kind) && task.MainInfo.Name == name);
        }

        /// <summary>Возвращает или создаёт одно задание на исполнение для альтернативного списка участников.</summary>
        private static Task EnsureAlternative(ObjectContext context, Document letter, Assignment assignment, string name)
        {
            var kind = context.GetObject<KindsCardKind>(ExecutionKind);
            var tasks = context.GetService<ITaskService>();
            TaskList list = EnsureTaskList(context, letter);
            Task task = FindTask(list, kind, name);
            if (task == null)
            {
                task = tasks.CreateTask(kind);
                task.Description = name;
                task.MainInfo.Name = name;
                task.MainInfo.Author = context.GetObject<StaffEmployee>(assignment.Author);
                task.MainInfo.Content = assignment.Text;
                task.MainInfo.StartDate = DateTime.Now;
                task.MainInfo.EndDate = assignment.Deadline;
                task.MainInfo.Controller = context.GetObject<StaffEmployee>(assignment.Controller);
                task.MainInfo.ControlDate = assignment.Deadline;
                foreach (Recipient recipient in assignment.Performers)
                {
                    if (recipient.IsUnit)
                        tasks.AddSelectedPerformer(task.MainInfo, context.GetObject<StaffUnit>(recipient.Id));
                    else
                        tasks.AddSelectedPerformer(task.MainInfo, context.GetObject<StaffEmployee>(recipient.Id));
                }
                context.GetService<ITaskListService>().AddTask(list, task, letter);
                context.AcceptChanges();
            }
            StartTask(context, task);
            return task;
        }

        /// <summary>Выдаёт контролёру одно задание с двумя штатными вариантами завершения.</summary>
        private static void EnsureAcceptance(ObjectContext context, Document letter, Assignment assignment, string name)
        {
            var kind = context.GetObject<KindsCardKind>(ControlKind);
            var tasks = context.GetService<ITaskService>();
            TaskList list = EnsureTaskList(context, letter);
            Task task = FindTask(list, kind, name);
            if (task == null)
            {
                task = tasks.CreateTask(kind);
                task.Description = name;
                task.MainInfo.Name = name;
                task.MainInfo.Author = context.GetObject<StaffEmployee>(assignment.RecipientId);
                task.MainInfo.Content = "Проверьте результаты исполнения письма. При отклонении исполнителям будут выданы задания на повторное исполнение.";
                task.MainInfo.StartDate = DateTime.Now;
                task.MainInfo.EndDate = assignment.Deadline;
                task.Preset.Completion.ReportRequired = false;
                task.Preset.Completion.CompletionOptions.Clear();
                tasks.AddTaskCompletionOption(task, "Принято", Guid.Empty, Accepted, null);
                tasks.AddTaskCompletionOption(task, "Отклонено", Guid.Empty, Rejected, null);
                tasks.AddSelectedPerformer(task.MainInfo, context.GetObject<StaffEmployee>(assignment.Controller));
                context.GetService<ITaskListService>().AddTask(list, task, letter);
                context.AcceptChanges();
            }
            StartTask(context, task);
        }

        /// <summary>Запускает подготовленное задание через штатный переход Docsvision.</summary>
        private static void StartTask(ObjectContext context, Task task)
        {
            var states = context.GetService<IStateService>();
            StatesStateMachineBranch start = states.FindBranchByBuiltIn(Task.InitializationToStartedByStart, task.SystemInfo.State);
            if (start != null)
            {
                context.GetService<ITaskService>().StartTask(task);
                states.ChangeState(task, start);
                context.AcceptChanges();
            }
        }

        /// <summary>Возвращает список заданий письма, создавая его при первом назначении.</summary>
        private static TaskList EnsureTaskList(ObjectContext context, Document letter)
        {
            if (letter.MainInfo.Tasks == null)
                letter.MainInfo.Tasks = context.GetService<ITaskListService>().CreateTaskList();
            return letter.MainInfo.Tasks;
        }

        /// <summary>Сверяет фактическое состояние письма с ожидаемым состоянием маршрута.</summary>
        private static bool InState(ObjectContext context, Document document, Guid state)
        {
            return Equals(document.SystemInfo.State, context.GetObject<StatesState>(state));
        }

        /// <summary>Применяет и сохраняет штатный переход состояния письма по указанной ветке.</summary>
        private static void Move(ObjectContext context, Document document, Guid transition)
        {
            string error;
            context.GetService<IStateService>().ChangeState(document,
                context.GetObject<StatesStateMachineBranch>(transition), false, out error);
            if (!string.IsNullOrWhiteSpace(error))
                throw new InvalidOperationException(error);
            context.AcceptChanges();
        }

        /// <summary>Возвращает строку реквизитов при её наличии.</summary>
        private static RowData ExistingRow(SectionData section)
        {
            return section.Rows.Count > 0 ? section.Rows[0] : null;
        }

        /// <summary>Читает сотрудников и подразделения из секции участников задания.</summary>
        private static Recipient[] ReadPeople(SectionData section, string employeeField, string unitField)
        {
            return DistinctPeople(section.Rows.Cast<RowData>().Select(row =>
            {
                Guid employee = row.GetGuid(employeeField).GetValueOrDefault();
                if (employee != Guid.Empty || unitField == null)
                    return new Recipient { Id = employee };
                return new Recipient { Id = row.GetGuid(unitField).GetValueOrDefault(), IsUnit = true };
            }));
        }

        /// <summary>Удаляет пустые записи и повторные назначения одному участнику.</summary>
        private static Recipient[] DistinctPeople(IEnumerable<Recipient> people)
        {
            return people.Where(person => person.Id != Guid.Empty)
                .GroupBy(person => new { person.Id, person.IsUnit }).Select(group => group.First()).ToArray();
        }

        /// <summary>Заменяет содержимое секции актуальным списком назначений.</summary>
        private static void ReplacePeople(SectionData section, string employeeField, string unitField, Recipient[] people)
        {
            foreach (Guid id in section.Rows.Cast<RowData>().Select(row => row.Id).ToArray())
                section.DeleteRow(id);
            foreach (Recipient person in people)
            {
                RowData row = section.CreateRow();
                row.SetGuid(person.IsUnit ? unitField : employeeField, person.Id);
            }
        }

        /// <summary>Фиксирует группу изменений карточки или отменяет её при ошибке.</summary>
        private static void Update(CardData card, Action change)
        {
            card.BeginUpdate();
            try
            {
                change();
                card.EndUpdate();
            }
            catch
            {
                card.CancelUpdate();
                throw;
            }
        }
    }
}
