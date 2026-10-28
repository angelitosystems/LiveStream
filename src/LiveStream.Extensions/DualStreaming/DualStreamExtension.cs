using LiveStream.Application.Extensions;

namespace LiveStream.Extensions.DualStreaming;

/// <summary>Composición y emisión dual (horizontal 16:9 y vertical 9:16) sobre el supervisor de salidas del núcleo.</summary>
public sealed class DualStreamExtension : StudioExtensionBase
{
    public const string Id = "dual-stream";

    public override ExtensionManifest Manifest { get; } = Describe(Id, "Emisión dual",
        "Edita y emite a la vez en horizontal y en vertical, cada formato a sus destinos.", null, ExtensionPermissions.Studio);

    protected override void OnRegister(IExtensionServices services) => services.Publish(new DualStreamCoordinator(Context.Session));
}
