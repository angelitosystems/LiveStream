using LiveStream.Application.Extensions;

namespace LiveStream.Extensions.Alerts;

/// <summary>Banco de sonidos: genera los sonidos de serie y gestiona los importados y descargados.</summary>
/// <param name="root">Carpeta de sonidos; null = %APPDATA%\LiveStream\sounds.</param>
public sealed class SoundBankExtension(string? root = null) : StudioExtensionBase
{
    public const string Id = "sound-bank";

    public override ExtensionManifest Manifest { get; } = Describe(Id, "Banco de sonidos",
        "Sonidos de serie y propios para alertas y efectos.", null, ExtensionPermissions.Audio, ExtensionPermissions.Network);

    // Al publicarse se generan los sonidos de serie: antes solo existían tras abrir el diálogo del banco una vez.
    protected override void OnRegister(IExtensionServices services) => services.Publish(new SoundBankService(root));

    protected override Task OnStopAsync(CancellationToken ct)
    {
        SoundPlayerService.Stop();
        return Task.CompletedTask;
    }
}
