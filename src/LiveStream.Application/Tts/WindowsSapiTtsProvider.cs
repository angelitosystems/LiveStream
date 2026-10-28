using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Domain.Tts;

namespace LiveStream.Application.Tts;

/// <summary>
/// Proveedor nativo de síntesis de voz en Windows mediante SpeechSynthesizer (SAPI / OneCore).
/// Funciona 100% de forma local, fuera de línea y con latencia cero utilizando las voces instaladas en el sistema operativo.
/// </summary>
public sealed class WindowsSapiTtsProvider : ITtsProvider
{
    private readonly SpeechSynthesizer _synth;
    private readonly object _lock = new();
    private TaskCompletionSource<bool>? _currentSpeechTcs;
    private bool _disposed;
    private bool _isPaused;

    public string ProviderId => "windows_sapi";
    public string DisplayName => "Voces locales de Windows (SAPI)";
    public bool IsAvailable => true;
    public string? StatusMessage => "Disponible (voces locales de Windows instaladas)";

    public WindowsSapiTtsProvider()
    {
        _synth = new SpeechSynthesizer();
        _synth.SetOutputToDefaultAudioDevice();
        _synth.SpeakCompleted += OnSpeakCompleted;
    }

    public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            var list = new List<TtsVoice>();
            try
            {
                var installed = _synth.GetInstalledVoices();
                foreach (var v in installed.Where(iv => iv.Enabled))
                {
                    var info = v.VoiceInfo;
                    var isDefault = info.Name.Contains("Helena", StringComparison.OrdinalIgnoreCase) ||
                                    info.Name.Contains("Sabina", StringComparison.OrdinalIgnoreCase) ||
                                    info.Name.Contains("David", StringComparison.OrdinalIgnoreCase);

                    list.Add(new TtsVoice(
                        Id: info.Name,
                        Name: $"{info.Name} ({info.Culture.DisplayName})",
                        Culture: info.Culture.Name,
                        Gender: info.Gender.ToString(),
                        ProviderId: ProviderId,
                        IsDefault: isDefault));
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("tts", "Error al listar voces nativas de Windows", ex);
            }

            return Task.FromResult<IReadOnlyList<TtsVoice>>(list);
        }
    }

    public async Task SpeakAsync(TtsUtterance utterance, Action<TtsPlaybackStatus>? statusCallback, CancellationToken ct)
    {
        TaskCompletionSource<bool> tcs;
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WindowsSapiTtsProvider));

            // Cancelar cualquier síntesis previa antes de iniciar la nueva
            _synth.SpeakAsyncCancelAll();

            // Calibrar volumen y velocidad
            _synth.Volume = Math.Clamp(utterance.Volume, 0, 100);
            _synth.Rate = Math.Clamp(utterance.Rate, -10, 10);

            // Seleccionar voz si fue especificada y existe
            if (!string.IsNullOrWhiteSpace(utterance.VoiceId))
            {
                try
                {
                    _synth.SelectVoice(utterance.VoiceId);
                }
                catch
                {
                    // Si no encuentra la voz específica, mantiene la predeterminada del sistema
                }
            }

            _currentSpeechTcs = tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _isPaused = false;
        }

        statusCallback?.Invoke(TtsPlaybackStatus.Playing);

        using var reg = ct.Register(() =>
        {
            lock (_lock)
            {
                _synth.SpeakAsyncCancelAll();
                _currentSpeechTcs?.TrySetCanceled(ct);
            }
        });

        try
        {
            _synth.SpeakAsync(utterance.Text);
            await tcs.Task.ConfigureAwait(false);
            statusCallback?.Invoke(TtsPlaybackStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            statusCallback?.Invoke(TtsPlaybackStatus.Canceled);
        }
        catch (Exception ex)
        {
            AppLog.Error("tts", $"Error durante la reproducción TTS: {utterance.Text}", ex);
            statusCallback?.Invoke(TtsPlaybackStatus.Error);
        }
    }

    public Task StopAsync()
    {
        lock (_lock)
        {
            _synth.SpeakAsyncCancelAll();
            _isPaused = false;
            _currentSpeechTcs?.TrySetCanceled();
        }
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        lock (_lock)
        {
            if (!_isPaused && _synth.State == SynthesizerState.Speaking)
            {
                _synth.Pause();
                _isPaused = true;
            }
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        lock (_lock)
        {
            if (_isPaused && _synth.State == SynthesizerState.Paused)
            {
                _synth.Resume();
                _isPaused = false;
            }
        }
        return Task.CompletedTask;
    }

    private void OnSpeakCompleted(object? sender, SpeakCompletedEventArgs e)
    {
        lock (_lock)
        {
            if (e.Cancelled)
                _currentSpeechTcs?.TrySetCanceled();
            else if (e.Error != null)
                _currentSpeechTcs?.TrySetException(e.Error);
            else
                _currentSpeechTcs?.TrySetResult(true);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _synth.SpeakCompleted -= OnSpeakCompleted;
            _synth.SpeakAsyncCancelAll();
            _synth.Dispose();
        }
    }
}
