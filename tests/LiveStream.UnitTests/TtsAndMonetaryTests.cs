using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Application.Alerts;
using LiveStream.Application.Chat;
using LiveStream.Application.Tts;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Tts;
using Xunit;

namespace LiveStream.UnitTests;

public sealed class TtsAndMonetaryTests
{
    private sealed class MockTtsProvider : ITtsProvider
    {
        public string ProviderId => "mock_test";
        public string DisplayName => "Mock Test Voice Provider";
        public bool IsAvailable => true;
        public string? StatusMessage => "Ready";

        public List<TtsUtterance> SpokenUtterances { get; } = [];

        public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TtsVoice>>([
                new TtsVoice("voice-1", "Voz Española Elena", "es-ES", "Female", "mock_test", true),
                new TtsVoice("voice-2", "Voz Española Carlos", "es-ES", "Male", "mock_test", false)
            ]);

        public Task SpeakAsync(TtsUtterance utterance, Action<TtsPlaybackStatus>? statusCallback, CancellationToken ct)
        {
            SpokenUtterances.Add(utterance);
            statusCallback?.Invoke(TtsPlaybackStatus.Playing);
            statusCallback?.Invoke(TtsPlaybackStatus.Completed);
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public void TtsSanitizer_StripsHtmlTagsAndDangerousScripts()
    {
        var input = "<script>alert('xss')</script><b>Hola</b> stream!";
        var result = TtsSanitizer.Sanitize(input, maxLength: 200);

        Assert.DoesNotContain("<script>", result);
        Assert.DoesNotContain("<b>", result);
        Assert.Contains("Hola stream!", result);
    }

    [Fact]
    public void TtsSanitizer_ShortensUrlsToFriendlySpeech()
    {
        var input = "Visita https://twitch.tv/streamer y http://youtube.com/watch?v=123 para más info";
        var result = TtsSanitizer.Sanitize(input, maxLength: 200);

        Assert.DoesNotContain("https://", result);
        Assert.DoesNotContain("http://", result);
        Assert.Contains("enlace a twitch.tv", result);
        Assert.Contains("enlace a youtube.com", result);
    }

    [Fact]
    public void TtsSanitizer_CollapsesExcessiveRepetitionSpam()
    {
        var input = "Hoooolaaaaaaa a tooodosssssssss!!!!!!!";
        var result = TtsSanitizer.Sanitize(input, maxLength: 200);

        // Repeticiones se contraen a máximo 2 caracteres consecutivos
        Assert.DoesNotContain("aaaa", result);
        Assert.DoesNotContain("ssss", result);
        Assert.DoesNotContain("!!!!", result);
    }

    [Fact]
    public void TtsSanitizer_ClampsExcessiveLength()
    {
        var longText = "Este es un comentario sumamente extenso diseñado específicamente para comprobar el truncamiento seguro de mensajes en el sistema de síntesis de voz en directo.";
        var result = TtsSanitizer.Sanitize(longText, maxLength: 40);

        Assert.True(result.Length <= 42); // 40 + '…'
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void TtsSanitizer_FiltersBlacklistedProfanity()
    {
        var input = "eres un tonto y un idiota";
        var result = TtsSanitizer.Sanitize(input, maxLength: 200, bannedWords: ["idiota", "tonto"]);

        Assert.DoesNotContain("idiota", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("***", result);
    }

    [Fact]
    public void TtsSanitizer_StripsDangerousPrefixCommands()
    {
        var input = "!drop /ban @everyone mira esto";
        var result = TtsSanitizer.Sanitize(input, maxLength: 200);

        Assert.False(result.StartsWith("!"));
        Assert.False(result.StartsWith("/"));
        Assert.False(result.StartsWith("@"));
    }

    [Fact]
    public async Task TtsQueueService_PrioritizesMonetaryAlertsOverNormalChat()
    {
        var mock = new MockTtsProvider();
        using var queue = new TtsQueueService(mock);
        await queue.TogglePauseAsync(); // Pausamos para examinar la cola en reposo

        var normalChat = new TtsUtterance("chat-1", "Comentario normal", Priority: AlertPriority.Low);
        var monetaryAlert = new TtsUtterance("alert-1", "¡Super Chat de 50 USD!", Priority: AlertPriority.High);

        queue.Enqueue(normalChat);
        queue.Enqueue(monetaryAlert);

        // El elemento de mayor prioridad debe posicionarse al inicio de la cola pendiente
        Assert.Equal(2, queue.PendingQueue.Count);
        Assert.Equal("alert-1", queue.PendingQueue[0].Id);
        Assert.Equal("chat-1", queue.PendingQueue[1].Id);
    }

    [Fact]
    public async Task TtsQueueService_DeduplicatesUtterancesById()
    {
        var mock = new MockTtsProvider();
        using var queue = new TtsQueueService(mock);
        await queue.TogglePauseAsync();

        var utt1 = new TtsUtterance("dup-1", "Mensaje único", Author: "UserA");
        var utt2 = new TtsUtterance("dup-1", "Mensaje repetido", Author: "UserA");

        var added1 = queue.Enqueue(utt1);
        var added2 = queue.Enqueue(utt2);

        Assert.True(added1);
        Assert.False(added2);
        Assert.Single(queue.PendingQueue);
    }

    [Fact]
    public async Task TtsQueueService_RateLimitsRapidUserMessages()
    {
        var mock = new MockTtsProvider();
        using var queue = new TtsQueueService(mock);
        await queue.TogglePauseAsync();

        var utt1 = new TtsUtterance("msg-1", "Primer comentario", Author: "Spammer", AuthorId: "spammer-id");
        var utt2 = new TtsUtterance("msg-2", "Segundo comentario instantáneo", Author: "Spammer", AuthorId: "spammer-id");

        var added1 = queue.Enqueue(utt1);
        var added2 = queue.Enqueue(utt2);

        Assert.True(added1);
        Assert.False(added2); // Rechazado por cooldown
    }

    [Fact]
    public void TtsQueueService_MuteAndPauseControlsWork()
    {
        var mock = new MockTtsProvider();
        using var queue = new TtsQueueService(mock);

        queue.SetMuted(true);
        Assert.True(queue.IsMuted);

        queue.SetMuted(false);
        Assert.False(queue.IsMuted);
    }

    [Fact]
    public async Task MonetaryAlertService_EvaluatesRuleThresholdAndDispatchesSpeech()
    {
        var mock = new MockTtsProvider();
        using var queue = new TtsQueueService(mock);
        await queue.TogglePauseAsync(); // Pausar cola para inspeccionar

        var alertQueue = new AlertQueue();
        var pointsService = new ChannelPointsService();
        var monetaryService = new MonetaryAlertService(queue, alertQueue, pointsService);
        monetaryService.ClearRules();

        var rule = new MonetaryAlertRule(
            Id: "rule-superchat-usd",
            Name: "Super Chat USD",
            Platform: StreamPlatform.YouTube,
            Kind: AlertKind.SuperChat,
            MinAmount: 10m,
            Currency: "USD",
            SpeechTemplate: "¡{username} envió {amount} {currency} diciendo {message}!"
        );
        monetaryService.AddRule(rule);

        // Evento que cumple el umbral ($25 >= $10)
        var highEvent = new AlertEvent(
            Id: "evt-sc-1",
            Platform: StreamPlatform.YouTube,
            Kind: AlertKind.SuperChat,
            Username: "PedroStream",
            Timestamp: DateTime.UtcNow,
            Amount: 25.00m,
            Currency: "USD",
            Message: "Gran partida!"
        );

        var processed = monetaryService.ProcessIncomingAlert(highEvent);
        Assert.True(processed);

        // Debe haber encolado una locución TTS con la plantilla formateada
        Assert.NotEmpty(queue.PendingQueue);
        var utterance = queue.PendingQueue.First();
        Assert.Contains("PedroStream envió 25 USD diciendo Gran partida!", utterance.Text);
        Assert.Equal(AlertPriority.High, utterance.Priority);
    }

    [Fact]
    public async Task MonetaryAlertService_IgnoresEventsBelowThreshold()
    {
        var mock = new MockTtsProvider();
        using var queue = new TtsQueueService(mock);
        await queue.TogglePauseAsync();

        var alertQueue = new AlertQueue();
        var pointsService = new ChannelPointsService();
        var monetaryService = new MonetaryAlertService(queue, alertQueue, pointsService);
        monetaryService.ClearRules();

        var rule = new MonetaryAlertRule(
            Id: "rule-donations",
            Name: "Donaciones de $5 o más",
            Platform: StreamPlatform.Twitch,
            Kind: AlertKind.Donation,
            MinAmount: 5m,
            Currency: "USD"
        );
        monetaryService.AddRule(rule);

        // Evento que NO cumple el umbral ($2 < $5)
        var lowEvent = new AlertEvent(
            Id: "evt-don-low",
            Platform: StreamPlatform.Twitch,
            Kind: AlertKind.Donation,
            Username: "MiniDonador",
            Timestamp: DateTime.UtcNow,
            Amount: 2.00m,
            Currency: "USD"
        );

        monetaryService.ProcessIncomingAlert(lowEvent);

        Assert.Empty(queue.PendingQueue);
    }

    [Fact]
    public void ChannelPointsService_AwardsPointsForDonationsAndDeduplicates()
    {
        var points = new ChannelPointsService();
        points.Settings = new VirtualPointsSettings
        {
            Enabled = true,
            PointName = "Gemas",
            PointsPerCurrencyUnit = 100m, // $10 = 1000 gemas
            PointsPerSubscription = 500,
            PointsPerGiftUnit = 50
        };

        var donEvent = new AlertEvent(
            Id: "evt-points-1",
            Platform: StreamPlatform.Twitch,
            Kind: AlertKind.Donation,
            Username: "GamerPro",
            Timestamp: DateTime.UtcNow,
            Amount: 10.00m,
            Currency: "USD"
        );

        var awarded1 = points.ProcessAlertEvent(donEvent);
        Assert.Equal(1000, awarded1);
        Assert.Equal(1000, points.GetUserBalance("GamerPro"));

        // Deduplicación por Id de evento
        var awarded2 = points.ProcessAlertEvent(donEvent);
        Assert.Equal(0, awarded2);
        Assert.Equal(1000, points.GetUserBalance("GamerPro"));

        // Evento de suscripción
        var subEvent = new AlertEvent(
            Id: "evt-sub-1",
            Platform: StreamPlatform.Twitch,
            Kind: AlertKind.Subscription,
            Username: "GamerPro",
            Timestamp: DateTime.UtcNow
        );
        var awardedSub = points.ProcessAlertEvent(subEvent);
        Assert.Equal(500, awardedSub);
        Assert.Equal(1500, points.GetUserBalance("GamerPro"));

        // Registro de historial
        Assert.Equal(2, points.History.Count);
        Assert.Contains(points.History, h => h.Reason.Contains("Donación"));
        Assert.Contains(points.History, h => h.Reason.Contains("Suscripción"));
    }

    [Fact]
    public async Task ChatTtsCoordinator_AppliesPlatformRulesAndSubscribersOnlyFilter()
    {
        var mock = new MockTtsProvider();
        using var queue = new TtsQueueService(mock);
        await queue.TogglePauseAsync();

        var chat = new UnifiedChatService();
        var coordinator = new ChatTtsCoordinator(chat, queue);

        // Regla Twitch: solo suscriptores
        coordinator.SetPlatformRule(new TtsPlatformRule(
            Platform: StreamPlatform.Twitch,
            Enabled: true,
            SubscribersOnly: true
        ));

        // Regla YouTube: desactivado
        coordinator.SetPlatformRule(new TtsPlatformRule(
            Platform: StreamPlatform.YouTube,
            Enabled: false
        ));

        // 1. Mensaje de Twitch de usuario NO suscriptor -> Ignorado
        var twNonSub = new ChatMessage(
            Id: "msg-tw-1",
            Platform: StreamPlatform.Twitch,
            AuthorName: "ViewerComun",
            AuthorId: "user-tw-1",
            Message: "Hola Twitch",
            Timestamp: DateTime.UtcNow,
            IsSubscriber: false
        );
        chat.AddMessage(twNonSub);
        Assert.Empty(queue.PendingQueue);

        // 2. Mensaje de Twitch de usuario suscriptor -> Aceptado
        var twSub = new ChatMessage(
            Id: "msg-tw-2",
            Platform: StreamPlatform.Twitch,
            AuthorName: "ViewerSub",
            AuthorId: "user-tw-2",
            Message: "Hola con sub!",
            Timestamp: DateTime.UtcNow,
            IsSubscriber: true
        );
        chat.AddMessage(twSub);
        Assert.Single(queue.PendingQueue);
        queue.ClearQueue();

        // 3. Mensaje de YouTube con plataforma desactivada -> Ignorado
        var ytMsg = new ChatMessage(
            Id: "msg-yt-1",
            Platform: StreamPlatform.YouTube,
            AuthorName: "YtUser",
            AuthorId: "user-yt-1",
            Message: "Hola YouTube",
            Timestamp: DateTime.UtcNow
        );
        chat.AddMessage(ytMsg);
        Assert.Empty(queue.PendingQueue);
    }

    [Fact]
    public async Task ChatTtsCoordinator_ManualSpeakingWorksEvenWhenAutoReadIsOff()
    {
        var mock = new MockTtsProvider();
        using var queue = new TtsQueueService(mock);
        await queue.TogglePauseAsync();

        var chat = new UnifiedChatService();
        var coordinator = new ChatTtsCoordinator(chat, queue)
        {
            AutoReadEnabled = false // Auto-lectura desactivada
        };

        var msg = new ChatMessage(
            Id: "manual-msg",
            Platform: StreamPlatform.YouTube,
            AuthorName: "FanDestacado",
            AuthorId: "user-fan",
            Message: "¡Léeme este comentario!",
            Timestamp: DateTime.UtcNow
        );

        // El procesamiento automático no hace nada
        chat.AddMessage(msg);
        Assert.Empty(queue.PendingQueue);

        // Pero la acción manual "Leer ahora" sí encola y marca IsManual = true
        coordinator.SpeakMessageManually(msg);
        Assert.Single(queue.PendingQueue);
        Assert.True(queue.PendingQueue[0].IsManual);
        Assert.Contains("¡Léeme este comentario!", queue.PendingQueue[0].Text);
    }

    [Fact]
    public void ChatTtsCoordinator_BlockedUserIsIgnored()
    {
        var mock = new MockTtsProvider();
        using var queue = new TtsQueueService(mock);
        var chat = new UnifiedChatService();
        var coordinator = new ChatTtsCoordinator(chat, queue);

        coordinator.BlockUser("toxic-user-id");
        Assert.True(coordinator.IsUserBlocked("toxic-user-id"));

        var msg = new ChatMessage(
            Id: "msg-toxic",
            Platform: StreamPlatform.YouTube,
            AuthorName: "ToxicViewer",
            AuthorId: "toxic-user-id",
            Message: "Spam molesto",
            Timestamp: DateTime.UtcNow
        );

        chat.AddMessage(msg);
        Assert.Empty(queue.PendingQueue);
    }

    [Fact]
    public void SoundPlayerService_GlobalMute_PreventsPlayingSounds()
    {
        SoundPlayerService.IsMuted = true;
        Assert.True(SoundPlayerService.IsMuted);

        // Intentar reproducir sonido no lanza excepciones y se descarta
        SoundPlayerService.Play("level_up.wav");

        SoundPlayerService.IsMuted = false;
        Assert.False(SoundPlayerService.IsMuted);
    }

    [Fact]
    public void AlertQueue_IsSoundMuted_ReflectsGlobalState()
    {
        using var alertQueue = new AlertQueue();

        alertQueue.IsSoundMuted = true;
        Assert.True(SoundPlayerService.IsMuted);
        Assert.True(alertQueue.IsSoundMuted);

        alertQueue.IsSoundMuted = false;
        Assert.False(SoundPlayerService.IsMuted);
        Assert.False(alertQueue.IsSoundMuted);
    }

    [Fact]
    public async Task ChatTtsCoordinator_PerPlatformVoice_AppliesToPlatformMessages()
    {
        var mock = new MockTtsProvider();
        using var queue = new TtsQueueService(mock);
        await queue.TogglePauseAsync();

        var chat = new UnifiedChatService();
        var coordinator = new ChatTtsCoordinator(chat, queue);

        coordinator.SetPlatformRule(new TtsPlatformRule(
            Platform: StreamPlatform.TikTok,
            Enabled: true,
            CustomVoiceId: "voice-tiktok-special"
        ));

        var msg = new ChatMessage(
            Id: "msg-tt-voice",
            Platform: StreamPlatform.TikTok,
            AuthorName: "TiktokStar",
            AuthorId: "tt-user-1",
            Message: "Probando voz de TikTok",
            Timestamp: DateTime.UtcNow
        );

        chat.AddMessage(msg);

        Assert.Single(queue.PendingQueue);
        Assert.Equal("voice-tiktok-special", queue.PendingQueue[0].VoiceId);
    }

    [Fact]
    public async Task TtsQueueService_UtteranceCompleted_FiresWhenPlaybackFinishes()
    {
        var mock = new MockTtsProvider();
        using var queue = new TtsQueueService(mock);

        var completedTcs = new TaskCompletionSource<TtsUtterance>();
        queue.UtteranceCompleted += utt => completedTcs.TrySetResult(utt);

        var msg = new TtsUtterance("utt-complete-test", "Hola a todos");
        queue.Enqueue(msg);

        var completed = await Task.WhenAny(completedTcs.Task, Task.Delay(2000));
        Assert.Same(completedTcs.Task, completed);
        Assert.Equal("utt-complete-test", (await completedTcs.Task).Id);
    }
}
