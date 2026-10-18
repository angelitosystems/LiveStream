using StreamPilot.Application.Abstractions;
using StreamPilot.Domain.Entities;
using StreamPilot.Domain.ValueObjects;
using StreamPilot.Native.Engine;
using Xunit.Abstractions;

namespace StreamPilot.IntegrationTests;

/// <summary>
/// Pruebas contra libobs real. Requieren el runtime de scripts/build-native.ps1 y una sesión de escritorio
/// con GPU (D3D11). libobs es un singleton por proceso y no admite reiniciarse en el mismo proceso,
/// así que todo el ciclo va en una única prueba secuencial. El reinicio repetido se verifica ejecutando
/// esta prueba varias veces (un proceso por ejecución).
/// </summary>
public sealed class LibObsEngineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FullLifecycle_InitEnumerateSceneRecordShutdown_ThenRejectsReinit()
    {
        Assert.True(LibObsEngine.TryProbeBridge(out var probeError), probeError);

        var lines = new List<string>();
        // Copia en disco línea a línea: si libobs tumba el proceso, el log sobrevive.
        var logPath = Path.Combine(Path.GetTempPath(), "StreamPilotTests", "libobs.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, string.Empty);
        output.WriteLine($"Log de libobs: {logPath}");
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
            await RunCycleAsync();

            await using var second = new LibObsEngine();
            var ex = await Assert.ThrowsAsync<NativeEngineException>(() =>
                second.InitializeAsync(new EngineSettings(), CancellationToken.None));
            output.WriteLine($"Reinicio en el mismo proceso rechazado: {ex.Message}");
            Assert.False(second.IsInitialized);
        }
        finally
        {
            LibObsEngine.Log -= onLog;
            lock (lines)
                output.WriteLine($"Errores de libobs: {lines.Count(l => l.StartsWith("Error"))}");
        }
    }

    private async Task RunCycleAsync()
    {
        await using var engine = new LibObsEngine();
        await engine.InitializeAsync(new EngineSettings(BaseWidth: 1280, BaseHeight: 720, OutputWidth: 1280, OutputHeight: 720, FpsNumerator: 30),
            CancellationToken.None);
        Assert.True(engine.IsInitialized);

        var devices = await engine.ListDevicesAsync(CancellationToken.None);
        output.WriteLine($"Dispositivos: {devices.Count}");
        foreach (var d in devices)
            output.WriteLine($"  {d.Kind}: {d.Name}");
        Assert.Contains(devices, d => d.Kind == CaptureDeviceKind.Display);

        // SP_TEST_SOURCES=display|mic|both permite aislar fuentes al diagnosticar.
        var sources = Environment.GetEnvironmentVariable("SP_TEST_SOURCES") ?? "both";
        var sceneId = Guid.NewGuid();
        await engine.CreateSceneAsync(sceneId, "Integración", CancellationToken.None);
        if (sources is "both" or "display")
            await engine.AddSourceAsync(sceneId, new SourceDefinition(Guid.NewGuid(), "Pantalla", SourceKind.DisplayCapture,
                new Dictionary<string, string>()), CancellationToken.None);
        var mic = devices.FirstOrDefault(d => d.Kind == CaptureDeviceKind.Microphone);
        if (mic is not null && sources is "both" or "mic")
            await engine.AddSourceAsync(sceneId, new SourceDefinition(Guid.NewGuid(), "Mic", SourceKind.AudioInput,
                new Dictionary<string, string> { ["device_id"] = mic.Id }), CancellationToken.None);
        await engine.SetProgramSceneAsync(sceneId, CancellationToken.None);

        var path = Path.Combine(Path.GetTempPath(), "StreamPilotTests", $"rec-{Guid.NewGuid():N}.mkv");
        await engine.StartRecordingAsync(new RecordingSettings(path, VideoBitrateKbps: 2500), CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(5));
        var metrics = await engine.GetMetricsAsync(CancellationToken.None);
        output.WriteLine($"fps={metrics.ActiveFps:F1} render={metrics.AverageFrameRenderMs:F2}ms " +
                         $"total={metrics.TotalFrames} lag={metrics.LaggedFrames} skipped={metrics.SkippedEncoderFrames} rec={metrics.IsRecording}");
        Assert.True(metrics.IsRecording);
        await engine.StopRecordingAsync(CancellationToken.None);

        var info = new FileInfo(path);
        output.WriteLine($"Archivo {info.FullName} {info.Length} bytes");
        Assert.True(info.Exists && info.Length > 50_000, "La grabación no produjo un archivo con contenido.");
        info.Delete();

        await engine.ShutdownAsync(CancellationToken.None);
        Assert.False(engine.IsInitialized);
    }
}
