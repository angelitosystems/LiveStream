using System;
using System.Collections.Generic;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Entities;

namespace LiveStream.Domain.Tts;

/// <summary>Voz sintetizada disponible en el sistema (local o remota).</summary>
public sealed record TtsVoice(
    string Id,
    string Name,
    string Culture,
    string Gender,
    string ProviderId,
    bool IsDefault = false);

/// <summary>Mensaje individual encolado para síntesis y reproducción de voz.</summary>
public sealed record TtsUtterance(
    string Id,
    string Text,
    string? VoiceId = null,
    int Rate = 0,        // -10 a +10
    int Volume = 100,    // 0 a 100
    int Pitch = 0,
    AlertPriority Priority = AlertPriority.Normal,
    StreamPlatform? Platform = null,
    string? Author = null,
    string? AuthorId = null,
    DateTime Timestamp = default,
    bool IsManual = false);

/// <summary>Modo de acción y temporización al interactuar con comentarios o alertas en directo.</summary>
public enum ChatClickAction
{
    ReadOnly = 0,         // Solo leer en voz alta con TTS
    ShowOnly = 1,         // Solo mostrar alerta en pantalla
    ReadAndShow = 2,      // Mostrar en pantalla y leer simultáneamente
    ShowAfterRead = 3,    // Mostrar en pantalla solo después de que se leyó por voz
    ReadAfterShow = 4     // Leer por voz solo después de que se mostró en pantalla
}

/// <summary>Ajustes generales del subsistema de texto a voz (TTS).</summary>
public sealed record TtsSettings
{
    public bool Enabled { get; init; } = true;
    public string ProviderId { get; init; } = "windows_sapi";
    public string? DefaultVoiceId { get; init; }
    public int Rate { get; init; } = 0;       // -10 (lento) a +10 (rápido)
    public int Volume { get; init; } = 100;   // 0 a 100
    public int Pitch { get; init; } = 0;
    public int MaxLength { get; init; } = 160;
    public bool FilterUrls { get; init; } = true;
    public bool ReduceRepeatedChars { get; init; } = true;
    public bool ModerationMode { get; init; } = false;
    public bool Muted { get; init; } = false;
    public int UserCooldownSeconds { get; init; } = 8;
    public ChatClickAction ClickAction { get; init; } = ChatClickAction.ReadAndShow;
    public List<string> BannedWords { get; init; } = [];
    public List<string> BlockedUsers { get; init; } = [];
}

/// <summary>Regla de lectura automática para una plataforma específica.</summary>
public sealed record TtsPlatformRule(
    StreamPlatform Platform,
    bool Enabled = true,
    bool SubscribersOnly = false,
    bool ModeratorsOnly = false,
    bool HighlightedOnly = false,
    string? CustomVoiceId = null,
    int MaxLength = 150);
