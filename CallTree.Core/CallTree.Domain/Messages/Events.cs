using CallTree.Domain.Primitives;

namespace CallTree.Domain.Messages;

public sealed record MessageReceived(Guid MessageId, MessageSource Source) : IDomainEvent;

public sealed record MessageRelayed(Guid MessageId, MessageSource Source) : IDomainEvent;

public sealed record MessageFailed(Guid MessageId, string Reason) : IDomainEvent;
