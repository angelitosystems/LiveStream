using System;
using LiveStream.Domain.Entities;

namespace LiveStream.Domain.Alerts;

public enum AlertKind
{
    Follower,
    Subscription,
    Donation,
    SuperChat,
    VirtualGift,
    Share,
    CommunityMember,
    Raid,
    Greeting,
    Custom,
}

public enum AlertPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3,
}

public sealed record AlertEvent(
    string Id,
    StreamPlatform Platform,
    AlertKind Kind,
    string Username,
    DateTime Timestamp,
    string? AvatarUrl = null,
    decimal? Amount = null,
    string? Currency = null,
    string? Tier = null,
    int? Months = null,
    int? Viewers = null,
    string? Message = null,
    string? GiftName = null,
    int? GiftCount = null,
    AlertPriority Priority = AlertPriority.Normal,
    string? RawPayload = null);
