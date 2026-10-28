namespace LiveStream.Domain.Chat;

public enum BadgeKind
{
    Broadcaster,
    Moderator,
    Subscriber,
    Member,
    Vip,
    Donator,
    Verified,
    Custom,
}

public sealed record ChatBadge(BadgeKind Kind, string Label, string? IconKey = null);
