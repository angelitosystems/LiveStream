using StreamPilot.Application.Abstractions;
using StreamPilot.Application.Optimization;
using StreamPilot.Application.Studio;
using StreamPilot.Domain.Entities;
using StreamPilot.Infrastructure.Persistence;
using StreamPilot.Infrastructure.Security;

namespace StreamPilot.UnitTests;

/// <summary>Fase 5: controlador de bitrate (determinista), recomendador de perfiles y optimizador con auditoría.</summary>
public sealed class OptimizationTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 20, 0, 0, DateTimeKind.Utc);

    /// <summary>Simula una emisión a 60 fps: devuelve las muestras acumuladas segundo a segundo.</summary>
    private static IEnumerable<StreamSample> Series(Func<int, (double DropRate, double Congestion)> network, int seconds, Func<int> bitrate)
    {
        uint dropped = 0, total = 0;
        for (var s = 0; s <= seconds; s++)
        {
            var (drop, congestion) = network(s);
            var lost = (uint)Math.Round(60 * drop);
            dropped += lost;
            total += 60 - lost;
            yield return new StreamSample(T0.AddSeconds(s), dropped, total, congestion, false, bitrate());
        }
    }

    private static List<BitrateDecision> Run(AdaptiveBitrateController c, Func<int, (double, double)> network, int seconds, ref int bitrate)
    {
        var decisions = new List<BitrateDecision>();
        var current = bitrate;
        foreach (var sample in Series(network, seconds, () => current))
        {
            if (c.Observe(sample) is { } d)
            {
                decisions.Add(d);
                current = d.ToKbps;
            }
        }
        bitrate = current;
        return decisions;
    }

    [Fact]
    public void Controller_DoesNothingOnAHealthyConnection()
    {
        var c = new AdaptiveBitrateController(6000);
        var bitrate = 6000;
        Assert.Empty(Run(c, _ => (0, 0.05), 300, ref bitrate));
    }

    [Fact]
    public void Controller_DropsFastOnLoss_RespectsFloor_ThenRecoversSlowlyToTarget()
    {
        var c = new AdaptiveBitrateController(6000);
        var bitrate = 6000;
        var down = Run(c, _ => (0.10, 0.8), 120, ref bitrate);
        Assert.NotEmpty(down);
        Assert.All(down, d => Assert.True(d.ToKbps < d.FromKbps));
        Assert.Equal(c.FloorKbps, bitrate); // nunca por debajo del suelo
        Assert.Equal(1800, c.FloorKbps);
        // Cada bajada espera al menos 5 s desde la anterior (histéresis temporal).
        Assert.True(down.Count <= 120 / 5);

        var up = Run(c, _ => (0, 0.02), 1200, ref bitrate);
        Assert.NotEmpty(up);
        Assert.All(up, d => Assert.True(d.ToKbps > d.FromKbps));
        Assert.Equal(6000, bitrate); // vuelve al objetivo y no lo supera
        Assert.True(up.Count >= down.Count, "La subida debe ser más gradual que la bajada.");
    }

    [Fact]
    public void Controller_IgnoresShortSpikes_AndIntermediateNoise()
    {
        var c = new AdaptiveBitrateController(6000);
        var bitrate = 6000;
        // Un pico de 1 s cada 20 s: la media móvil y las muestras consecutivas lo filtran.
        Assert.Empty(Run(c, s => s % 20 == 0 ? (0.05, 0.6) : (0, 0.05), 300, ref bitrate));
        // Zona intermedia (ni sana ni saturada): no se sube ni se baja.
        Assert.Empty(Run(c, _ => (0.008, 0.3), 300, ref bitrate));
    }

    [Fact]
    public void Controller_FreezesWhileReconnecting()
    {
        var c = new AdaptiveBitrateController(6000);
        uint total = 0;
        for (var s = 0; s < 60; s++)
        {
            total += 30;
            Assert.Null(c.Observe(new StreamSample(T0.AddSeconds(s), total, total, 1, Reconnecting: true, 6000)));
        }
    }

    [Theory]
    [InlineData(1280, 720, 30, 3000)]
    [InlineData(1920, 1080, 30, 4500)]
    [InlineData(1920, 1080, 60, 6000)]
    [InlineData(1080, 1920, 60, 6000)]
    [InlineData(3840, 2160, 30, 12000)]
    public void Advisor_BitrateTable(int w, int h, int fps, int expected) =>
        Assert.Equal(expected, ProfileAdvisor.StreamBitrateFor(new CanvasSettings(w, h, fps)));

    private static HardwareInfo Laptop(int renderAdapter = 0) => new("Intel Core i7", 20, 16,
        [new GraphicsAdapter(0, "Intel UHD", 0x8086, 128L << 20, false), new GraphicsAdapter(1, "RTX 4050", 0x10DE, 6L << 30, false)],
        [new EncoderCapability("obs_x264", "x264", "h264", false, "CPU", true), new EncoderCapability("obs_nvenc_h264_tex", "NVENC", "h264", true, "NVIDIA", true)],
        renderAdapter);

    [Fact]
    public void Advisor_PrefersAHealthyHardwareEncoder_AndSuggestsItsGpuForRendering()
    {
        var project = new StudioProject(Guid.NewGuid(), "P") { Canvas = new CanvasSettings(1920, 1080, 60) };
        var plan = ProfileAdvisor.Advise(Laptop(), [
            new BenchmarkResult("obs_x264", "x264", false, 600, 0.2, 0, 45),
            new BenchmarkResult("obs_nvenc_h264_tex", "NVENC", true, 600, 0, 0, 8),
        ], project);

        Assert.Equal("obs_nvenc_h264_tex", plan.Stream!.VideoEncoderId);
        Assert.Equal(6000, plan.Stream.VideoBitrateKbps);
        Assert.Equal(15000, plan.Recording!.VideoBitrateKbps);
        Assert.Equal(1, plan.RenderAdapterIndex);
        Assert.Contains(plan.Items, i => i.Id == "adapter" && i.Impact == RecommendationImpact.Restart);
        Assert.Null(plan.Canvas);

        // Ya en la GPU dedicada: no se vuelve a proponer.
        Assert.Null(ProfileAdvisor.Advise(Laptop(renderAdapter: 1), [new BenchmarkResult("obs_nvenc_h264_tex", "NVENC", true, 600, 0, 0, 8)], project).RenderAdapterIndex);
    }

    [Fact]
    public void Advisor_WhenEverythingStruggles_ProposesALighterCanvasInsteadOfAnEncoder()
    {
        var project = new StudioProject(Guid.NewGuid(), "P") { Canvas = new CanvasSettings(1920, 1080, 60) };
        var plan = ProfileAdvisor.Advise(Laptop(), [
            new BenchmarkResult("obs_x264", "x264", false, 400, 12, 5, 98),
            new BenchmarkResult("obs_nvenc_h264_tex", "NVENC", true, 0, 0, 0, 0, "No se pudo crear el codificador"),
        ], project);
        Assert.Null(plan.Stream);
        Assert.Equal(new CanvasSettings(1920, 1080, 30), plan.Canvas);
        Assert.Equal(new CanvasSettings(1280, 720, 30), ProfileAdvisor.Lighter(plan.Canvas!));
        Assert.Null(ProfileAdvisor.Lighter(new CanvasSettings(1280, 720, 30)));
    }

    /// <summary>Sesión con destinos ya emitiendo en el motor falso.</summary>
    private static async Task<(StudioSession Session, FakeStreamingEngine Engine, string Dir, StreamDestination[] Destinations)> LiveAsync(
        bool dynamic, params EncoderSettings?[] encoders)
    {
        var dir = Directory.CreateTempSubdirectory("sp-opt-").FullName;
        var engine = new FakeStreamingEngine { DynamicBitrate = dynamic };
        var session = new StudioSession(engine, new JsonProjectRepository(dir), new DpapiSecretStore(Path.Combine(dir, "s")),
            StudioSession.CreateDefaultProject(), Path.Combine(dir, "p.spproj"), TimeSpan.FromHours(1));
        var destinations = encoders.Select((e, i) => StreamDestination.Create($"D{i}", StreamPlatform.Custom, $"rtmp://srv{i}.example/app", e)).ToArray();
        foreach (var d in destinations)
        {
            session.Project.InsertDestination(d);
            await session.SetDestinationKeyAsync(d.Id, "k");
        }
        await session.StartStreamAsync();
        return (session, engine, dir, destinations);
    }

    /// <summary>Un segundo de red: suma pérdidas y fotogramas a los destinos indicados.</summary>
    private static void Network(FakeStreamingEngine engine, IEnumerable<StreamDestination> destinations, uint lost, double congestion)
    {
        foreach (var d in destinations)
        {
            var s = engine.Destinations[d.Id];
            engine.Destinations[d.Id] = s with { DroppedFrames = s.DroppedFrames + lost, TotalFrames = s.TotalFrames + 60 - lost, Congestion = congestion };
        }
    }

    private static EngineMetrics Metrics(int s) => new(60, 5, (uint)(s * 60), 0, 0, false, IsStreaming: true, EncodedFrames: (uint)(s * 60));

    [Fact]
    public async Task Optimizer_AdjustsLiveBitrate_LogsWhy_AndRestores()
    {
        var (session, engine, dir, destinations) = await LiveAsync(dynamic: true, (EncoderSettings?)null);
        await using var _ = session;
        var optimizer = new PerformanceOptimizer(session);
        for (var s = 0; s < 40; s++)
        {
            Network(engine, destinations, 6, 0.7);
            await session.Destinations.TickAsync(T0.AddSeconds(s));
            await optimizer.TickAsync(Metrics(s), T0.AddSeconds(s), session.Destinations.Statuses);
        }
        Assert.NotEmpty(engine.BitrateChanges);
        Assert.Contains(optimizer.Events, e => e.Kind == OptimizationEventKind.Adjusted && e.Detail.Contains("Pérdidas"));
        Assert.True(optimizer.Status.CurrentKbps < 6000);
        Assert.Contains("objetivo 6000", optimizer.Report());

        await optimizer.RestoreTargetAsync(session.Destinations.Statuses);
        Assert.Equal(6000, engine.BitrateChanges[^1]);
        Assert.False(optimizer.AdaptiveEnabled);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Optimizer_DecidesOncePerSharedEncoder_AndSeparatelyForOwnEncoders()
    {
        // D0 y D1 comparten el codificador común; D2 tiene uno propio. Solo D2 sufre pérdidas.
        var (session, engine, dir, d) = await LiveAsync(dynamic: true, null, null, new EncoderSettings("obs_x264", 3000));
        await using var _ = session;
        var optimizer = new PerformanceOptimizer(session);
        for (var s = 0; s < 40; s++)
        {
            Network(engine, d[..2], 0, 0.02);
            Network(engine, [d[2]], 6, 0.7);
            await session.Destinations.TickAsync(T0.AddSeconds(s));
            await optimizer.TickAsync(Metrics(s), T0.AddSeconds(s), session.Destinations.Statuses);
        }
        Assert.Equal(6000, engine.Destinations[d[0].Id].VideoBitrateKbps);
        Assert.Equal(6000, engine.Destinations[d[1].Id].VideoBitrateKbps);
        Assert.True(engine.Destinations[d[2].Id].VideoBitrateKbps < 3000);
        Assert.All(optimizer.Events.Where(e => e.Kind == OptimizationEventKind.Adjusted), e => Assert.Contains("D2", e.Title));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Optimizer_WithoutDynamicBitrateEncoder_NeverTouchesTheStream()
    {
        var (session, engine, dir, destinations) = await LiveAsync(dynamic: false, (EncoderSettings?)null);
        await using var _ = session;
        var optimizer = new PerformanceOptimizer(session);
        for (var s = 0; s < 30; s++)
        {
            Network(engine, destinations, 10, 0.9);
            await session.Destinations.TickAsync(T0.AddSeconds(s));
            await optimizer.TickAsync(Metrics(s), T0.AddSeconds(s), session.Destinations.Statuses);
        }
        Assert.Empty(engine.BitrateChanges);
        Assert.Contains("no admite", optimizer.Status.Note);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Optimizer_WarnsAboutEncoderOverload_OnlyOncePerWindow()
    {
        var dir = Directory.CreateTempSubdirectory("sp-opt-").FullName;
        await using var session = new StudioSession(new FakeStreamingEngine(), new JsonProjectRepository(dir), new DpapiSecretStore(Path.Combine(dir, "s")),
            StudioSession.CreateDefaultProject(), Path.Combine(dir, "p.spproj"), TimeSpan.FromHours(1));
        var optimizer = new PerformanceOptimizer(session);
        for (var s = 0; s < 120; s++)
            await optimizer.TickAsync(new EngineMetrics(60, 5, (uint)(s * 60), 0, (uint)(s * 6), true, EncodedFrames: (uint)(s * 54)), T0.AddSeconds(s), []);
        Assert.Single(optimizer.Events, e => e.Title == "El codificador no da abasto");
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Optimizer_AppliedProfile_CanBeReverted()
    {
        var dir = Directory.CreateTempSubdirectory("sp-opt-").FullName;
        await using var session = new StudioSession(new FakeStreamingEngine(), new JsonProjectRepository(dir), new DpapiSecretStore(Path.Combine(dir, "s")),
            StudioSession.CreateDefaultProject(), Path.Combine(dir, "p.spproj"), TimeSpan.FromHours(1));
        var optimizer = new PerformanceOptimizer(session);
        var before = session.Project.Stream.EffectiveEncoder;
        var plan = new OptimizationPlan([], new EncoderSettings("obs_nvenc_h264_tex", 6000), new EncoderSettings("obs_nvenc_h264_tex", 15000), null, null);

        Assert.True(await optimizer.ApplyOutputsAsync(plan));
        Assert.Equal("obs_nvenc_h264_tex", session.Project.Stream.EffectiveEncoder.VideoEncoderId);
        var applied = optimizer.Events.Single(e => e.Kind == OptimizationEventKind.Applied);
        Assert.True(optimizer.CanRevert(applied.RevertId!.Value));

        Assert.True(await optimizer.RevertAsync(applied.RevertId!.Value));
        Assert.Equal(before, session.Project.Stream.EffectiveEncoder);
        Assert.False(optimizer.CanRevert(applied.RevertId!.Value));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void MachineSettings_SurviveACorruptFile()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("sp-m-").FullName, "machine.json");
        var store = new MachineSettingsStore(path);
        Assert.Equal(new MachineSettings(), store.Load());
        store.Save(new MachineSettings(1, false));
        Assert.Equal(new MachineSettings(1, false), store.Load());
        File.WriteAllText(path, "{roto");
        Assert.Equal(new MachineSettings(), store.Load());
    }
}
