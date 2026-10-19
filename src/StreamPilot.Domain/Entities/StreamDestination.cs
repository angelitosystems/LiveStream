namespace StreamPilot.Domain.Entities;

public enum StreamPlatform
{
    Custom,
    YouTube,
    Twitch,
    Facebook,
    Kick,
    TikTok,
    /// <summary>Servidor relay propio (SRS, MediaMTX…) que reenvía a las plataformas.</summary>
    Relay,
}

/// <summary>
/// Destino de emisión. La clave NO se guarda en el proyecto: <see cref="CredentialName"/> apunta al almacén cifrado.
/// <see cref="Encoder"/> null = usa el codificador común del proyecto (y lo comparte con otros destinos iguales).
/// </summary>
public sealed record StreamDestination(
    Guid Id,
    string Name,
    StreamPlatform Platform,
    string Server,
    string CredentialName,
    bool Enabled = true,
    EncoderSettings? Encoder = null)
{
    public static StreamDestination Create(string name, StreamPlatform platform, string server, EncoderSettings? encoder = null)
    {
        var id = Guid.NewGuid();
        return new StreamDestination(id, name, platform, server, $"stream-key-{id:N}", true, encoder);
    }

    public EncoderSettings EffectiveEncoder(StreamSettings common) => Encoder ?? common.EffectiveEncoder;

    public bool UsesSecureTransport => Server.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"StreamDestination {{ {Name}, {Platform}, credencial = {CredentialName} }}";
}
