using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Tts;

namespace LiveStream.Application.Tts;

/// <summary>
/// Administrador central de la cola de reproducción de voz (TTS).
/// Coordina prioridades (alertas monetarias > manuales > chat automático),
/// deduplicación, límites por usuario y ciclo de vida de reproducción.
/// </summary>
public sealed class TtsQueueService : IDisposable
{
    private readonly ITtsProvider _provider;
    private readonly object _lock = new();
    private readonly List<TtsUtterance> _queue = [];
    private readonly HashSet<string> _seenIds = [];
    private readonly ConcurrentDictionary<string, DateTime> _userLastSpoke = new();
    private readonly CancellationTokenSource _disposeCts = new();

    private TtsUtterance? _current;
    private TtsUtterance? _lastPlayed;
    private CancellationTokenSource? _currentPlaybackCts;
    private Task? _playbackWorker;
    private bool _isPaused;
    private bool _isMuted;
    private bool _disposed;

    public event Action<TtsUtterance?>? CurrentUtteranceChanged;
    public event Action? QueueChanged;
    public event Action<TtsPlaybackStatus>? StatusChanged;
    public event Action<TtsUtterance>? UtteranceCompleted;

    public TtsSettings Settings { get; set; } = new();

    public TtsUtterance? CurrentUtterance
    {
        get { lock (_lock) return _current; }
    }

    public IReadOnlyList<TtsUtterance> PendingQueue
    {
        get { lock (_lock) return _queue.ToList(); }
    }

    public bool IsPaused => _isPaused;
    public bool IsMuted => _isMuted;
    public bool IsPlaying => _current is not null && !_isPaused;

    public TtsQueueService(ITtsProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _playbackWorker = Task.Run(PlaybackLoopAsync);
    }

    /// <summary>Encola un mensaje para lectura, respetando prioridad y deduplicación.</summary>
    public bool Enqueue(TtsUtterance utterance)
    {
        if (!Settings.Enabled && !utterance.IsManual)
            return false;

        lock (_lock)
        {
            if (_disposed) return false;

            // 1. Deduplicación por ID de mensaje o alerta
            if (!string.IsNullOrEmpty(utterance.Id) && !_seenIds.Add(utterance.Id) && !utterance.IsManual)
            {
                AppLog.Info("tts", $"Mensaje duplicado omitido: {utterance.Id}");
                return false;
            }

            // 2. Límites de frecuencia por usuario (anti-spam) para mensajes automáticos
            if (!utterance.IsManual && utterance.AuthorId is { } authorId && utterance.Priority <= AlertPriority.Normal)
            {
                if (_userLastSpoke.TryGetValue(authorId, out var lastTime))
                {
                    if (DateTime.UtcNow - lastTime < TimeSpan.FromSeconds(Settings.UserCooldownSeconds))
                    {
                        AppLog.Info("tts", $"Comentario omitido por cooldown del usuario {utterance.Author}");
                        return false;
                    }
                }
            }

            // 3. Sanitizar texto
            var sanitizedText = TtsSanitizer.Sanitize(
                utterance.Text,
                Settings.MaxLength,
                Settings.FilterUrls,
                Settings.ReduceRepeatedChars,
                Settings.BannedWords);

            if (string.IsNullOrWhiteSpace(sanitizedText))
                return false;

            var prepared = utterance with { Text = sanitizedText };

            // 4. Inserción según nivel de prioridad
            if (prepared.AuthorId is { } aId && !prepared.IsManual)
            {
                _userLastSpoke[aId] = DateTime.UtcNow;
            }

            if (prepared.Priority >= AlertPriority.High)
            {
                // Alertas monetarias de alta prioridad van al inicio
                var insertIdx = _queue.FindLastIndex(u => u.Priority >= AlertPriority.High) + 1;
                _queue.Insert(insertIdx, prepared);

                // Si se está leyendo un comentario normal, interrumpirlo para dar paso a la donación
                if (_current is not null && _current.Priority < AlertPriority.High)
                {
                    _currentPlaybackCts?.Cancel();
                }
            }
            else if (prepared.IsManual || prepared.Priority == AlertPriority.Normal)
            {
                var insertIdx = _queue.FindLastIndex(u => u.Priority >= prepared.Priority) + 1;
                _queue.Insert(insertIdx, prepared);
            }
            else
            {
                _queue.Add(prepared);
            }

            Monitor.PulseAll(_lock);
        }

        QueueChanged?.Invoke();
        return true;
    }

    /// <summary>Detiene la reproducción actual y pasa al siguiente mensaje en cola.</summary>
    public void SkipCurrent()
    {
        lock (_lock)
        {
            _currentPlaybackCts?.Cancel();
        }
    }

    /// <summary>Vacía todos los mensajes pendientes de la cola.</summary>
    public void ClearQueue()
    {
        lock (_lock)
        {
            _queue.Clear();
        }
        QueueChanged?.Invoke();
    }

    /// <summary>Elimina un mensaje específico de la cola pendiente.</summary>
    public bool RemoveFromQueue(string utteranceId)
    {
        var removed = false;
        lock (_lock)
        {
            removed = _queue.RemoveAll(u => u.Id == utteranceId) > 0;
        }
        if (removed)
            QueueChanged?.Invoke();
        return removed;
    }

    /// <summary>Pausa o reanuda la reproducción actual.</summary>
    public async Task TogglePauseAsync()
    {
        if (_isPaused)
            await ResumeAsync();
        else
            await PauseAsync();
    }

    public async Task PauseAsync()
    {
        _isPaused = true;
        await _provider.PauseAsync();
        StatusChanged?.Invoke(TtsPlaybackStatus.Paused);
    }

    public async Task ResumeAsync()
    {
        _isPaused = false;
        await _provider.ResumeAsync();
        StatusChanged?.Invoke(TtsPlaybackStatus.Playing);
    }

    /// <summary>Silencia o reactiva la voz sin perder la cola.</summary>
    public void SetMuted(bool muted)
    {
        _isMuted = muted;
        if (muted)
            _currentPlaybackCts?.Cancel();
    }

    /// <summary>Repite la última alocución leída.</summary>
    public void RepeatLast()
    {
        lock (_lock)
        {
            if (_lastPlayed is not null)
            {
                var copy = _lastPlayed with { Id = Guid.NewGuid().ToString(), IsManual = true };
                _queue.Insert(0, copy);
                Monitor.PulseAll(_lock);
            }
        }
        QueueChanged?.Invoke();
    }

    private async Task PlaybackLoopAsync()
    {
        while (!_disposeCts.IsCancellationRequested)
        {
            TtsUtterance? next = null;

            lock (_lock)
            {
                while (_queue.Count == 0 || _isPaused || _isMuted)
                {
                    if (_disposeCts.IsCancellationRequested)
                        return;

                    Monitor.Wait(_lock, 250);
                }

                next = _queue[0];
                _queue.RemoveAt(0);
                _current = next;
            }

            QueueChanged?.Invoke();
            CurrentUtteranceChanged?.Invoke(_current);

            if (next is not null)
            {
                if (next.AuthorId is { } aId)
                    _userLastSpoke[aId] = DateTime.UtcNow;

                _currentPlaybackCts = new CancellationTokenSource();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_currentPlaybackCts.Token, _disposeCts.Token);

                try
                {
                    // Calibrar voz predeterminada si no tiene una asignada
                    var effectiveVoice = string.IsNullOrWhiteSpace(next.VoiceId) ? Settings.DefaultVoiceId : next.VoiceId;
                    var effectiveUtterance = next with
                    {
                        VoiceId = effectiveVoice,
                        Rate = Settings.Rate,
                        Volume = Settings.Volume
                    };

                    await _provider.SpeakAsync(effectiveUtterance, status => StatusChanged?.Invoke(status), linkedCts.Token).ConfigureAwait(false);
                    _lastPlayed = next;
                    UtteranceCompleted?.Invoke(next);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppLog.Error("tts", $"Fallo en lectura de mensaje: {next.Text}", ex);
                }
                finally
                {
                    _currentPlaybackCts.Dispose();
                    _currentPlaybackCts = null;
                }
            }

            lock (_lock)
            {
                _current = null;
            }

            CurrentUtteranceChanged?.Invoke(null);
            StatusChanged?.Invoke(TtsPlaybackStatus.Idle);

            // Breve respiro acústico entre mensajes (150 ms)
            try
            {
                await Task.Delay(150, _disposeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _disposeCts.Cancel();
            _currentPlaybackCts?.Cancel();
            Monitor.PulseAll(_lock);
        }

        try
        {
            _playbackWorker?.Wait(500);
        }
        catch { }

        _disposeCts.Dispose();
        _provider.Dispose();
    }
}
