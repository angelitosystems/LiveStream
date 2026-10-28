using System;
using System.Collections.Generic;
using System.Linq;
using LiveStream.Domain.Entities;

namespace LiveStream.Domain.Chat;

public sealed record ChatFilter(
    StreamPlatform? Platform = null,
    string? UserQuery = null,
    string? SearchQuery = null,
    IReadOnlyList<string>? WordBlacklist = null,
    IReadOnlyList<string>? HighlightKeywords = null,
    bool ShowOnlyModerated = false,
    bool ShowOnlyHighlights = false,
    bool ShowOnlyDonations = false)
{
    public bool Matches(ChatMessage message)
    {
        if (Platform.HasValue && message.Platform != Platform.Value)
            return false;

        if (!string.IsNullOrWhiteSpace(UserQuery) &&
            !message.AuthorName.Contains(UserQuery.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(SearchQuery) &&
            !message.Message.Contains(SearchQuery.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (ShowOnlyModerated && !message.IsModerated)
            return false;

        if (ShowOnlyHighlights && !message.IsHighlighted)
            return false;

        if (ShowOnlyDonations && !message.IsDonation)
            return false;

        if (WordBlacklist is { Count: > 0 })
        {
            foreach (var blocked in WordBlacklist)
            {
                if (!string.IsNullOrWhiteSpace(blocked) &&
                    message.Message.Contains(blocked.Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        return true;
    }

    public bool IsHighlightedWord(string messageText)
    {
        if (HighlightKeywords is null or { Count: 0 })
            return false;
        return HighlightKeywords.Any(k => !string.IsNullOrWhiteSpace(k) &&
            messageText.Contains(k.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
