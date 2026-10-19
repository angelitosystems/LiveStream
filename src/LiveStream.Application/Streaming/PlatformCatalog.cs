using LiveStream.Domain.Entities;

namespace LiveStream.Application.Streaming;

/// <summary>
/// Plataformas conocidas. Los servidores por defecto son los publicados por cada plataforma; el valor que muestre
/// su panel de emisión manda siempre. Nada aquí garantiza acceso: algunas plataformas exigen permisos o elegibilidad.
/// </summary>
public static class PlatformCatalog
{
    public sealed record Entry(
        StreamPlatform Platform,
        string Name,
        string DefaultServer,
        string Help,
        bool RequiresRtmps = false,
        int? MaxVideoBitrateKbps = null);

    public static IReadOnlyList<Entry> All { get; } =
    [
        new(StreamPlatform.YouTube, "YouTube", "rtmp://a.rtmp.youtube.com/live2",
            "Clave en YouTube Studio → Emitir en directo. También admite rtmps://a.rtmps.youtube.com/live2."),
        new(StreamPlatform.Twitch, "Twitch", "rtmp://live.twitch.tv/app",
            "Clave en el panel de creador → Configuración → Transmisión. Twitch limita el bitrate de video a 6000 kb/s.",
            MaxVideoBitrateKbps: 6000),
        new(StreamPlatform.Facebook, "Facebook", "rtmps://live-api-s.facebook.com:443/rtmp/",
            "Clave en Live Producer al elegir «Software de transmisión». Facebook solo acepta RTMPS.", RequiresRtmps: true),
        new(StreamPlatform.Kick, "Kick", "",
            "Copia la URL del servidor y la clave desde Kick → Configuración del canal → Clave de transmisión."),
        new(StreamPlatform.TikTok, "TikTok", "",
            "TikTok solo da servidor y clave a cuentas habilitadas para emitir con software externo (requisitos de edad, " +
            "seguidores o permiso de LIVE). Si tu cuenta no lo tiene, LiveStream no puede emitir allí."),
        new(StreamPlatform.Relay, "Relay propio (SRS, MediaMTX…)", "",
            "Emite una sola vez a tu servidor relay y deja que él reenvíe a las plataformas: ahorra subida si el relay " +
            "está en la nube. La credencial es la del relay, guardada aparte de las de las plataformas."),
        new(StreamPlatform.Custom, "Servidor RTMP personalizado", "", "Cualquier servidor RTMP o RTMPS."),
    ];

    public static Entry For(StreamPlatform platform) => All.First(e => e.Platform == platform);

    /// <summary>Errores que impiden emitir a un destino (null si está bien).</summary>
    public static string? Validate(StreamDestination destination, EncoderSettings encoder)
    {
        if (string.IsNullOrWhiteSpace(destination.Name))
            return "El destino necesita un nombre.";
        var server = destination.Server.Trim();
        if (server.Length == 0)
            return $"«{destination.Name}»: falta el servidor.";
        if (!server.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) && !server.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
            return $"«{destination.Name}»: el servidor debe empezar por rtmp:// o rtmps://.";
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return $"«{destination.Name}»: la dirección del servidor no es válida.";
        var entry = For(destination.Platform);
        if (entry.RequiresRtmps && !destination.UsesSecureTransport)
            return $"«{destination.Name}»: {entry.Name} solo acepta conexiones RTMPS (rtmps://).";
        if (entry.MaxVideoBitrateKbps is { } max && encoder.VideoBitrateKbps > max)
            return $"«{destination.Name}»: {entry.Name} admite como máximo {max} kb/s de video (configurado {encoder.VideoBitrateKbps}).";
        return null;
    }
}
