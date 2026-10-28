using LiveStream.Application.Extensions;
using LiveStream.Extensions.Alerts;
using LiveStream.Extensions.Chat;
using LiveStream.Extensions.DualStreaming;
using LiveStream.Extensions.Tts;

namespace LiveStream.Extensions;

/// <summary>Las funcionalidades que trae la app. El host las ordena por dependencias; el orden aquí no importa.</summary>
public static class BuiltInExtensions
{
    /// <param name="soundsRoot">Carpeta del banco de sonidos (las pruebas usan una temporal).</param>
    public static IReadOnlyList<IStudioExtension> Create(string? soundsRoot = null) =>
    [
        new SoundBankExtension(soundsRoot),
        new AlertsExtension(),
        new ChatExtension(),
        new TtsExtension(),
        new DonationsExtension(),
        new DualStreamExtension(),
    ];

    public static void AddTo(ExtensionHost host, string? soundsRoot = null)
    {
        foreach (var extension in Create(soundsRoot))
            host.Add(extension);
    }
}
