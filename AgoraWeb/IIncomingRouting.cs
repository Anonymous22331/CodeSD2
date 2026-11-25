using System;

namespace Agora.Web;

public interface IIncomingRouting
{
    IncomingRoutingOutcome Send(Guid id, long timestamp);
    IncomingRoutingOutcome Register(Guid id, long timestamp);
}

public enum IncomingRoutingOutcome
{
    Sent,
    Forbidden,
    NotFound,
    RequiresRegistration,
    Locked,
    Changed,
    Disabled,
    MainFileMissing
}
