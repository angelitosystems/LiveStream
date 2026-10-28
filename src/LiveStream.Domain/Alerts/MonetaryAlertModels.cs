using System;
using System.Collections.Generic;
using LiveStream.Domain.Entities;

namespace LiveStream.Domain.Alerts;

/// <summary>Regla para activar alertas y voz TTS en eventos monetarios o regalos.</summary>
public sealed record MonetaryAlertRule(
    string Id,
    string Name,
    StreamPlatform Platform,
    AlertKind Kind,
    decimal MinAmount = 0,
    string Currency = "USD",
    string SpeechTemplate = "¡Gracias {username} por tu apoyo de {amount} {currency}!",
    string? SoundPath = null,
    string? AnimationTemplate = "gold",
    int DurationMs = 6000,
    AlertPriority Priority = AlertPriority.High,
    bool Enabled = true,
    string? CustomVoiceId = null,
    int? MinGiftUnits = null);

/// <summary>Configuración de recompensas y puntos virtuales del canal (no monetarios).</summary>
public sealed record VirtualPointsSettings
{
    public bool Enabled { get; init; } = true;
    public string PointName { get; init; } = "Puntos de Canal";
    public decimal PointsPerCurrencyUnit { get; init; } = 100m; // 1 USD/EUR = 100 puntos
    public int PointsPerSubscription { get; init; } = 500;
    public int PointsPerGiftUnit { get; init; } = 50;
    public int PointsPerChatParticipation { get; init; } = 5;
}

/// <summary>Registro auditable de puntos virtuales otorgados.</summary>
public sealed record VirtualPointsEntry(
    string Id,
    string UserId,
    string Username,
    StreamPlatform Platform,
    long PointsAwarded,
    string Reason,
    DateTime Timestamp,
    string? ReferenceEventId = null);
