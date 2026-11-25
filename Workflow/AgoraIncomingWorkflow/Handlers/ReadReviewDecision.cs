using System;
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
        private static readonly Guid LetterControl = new Guid("d5c373ad-8008-446e-b2d6-db96ace455c4");
        private static readonly Guid LetterResolution = new Guid("e8a5eeac-be18-4227-95bf-ff2c16f020a4");
        private static readonly Guid LetterReaders = new Guid("af798ae7-baac-486e-84ef-82c59dc00a7e");
        private static readonly Guid TaskResolution = new Guid("870e1272-9858-409c-a85f-eb7b6151c102");
        private static readonly Guid TaskReaders = new Guid("8fc00166-4525-4b03-89f1-11e3c4a3f365");

        /// <summary>Подготавливает задание на рассмотрение и ожидает одного из трёх решений Агора.</summary>
        public ExecResultEnum Execute(ProcessInfo process, PassState passInfo, ObjectContext context, UserSession session)
        {
            var reference = process.GetVariableByName("Задание на рассмотрение").Value as DVCard;
            if (reference == null)
                throw new InvalidOperationException("В процессе отсутствует созданное задание на рассмотрение.");

            Task task = context.GetObject<Task>(Guid.Parse(reference.ID));
            var stateService = context.GetService<IStateService>();
            Guid current = task.SystemInfo.State.BuiltInState;
            if (current == Task.InitializationState.Id)
            {
                PrepareReview(process, session, Guid.Parse(reference.ID));
                var start = stateService.FindBranchByBuiltIn(Task.InitializationToStartedByStart, task.SystemInfo.State);
                if (start == null)
                    throw new InvalidOperationException("Для задания «На рассмотрении Агора» не найден переход запуска.");
                context.GetService<ITaskService>().StartTask(task);
                stateService.ChangeState(task, start);
                context.AcceptChanges();
            }

            var completed = stateService.FindStateByBuiltIn(Task.CompletedState, task);
            if (!Equals(task.SystemInfo.State, completed))
            {
                if (current == Task.RecalledState.Id || current == Task.RejectedState.Id)
                    throw new InvalidOperationException("Задание на рассмотрение отозвано или отклонено без решения.");
                passInfo.ExecuteTime = DateTime.Now.AddMinutes(1);
                return ExecResultEnum.KeepActive;
            }

            TaskCompletionOption result = task.MainInfo.CompletionResult;
            if (result == null || result.Value < 1 || result.Value > 3)
                throw new InvalidOperationException("Задание завершено без одного из трёх решений Агора.");

            process.GetVariableByName("Решение по рассмотрению").Value = result.Value;
            return ExecResultEnum.Done;
        }

        /// <summary>Копирует начальные реквизиты письма и список ознакомителей в задание.</summary>
        private static void PrepareReview(ProcessInfo process, UserSession session, Guid taskId)
        {
            var reference = (DVCard)process.GetVariableByName("Документ").Value;
            CardData document = session.CardManager.GetCardData(Guid.Parse(reference.ID));
            CardData review = session.CardManager.GetCardData(taskId);
            SectionData sourceControl = document.Sections[LetterControl];
            SectionData sourceResolution = document.Sections[LetterResolution];
            review.BeginUpdate();
            try
            {
                RowData fields = review.Sections[TaskResolution].FirstRow;
                if (sourceControl.Rows.Count > 0)
                {
                    RowData control = sourceControl.Rows[0];
                    fields.SetDateTime("Deadline", control.GetDateTime("ControlDate"));
                    fields.SetGuid("ControllerId", control.GetGuid("DocumentInspectorId"));
                }
                if (sourceResolution.Rows.Count > 0)
                {
                    RowData resolution = sourceResolution.Rows[0];
                    fields.SetGuid("ResolutionTypeId", resolution.GetGuid("ResolutionTypeId"));
                    fields.SetString("ResolutionText", resolution.GetString("ResolutionText"));
                }

                SectionData readers = review.Sections[TaskReaders];
                if (readers.Rows.Count == 0)
                {
                    foreach (Guid employee in document.Sections[LetterReaders].Rows.Cast<RowData>()
                        .Select(row => row.GetGuid("AcquaintancePersons").GetValueOrDefault())
                        .Where(id => id != Guid.Empty).Distinct())
                    {
                        readers.CreateRow().SetGuid("EmployeeId", employee);
                    }
                }
                review.EndUpdate();
            }
            catch
            {
                review.CancelUpdate();
                throw;
            }
        }
    }
}
