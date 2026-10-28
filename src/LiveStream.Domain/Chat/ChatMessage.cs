using System;
using System.Collections.Generic;
using LiveStream.Domain.Entities;

namespace LiveStream.Domain.Chat;

public sealed record ChatMessage(
    string Id,
    StreamPlatform Platform,
    string AuthorName,
    string AuthorId,
    string Message,
    DateTime Timestamp,
    string? AvatarUrl = null,
    IReadOnlyList<ChatBadge>? Badges = null,
    bool IsModerator = false,
    bool IsSubscriber = false,
    bool IsDonation = false,
    decimal? DonationAmount = null,
    string? Currency = null,
    bool IsHighlighted = false,
    bool IsFirstTimeChatter = false,
    bool IsModerated = false,
    string? RawData = null)
{
    public IReadOnlyList<ChatBadge> EffectiveBadges => Badges ?? Array.Empty<ChatBadge>();

    public string FormattedDonation => IsDonation && DonationAmount.HasValue
        ? $"{DonationAmount.Value:0.00} {Currency ?? "USD"}"
        : string.Empty;
}
