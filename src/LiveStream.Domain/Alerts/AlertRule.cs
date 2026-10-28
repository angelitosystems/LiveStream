using System;
using LiveStream.Domain.Compositions;
using LiveStream.Domain.Entities;

namespace LiveStream.Domain.Alerts;

public sealed record AlertRule(
    Guid Id,
    string Name,
    AlertKind Kind,
    string Template,
    StreamPlatform? Platform = null,
    bool Enabled = true,
    decimal MinAmount = 0m,
    int MinViewers = 0,
    AlertPriority Priority = AlertPriority.Normal,
    int DurationSec = 5,
    string? SoundPath = null,
    string? ImagePath = null,
    string TextColor = "#FFFFFF",
    string AccentColor = "#F59E0B",
    string BackgroundColor = "#1E293B",
    string AnimationIn = "Pop",
    string AnimationOut = "Fade",
    CompositionFormat TargetComposition = CompositionFormat.Both)
{
    public static AlertRule CreateDefault(AlertKind kind, StreamPlatform? platform = null)
    {
        var (name, template, sound) = kind switch
        {
            AlertKind.Follower => ("Nuevo seguidor", "¡Gracias por seguirnos, {username}!", "ding_notification.wav"),
            AlertKind.Subscription => ("Nueva suscripción", "¡{username} se ha suscrito! ({tier})", "level_up.wav"),
            AlertKind.Donation => ("Aportación económica", "¡{username} ha donado {amount} {currency}!", "arcade_coin.wav"),
            AlertKind.SuperChat => ("Super Chat", "¡Super Chat de {username}: {amount} {currency}!", "victory_arpeggio.wav"),
            AlertKind.VirtualGift => ("Regalo virtual", "¡{username} envió {gift}!", "arcade_coin.wav"),
            AlertKind.Share => ("Transmisión compartida", "¡{username} compartió la transmisión!", "ding_notification.wav"),
            AlertKind.CommunityMember => ("Nuevo miembro", "¡{username} se unió a la comunidad!", "level_up.wav"),
            AlertKind.Raid => ("Raid / Espectadores", "¡Raid de {username} con {viewers} espectadores!", "fanfare_tada.wav"),
            AlertKind.Greeting => ("Bienvenida nuevo espectador", "¡Bienvenido, {username}! Gracias por acompañarnos en el directo.", "chime_bell.wav"),
            _ => ("Alerta personalizada", "¡Evento de {username}!", "ding_notification.wav")
        };

        return new AlertRule(Guid.NewGuid(), name, kind, template, platform, SoundPath: sound);
    }
}
