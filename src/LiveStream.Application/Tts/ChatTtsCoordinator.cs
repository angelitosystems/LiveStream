using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LiveStream.Application.Chat;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Tts;

namespace LiveStream.Application.Tts;

/// <summary>
/// Coordinador entre el chat unificado y la cola de síntesis de voz (TTS).
/// Evalúa reglas por plataforma, ejecuta lecturas manuales y gestiona exclusiones.
/// </summary>
public sealed class ChatTtsCoordinator
{
    private readonly UnifiedChatService _chat;
    private readonly TtsQueueService _tts;
    private readonly ConcurrentDictionary<StreamPlatform, TtsPlatformRule> _platformRules = new();
    private readonly HashSet<string> _blockedUserIds = [];
    private readonly object _lock = new();

    public TtsQueueService Queue => _tts;

    public bool AutoReadEnabled { get; set; } = true;

    public IReadOnlyDictionary<StreamPlatform, TtsPlatformRule> PlatformRules => _platformRules;

    public ChatTtsCoordinator(UnifiedChatService chat, TtsQueueService tts)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _tts = tts ?? throw new ArgumentNullException(nameof(tts));

        InitializeDefaultPlatformRules();
        _chat.MessageAdded += OnChatMessageAdded;
    }

    public void SetPlatformRule(TtsPlatformRule rule)
    {
        _platformRules[rule.Platform] = rule;
    }

    public TtsPlatformRule GetRuleFor(StreamPlatform platform) =>
        _platformRules.TryGetValue(platform, out var rule)
            ? rule
            : new TtsPlatformRule(platform);

    /// <summary>Lectura manual forzada de un comentario específico seleccionado por el creador en el chat.</summary>
    public bool SpeakMessageManually(ChatMessage message)
    {
        var utterance = new TtsUtterance(
            Id: message.Id,
            Text: $"{message.AuthorName} dice: {message.Message}",
            VoiceId: GetRuleFor(message.Platform).CustomVoiceId,
            Priority: AlertPriority.Normal,
            Platform: message.Platform,
            Author: message.AuthorName,
            AuthorId: message.AuthorId,
            Timestamp: message.Timestamp,
            IsManual: true);

        return _tts.Enqueue(utterance);
    }

    /// <summary>Bloquea temporalmente a un usuario para que sus mensajes no sean leídos por voz.</summary>
    public void BlockUser(string authorId)
    {
        if (string.IsNullOrWhiteSpace(authorId)) return;
        lock (_lock)
        {
            _blockedUserIds.Add(authorId);
        }
        AppLog.Info("tts", $"Usuario bloqueado para TTS: {authorId}");
    }

    public bool IsUserBlocked(string authorId)
    {
        if (string.IsNullOrWhiteSpace(authorId)) return false;
        lock (_lock)
        {
            return _blockedUserIds.Contains(authorId);
        }
    }

    private void OnChatMessageAdded(object? sender, ChatMessage message)
    {
        if (!AutoReadEnabled || !_tts.Settings.Enabled)
            return;

        // Comprobar bloqueo de usuario
        if (!string.IsNullOrWhiteSpace(message.AuthorId) && IsUserBlocked(message.AuthorId))
            return;

        // Comprobar regla específica de la plataforma
        var rule = GetRuleFor(message.Platform);
        if (!rule.Enabled)
            return;

        if (rule.SubscribersOnly && !message.IsSubscriber)
            return;

        if (rule.ModeratorsOnly && !message.IsModerator)
            return;

        if (rule.HighlightedOnly && !message.IsHighlighted && !message.IsDonation)
            return;

        // Encolar mensaje para lectura automática
        var speechText = $"{message.AuthorName}: {message.Message}";
        var utterance = new TtsUtterance(
            Id: message.Id,
            Text: speechText,
            VoiceId: rule.CustomVoiceId,
            Priority: AlertPriority.Normal,
            Platform: message.Platform,
            Author: message.AuthorName,
            AuthorId: message.AuthorId,
            Timestamp: message.Timestamp,
            IsManual: false);

        _tts.Enqueue(utterance);
    }

    private void InitializeDefaultPlatformRules()
    {
        _platformRules[StreamPlatform.YouTube] = new TtsPlatformRule(StreamPlatform.YouTube, Enabled: true, MaxLength: 160);
        _platformRules[StreamPlatform.TikTok] = new TtsPlatformRule(StreamPlatform.TikTok, Enabled: true, MaxLength: 140);
        _platformRules[StreamPlatform.Twitch] = new TtsPlatformRule(StreamPlatform.Twitch, Enabled: true, MaxLength: 160);
        _platformRules[StreamPlatform.Facebook] = new TtsPlatformRule(StreamPlatform.Facebook, Enabled: true, MaxLength: 160);
        _platformRules[StreamPlatform.Instagram] = new TtsPlatformRule(StreamPlatform.Instagram, Enabled: true, MaxLength: 140);
    }
}
