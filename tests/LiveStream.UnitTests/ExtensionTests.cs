using LiveStream.Application.Extensions;
using LiveStream.Application.Studio;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using LiveStream.Domain.ValueObjects;
using LiveStream.Extensions;
using LiveStream.Extensions.Alerts;
using LiveStream.Extensions.Chat;
using LiveStream.Extensions.Integrations;
using LiveStream.Extensions.Tts;
using LiveStream.Infrastructure.Persistence;
using LiveStream.Infrastructure.Security;

namespace LiveStream.UnitTests;

/// <summary>Sistema de extensiones: ciclo de vida, dependencias, aislamiento de fallos, permisos y flujos reales por el bus.</summary>
public sealed class ExtensionTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sp-ext-tests-").FullName;
    private readonly List<StudioSession> _sessions = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions)
            await session.Extensions.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private StudioSession NewSession(IExtensionStateStore? store = null)
    {
        var session = new StudioSession(new FakeStreamingEngine(), new JsonProjectRepository(_dir),
            new DpapiSecretStore(Path.Combine(_dir, "s.dat")), StudioSession.CreateDefaultProject("Ext", new CanvasSettings(1920, 1080, 60)),
            Path.Combine(_dir, "p.spproj"), extensionState: store);
        _sessions.Add(session);
        return session;
    }

    /// <summary>Extensión de prueba configurable que anota su ciclo de vida.</summary>
    private sealed class Probe(string id, string[]? deps = null, string[]? permissions = null) : StudioExtensionBase
    {
        public static readonly List<string> Log = [];

        public override ExtensionManifest Manifest { get; } = Describe(id, id, "prueba", deps, permissions ?? []);

        public Action<IExtensionServices>? OnRegisterAction { get; init; }
        public Exception? StartFails { get; init; }
        public Action<string>? OnMessage { get; init; }
        public int Received { get; private set; }

        protected override void OnRegister(IExtensionServices services) => OnRegisterAction?.Invoke(services);

        protected override Task OnStartAsync(CancellationToken ct)
        {
            lock (Log)
                Log.Add($"start:{Manifest.Id}");
            if (StartFails is not null)
                throw StartFails;
            On<string>(m =>
            {
                Received++;
                OnMessage?.Invoke(m);
            });
            return Task.CompletedTask;
        }

        protected override Task OnStopAsync(CancellationToken ct)
        {
            lock (Log)
                Log.Add($"stop:{Manifest.Id}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Extensions_start_in_dependency_order_whatever_the_order_they_were_added()
    {
        var host = NewSession().Extensions;
        host.Add(new Probe("c", ["b"]));
        host.Add(new Probe("b", ["a"]));
        host.Add(new Probe("a"));
        lock (Probe.Log)
            Probe.Log.Clear();

        await host.StartAsync();

        Assert.All(host.Statuses, s => Assert.Equal(ExtensionState.Running, s.State));
        List<string> starts;
        lock (Probe.Log)
            starts = [.. Probe.Log.Where(l => l is "start:a" or "start:b" or "start:c")];
        Assert.Equal(["start:a", "start:b", "start:c"], starts);
    }

    [Fact]
    public async Task A_failing_extension_blocks_only_its_dependents()
    {
        var host = NewSession().Extensions;
        host.Add(new Probe("bad", null) { StartFails = new InvalidOperationException("boom") });
        host.Add(new Probe("needs-bad", ["bad"]));
        host.Add(new Probe("independent"));
        host.Add(new Probe("orphan", ["missing"]));

        await host.StartAsync();

        Assert.Equal(ExtensionState.Faulted, host.StateOf("bad"));
        Assert.Equal(ExtensionState.Blocked, host.StateOf("needs-bad"));
        Assert.Equal(ExtensionState.Blocked, host.StateOf("orphan"));
        Assert.Equal(ExtensionState.Running, host.StateOf("independent"));
        Assert.Contains("boom", host.Statuses.Single(s => s.Manifest.Id == "bad").Error);
    }

    [Fact]
    public async Task A_registration_failure_or_a_cycle_is_isolated()
    {
        var host = NewSession().Extensions;
        host.Add(new Probe("broken") { OnRegisterAction = _ => throw new InvalidOperationException("ctor") });
        host.Add(new Probe("x", ["y"]));
        host.Add(new Probe("y", ["x"]));
        host.Add(new Probe("fine"));

        await host.StartAsync();

        Assert.Equal(ExtensionState.Faulted, host.StateOf("broken"));
        Assert.NotEqual(ExtensionState.Running, host.StateOf("x"));
        Assert.NotEqual(ExtensionState.Running, host.StateOf("y"));
        Assert.Equal(ExtensionState.Running, host.StateOf("fine"));
    }

    [Fact]
    public async Task Disabling_cascades_to_dependents_persists_and_reenabling_restores_both()
    {
        var store = new InMemoryExtensionStateStore();
        var host = NewSession(store).Extensions;
        var base_ = new Probe("base");
        var top = new Probe("top", ["base"]);
        host.Add(top);
        host.Add(base_);
        await host.StartAsync();

        await host.SetEnabledAsync("base", false);

        Assert.Equal(ExtensionState.Disabled, host.StateOf("base"));
        Assert.Equal(ExtensionState.Blocked, host.StateOf("top"));
        Assert.False(store.IsEnabled("base"));
        host.Events.Publish("hola");
        Assert.Equal(0, top.Received); // desuscrita al detenerse

        await host.SetEnabledAsync("base", true);

        Assert.Equal(ExtensionState.Running, host.StateOf("base"));
        Assert.Equal(ExtensionState.Running, host.StateOf("top"));
        host.Events.Publish("hola");
        Assert.Equal(1, top.Received);
    }

    [Fact]
    public async Task A_disabled_extension_stays_off_on_the_next_start()
    {
        var store = new InMemoryExtensionStateStore();
        store.SetEnabled("quiet", false);
        var host = NewSession(store).Extensions;
        host.Add(new Probe("quiet"));

        await host.StartAsync();

        Assert.Equal(ExtensionState.Disabled, host.StateOf("quiet"));
    }

    [Fact]
    public async Task A_handler_that_keeps_throwing_stops_its_extension_without_affecting_others()
    {
        var host = NewSession().Extensions;
        var faulty = new Probe("faulty") { OnMessage = _ => throw new InvalidOperationException("handler") };
        var healthy = new Probe("healthy");
        host.Add(faulty);
        host.Add(healthy);
        await host.StartAsync();

        for (var i = 0; i < ExtensionHost.MaxFaults; i++)
            host.Events.Publish("evento"); // no lanza hacia quien publica

        await WaitUntil(() => host.StateOf("faulty") == ExtensionState.Faulted);
        Assert.Equal(ExtensionHost.MaxFaults, healthy.Received);
        host.Events.Publish("otro");
        Assert.Equal(ExtensionHost.MaxFaults, faulty.Received); // ya no recibe
        Assert.Equal(ExtensionHost.MaxFaults + 1, healthy.Received);
    }

    [Fact]
    public async Task Undeclared_permissions_and_dependencies_are_refused()
    {
        var host = NewSession().Extensions;
        host.Add(new Probe("publisher") { OnRegisterAction = s => s.Publish(new object()) });
        host.Add(new Probe("sneaky") { OnRegisterAction = s => s.Require<object>() });
        host.Add(new Probe("nosy"));
        await host.StartAsync();

        Assert.Equal(ExtensionState.Faulted, host.StateOf("sneaky")); // usa un servicio sin declarar la dependencia
        Assert.Contains("sin declararla", host.Statuses.Single(s => s.Manifest.Id == "sneaky").Error);

        var nosy = NewSession().Extensions;
        var secretsReader = new SecretsReader();
        nosy.Add(secretsReader);
        await nosy.StartAsync();
        Assert.Equal(ExtensionState.Faulted, nosy.StateOf("secrets-reader"));
        Assert.Contains("secrets", nosy.Statuses.Single().Error);
    }

    private sealed class SecretsReader : StudioExtensionBase
    {
        public override ExtensionManifest Manifest { get; } = Describe("secrets-reader", "x", "x", null, ExtensionPermissions.Studio);

        protected override void OnRegister(IExtensionServices services) => _ = Context.Secrets;
    }

    // ---- Extensiones integradas -----------------------------------------------------------------------

    private async Task<ExtensionHost> StartBuiltInsAsync(IExtensionStateStore? store = null)
    {
        var host = NewSession(store).Extensions;
        BuiltInExtensions.AddTo(host, Path.Combine(_dir, "sounds"));
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task All_built_in_extensions_start_and_publish_their_services()
    {
        var host = await StartBuiltInsAsync();

        Assert.All(host.Statuses, s => Assert.Equal(ExtensionState.Running, s.State));
        Assert.NotNull(host.Get<UnifiedChatService>());
        Assert.NotNull(host.Get<AlertQueue>());
        Assert.NotNull(host.Get<TtsQueueService>());
        Assert.NotNull(host.Get<MonetaryAlertService>());
        Assert.NotNull(host.Get<SoundBankService>());
        Assert.True(File.Exists(Path.Combine(_dir, "sounds", "stock", "ding_notification.wav")));
    }

    [Fact]
    public async Task A_platform_event_reaches_alerts_and_donations_through_the_bus()
    {
        var host = await StartBuiltInsAsync();
        var session = _sessions[^1];
        session.Project.SetAlertRules([AlertRule.CreateDefault(AlertKind.SuperChat, StreamPlatform.YouTube)]);
        var donations = new List<AlertEvent>();
        host.Get<MonetaryAlertService>()!.AlertTriggered += (_, a) => donations.Add(a);
        var alerts = host.Get<AlertQueue>()!;
        var started = new TaskCompletionSource<ActiveAlertState>(TaskCreationOptions.RunContinuationsAsynchronously);
        alerts.AlertStarted += (_, a) => started.TrySetResult(a);

        host.Get<IntegrationManager>()!.SimulateAlert(new AlertEvent("1", StreamPlatform.YouTube, AlertKind.SuperChat, "Ana", DateTime.UtcNow, Amount: 5m, Currency: "USD"));

        Assert.Equal("Ana", (await started.Task.WaitAsync(TimeSpan.FromSeconds(5))).Event.Username);
        Assert.Single(donations);
    }

    [Fact]
    public async Task Chat_reaches_tts_only_while_tts_is_enabled()
    {
        var host = await StartBuiltInsAsync();
        var tts = host.Get<TtsQueueService>()!;
        await tts.TogglePauseAsync(); // en pausa: lo encolado se queda en la cola y se puede contar
        var integrations = host.Get<IntegrationManager>()!;

        integrations.SimulateMessage(new ChatMessage("1", StreamPlatform.Twitch, "Ana", "u1", "hola a todos", DateTime.UtcNow));
        var queuedWhileEnabled = tts.PendingQueue.Count;

        await host.SetEnabledAsync(TtsExtension.Id, false);
        Assert.Equal(ExtensionState.Blocked, host.StateOf(DonationsExtension.Id)); // depende de tts
        integrations.SimulateMessage(new ChatMessage("2", StreamPlatform.Twitch, "Ana", "u1", "sigo aquí", DateTime.UtcNow));

        Assert.Equal(1, queuedWhileEnabled);
        Assert.Equal(0, host.Events.SubscriberCount<ChatMessageReceived>());
        Assert.Empty(tts.PendingQueue);
        Assert.Equal(2, host.Get<UnifiedChatService>()!.AllMessages.Count); // el chat sigue funcionando
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
