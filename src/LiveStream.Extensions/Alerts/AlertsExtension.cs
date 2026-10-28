using LiveStream.Application.Extensions;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Graphics;

namespace LiveStream.Extensions.Alerts;

/// <summary>
/// Alertas en directo: atiende <see cref="PlatformAlertReceived"/> y <see cref="AlertRequested"/> del bus, las encola
/// según las reglas del proyecto, suena el sonido y muestra el gráfico de alerta.
/// </summary>
public sealed class AlertsExtension : StudioExtensionBase
{
    public const string Id = "alerts";

    private AlertQueue? _queue;

    public override ExtensionManifest Manifest { get; } = Describe(Id, "Alertas",
        "Cola de alertas (seguidores, suscripciones, raids…) con sonido y gráfico en pantalla.", [SoundBankExtension.Id],
        ExtensionPermissions.Studio, ExtensionPermissions.Audio);

    protected override void OnRegister(IExtensionServices services)
    {
        _queue = new AlertQueue();
        services.Publish(_queue);
    }

    protected override Task OnStartAsync(CancellationToken ct)
    {
        var queue = _queue!;
        On<PlatformAlertReceived>(e => Enqueue(e.Alert));
        On<AlertRequested>(e => Enqueue(e.Alert));
        EventHandler<ActiveAlertState> started = (_, active) => Show(active);
        EventHandler<ActiveAlertState> completed = (_, _) => Hide();
        Track(() => queue.AlertStarted += started, () => queue.AlertStarted -= started);
        Track(() => queue.AlertCompleted += completed, () => queue.AlertCompleted -= completed);
        return Task.CompletedTask;
    }

    // Desactivadas: no queda ninguna alerta pendiente que salga más tarde.
    protected override Task OnStopAsync(CancellationToken ct)
    {
        _queue!.Clear();
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        _queue?.Dispose();
        return ValueTask.CompletedTask;
    }

    // Las reglas viven en el proyecto, que solo se toca en su hilo.
    private void Enqueue(AlertEvent alert) => Context.Session.OnOwnerThread("Encolar alerta", () =>
    {
        if (AlertProcessor.FindMatchingRule(Context.Session.Project.AlertRules, alert) is { } rule)
            _queue!.Enqueue(alert, rule);
        return Task.CompletedTask;
    });

    private void Show(ActiveAlertState active) => Context.Session.OnOwnerThread("Mostrar alerta", async () =>
    {
        SoundPlayerService.Play(string.IsNullOrWhiteSpace(active.Rule.SoundPath) ? DefaultSound(active.Rule.Kind) : active.Rule.SoundPath);

        var session = Context.Session;
        var alertGraphic = session.Project.Graphics.FirstOrDefault(g => g.Kind == GraphicKind.Alert);
        if (alertGraphic is not null && session.Engine.IsInitialized)
        {
            session.Project.ReplaceGraphic(alertGraphic with
            {
                Fields = new Dictionary<string, string>(alertGraphic.Fields)
                {
                    ["title"] = active.Rule.Name,
                    ["message"] = active.FormattedText,
                },
            });
            await session.Graphics.TakeInAsync(alertGraphic.Id);
        }
    });

    private void Hide() => Context.Session.OnOwnerThread("Ocultar alerta", async () =>
    {
        var session = Context.Session;
        var alertGraphic = session.Project.Graphics.FirstOrDefault(g => g.Kind == GraphicKind.Alert);
        if (alertGraphic is not null && session.Engine.IsInitialized)
            await session.Graphics.TakeOutAsync(alertGraphic.Id);
    });

    private static string DefaultSound(AlertKind kind) => kind switch
    {
        AlertKind.Follower => "ding_notification.wav",
        AlertKind.Subscription => "level_up.wav",
        AlertKind.Donation => "arcade_coin.wav",
        AlertKind.SuperChat => "victory_arpeggio.wav",
        AlertKind.Raid => "fanfare_tada.wav",
        AlertKind.Greeting => "chime_bell.wav",
        _ => "ding_notification.wav",
    };
}
