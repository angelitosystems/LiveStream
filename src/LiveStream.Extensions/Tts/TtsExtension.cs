using LiveStream.Application.Extensions;

namespace LiveStream.Extensions.Tts;

/// <summary>Lectura de chat por voz: cola de TTS y reglas por plataforma; recibe los mensajes por el bus, sin depender del chat.</summary>
public sealed class TtsExtension : StudioExtensionBase
{
    public const string Id = "tts";

    private ITtsProvider? _provider;
    private TtsQueueService? _queue;
    private ChatTtsCoordinator? _chatTts;

    public override ExtensionManifest Manifest { get; } = Describe(Id, "Lectura de chat (TTS)",
        "Lee en voz alta el chat y las donaciones con voces de Windows.", null, ExtensionPermissions.Audio);

    protected override void OnRegister(IExtensionServices services)
    {
        _provider = new WindowsSapiTtsProvider();
        _queue = new TtsQueueService(_provider);
        _chatTts = new ChatTtsCoordinator(_queue);
        services.Publish(_provider);
        services.Publish(_queue);
        services.Publish(_chatTts);
    }

    protected override Task OnStartAsync(CancellationToken ct)
    {
        On<ChatMessageReceived>(e => _chatTts!.HandleMessage(e.Message));
        return Task.CompletedTask;
    }

    // Desactivada: calla lo que esté leyendo y vacía lo pendiente.
    protected override Task OnStopAsync(CancellationToken ct)
    {
        _queue!.ClearQueue();
        _queue.SkipCurrent();
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        _queue?.Dispose();
        _provider?.Dispose();
        return ValueTask.CompletedTask;
    }
}
