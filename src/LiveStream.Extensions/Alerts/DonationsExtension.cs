using LiveStream.Application.Extensions;
using LiveStream.Extensions.Tts;

namespace LiveStream.Extensions.Alerts;

/// <summary>Donaciones, regalos y puntos del canal: convierte los eventos monetarios del bus en alertas y lectura por voz.</summary>
public sealed class DonationsExtension : StudioExtensionBase
{
    public const string Id = "donations";

    private MonetaryAlertService? _monetary;

    public override ExtensionManifest Manifest { get; } = Describe(Id, "Donaciones y recompensas",
        "Reglas para donaciones, Super Chats, bits y canjes de puntos del canal.", [AlertsExtension.Id, TtsExtension.Id]);

    protected override void OnRegister(IExtensionServices services)
    {
        var points = new ChannelPointsService();
        _monetary = new MonetaryAlertService(services.Require<TtsQueueService>(), services.Require<AlertQueue>(), points);
        services.Publish(points);
        services.Publish(_monetary);
    }

    protected override Task OnStartAsync(CancellationToken ct)
    {
        On<PlatformAlertReceived>(e => _monetary!.ProcessIncomingAlert(e.Alert));
        return Task.CompletedTask;
    }
}
