using LiveStream.Application.Extensions;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Extensions.Integrations;

namespace LiveStream.Extensions.Chat;

/// <summary>
/// Chat multicanal: conectores de plataformas, chat unificado y chat en pantalla. Publica en el bus los mensajes
/// (<see cref="ChatMessageReceived"/>), los eventos de plataforma (<see cref="PlatformAlertReceived"/>) y los saludos
/// (<see cref="AlertRequested"/>); quien los consume (alertas, TTS, donaciones) no conoce estos servicios.
/// </summary>
public sealed class ChatExtension : StudioExtensionBase
{
    public const string Id = "chat";

    private IntegrationManager? _integrations;
    private UnifiedChatService? _chat;
    private ChatOverlayService? _overlay;

    public override ExtensionManifest Manifest { get; } = Describe(Id, "Chat multicanal",
        "Conecta YouTube, Twitch, TikTok, Facebook e Instagram, unifica su chat y lo muestra en pantalla.", null,
        ExtensionPermissions.Studio, ExtensionPermissions.Secrets, ExtensionPermissions.Network);

    protected override void OnRegister(IExtensionServices services)
    {
        _integrations = new IntegrationManager(Context.Secrets);
        _chat = new UnifiedChatService();
        _overlay = new ChatOverlayService(Context.Session, _chat);
        services.Publish(_integrations);
        services.Publish(_chat);
        services.Publish(_overlay);
    }

    protected override Task OnStartAsync(CancellationToken ct)
    {
        var (integrations, chat, overlay, bus) = (_integrations!, _chat!, _overlay!, Context.Events);
        EventHandler<ChatMessage> incoming = (_, m) => chat.AddMessage(m);
        EventHandler<AlertEvent> alert = (_, a) => bus.Publish(new PlatformAlertReceived(a));
        EventHandler<ChatMessage> added = (_, m) => bus.Publish(new ChatMessageReceived(m));
        EventHandler<AlertEvent> greeting = (_, a) => bus.Publish(new AlertRequested(a));
        Track(() => integrations.MessageReceived += incoming, () => integrations.MessageReceived -= incoming);
        Track(() => integrations.AlertReceived += alert, () => integrations.AlertReceived -= alert);
        Track(() => chat.MessageAdded += added, () => chat.MessageAdded -= added);
        Track(() => chat.AutoGreetingTriggered += greeting, () => chat.AutoGreetingTriggered -= greeting);
        Track(overlay.Start, overlay.Stop);
        return Task.CompletedTask;
    }

    // Desactivar el chat corta las conexiones con las plataformas.
    protected override Task OnStopAsync(CancellationToken ct) => _integrations!.DisconnectAllAsync(ct);

    public override async ValueTask DisposeAsync()
    {
        _overlay?.Dispose();
        if (_integrations is not null)
            await _integrations.DisposeAsync().ConfigureAwait(false);
    }
}
