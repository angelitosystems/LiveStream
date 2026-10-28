using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Integrations;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Integrations;
using Xunit;

namespace LiveStream.UnitTests;

public sealed class IntegrationsTests
{
    private sealed class MemorySecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>("test-secret-token");
        public Task SetAsync(string key, string secret, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void TwitchConnector_ParseIrc_ParsesPrivmsgWithTags()
    {
        var raw = "@badge-info=;badges=broadcaster/1,subscriber/3006;color=#00FF7F;display-name=StreamerPro;emotes=;first-msg=0;flags=;id=abc-123;mod=0;room-id=12345;subscriber=1;tmi-sent-ts=1672531199000;turbo=0;user-id=99999;user-type= :streamerpro!streamerpro@streamerpro.tmi.twitch.tv PRIVMSG #streamerpro :¡Hola a todos bienvenidos al stream!";

        var (tags, command, trailing, prefix) = TwitchConnector.ParseIrc(raw);

        Assert.Equal("PRIVMSG", command);
        Assert.Equal("¡Hola a todos bienvenidos al stream!", trailing);
        Assert.Equal("StreamerPro", tags["display-name"]);
        Assert.Equal("abc-123", tags["id"]);
        Assert.Equal("1", tags["subscriber"]);
    }

    [Fact]
    public void TwitchConnector_ParseIrc_ParsesUserNoticeSub()
    {
        var raw = "@badge-info=;badges=subscriber/12;display-name=SuperFan;msg-id=sub;msg-param-sub-plan=1000;system-msg=SuperFan\\ssubscribed\\sat\\sTier\\s1. :tmi.twitch.tv USERNOTICE #channel :Amo este canal!";

        var (tags, command, trailing, _) = TwitchConnector.ParseIrc(raw);

        Assert.Equal("USERNOTICE", command);
        Assert.Equal("sub", tags["msg-id"]);
        Assert.Equal("SuperFan", tags["display-name"]);
        Assert.Equal("Amo este canal!", trailing);
    }

    [Fact]
    public void TwitchConnector_ParseBadges_RecognizesTypes()
    {
        var badges = TwitchConnector.ParseBadges("broadcaster/1,moderator/1,vip/1,subscriber/6");

        Assert.Equal(4, badges.Count);
        Assert.Contains(badges, b => b.Kind == BadgeKind.Broadcaster);
        Assert.Contains(badges, b => b.Kind == BadgeKind.Moderator);
        Assert.Contains(badges, b => b.Kind == BadgeKind.Vip);
        Assert.Contains(badges, b => b.Kind == BadgeKind.Subscriber);
    }

    [Fact]
    public async Task IntegrationManager_SimulateMessageAndAlert_DispatchesEvents()
    {
        var store = new MemorySecretStore();
        await using var manager = new IntegrationManager(store);

        ChatMessage? receivedMsg = null;
        AlertEvent? receivedAlert = null;

        manager.MessageReceived += (_, msg) => receivedMsg = msg;
        manager.AlertReceived += (_, alert) => receivedAlert = alert;

        var testMsg = new ChatMessage("m-1", StreamPlatform.YouTube, "YT_User", "u-yt", "Test message", DateTime.UtcNow);
        manager.SimulateMessage(testMsg);

        Assert.NotNull(receivedMsg);
        Assert.Equal("YT_User", receivedMsg.AuthorName);
        Assert.Equal("Test message", receivedMsg.Message);

        var testAlert = new AlertEvent("a-1", StreamPlatform.TikTok, AlertKind.VirtualGift, "TikToker", DateTime.UtcNow, GiftName: "León", GiftCount: 1);
        manager.SimulateAlert(testAlert);

        Assert.NotNull(receivedAlert);
        Assert.Equal("TikToker", receivedAlert.Username);
        Assert.Equal(AlertKind.VirtualGift, receivedAlert.Kind);
        Assert.Equal("León", receivedAlert.GiftName);
    }

    [Fact]
    public void PlatformIntegrationConfig_DefaultsAndValidation()
    {
        var config = PlatformIntegrationConfig.CreateDefault(StreamPlatform.Twitch);
        Assert.Equal(StreamPlatform.Twitch, config.Platform);
        Assert.True(config.EnableChat);
        Assert.True(config.EnableAlerts);
        Assert.True(config.AutoReconnect);
    }
}
