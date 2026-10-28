using System;
using System.Collections.Generic;
using System.Linq;
using LiveStream.Extensions.Tts;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Tts;

namespace LiveStream.Extensions.Alerts;

/// <summary>
/// Servicio de alertas monetarias, donaciones y regalos multiplataforma.
/// Evalúa umbrales por moneda o unidades de regalo, sintetiza voz TTS personalizada
/// y sincroniza con el procesador de animaciones visuales y puntos de canal.
/// </summary>
public sealed class MonetaryAlertService
{
    private readonly TtsQueueService _tts;
    private readonly AlertQueue _alertQueue;
    private readonly ChannelPointsService _points;
    private readonly List<MonetaryAlertRule> _rules = [];
    private readonly object _lock = new();

    public event Action<MonetaryAlertRule, AlertEvent>? AlertTriggered;

    public IReadOnlyList<MonetaryAlertRule> Rules
    {
        get { lock (_lock) return _rules.ToList(); }
    }

    public MonetaryAlertService(TtsQueueService tts, AlertQueue alertQueue, ChannelPointsService points)
    {
        _tts = tts ?? throw new ArgumentNullException(nameof(tts));
        _alertQueue = alertQueue ?? throw new ArgumentNullException(nameof(alertQueue));
        _points = points ?? throw new ArgumentNullException(nameof(points));

        InitializeDefaultRules();
    }

    public void AddRule(MonetaryAlertRule rule)
    {
        lock (_lock)
        {
            _rules.RemoveAll(r => r.Id == rule.Id);
            _rules.Add(rule);
        }
    }

    public bool RemoveRule(string ruleId)
    {
        lock (_lock)
        {
            return _rules.RemoveAll(r => r.Id == ruleId) > 0;
        }
    }

    public void ClearRules()
    {
        lock (_lock)
        {
            _rules.Clear();
        }
    }

    /// <summary>Procesa un evento entrante desde cualquier plataforma de streaming.</summary>
    public bool ProcessIncomingAlert(AlertEvent alert)
    {
        MonetaryAlertRule? matchedRule = null;

        lock (_lock)
        {
            // Buscar la regla activa más específica con el umbral más alto alcanzado
            matchedRule = _rules
                .Where(r =>
                    r.Enabled &&
                    r.Platform == alert.Platform &&
                    r.Kind == alert.Kind &&
                    (!alert.Amount.HasValue || alert.Amount.Value >= r.MinAmount) &&
                    (!r.MinGiftUnits.HasValue || (alert.GiftCount ?? 0) >= r.MinGiftUnits.Value))
                .OrderByDescending(r => r.MinAmount)
                .ThenByDescending(r => r.MinGiftUnits ?? 0)
                .FirstOrDefault();

            // Si no hay regla específica por plataforma, buscar regla genérica
            matchedRule ??= _rules
                .Where(r =>
                    r.Enabled &&
                    r.Kind == alert.Kind &&
                    (!alert.Amount.HasValue || alert.Amount.Value >= r.MinAmount) &&
                    (!r.MinGiftUnits.HasValue || (alert.GiftCount ?? 0) >= r.MinGiftUnits.Value))
                .OrderByDescending(r => r.MinAmount)
                .ThenByDescending(r => r.MinGiftUnits ?? 0)
                .FirstOrDefault();
        }

        if (matchedRule is null)
            return false;

        // 1. Sustituir tokens en la plantilla hablada
        var spokenText = FormatTemplate(matchedRule.SpeechTemplate, alert);

        // 2. Enviar a la cola de voz con alta prioridad (interrumpe comentarios normales si está configurado)
        var utterance = new TtsUtterance(
            Id: alert.Id,
            Text: spokenText,
            VoiceId: matchedRule.CustomVoiceId,
            Priority: matchedRule.Priority,
            Platform: alert.Platform,
            Author: alert.Username,
            Timestamp: alert.Timestamp,
            IsManual: false);

        _tts.Enqueue(utterance);

        // 3. Enviar a las alertas visuales en pantalla
        var visualRule = new AlertRule(
            Id: Guid.NewGuid(),
            Name: matchedRule.Name,
            Kind: matchedRule.Kind,
            Template: matchedRule.SpeechTemplate,
            Platform: matchedRule.Platform,
            Priority: matchedRule.Priority);
        _alertQueue.Enqueue(alert with { Priority = matchedRule.Priority }, visualRule);

        // 4. Adjudicar puntos virtuales del canal
        _points.ProcessAlertEvent(alert);

        AlertTriggered?.Invoke(matchedRule, alert);
        AppLog.Info("monetary", $"Alerta activada: {matchedRule.Name} para {alert.Username} ({alert.Kind})");

        return true;
    }

    /// <summary>Dispara una prueba simulada de alerta monetaria para verificar sonido, voz y diseño.</summary>
    public void TriggerTestAlert(MonetaryAlertRule rule)
    {
        var testEvent = new AlertEvent(
            Id: $"test_{Guid.NewGuid():N}",
            Platform: rule.Platform,
            Kind: rule.Kind,
            Username: "DonanteEstrella",
            Timestamp: DateTime.UtcNow,
            Amount: rule.MinAmount > 0 ? rule.MinAmount : 10m,
            Currency: rule.Currency,
            GiftName: "Cofre Mágico",
            GiftCount: rule.MinGiftUnits ?? 5,
            Tier: "Nivel 1",
            Months: 6,
            Priority: rule.Priority,
            Message: "¡Excelente transmisión!");

        ProcessIncomingAlert(testEvent);
    }

    public static string FormatTemplate(string template, AlertEvent alert)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        return template
            .Replace("{username}", alert.Username, StringComparison.OrdinalIgnoreCase)
            .Replace("{amount}", alert.Amount.HasValue ? $"{alert.Amount.Value:0.##}" : "0", StringComparison.OrdinalIgnoreCase)
            .Replace("{currency}", alert.Currency ?? "USD", StringComparison.OrdinalIgnoreCase)
            .Replace("{gift_name}", alert.GiftName ?? "Regalo", StringComparison.OrdinalIgnoreCase)
            .Replace("{gift_count}", alert.GiftCount.HasValue ? alert.GiftCount.Value.ToString() : "1", StringComparison.OrdinalIgnoreCase)
            .Replace("{tier}", alert.Tier ?? "Nivel 1", StringComparison.OrdinalIgnoreCase)
            .Replace("{months}", alert.Months.HasValue ? alert.Months.Value.ToString() : "1", StringComparison.OrdinalIgnoreCase)
            .Replace("{message}", alert.Message ?? "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private void InitializeDefaultRules()
    {
        // YouTube: Super Chat
        _rules.Add(new MonetaryAlertRule(
            Id: "yt_superchat_default",
            Name: "YouTube — Super Chat",
            Platform: StreamPlatform.YouTube,
            Kind: AlertKind.SuperChat,
            MinAmount: 2.00m,
            Currency: "USD",
            SpeechTemplate: "¡Muchas gracias, {username}, por tu Super Chat de {amount} {currency}!",
            Priority: AlertPriority.High));

        // TikTok: Regalos virtuales
        _rules.Add(new MonetaryAlertRule(
            Id: "tiktok_gift_default",
            Name: "TikTok LIVE — Regalo Virtual",
            Platform: StreamPlatform.TikTok,
            Kind: AlertKind.VirtualGift,
            MinAmount: 0m,
            MinGiftUnits: 1,
            SpeechTemplate: "¡Gracias, {username}, por enviar {gift_count} {gift_name}!",
            Priority: AlertPriority.High));

        // Twitch: Bits y Donaciones
        _rules.Add(new MonetaryAlertRule(
            Id: "twitch_donation_default",
            Name: "Twitch — Donación / Bits",
            Platform: StreamPlatform.Twitch,
            Kind: AlertKind.Donation,
            MinAmount: 1.00m,
            Currency: "USD",
            SpeechTemplate: "¡Gracias, {username}, por tu apoyo de {amount} {currency}!",
            Priority: AlertPriority.High));

        // Suscripción general
        _rules.Add(new MonetaryAlertRule(
            Id: "sub_default",
            Name: "Suscripción / Membresía",
            Platform: StreamPlatform.Twitch,
            Kind: AlertKind.Subscription,
            SpeechTemplate: "¡Bienvenido al canal {username}, gracias por tu suscripción {tier}!",
            Priority: AlertPriority.High));
    }
}
