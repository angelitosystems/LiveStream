using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Entities;

namespace LiveStream.Application.Alerts;

/// <summary>
/// Sistema de puntos y recompensas virtuales del canal (no monetarios).
/// Registra aportaciones, participaciones y fidelidad sin atribuir valor monetario real ni comisiones.
/// Garantiza no duplicación de eventos con identificadores únicos y auditoría verificable.
/// </summary>
public sealed class ChannelPointsService
{
    private readonly ConcurrentDictionary<string, long> _userBalances = new();
    private readonly List<VirtualPointsEntry> _ledger = [];
    private readonly HashSet<string> _creditedEvents = [];
    private readonly object _lock = new();

    public VirtualPointsSettings Settings { get; set; } = new();

    public event Action<VirtualPointsEntry>? PointsAwarded;

    public IReadOnlyList<VirtualPointsEntry> History
    {
        get { lock (_lock) return _ledger.TakeLast(200).ToList(); }
    }

    public long GetUserBalance(string userId) =>
        !string.IsNullOrEmpty(userId) && _userBalances.TryGetValue(userId.Trim().ToLowerInvariant(), out var bal) ? bal : 0;

    /// <summary>
    /// Procesa un evento monetario o de apoyo para adjudicar puntos virtuales según las reglas del creador.
    /// Si el evento ya fue contabilizado previamente, se descarta para evitar duplicaciones.
    /// </summary>
    public long ProcessAlertEvent(AlertEvent alert)
    {
        if (!Settings.Enabled)
            return 0;

        if (string.IsNullOrWhiteSpace(alert.Username))
            return 0;

        var eventKey = string.IsNullOrWhiteSpace(alert.Id)
            ? $"{alert.Platform}:{alert.Username}:{alert.Timestamp.Ticks}"
            : alert.Id;

        lock (_lock)
        {
            if (!_creditedEvents.Add(eventKey))
            {
                AppLog.Info("points", $"Evento ya procesado para puntos de canal: {eventKey}");
                return 0;
            }

            long points = 0;
            var reason = string.Empty;

            switch (alert.Kind)
            {
                case AlertKind.Donation or AlertKind.SuperChat when alert.Amount.HasValue && alert.Amount.Value > 0:
                    points = (long)(alert.Amount.Value * Settings.PointsPerCurrencyUnit);
                    reason = $"Donación / Super Chat de {alert.Amount.Value:0.00} {alert.Currency ?? "USD"}";
                    break;

                case AlertKind.VirtualGift when alert.GiftCount.HasValue && alert.GiftCount.Value > 0:
                    points = (long)alert.GiftCount.Value * Settings.PointsPerGiftUnit;
                    reason = $"Regalo virtual ({alert.GiftName ?? "Regalo"} × {alert.GiftCount.Value})";
                    break;

                case AlertKind.Subscription:
                    points = Settings.PointsPerSubscription;
                    reason = "Suscripción / Membresía de canal";
                    break;

                default:
                    return 0;
            }

            if (points <= 0)
                return 0;

            var userId = alert.Username.ToLowerInvariant();
            var newTotal = _userBalances.AddOrUpdate(userId, points, (_, current) => current + points);

            var entry = new VirtualPointsEntry(
                Id: Guid.NewGuid().ToString(),
                UserId: userId,
                Username: alert.Username,
                Platform: alert.Platform,
                PointsAwarded: points,
                Reason: reason,
                Timestamp: DateTime.UtcNow,
                ReferenceEventId: alert.Id);

            _ledger.Add(entry);
            PointsAwarded?.Invoke(entry);

            AppLog.Info("points", $"Adjudicados {points} {Settings.PointName} a {alert.Username}. Total: {newTotal}");
            return points;
        }
    }

    /// <summary>Añade manualmente puntos a un usuario (ej. concursos o interacción).</summary>
    public void AwardManualPoints(string username, StreamPlatform platform, long amount, string reason)
    {
        if (string.IsNullOrWhiteSpace(username) || amount <= 0)
            return;

        lock (_lock)
        {
            var userId = username.ToLowerInvariant();
            _userBalances.AddOrUpdate(userId, amount, (_, current) => current + amount);

            var entry = new VirtualPointsEntry(
                Id: Guid.NewGuid().ToString(),
                UserId: userId,
                Username: username,
                Platform: platform,
                PointsAwarded: amount,
                Reason: reason,
                Timestamp: DateTime.UtcNow);

            _ledger.Add(entry);
            PointsAwarded?.Invoke(entry);
        }
    }
}
