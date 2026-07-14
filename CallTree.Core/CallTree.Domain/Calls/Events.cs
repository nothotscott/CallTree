using CallTree.Domain.Primitives;

namespace CallTree.Domain.Calls;

public sealed record CallStarted(Guid CallId, CallSource Source) : IDomainEvent;

public sealed record CallAnswered(Guid CallId) : IDomainEvent;

public sealed record CallBridged(Guid CallId) : IDomainEvent;

public sealed record CallEnded(Guid CallId, CallStatus FinalStatus) : IDomainEvent;
