using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DocsVision.BackOffice.ObjectModel;
using DocsVision.BackOffice.ObjectModel.Services;
using DocsVision.Platform.ObjectManager;
using DocsVision.Platform.ObjectManager.SystemCards;
using DocsVision.Platform.Security.AccessControl;
using DocsVision.Platform.WebClient;

namespace Agora.Web;

internal sealed class IncomingNumbering : IIncomingNumbering
{
    private static readonly Guid IncomingKind = new("9530f638-6669-48b8-867a-a135ce4232a6");
    private static readonly Guid DocumentType = new("b9f7bfd7-7429-455e-a3f1-94ffb569c794");
    private static readonly Guid Preparation = new("de445cf0-2482-48ae-80b3-49746540c4de");
    private static readonly Guid MainSection = new("30eb9b87-822b-4753-9a50-a1825dca1b74");
    private static readonly Guid SystemSection = new("91b2c5f7-9324-4cef-9afe-a457c8310f06");
    private static readonly Guid JournalSection = new("e8a5eeac-be18-4227-95bf-ff2c16f020a4");
    private static readonly Guid NumbersSection = new("61c8cc7c-35ce-49e5-9ccd-e9f3c1129445");
    private static readonly Guid CounterId = new("8447d8d9-af29-4615-bed6-b656a235ed0b");
    private static readonly Guid HeadOffice = new("5fb75938-6158-4c51-8303-2444c9c6ac15");
    private static readonly Guid Saratov = new("d114b483-b990-42f0-b480-149408d6e597");
    private static readonly Guid Kazan = new("52b3f106-0450-44ec-b6d5-ca914c44f255");
    private static readonly object ZoneCreation = new();
    private static readonly Regex NumberPattern = new(@"^(Вх-[0-9]{3,})/(ГО|64|16)$", RegexOptions.CultureInvariant);

    private readonly ICurrentObjectContextProvider contexts;

    public IncomingNumbering(ICurrentObjectContextProvider contexts)
    {
        this.contexts = contexts;
    }

    public IncomingNumberingResult Number(Guid cardId)
    {
        var actor = contexts.GetOrCreateCurrentSessionContext();
        if (!actor.AdvancedCardManager.CardReadAccessCheck(cardId))
            return new(IncomingNumberingOutcome.Forbidden);

        var card = actor.Session.CardManager.GetCardData(cardId);
        if (card.Type.Id != DocumentType)
            return new(IncomingNumberingOutcome.NotFound);

        var context = actor.ObjectContext;
        var letter = context.GetObject<Document>(cardId);
        if (!context.LockObject(letter, false))
            return new(IncomingNumberingOutcome.Locked);

        try
        {
            card.Refresh(true);
            var system = card.Sections[SystemSection].FirstRow;
            if (system.GetGuid("Kind") != IncomingKind)
                return new(IncomingNumberingOutcome.NotFound);
            if (system.GetGuid("State") != Preparation)
                return new(IncomingNumberingOutcome.RequiresPreparation);
            if (!card.AccessCheck((int)CardDataRights.WriteAttributes))
                return new(IncomingNumberingOutcome.Forbidden);

            var main = card.Sections[MainSection].FirstRow;
            var staff = context.GetService<IStaffService>().GetCurrentEmployee();
            if (staff == null)
                return new(IncomingNumberingOutcome.Forbidden);
            Guid staffId = context.GetObjectRef(staff).Id;
            if (main.GetGuid("Registrar").GetValueOrDefault() == Guid.Empty && main.GetGuid("Author") == staffId)
                main.SetGuid("Registrar", staffId);

            Guid? journal = card.Sections[JournalSection].FirstRow?.GetGuid("JournalId");
            string suffix = journal == HeadOffice ? "ГО" : journal == Saratov ? "64" : journal == Kazan ? "16" : null;
            if (suffix == null)
                return new(IncomingNumberingOutcome.RequiresJournal);

            Guid registration = main.GetGuid("RegNumber").GetValueOrDefault();
            if (registration != Guid.Empty)
            {
                var existing = card.Sections[NumbersSection].Rows[registration];
                string assigned = existing.GetString("Number");
                Match match = NumberPattern.Match(assigned ?? string.Empty);
                if (match.Success && match.Groups[2].Value != suffix && existing.GetGuid("NumericPart").GetValueOrDefault() != Guid.Empty)
                {
                    assigned = match.Groups[1].Value + "/" + suffix;
                    existing.SetString("Number", assigned);
                }
                return new(IncomingNumberingOutcome.Assigned, registration, assigned, actor.AdvancedCardManager.GetCardTimestampModel(card.Id).Timestamp);
            }

            using var pool = contexts.GetOrCreateExclusivePoolSessionContext();
            var counter = (NumeratorCard)pool.Context.Session.CardManager.GetCard(CounterId);
            string year = "Y" + card.CreateDate.Year.ToString(CultureInfo.InvariantCulture);
            NumeratorZone zone;
            lock (ZoneCreation)
                zone = counter.Zones.Cast<NumeratorZone>().FirstOrDefault(item => item.Name == year)
                    ?? counter.Zones.AddNew(year);

            int sequence = zone.GetNumber(staffId, true, out Guid reserved);
            string number = "Вх-" + sequence.ToString("D3", CultureInfo.InvariantCulture) + "/" + suffix;
            try
            {
                card.BeginUpdate();
                var row = card.Sections[NumbersSection].Rows.AddNew();
                row.SetGuid("NumericPart", reserved);
                row.SetString("Number", number);
                main.SetGuid("RegNumber", row.Id);
                registration = row.Id;
                card.EndUpdate();
            }
            catch
            {
                card.CancelUpdate();
                card.Refresh(true);
                if (card.Sections[MainSection].FirstRow.GetGuid("RegNumber") != registration || registration == Guid.Empty)
                    zone.ReleaseNumber(reserved);
                throw;
            }
            return new(IncomingNumberingOutcome.Assigned, registration, number, actor.AdvancedCardManager.GetCardTimestampModel(card.Id).Timestamp);
        }
        finally
        {
            context.UnlockObject(letter);
        }
    }
}
