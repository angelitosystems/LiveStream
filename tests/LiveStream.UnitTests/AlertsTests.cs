using System;
using System.Collections.Generic;
using LiveStream.Application.Alerts;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Entities;
using Xunit;

namespace LiveStream.UnitTests;

public sealed class AlertsTests
{
    [Fact]
    public void AlertProcessor_FormatTemplate_ReplacesVariablesCorrectly()
    {
        var evt = new AlertEvent(
            Id: "evt-123",
            Platform: StreamPlatform.YouTube,
            Kind: AlertKind.SuperChat,
            Username: "Gamer99",
            Timestamp: DateTime.UtcNow,
            Amount: 50.00m,
            Currency: "EUR",
            Message: "Gran stream, crack!"
        );

        var template = "¡{username} ha enviado un Super Chat de {amount} {currency}! Mensaje: {message}";
        var result = AlertProcessor.FormatTemplate(template, evt);

        Assert.Equal("¡Gamer99 ha enviado un Super Chat de 50.00 EUR! Mensaje: Gran stream, crack!", result);
    }

    [Fact]
    public void AlertProcessor_FormatTemplate_FallbackForMissingValues()
    {
        var evt = new AlertEvent(
            Id: "evt-456",
            Platform: StreamPlatform.Twitch,
            Kind: AlertKind.Follower,
            Username: "",
            Timestamp: DateTime.UtcNow
        );

        var template = "¡Bienvenido {username} de {platform}!";
        var result = AlertProcessor.FormatTemplate(template, evt);

        Assert.Equal("¡Bienvenido Espectador de Twitch!", result);
    }

    [Fact]
    public void AlertProcessor_FindMatchingRule_RespectsThresholds()
    {
        var ruleLow = AlertRule.CreateDefault(AlertKind.Donation, StreamPlatform.Twitch) with
        {
            Template = "Donación modesta de {username}: {amount}",
            MinAmount = 5.0m
        };

        var ruleHigh = AlertRule.CreateDefault(AlertKind.Donation, StreamPlatform.Twitch) with
        {
            Template = "¡DONACIÓN GIGANTE de {username}: {amount}!",
            MinAmount = 100.0m,
            Priority = AlertPriority.Urgent
        };

        var rules = new List<AlertRule> { ruleLow, ruleHigh };

        // Test donation under 5 -> matches neither
        var smallDonation = new AlertEvent("d1", StreamPlatform.Twitch, AlertKind.Donation, "Viewer1", DateTime.UtcNow, Amount: 2.0m);
        Assert.Null(AlertProcessor.FindMatchingRule(rules, smallDonation));

        // Test donation of 10 -> matches ruleLow
        var medDonation = new AlertEvent("d2", StreamPlatform.Twitch, AlertKind.Donation, "Viewer2", DateTime.UtcNow, Amount: 10.0m);
        var matchMed = AlertProcessor.FindMatchingRule(rules, medDonation);
        Assert.NotNull(matchMed);
        Assert.Equal(ruleLow.Id, matchMed.Id);

        // Test donation of 200 -> matches ruleHigh due to priority and threshold
        var bigDonation = new AlertEvent("d3", StreamPlatform.Twitch, AlertKind.Donation, "Viewer3", DateTime.UtcNow, Amount: 200.0m);
        var matchBig = AlertProcessor.FindMatchingRule(rules, bigDonation);
        Assert.NotNull(matchBig);
        Assert.Equal(ruleHigh.Id, matchBig.Id);
    }

    [Fact]
    public void AlertQueue_Deduplication_IgnoresDuplicateIds()
    {
        using var queue = new AlertQueue();
        var rule = AlertRule.CreateDefault(AlertKind.Follower, StreamPlatform.Twitch);

        var alert1 = new AlertEvent("dup-id-1", StreamPlatform.Twitch, AlertKind.Follower, "ViewerA", DateTime.UtcNow);
        var alert2 = new AlertEvent("dup-id-1", StreamPlatform.Twitch, AlertKind.Follower, "ViewerA", DateTime.UtcNow);

        var enqueuedFirst = queue.Enqueue(alert1, rule);
        var enqueuedSecond = queue.Enqueue(alert2, rule);

        Assert.True(enqueuedFirst);
        Assert.False(enqueuedSecond); // Duplicate discarded
    }

    [Fact]
    public void AlertQueue_PauseResumeAndSkip()
    {
        using var queue = new AlertQueue();
        queue.IsPaused = true;

        var rule = AlertRule.CreateDefault(AlertKind.Follower, StreamPlatform.Twitch);
        var alert1 = new AlertEvent("p1", StreamPlatform.Twitch, AlertKind.Follower, "Viewer1", DateTime.UtcNow);
        var alert2 = new AlertEvent("p2", StreamPlatform.Twitch, AlertKind.Follower, "Viewer2", DateTime.UtcNow);

        queue.Enqueue(alert1, rule);
        queue.Enqueue(alert2, rule);

        // While paused, active alert shouldn't automatically play
        Assert.Null(queue.ActiveAlert);
        Assert.Equal(2, queue.PendingCount);

        // Unpause: starts playing first
        queue.IsPaused = false;
        Assert.NotNull(queue.ActiveAlert);
        Assert.Equal("Viewer1", queue.ActiveAlert.Event.Username);
        Assert.Equal(1, queue.PendingCount);

        // Skip active: starts playing second
        queue.SkipActive();
        Assert.NotNull(queue.ActiveAlert);
        Assert.Equal("Viewer2", queue.ActiveAlert.Event.Username);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void SoundBankService_EnsuresStockSounds_AndListsCatalog()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"LiveStream_SoundTest_{Guid.NewGuid():N}");
        try
        {
            var soundBank = new SoundBankService(tempDir);
            var sounds = soundBank.List();

            Assert.NotEmpty(sounds);
            Assert.Contains(sounds, s => s.Name.Contains("Campana"));
            Assert.Contains(sounds, s => s.Name.Contains("Moneda"));
            Assert.Contains(sounds, s => s.Name.Contains("Nivel"));

            // Verify search
            var searchCoin = soundBank.List("moneda");
            Assert.Single(searchCoin);
            Assert.Equal("Retro y Arcade", searchCoin[0].Category);

            // Verify file exists on disk and is a valid WAV (RIFF header)
            var coinPath = searchCoin[0].LocalFilePath;
            Assert.True(System.IO.File.Exists(coinPath));
            var bytes = System.IO.File.ReadAllBytes(coinPath);
            Assert.True(bytes.Length > 44);
            Assert.Equal((byte)'R', bytes[0]);
            Assert.Equal((byte)'I', bytes[1]);
            Assert.Equal((byte)'F', bytes[2]);
            Assert.Equal((byte)'F', bytes[3]);
        }
        finally
        {
            if (System.IO.Directory.Exists(tempDir))
                System.IO.Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AlertRule_CreateDefault_AssignsMatchingSound()
    {
        var followerRule = AlertRule.CreateDefault(AlertKind.Follower);
        Assert.Equal("ding_notification.wav", followerRule.SoundPath);

        var subRule = AlertRule.CreateDefault(AlertKind.Subscription);
        Assert.Equal("level_up.wav", subRule.SoundPath);

        var donationRule = AlertRule.CreateDefault(AlertKind.Donation);
        Assert.Equal("arcade_coin.wav", donationRule.SoundPath);
    }
}
