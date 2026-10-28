using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Entities;

namespace LiveStream.Extensions.Alerts;

public sealed record ActiveAlertState(AlertEvent Event, AlertRule Rule, string FormattedText, DateTime StartedAt, int DurationSec);

/// <summary>
/// Cola de reproducción secuencial de alertas con prioridades, prevención de superposiciones involuntarias,
/// deduplicación por identificador de evento y modo de prueba interactivo.
/// </summary>
public sealed class AlertQueue : IDisposable
{
    private readonly object _lock = new();
    private readonly PriorityQueue<(AlertEvent Event, AlertRule Rule, string FormattedText), int> _queue = new();
    private readonly ConcurrentDictionary<string, DateTime> _dedupCache = new();
    private readonly TimeSpan _dedupWindow = TimeSpan.FromMinutes(5);
    private readonly System.Timers.Timer _playbackTimer;

    private ActiveAlertState? _activeAlert;
    private bool _isPaused;
    private int _remainingDurationMs;

    public event EventHandler<ActiveAlertState>? AlertStarted;
    public event EventHandler<ActiveAlertState>? AlertCompleted;
    public event EventHandler? QueueChanged;

    public AlertQueue()
    {
        _playbackTimer = new System.Timers.Timer { AutoReset = false };
        _playbackTimer.Elapsed += (_, _) => OnAlertDurationElapsed();
    }

    public ActiveAlertState? ActiveAlert
    {
        get
        {
            lock (_lock) return _activeAlert;
        }
    }

    public bool IsSoundMuted
    {
        get => SoundPlayerService.IsMuted;
        set => SoundPlayerService.IsMuted = value;
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            lock (_lock)
            {
                if (_isPaused == value) return;
                _isPaused = value;
                if (_isPaused)
                {
                    _playbackTimer.Stop();
                }
                else if (_activeAlert is not null)
                {
                    _playbackTimer.Interval = Math.Max(500, _remainingDurationMs);
                    _playbackTimer.Start();
                }
                else
                {
                    TryPlayNext_Locked();
                }
            }
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_lock) return _queue.Count;
        }
    }

    public bool Enqueue(AlertEvent alert, AlertRule rule)
    {
        // 1. Deduplicación por Id
        if (!string.IsNullOrWhiteSpace(alert.Id))
        {
            PruneDedupCache();
            if (!_dedupCache.TryAdd(alert.Id, DateTime.UtcNow))
                return false; // Duplicado detectado y descartado
        }

        var formattedText = AlertProcessor.FormatTemplate(rule.Template, alert);

        lock (_lock)
        {
            // La prioridad menor en PriorityQueue de .NET sale primero, por lo que invertimos:
            // Urgent(3) -> -3, High(2) -> -2, Normal(1) -> -1, Low(0) -> 0
            var priorityKey = -((int)alert.Priority + (int)rule.Priority);
            _queue.Enqueue((alert, rule, formattedText), priorityKey);

            if (_activeAlert is null && !_isPaused)
                TryPlayNext_Locked();
        }

        QueueChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SkipActive()
    {
        ActiveAlertState? finished = null;
        lock (_lock)
        {
            if (_activeAlert is null) return;
            _playbackTimer.Stop();
            finished = _activeAlert;
            _activeAlert = null;
            if (!_isPaused)
                TryPlayNext_Locked();
        }

        if (finished is not null)
            AlertCompleted?.Invoke(this, finished);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _playbackTimer.Stop();
            _activeAlert = null;
            _queue.Clear();
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Genera y reproduce una alerta de prueba sintética con datos realistas según la plataforma y tipo de evento.
    /// </summary>
    public void TriggerTestAlert(AlertKind kind, StreamPlatform platform, IEnumerable<AlertRule> rules)
    {
        var testEvent = kind switch
        {
            AlertKind.Follower => new AlertEvent(Guid.NewGuid().ToString("N"), platform, kind, "AlexGamer", DateTime.UtcNow),
            AlertKind.Subscription => new AlertEvent(Guid.NewGuid().ToString("N"), platform, kind, "SofiStreams", DateTime.UtcNow, Tier: "Tier 1", Months: 3),
            AlertKind.Donation => new AlertEvent(Guid.NewGuid().ToString("N"), platform, kind, "Carlos_Dev", DateTime.UtcNow, Amount: 15.00m, Currency: "USD", Message: "¡Excelente contenido!"),
            AlertKind.SuperChat => new AlertEvent(Guid.NewGuid().ToString("N"), platform, kind, "ElenaP", DateTime.UtcNow, Amount: 25.00m, Currency: "USD", Message: "¡Saludos desde Madrid!"),
            AlertKind.VirtualGift => new AlertEvent(Guid.NewGuid().ToString("N"), platform, kind, "GamerPro", DateTime.UtcNow, GiftName: "Rosa", GiftCount: 10),
            AlertKind.Share => new AlertEvent(Guid.NewGuid().ToString("N"), platform, kind, "Lucas_99", DateTime.UtcNow),
            AlertKind.CommunityMember => new AlertEvent(Guid.NewGuid().ToString("N"), platform, kind, "MartaLive", DateTime.UtcNow),
            AlertKind.Raid => new AlertEvent(Guid.NewGuid().ToString("N"), platform, kind, "StreamerAmigo", DateTime.UtcNow, Viewers: 142),
            AlertKind.Greeting => new AlertEvent(Guid.NewGuid().ToString("N"), platform, kind, "NuevoEspectador", DateTime.UtcNow, Message: "¡Hola a todos!"),
            _ => new AlertEvent(Guid.NewGuid().ToString("N"), platform, kind, "UsuarioPrueba", DateTime.UtcNow)
        };

        var matchingRule = AlertProcessor.FindMatchingRule(rules, testEvent)
            ?? AlertRule.CreateDefault(kind, platform);

        Enqueue(testEvent, matchingRule);
    }

    private void TryPlayNext_Locked()
    {
        if (_queue.Count == 0 || _isPaused)
        {
            _activeAlert = null;
            return;
        }

        var (ev, rule, text) = _queue.Dequeue();
        var durationSec = Math.Clamp(rule.DurationSec, 2, 60);
        _remainingDurationMs = durationSec * 1000;

        _activeAlert = new ActiveAlertState(ev, rule, text, DateTime.UtcNow, durationSec);
        _playbackTimer.Interval = _remainingDurationMs;
        _playbackTimer.Start();

        Task.Run(() => AlertStarted?.Invoke(this, _activeAlert));
    }

    private void OnAlertDurationElapsed()
    {
        ActiveAlertState? finished = null;
        lock (_lock)
        {
            finished = _activeAlert;
            _activeAlert = null;
            if (!_isPaused)
                TryPlayNext_Locked();
        }

        if (finished is not null)
            AlertCompleted?.Invoke(this, finished);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PruneDedupCache()
    {
        var cutoff = DateTime.UtcNow - _dedupWindow;
        foreach (var (key, time) in _dedupCache)
        {
            if (time < cutoff)
                _dedupCache.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _playbackTimer.Dispose();
        _queue.Clear();
    }
}
