using LiveStream.Domain.Compositions;

namespace LiveStream.Domain.Entities;

public enum StreamPlatform
{
    Custom,
    YouTube,
    Twitch,
    Facebook,
    Kick,
    TikTok,
    Instagram,
    /// <summary>Servidor relay propio (SRS, MediaMTX…) que reenvía a las plataformas.</summary>
    Relay,
}

/// <summary>
/// Destino de emisión. La clave NO se guarda en el proyecto: <see cref="CredentialName"/> apunta al almacén cifrado.
/// <see cref="Encoder"/> null = usa el codificador común del proyecto (y lo comparte con otros destinos iguales).
/// <see cref="TargetFormat"/> indica si emite la composición horizontal (16:9), vertical (9:16) o ambas.
/// </summary>
public sealed record StreamDestination(
    Guid Id,
    string Name,
    StreamPlatform Platform,
    string Server,
    string CredentialName,
    bool Enabled = true,
    EncoderSettings? Encoder = null,
    CompositionFormat TargetFormat = CompositionFormat.Horizontal)
{
    public static StreamDestination Create(string name, StreamPlatform platform, string server, EncoderSettings? encoder = null, CompositionFormat targetFormat = CompositionFormat.Horizontal)
    {
        var id = Guid.NewGuid();
        return new StreamDestination(id, name, platform, server, $"stream-key-{id:N}", true, encoder, targetFormat);
    }

    public EncoderSettings EffectiveEncoder(StreamSettings common) => Encoder ?? common.EffectiveEncoder;

    public bool UsesSecureTransport => Server.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"StreamDestination {{ {Name}, {Platform}, formato = {TargetFormat}, credencial = {CredentialName} }}";
}
