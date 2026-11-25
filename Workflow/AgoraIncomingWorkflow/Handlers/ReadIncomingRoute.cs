using System;
using DocsVision.BackOffice.ObjectModel;
using DocsVision.BackOffice.ObjectModel.Services;
using DocsVision.Platform.ObjectManager;
using DocsVision.Platform.ObjectModel;
using DocsVision.Workflow.Gates;
using DocsVision.Workflow.Runtime;

namespace DVScriptHost
{
    internal sealed class DVScript
    {
        private static readonly Guid IncomingKind = new Guid("9530f638-6669-48b8-867a-a135ce4232a6");
        private static readonly Guid MainSection = new Guid("30eb9b87-822b-4753-9a50-a1825dca1b74");
        private static readonly Guid SystemSection = new Guid("91b2c5f7-9324-4cef-9afe-a457c8310f06");
        private static readonly Guid RouteSection = new Guid("e8a5eeac-be18-4227-95bf-ff2c16f020a4");
        private static readonly Guid ReceiversSection = new Guid("b6dfaead-baaa-4024-908c-5dbd693d0fd3");

        /// <summary>Проверяет вид входящего письма и передаёт данные получателя, регистратора и срока в переменные маршрута.</summary>
        public void Execute(ProcessInfo process, DVGate gate, UserSession session, ObjectContext context)
        {
            var input = process.GetVariableByName("Документ").Value as DVCard;
            if (input == null)
                throw new InvalidOperationException("Не заполнен инициирующий документ.");

            CardData card = session.CardManager.GetCardData(Guid.Parse(input.ID));
            RowData main = card.Sections[MainSection].FirstRow;
            if (main == null || card.Sections[SystemSection].FirstRow.GetGuid("Kind") != IncomingKind)
                throw new InvalidOperationException("Маршрут доступен для вида «Входящее письмо Агора».");

            Guid registrar = main.GetGuid("Registrar") ?? Guid.Empty;
            SectionData receivers = card.Sections[ReceiversSection];
            if (registrar == Guid.Empty || receivers.Rows.Count != 1)
                throw new InvalidOperationException("Заполните регистратора и укажите одного получателя письма.");

            Guid recipient = receivers.Rows[0].GetGuid("ReceiverStaff") ?? Guid.Empty;
            if (recipient == Guid.Empty)
                throw new InvalidOperationException("Укажите сотрудника в поле «Получатель».");

            SectionData route = card.Sections[RouteSection];
            bool requiresReview = route.Rows.Count > 0
                && route.Rows[0].GetBoolean("RequiresReview") == true;
            process.GetVariableByName("Требует рассмотрения").Value = requiresReview;
            process.GetVariableByName("Регистратор").Value = gate.GetVariable((int)DVVariableType.PRINCIPAL, null, registrar.ToString("B"));
            process.GetVariableByName("Получатель письма").Value = gate.GetVariable((int)DVVariableType.PRINCIPAL, null, recipient.ToString("B"));
            if (requiresReview)
            {
                var calendar = context.GetService<ICalendarService>();
                Guid calendarId = calendar.GetCalendarID(new[] { recipient });
                if (calendarId == Guid.Empty)
                    calendarId = calendar.GetDefaultBusinessCalendarId();
                DateTime start = DateTime.Now;
                process.GetVariableByName("Начало рассмотрения").Value = start;
                process.GetVariableByName("Срок рассмотрения").Value = calendar.GetEndDate(calendarId, start, 3d);
            }
        }
    }
}
