using System;
using System.Linq;
using LiveStream.Extensions.Chat;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using Xunit;

namespace LiveStream.UnitTests;

public sealed class ChatTests
{
    [Fact]
    public void ChatMessage_CreationAndBadges_AreCorrect()
    {
        var msg = new ChatMessage(
            Id: Guid.NewGuid().ToString(),
            Platform: StreamPlatform.Twitch,
            AuthorName: "StreamFan",
            AuthorId: "user-123",
            Message: "Hola a todos! Gran directo!",
            Timestamp: DateTime.UtcNow,
            Badges: [new ChatBadge(BadgeKind.Moderator, "Mod"), new ChatBadge(BadgeKind.Subscriber, "Sub 6m")],
            DonationAmount: null,
            IsHighlighted: true
        );

        Assert.Equal(StreamPlatform.Twitch, msg.Platform);
        Assert.Equal("StreamFan", msg.AuthorName);
        Assert.Equal(2, msg.Badges!.Count);
        Assert.Contains(msg.Badges, b => b.Kind == BadgeKind.Moderator);
        Assert.True(msg.IsHighlighted);
    }

    [Fact]
    public void UnifiedChatService_FiltersByPlatform()
    {
        var service = new UnifiedChatService(maxCapacity: 100);

        service.AddMessage(new ChatMessage("1", StreamPlatform.Twitch, "Alice", "u1", "Hello from Twitch", DateTime.UtcNow));
        service.AddMessage(new ChatMessage("2", StreamPlatform.YouTube, "Bob", "u2", "Hello from YouTube", DateTime.UtcNow));
        service.AddMessage(new ChatMessage("3", StreamPlatform.TikTok, "Charlie", "u3", "Hello from TikTok", DateTime.UtcNow));

        Assert.Equal(3, service.FilteredMessages.Count);

        service.Filter = new ChatFilter(Platform: StreamPlatform.Twitch);
        var twitchOnly = service.FilteredMessages;
        Assert.Single(twitchOnly);
        Assert.Equal("Alice", twitchOnly[0].AuthorName);

        service.Filter = new ChatFilter(Platform: StreamPlatform.YouTube);
        var ytOnly = service.FilteredMessages;
        Assert.Single(ytOnly);
        Assert.Equal("Bob", ytOnly[0].AuthorName);
    }

    [Fact]
    public void UnifiedChatService_FiltersByBlockedWords()
    {
        var service = new UnifiedChatService(maxCapacity: 100);
        service.Filter = new ChatFilter(WordBlacklist: ["spam", "prohibido"]);

        service.AddMessage(new ChatMessage("1", StreamPlatform.Twitch, "User1", "u1", "Este es un mensaje normal", DateTime.UtcNow));
        service.AddMessage(new ChatMessage("2", StreamPlatform.Twitch, "User2", "u2", "Entra aquí es spam puro", DateTime.UtcNow));
        service.AddMessage(new ChatMessage("3", StreamPlatform.Twitch, "User3", "u3", "Contenido Prohibido aquí", DateTime.UtcNow));

        var visible = service.FilteredMessages;
        Assert.Single(visible);
        Assert.Equal("User1", visible[0].AuthorName);
    }

    [Fact]
    public void UnifiedChatService_FiltersBySearchAndUser()
    {
        var service = new UnifiedChatService(maxCapacity: 100);

        service.AddMessage(new ChatMessage("1", StreamPlatform.Twitch, "GamerPro", "u1", "Increíble jugada!", DateTime.UtcNow));
        service.AddMessage(new ChatMessage("2", StreamPlatform.YouTube, "CasualFan", "u2", "Hola a todos", DateTime.UtcNow));
        service.AddMessage(new ChatMessage("3", StreamPlatform.Twitch, "GamerPro", "u1", "GG a todos", DateTime.UtcNow));

        service.Filter = new ChatFilter(UserQuery: "gamer");
        Assert.Equal(2, service.FilteredMessages.Count);

        service.Filter = new ChatFilter(SearchQuery: "jugada");
        var searchResult = service.FilteredMessages;
        Assert.Single(searchResult);
        Assert.Equal("1", searchResult[0].Id);
    }

    [Fact]
    public void UnifiedChatService_PauseAutoscroll_AndClear()
    {
        var service = new UnifiedChatService(maxCapacity: 100);
        Assert.False(service.IsAutoScrollPaused);

        service.IsAutoScrollPaused = true;
        Assert.True(service.IsAutoScrollPaused);

        service.AddMessage(new ChatMessage("1", StreamPlatform.Twitch, "User1", "u1", "Msg 1", DateTime.UtcNow));
        Assert.Single(service.FilteredMessages);

        service.Clear();
        Assert.Empty(service.FilteredMessages);
    }

    [Fact]
    public void GreetingService_DetectsFirstTimeChatters()
    {
        var greetingService = new GreetingService();
        var triggeredEvents = 0;
        greetingService.FirstTimeGreetingTriggered += (_, _) => triggeredEvents++;

        var msg1 = new ChatMessage("1", StreamPlatform.Twitch, "NewViewer", "u-100", "Hola primer chat!", DateTime.UtcNow);
        var isFirst = greetingService.CheckAndTrack(msg1, out var alert1);
        Assert.True(isFirst);
        Assert.NotNull(alert1);
        Assert.Equal(1, triggeredEvents);

        // Same author sends another message -> should not trigger first-time greeting
        var msg2 = new ChatMessage("2", StreamPlatform.Twitch, "NewViewer", "u-100", "Segundo mensaje", DateTime.UtcNow);
        var isSecond = greetingService.CheckAndTrack(msg2, out var alert2);
        Assert.False(isSecond);
        Assert.Null(alert2);
        Assert.Equal(1, triggeredEvents);

        // Different author -> triggers
        var msg3 = new ChatMessage("3", StreamPlatform.YouTube, "AnotherViewer", "u-200", "Buenas tardes", DateTime.UtcNow);
        var isAnotherFirst = greetingService.CheckAndTrack(msg3, out var alert3);
        Assert.True(isAnotherFirst);
        Assert.NotNull(alert3);
        Assert.Equal(2, triggeredEvents);
    }
}
