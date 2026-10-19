using System.Diagnostics;
using System.Text.Json;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Graphics;
using LiveStream.Application.Scenes;
using LiveStream.Application.Sources;
using LiveStream.Application.Streaming;
using LiveStream.Application.Studio;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using LiveStream.Domain.ValueObjects;
using LiveStream.Infrastructure.Persistence;
using LiveStream.Infrastructure.Security;
using LiveStream.Native.Engine;

// Prueba de resistencia: el estudio real durante N minutos con captura de pantalla, texto, gráficos animados,
// grabación y emisión (a un servidor RTMP local con ffmpeg si está instalado), cambiando de escena, editando y
// deshaciendo sin parar. Mide memoria, handles, render, codificación y pérdidas, y falla si algo se degrada.
//
//   dotnet run --project tools/LiveStream.Soak -c Release -- --minutes 10 --report soak.json

var minutes = ArgValue("--minutes") is { } m ? double.Parse(m, System.Globalization.CultureInfo.InvariantCulture) : 10;
var reportPath = ArgValue("--report") ?? Path.Combine(Path.GetTempPath(), "LiveStreamSoak", "soak-report.json");
var work = Directory.CreateTempSubdirectory("livestream-soak-").FullName;
Console.WriteLine($"Soak de {minutes} min. Carpeta temporal: {work}");

Process? rtmp = StartLocalRtmp();
await using var engine = new LibObsEngine();
var project = StudioSession.CreateDefaultProject("Soak", new CanvasSettings(1920, 1080, 30));
var session = new StudioSession(engine, new JsonProjectRepository(work), new DpapiSecretStore(Path.Combine(work, "secrets.dat")),
    project, Path.Combine(work, "soak.spproj"), TimeSpan.FromSeconds(5));
await session.StartEngineAsync();

// Escenas y fuentes reales.
var a = project.Scenes[0].Id;
await Run(new CreateSceneCommand("B"));
var b = project.Scenes.Single(s => s.Name == "B").Id;
var display = new AddSourceCommand(a, "Pantalla", SourceKind.DisplayCapture, "{}");
await Run(display);
var text = new AddSourceCommand(b, "Texto", SourceKind.Text, """{"text":"Soak"}""", new Transform(200, 200, 1, 1, 0));
await Run(text);
await Run(new AddSourceCommand(b, "Fondo", SourceKind.ColorSource, """{"width":1920,"height":1080,"color":4282664004}"""));
await Run(new AddExistingSourceCommand(b, display.SourceId!.Value, new Transform(960, 540, 0.4, 0.4, 0)));
var lower = GraphicTemplates.Create(GraphicKind.LowerThird, "Rótulo", project.Canvas);
var ticker = GraphicTemplates.Create(GraphicKind.Ticker, "Cintillo", project.Canvas);
var clock = GraphicTemplates.Create(GraphicKind.Clock, "Reloj", project.Canvas);
foreach (var g in new[] { lower, ticker, clock })
    await Run(new AddGraphicCommand(g));
await session.Graphics.TakeInAsync(ticker.Id);
await session.Graphics.TakeInAsync(clock.Id);
await Run(new SetTransitionCommand(new TransitionSettings("fade_transition", 300)));

// Salidas: grabación a bajo bitrate y emisión local.
await engine.StartRecordingAsync(new RecordingSettings(Path.Combine(work, "soak.mkv"), new EncoderSettings("obs_x264", 1500)), CancellationToken.None);
if (rtmp is not null)
{
    var dest = StreamDestination.Create("Local", StreamPlatform.Custom, "rtmp://127.0.0.1:19390/live", new EncoderSettings("obs_x264", 2500));
    await Run(new AddDestinationCommand(dest));
    await session.SetDestinationKeyAsync(dest.Id, "soak");
    await session.StartStreamAsync();
}

var process = Process.GetCurrentProcess();
var samples = new List<Sample>();
var errors = new List<string>();
var watch = Stopwatch.StartNew();
var end = TimeSpan.FromMinutes(minutes);
var tick = 0;
var start = await engine.GetMetricsAsync(CancellationToken.None);

while (watch.Elapsed < end)
{
    await Task.Delay(1000);
    tick++;
    try
    {
        if (tick % 5 == 0)
            await Run(new ActivateSceneCommand(tick % 10 == 0 ? a : b));
        if (tick % 7 == 0)
            await session.Graphics.ToggleAsync(lower.Id);
        if (tick % 11 == 0)
            await Run(new UpdateSourceSettingsCommand(text.SourceId!.Value, $$"""{"text":"Soak {{tick}}"}"""));
        if (tick % 13 == 0)
            await Run(new SetItemTransformCommand(text.ItemId!.Value, new Transform(100 + tick % 800, 200, 1, 1, tick % 30)));
        if (tick % 30 == 0)
        {
            await session.Executor.UndoAsync();
            await session.Executor.RedoAsync();
        }
        await session.Destinations.TickAsync(DateTime.UtcNow);
    }
    catch (Exception ex)
    {
        errors.Add($"{watch.Elapsed:mm\\:ss} {ex.GetType().Name}: {ex.Message}");
    }

    process.Refresh();
    var metrics = await engine.GetMetricsAsync(CancellationToken.None);
    samples.Add(new Sample(watch.Elapsed.TotalSeconds, process.PrivateMemorySize64 / 1048576.0, process.HandleCount,
        GC.GetTotalMemory(false) / 1048576.0, metrics.ActiveFps, metrics.LaggedFrames - start.LaggedFrames, metrics.TotalFrames - start.TotalFrames,
        metrics.SkippedEncoderFrames - start.SkippedEncoderFrames, metrics.EncodedFrames - start.EncodedFrames,
        metrics.StreamDroppedFrames, metrics.StreamTotalFrames, metrics.IsRecording, metrics.IsStreaming));
    if (tick % 60 == 0)
    {
        var s = samples[^1];
        Console.WriteLine($"{watch.Elapsed:mm\\:ss}  memoria {s.PrivateMb:0} MB  handles {s.Handles}  fps {s.Fps:0.0}  " +
                          $"render tarde {s.Lagged}/{s.Rendered}  descartes {s.Skipped}  emisión perdidos {s.Dropped}/{s.StreamFrames}");
    }
}

// Cierre ordenado: también forma parte de la prueba.
if (rtmp is not null)
    await session.StopStreamAsync();
await engine.StopRecordingAsync(CancellationToken.None);
await session.DisposeAsync();
await engine.ShutdownAsync(CancellationToken.None);
rtmp?.Kill(entireProcessTree: true);

// Análisis: tras un minuto de calentamiento, la memoria y los handles no deben crecer de forma sostenida.
var steady = samples.Where(s => s.Seconds >= Math.Min(60, end.TotalSeconds / 3)).ToList();
var memorySlope = Slope(steady, s => s.PrivateMb) * 60;   // MB por minuto
var handleSlope = Slope(steady, s => s.Handles) * 60;
var last = samples[^1];
var lagPct = last.Rendered == 0 ? 0 : 100.0 * last.Lagged / last.Rendered;
var skipPct = last.Encoded + last.Skipped == 0 ? 0 : 100.0 * last.Skipped / (last.Encoded + last.Skipped);
var dropPct = last.StreamFrames + last.Dropped == 0 ? 0 : 100.0 * last.Dropped / (last.StreamFrames + last.Dropped);
var checks = new Dictionary<string, (bool Ok, string Detail)>
{
    ["sin errores"] = (errors.Count == 0, $"{errors.Count} errores"),
    ["memoria estable"] = (memorySlope < 2.0, $"{memorySlope:+0.00;-0.00} MB/min (límite +2)"),
    ["handles estables"] = (handleSlope < 5, $"{handleSlope:+0.0;-0.0} handles/min (límite +5)"),
    ["render a tiempo"] = (lagPct < 1, $"{lagPct:0.00}% de fotogramas tarde (límite 1%)"),
    ["codificación al día"] = (skipPct < 1, $"{skipPct:0.00}% descartados (límite 1%)"),
    ["emisión sin pérdidas"] = (rtmp is null || dropPct < 0.5, rtmp is null ? "sin ffmpeg: no se emitió" : $"{dropPct:0.00}% perdidos (límite 0,5%)"),
    ["grabación activa todo el tiempo"] = (samples.All(s => s.Recording), samples.All(s => s.Recording) ? "sí" : "se detuvo"),
};

Console.WriteLine();
foreach (var (name, (ok, detail)) in checks)
    Console.WriteLine($"{(ok ? "OK   " : "FALLO")} {name}: {detail}");
foreach (var e in errors.Take(20))
    Console.WriteLine($"  error: {e}");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(new
{
    minutes,
    checks = checks.ToDictionary(c => c.Key, c => new { c.Value.Ok, c.Value.Detail }),
    errors,
    samples,
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Informe: {reportPath}");
try
{
    Directory.Delete(work, recursive: true);
}
catch (IOException)
{
    Console.WriteLine($"No se pudo borrar {work} (algún archivo sigue abierto).");
}
return checks.Values.All(c => c.Ok) ? 0 : 1;

async Task<bool> Run(LiveStream.Application.Commands.IStudioCommand command)
{
    var result = await session.Executor.ExecuteAsync(command);
    if (!result.Success)
        throw new InvalidOperationException($"{command.Description}: {result.Error}");
    return true;
}

static string? ArgValue(string name)
{
    var args = Environment.GetCommandLineArgs();
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static Process? StartLocalRtmp()
{
    var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(d => Path.Combine(d.Trim(), "ffmpeg.exe")).FirstOrDefault(File.Exists);
    if (ffmpeg is null)
    {
        Console.WriteLine("ffmpeg no encontrado: el soak no incluirá emisión.");
        return null;
    }
    var p = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -listen 1 -i rtmp://127.0.0.1:19390/live -f null -")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
    });
    Thread.Sleep(1000);
    return p;
}

static double Slope(IReadOnlyList<Sample> s, Func<Sample, double> y)
{
    if (s.Count < 2)
        return 0;
    var mx = s.Average(p => p.Seconds);
    var my = s.Average(y);
    var num = s.Sum(p => (p.Seconds - mx) * (y(p) - my));
    var den = s.Sum(p => (p.Seconds - mx) * (p.Seconds - mx));
    return den == 0 ? 0 : num / den;
}

internal sealed record Sample(double Seconds, double PrivateMb, int Handles, double ManagedMb, double Fps, uint Lagged, uint Rendered,
    uint Skipped, uint Encoded, uint Dropped, uint StreamFrames, bool Recording, bool Streaming);
