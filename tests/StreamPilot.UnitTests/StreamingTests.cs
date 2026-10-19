using StreamPilot.Application.Abstractions;
using StreamPilot.Application.Streaming;
using StreamPilot.Application.Studio;
using StreamPilot.Domain.Entities;
using StreamPilot.Infrastructure.Persistence;
using StreamPilot.Infrastructure.Security;

namespace StreamPilot.UnitTests;

/// <summary>Fase 6: destinos, migración, validación por plataforma, inicio parcial y supervisión.</summary>
public sealed class StreamingTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 10, 18, 21, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Directory.CreateTempSubdirectory("sp-ms-").FullName;
    private readonly FakeStreamingEngine _engine = new();
    private StudioSession _session = null!;

    public Task InitializeAsync()
    {
        _session = new StudioSession(_engine, new JsonProjectRepository(_dir), new DpapiSecretStore(Path.Combine(_dir, "s.dat")),
            StudioSession.CreateDefaultProject("MS"), Path.Combine(_dir, "p.spproj"), TimeSpan.FromHours(1));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<StreamDestination> AddAsync(string name, StreamPlatform platform, string server, string? key = "clave")
    {
        var d = StreamDestination.Create(name, platform, server);
        Assert.True((await _session.Executor.ExecuteAsync(new AddDestinationCommand(d))).Success);
        if (key is not null)
            await _session.SetDestinationKeyAsync(d.Id, key);
        return d;
    }

    [Fact]
    public void Migration_V3SingleServerBecomesPrincipalDestination_WithTheSameCredential()
    {
        var id = Guid.NewGuid();
        var json = """
            {"schemaVersion":3,"id":"ID","name":"Viejo","sources":[],"scenes":[],
             "stream":{"server":"rtmp://live.twitch.tv/app","credentialName":"stream-key-default","encoder":{"videoEncoderId":"obs_x264","videoBitrateKbps":4500,"audioBitrateKbps":160,"keyframeIntervalSec":2}}}
            """.Replace("ID", id.ToString());
        var project = ProjectSerializer.Deserialize(json);
        var d = Assert.Single(project.Destinations);
        Assert.Equal(("Principal", "rtmp://live.twitch.tv/app", "stream-key-default", true), (d.Name, d.Server, d.CredentialName, d.Enabled));
        Assert.Equal("", project.Stream.Server);
        Assert.Equal(4500, project.Stream.EffectiveEncoder.VideoBitrateKbps); // el codificador común se conserva
        Assert.Equal(d.Id, Assert.Single(ProjectSerializer.Deserialize(json).Destinations).Id); // migración determinista

        // Guardado como v4 y releído: el destino persiste tal cual y no se duplica.
        var again = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        Assert.Equal(d, Assert.Single(again.Destinations));
    }

    [Theory]
    [InlineData(StreamPlatform.Facebook, "rtmp://live-api-s.facebook.com/rtmp/", 4000, "RTMPS")]
    [InlineData(StreamPlatform.Twitch, "rtmp://live.twitch.tv/app", 8000, "6000")]
    [InlineData(StreamPlatform.Custom, "http://servidor/app", 4000, "rtmp://")]
    [InlineData(StreamPlatform.Custom, "rtmp://", 4000, "no es válida")]
    public void Platforms_RejectInvalidDestinations(StreamPlatform platform, string server, int bitrate, string expected)
    {
        var d = StreamDestination.Create("X", platform, server);
        Assert.Contains(expected, PlatformCatalog.Validate(d, new EncoderSettings(VideoBitrateKbps: bitrate)));
    }

    [Fact]
    public void Platforms_AcceptTheirDefaultServers()
    {
        foreach (var entry in PlatformCatalog.All.Where(e => e.DefaultServer.Length > 0))
            Assert.Null(PlatformCatalog.Validate(StreamDestination.Create(entry.Name, entry.Platform, entry.DefaultServer), new EncoderSettings(VideoBitrateKbps: 4500)));
    }

    [Fact]
    public async Task Start_OneFailingDestinationDoesNotStopTheOthers()
    {
        var yt = await AddAsync("YouTube", StreamPlatform.YouTube, "rtmp://a.rtmp.youtube.com/live2");
        var bad = await AddAsync("Caído", StreamPlatform.Custom, "rtmp://caido.example/app");
        var nokey = await AddAsync("Sin clave", StreamPlatform.Custom, "rtmp://otro.example/app", key: null);
        _engine.FailingServers.Add("rtmp://caido.example/app");

        var results = await _session.StartStreamAsync();
        Assert.Null(results.Single(r => r.Destination.Id == yt.Id).Error);
        Assert.Contains("conectar", results.Single(r => r.Destination.Id == bad.Id).Error);
        Assert.Contains("clave", results.Single(r => r.Destination.Id == nokey.Id).Error);
        Assert.True(_engine.Destinations[yt.Id].Active);

        await _session.Destinations.TickAsync(T0);
        var statuses = _session.Destinations.Statuses;
        Assert.Equal(DestinationState.Live, statuses.Single(s => s.Id == yt.Id).State);
        Assert.Equal(DestinationState.Failed, statuses.Single(s => s.Id == bad.Id).State);
        Assert.True(_session.Destinations.AnyLive);

        // Si no arranca ninguno, se informa como error.
        await _session.StopStreamAsync();
        _engine.FailingServers.Add("rtmp://a.rtmp.youtube.com/live2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.StartStreamAsync());
    }

    [Fact]
    public async Task Supervisor_ReportsConnectionLossReconnectAndFatalStop()
    {
        var d = await AddAsync("Twitch", StreamPlatform.Twitch, "rtmp://live.twitch.tv/app");
        await _session.StartDestinationAsync(d.Id);
        await _session.Destinations.TickAsync(T0);

        _engine.Destinations[d.Id] = _engine.Destinations[d.Id] with { Reconnecting = true };
        await _session.Destinations.TickAsync(T0.AddSeconds(1));
        Assert.Equal(DestinationState.Reconnecting, _session.Destinations.Statuses.Single().State);

        _engine.Destinations[d.Id] = _engine.Destinations[d.Id] with { Reconnecting = false };
        await _session.Destinations.TickAsync(T0.AddSeconds(8));
        var status = _session.Destinations.Statuses.Single();
        Assert.Equal((DestinationState.Live, 1), (status.State, status.Reconnections));
        Assert.Contains(_session.Destinations.Events, e => e.Message.Contains("Reconectado tras 7 s"));

        // El servidor rechaza la clave: libobs para la salida con código -3.
        _engine.Destinations[d.Id] = _engine.Destinations[d.Id] with { Active = false, LastStop = new OutputStopInfo(-3, "") };
        await _session.Destinations.TickAsync(T0.AddSeconds(9));
        status = _session.Destinations.Statuses.Single();
        Assert.Equal(DestinationState.Failed, status.State);
        Assert.Contains("clave", status.Error);
        Assert.False(_session.Destinations.AnyLive);
    }

    [Fact]
    public async Task Supervisor_MeasuresRealBitrateFromBytes()
    {
        var d = await AddAsync("YouTube", StreamPlatform.YouTube, "rtmp://a.rtmp.youtube.com/live2");
        await _session.StartDestinationAsync(d.Id);
        _engine.Destinations[d.Id] = _engine.Destinations[d.Id] with { TotalBytes = 1_000_000 };
        await _session.Destinations.TickAsync(T0);
        _engine.Destinations[d.Id] = _engine.Destinations[d.Id] with { TotalBytes = 1_000_000 + 750_000 };
        await _session.Destinations.TickAsync(T0.AddSeconds(1));
        Assert.Equal(6000, _session.Destinations.Statuses.Single().MeasuredKbps);
    }

    [Fact]
    public async Task Commands_AddEditRemove_WithUndo_AndNoRemovalWhileLive()
    {
        var d = await AddAsync("Kick", StreamPlatform.Kick, "rtmps://ingest.example/app");
        Assert.False((await _session.Executor.ExecuteAsync(new AddDestinationCommand(StreamDestination.Create("kick", StreamPlatform.Custom, "rtmp://x.example/a")))).Success);

        Assert.True((await _session.Executor.ExecuteAsync(new UpdateDestinationCommand(d with { Name = "Kick principal", Enabled = false }))).Success);
        Assert.Equal("Kick principal", _session.Project.Destinations.Single().Name);
        Assert.True((await _session.Executor.UndoAsync()).Success);
        Assert.Equal("Kick", _session.Project.Destinations.Single().Name);

        await _session.StartDestinationAsync(d.Id);
        await _session.Destinations.TickAsync(T0);
        Assert.False((await _session.Executor.ExecuteAsync(new RemoveDestinationCommand(d.Id, _session.Destinations.IsLive))).Success);
        await _session.StopDestinationAsync(d.Id);
        await _session.Destinations.TickAsync(T0.AddSeconds(1));
        Assert.True((await _session.Executor.ExecuteAsync(new RemoveDestinationCommand(d.Id, _session.Destinations.IsLive))).Success);
        Assert.Empty(_session.Project.Destinations);
        Assert.True((await _session.Executor.UndoAsync()).Success);
        Assert.Single(_session.Project.Destinations);
    }

    [Fact]
    public void Destinations_NeverPrintTheirCredentialValue()
    {
        var d = StreamDestination.Create("YouTube", StreamPlatform.YouTube, "rtmp://a.rtmp.youtube.com/live2");
        Assert.StartsWith("stream-key-", d.CredentialName);
        Assert.DoesNotContain("rtmp://", d.ToString());
    }
}
