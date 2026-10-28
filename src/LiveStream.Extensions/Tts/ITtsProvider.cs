using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Domain.Tts;

namespace LiveStream.Extensions.Tts;

public enum TtsPlaybackStatus
{
    Idle,
    Playing,
    Paused,
    Completed,
    Error,
    Canceled
}

/// <summary>
/// Contrato común para proveedores de síntesis de voz (locales de Windows o API externa).
/// Permite alternar proveedores en caliente sin reiniciar la aplicación ni modificar el chat.
/// </summary>
public interface ITtsProvider : IDisposable
{
    string ProviderId { get; }
    string DisplayName { get; }
    bool IsAvailable { get; }
    string? StatusMessage { get; }

    Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct);
    Task SpeakAsync(TtsUtterance utterance, Action<TtsPlaybackStatus>? statusCallback, CancellationToken ct);
    Task StopAsync();
    Task PauseAsync();
    Task ResumeAsync();
}
