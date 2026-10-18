using StreamPilot.Application.Abstractions;
using StreamPilot.Domain.Entities;
using StreamPilot.Domain.ValueObjects;
using StreamPilot.Native.Engine;
using Xunit.Abstractions;

namespace StreamPilot.IntegrationTests;

/// <summary>
/// Pruebas contra libobs real. Requieren el runtime de scripts/build-native.ps1 y una sesión de escritorio
/// con GPU (D3D11). libobs es un singleton por proceso y no admite reiniciarse en el mismo proceso,
/// así que todo va en una única prueba secuencial. El reinicio repetido se verifica ejecutando esta prueba
/// varias veces (un proceso por ejecución).
/// </summary>
public sealed class LibObsEngineTests(ITestOutputHelper output)
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task FullStudioLifecycle_AgainstRealLibobs()
    {
        Assert.True(LibObsEngine.TryProbeBridge(out var probeError), probeError);

        var lines = new List<string>();
        // Copia en disco línea a línea: si libobs tumba el proceso, el log sobrevive.
        var logPath = Path.Combine(Path.GetTempPath(), "StreamPilotTests", "libobs.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, string.Empty);
        Action<string, string> onLog = (level, message) =>
        {
            lock (lines)
            {
                lines.Add($"{level}: {message}");
                File.AppendAllText(logPath, $"{level}: {message}{Environment.NewLine}");
            }
        };
        LibObsEngine.Log += onLog;

        try
        {
            await using (var engine = new LibObsEngine())
            {
                await engine.InitializeAsync(new EngineSettings(1280, 720, 1280, 720, 30), None);
                await DevicesAndEncodersAsync(engine);
                await SourcesItemsAndPropertiesAsync(engine);
                await AudioMetersAsync(engine);
                await RecordingAsync(engine);
                var production = new ProductionFeatures(engine, output);
                await production.RunAsync();
                await StreamFailureIsReportedAsync(engine);
                await engine.ShutdownAsync(None);
                Assert.False(engine.IsInitialized);
                production.FilesToDelete.ForEach(File.Delete);
            }

            await using var second = new LibObsEngine();
            var ex = await Assert.ThrowsAsync<NativeEngineException>(() => second.InitializeAsync(new EngineSettings(), None));
            output.WriteLine($"Reinicio en el mismo proceso rechazado: {ex.Message}");
        }
        finally
        {
            LibObsEngine.Log -= onLog;
            lock (lines)
                output.WriteLine($"Log de libobs: {logPath} ({lines.Count(l => l.StartsWith("Error"))} errores)");
        }
    }

    private async Task DevicesAndEncodersAsync(LibObsEngine engine)
    {
        var devices = await engine.ListDevicesAsync(None);
        output.WriteLine($"Dispositivos: {devices.Count}");
        Assert.Contains(devices, d => d.Kind == CaptureDeviceKind.Display);

        var encoders = await engine.ListVideoEncodersAsync(None);
        output.WriteLine("Encoders: " + string.Join(", ", encoders.Select(e => $"{e.Id} ({e.Codec})")));
        Assert.Contains(encoders, e => e.Id == "obs_x264");
    }

    private async Task SourcesItemsAndPropertiesAsync(LibObsEngine engine)
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await engine.CreateSceneAsync(a, "A", None);
        await engine.CreateSceneAsync(b, "B", None);
        await engine.SetTransitionAsync(new TransitionSettings("fade_transition", 200), None);
        await engine.SetProgramSceneAsync(a, None);

        // Todos los tipos disponibles deben crearse con el id de libobs del mapa.
        var created = new Dictionary<SourceKind, Guid>();
        foreach (var kind in Enum.GetValues<SourceKind>().Where(SourceTypeMap.IsAvailable))
        {
            var id = Guid.NewGuid();
            var settings = kind switch
            {
                SourceKind.Text => """{"text":"StreamPilot"}""",
                SourceKind.ColorSource => """{"color":4278190335,"width":320,"height":180}""",
                _ => "{}",
            };
            await engine.CreateSourceAsync(new SourceDefinition(id, kind.ToString(), kind, settings), None);
            created[kind] = id;
        }
        output.WriteLine($"Tipos de fuente creados: {string.Join(", ", created.Keys)}");

        // Una fuente compartida en dos escenas.
        var color = created[SourceKind.ColorSource];
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        await engine.AddItemAsync(a, itemA, color, None);
        await engine.AddItemAsync(b, itemB, color, None);

        var transform = new Transform(100, 50, 2, 1.5, 30, 10, 5, 0, 20);
        await engine.SetItemTransformAsync(itemA, transform, None);
        await engine.SetItemVisibleAsync(itemB, false, None);
        await engine.SetItemLockedAsync(itemA, true, None);
        await engine.SetItemOrderAsync(itemA, 0, None);

        var size = await engine.GetSourceSizeAsync(color, None);
        Assert.Equal((320, 180), size);

        var props = await engine.GetSourcePropertiesAsync(created[SourceKind.Text], None);
        output.WriteLine("Propiedades de texto: " + string.Join(", ", props.Select(p => $"{p.Name}:{p.Type}")));
        Assert.Contains(props, p => p.Name == "text" && p.Type == SourcePropertyType.Text);
        Assert.Contains(props, p => p.Type == SourcePropertyType.Font);

        await engine.UpdateSourceSettingsAsync(created[SourceKind.Text], """{"text":"Actualizado"}""", None);
        Assert.Contains("Actualizado", await engine.GetSourceSettingsAsync(created[SourceKind.Text], None));

        await engine.RenameSceneAsync(b, "B2", None);
        await engine.RenameSourceAsync(color, "Color renombrado", None);

        // Cambio de escena con transición de fundido y vuelta.
        await engine.SetProgramSceneAsync(b, None);
        await Task.Delay(400);
        await engine.SetProgramSceneAsync(a, None);

        // La escena de programa no se puede eliminar; otra sí, y con ella sus elementos.
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.RemoveSceneAsync(a, None));
        await engine.RemoveSceneAsync(b, None);
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.SetItemVisibleAsync(itemB, true, None));

        // Eliminar una fuente quita también sus elementos.
        await engine.RemoveSourceAsync(color, None);
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.SetItemTransformAsync(itemA, transform, None));

        foreach (var (kind, id) in created.Where(c => c.Key != SourceKind.ColorSource))
            await engine.RemoveSourceAsync(id, None);
    }

    private async Task AudioMetersAsync(LibObsEngine engine)
    {
        var desktop = Guid.NewGuid();
        await engine.CreateSourceAsync(new SourceDefinition(desktop, "Escritorio", SourceKind.AudioOutput, """{"device_id":"default"}"""), None);
        await engine.SetGlobalAudioChannelAsync(desktop, 1, None);
        await engine.SetVolumeAsync(desktop, 0.5, None);
        await engine.SetMutedAsync(desktop, false, None);
        await engine.SetMonitoringAsync(desktop, AudioMonitoring.None, None);
        await engine.SetSyncOffsetAsync(desktop, 100, None);

        await Task.Delay(500);
        var levels = await engine.GetAudioLevelsAsync(desktop, None);
        output.WriteLine($"Niveles escritorio: {levels}");
        Assert.NotNull(levels);
        Assert.InRange(levels!.PeakLeftDb, -100, 0.1);

        // Una fuente de video no tiene medidor.
        var image = Guid.NewGuid();
        await engine.CreateSourceAsync(new SourceDefinition(image, "Imagen", SourceKind.Image, "{}"), None);
        Assert.Null(await engine.GetAudioLevelsAsync(image, None));
        await engine.RemoveSourceAsync(image, None);
    }

    private async Task RecordingAsync(LibObsEngine engine)
    {
        var scene = Guid.NewGuid();
        var display = Guid.NewGuid();
        var item = Guid.NewGuid();
        await engine.CreateSceneAsync(scene, "Grabación", None);
        await engine.CreateSourceAsync(new SourceDefinition(display, "Pantalla", SourceKind.DisplayCapture, "{}"), None);
        await engine.AddItemAsync(scene, item, display, None);
        await engine.SetProgramSceneAsync(scene, None);

        var path = Path.Combine(Path.GetTempPath(), "StreamPilotTests", $"rec-{Guid.NewGuid():N}.mkv");
        await engine.StartRecordingAsync(new RecordingSettings(path, new EncoderSettings("obs_x264", 2500)), None);
        await Task.Delay(TimeSpan.FromSeconds(4));
        var m = await engine.GetMetricsAsync(None);
        output.WriteLine($"fps={m.ActiveFps:F1} cpu={m.CpuUsagePercent:F1}% mem={m.MemoryBytes / 1048576}MB lag={m.LaggedFrames}/{m.TotalFrames} " +
                         $"skipped={m.SkippedEncoderFrames} recBytes={m.RecordingTotalBytes}");
        Assert.True(m.IsRecording);
        Assert.True(m.RecordingTotalBytes > 0);
        await engine.StopRecordingAsync(None);

        var info = new FileInfo(path);
        Assert.True(info.Exists && info.Length > 50_000, "La grabación no produjo un archivo con contenido.");
        info.Delete();
        Assert.Equal(0, (await engine.GetLastStopAsync(OutputKind.Recording, None)).Code);
    }

    private async Task StreamFailureIsReportedAsync(LibObsEngine engine)
    {
        // Nada escucha en este puerto: la conexión falla de forma asíncrona y debe reportarse con código.
        await Assert.ThrowsAsync<NativeEngineException>(() =>
            engine.StartStreamAsync(new StreamTarget("http://no-rtmp", "x", new EncoderSettings("obs_x264", 1000)), None));

        await engine.StartStreamAsync(new StreamTarget("rtmp://127.0.0.1:1/live", "clave-de-prueba", new EncoderSettings("obs_x264", 1000)), None);
        OutputStopInfo stop = new(0, "");
        for (var i = 0; i < 100 && !stop.IsError; i++)
        {
            await Task.Delay(100);
            stop = await engine.GetLastStopAsync(OutputKind.Stream, None);
        }
        output.WriteLine($"Fallo de transmisión reportado: código {stop.Code} '{stop.Message}'");
        Assert.True(stop.IsError);
        Assert.False((await engine.GetMetricsAsync(None)).IsStreaming);

        lock (this)
        {
            var log = File.ReadAllText(Path.Combine(Path.GetTempPath(), "StreamPilotTests", "libobs.log"));
            Assert.DoesNotContain("clave-de-prueba", log); // la clave nunca llega al log
        }
    }
}
