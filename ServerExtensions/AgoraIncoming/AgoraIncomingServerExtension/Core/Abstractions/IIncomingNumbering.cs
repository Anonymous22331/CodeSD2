using System;

namespace Agora.Web;

public interface IIncomingNumbering
{
    IncomingNumberingResult Number(Guid cardId);
}

public enum IncomingNumberingOutcome
{
    Assigned,
    Forbidden,
    NotFound,
    Locked,
    RequiresPreparation,
    RequiresJournal
}

public sealed record IncomingNumberingResult(IncomingNumberingOutcome Outcome, Guid Id = default, string Number = null, long? Timestamp = null);
