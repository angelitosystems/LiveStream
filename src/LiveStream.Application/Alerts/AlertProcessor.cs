using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LiveStream.Domain.Alerts;

namespace LiveStream.Application.Alerts;

/// <summary>
/// Procesa y resuelve plantillas dinámicas de alertas con sustitución segura de variables
/// y validación de condiciones de activación (umbrales de donación, espectadores mínimos, plataforma).
/// </summary>
public static class AlertProcessor
{
    private static readonly Regex VariableRegex = new(@"\{([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Resuelve las variables dinámicas de una plantilla ({username}, {amount}, {currency}, {tier}, {months}, {viewers}, {gift}, {message}, {platform})
    /// con valores de sustitución seguros y textos por defecto ante datos ausentes.
    /// </summary>
    public static string FormatTemplate(string template, AlertEvent alert)
    {
        if (string.IsNullOrWhiteSpace(template))
            return $"¡Alerta de {alert.Username}!";

        return VariableRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value.ToLowerInvariant();
            return key switch
            {
                "username" or "user" or "autor" => !string.IsNullOrWhiteSpace(alert.Username) ? alert.Username : "Espectador",
                "amount" or "cantidad" or "donacion" => alert.Amount.HasValue ? alert.Amount.Value.ToString("0.00") : "0.00",
                "currency" or "moneda" => !string.IsNullOrWhiteSpace(alert.Currency) ? alert.Currency : "USD",
                "tier" or "nivel" => !string.IsNullOrWhiteSpace(alert.Tier) ? alert.Tier : "Nivel 1",
                "months" or "meses" => alert.Months.HasValue ? alert.Months.Value.ToString() : "1",
                "viewers" or "espectadores" or "raiders" => alert.Viewers.HasValue ? alert.Viewers.Value.ToString() : "0",
                "gift" or "regalo" => !string.IsNullOrWhiteSpace(alert.GiftName) ? alert.GiftName : "un regalo",
                "giftcount" => alert.GiftCount.HasValue ? alert.GiftCount.Value.ToString() : "1",
                "message" or "mensaje" or "comentario" => !string.IsNullOrWhiteSpace(alert.Message) ? alert.Message : "",
                "platform" or "plataforma" => alert.Platform.ToString(),
                _ => match.Value // Si la variable no coincide se deja intacta de forma segura
            };
        });
    }

    /// <summary>
    /// Encuentra la regla más adecuada para un evento según plataforma y condiciones de activación.
    /// </summary>
    public static AlertRule? FindMatchingRule(IEnumerable<AlertRule> rules, AlertEvent alert)
    {
        return rules
            .Where(r => r.Enabled && r.Kind == alert.Kind)
            .Where(r => !r.Platform.HasValue || r.Platform.Value == alert.Platform)
            .Where(r =>
            {
                // Validación de umbrales
                if (r.MinAmount > 0 && (alert.Amount ?? 0) < r.MinAmount)
                    return false;
                if (r.MinViewers > 0 && (alert.Viewers ?? 0) < r.MinViewers)
                    return false;
                return true;
            })
            .OrderByDescending(r => r.Platform.HasValue) // Prefiere reglas específicas de la plataforma
            .ThenByDescending(r => r.Priority)
            .FirstOrDefault();
    }
}
