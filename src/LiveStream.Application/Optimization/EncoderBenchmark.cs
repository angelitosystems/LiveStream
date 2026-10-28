using System.Diagnostics;
using System.Text.Json;
using LiveStream.Application.Abstractions;
using LiveStream.Domain.Entities;
using LiveStream.Domain.ValueObjects;

namespace LiveStream.Application.Optimization;

/// <summary>Resultado de codificar el programa real durante unos segundos con un codificador.</summary>
public sealed record BenchmarkResult(
    string EncoderId,
    string EncoderName,
    bool Hardware,
    uint EncodedFrames,
    double SkippedPercent,
    double LaggedPercent,
    double CpuPercent,
    string? Error = null)
{
    public bool Ok => Error is null && EncodedFrames > 0;

    /// <summary>Sin fotogramas descartados por el codificador ni retraso de render apreciable.</summary>
    public bool Healthy => Ok && SkippedPercent < 1 && LaggedPercent < 2;
}

/// <summary>
/// Benchmark real: graba el programa actual con cada codificador candidato a un archivo temporal y mide
/// fotogramas descartados por el codificador, retraso de render y CPU.
/// Somete al motor y codificador a un patrón visual de estrés (ruido entrópico, gráficos y movimiento fluido en el lienzo)
/// visible en tiempo real en la pantalla. Requiere que no se esté grabando ni emitiendo previamente.
/// </summary>
public static class EncoderBenchmark
{
    /// <summary>Códecs aptos para emitir por RTMP clásico.</summary>
    public static IEnumerable<EncoderCapability> Candidates(IEnumerable<EncoderCapability> encoders) =>
        encoders.Where(e => e.Codec == "h264").OrderBy(e => e.Hardware ? 0 : 1);

    public static async Task<IReadOnlyList<BenchmarkResult>> RunAsync(
        IStreamingEngine engine,
        IEnumerable<EncoderCapability> candidates,
        int bitrateKbps,
        TimeSpan duration,
        string tempDirectory,
        Action<string>? progress,
        IReadOnlyList<Guid>? targetSceneIds = null,
        int canvasWidth = 1920,
        int canvasHeight = 1080,
        CancellationToken ct = default)
    {
        var start = await engine.GetMetricsAsync(ct).ConfigureAwait(false);
        if (start.IsRecording || start.IsStreaming)
            throw new InvalidOperationException("Detén la grabación y la emisión antes de medir: el benchmark usa el codificador.");

        Directory.CreateDirectory(tempDirectory);

        // Genera la carta de ajuste con ruido pseudoaleatorio, rejilla y barras SMPTE para estrés del códec
        var stressBmpPath = Path.Combine(tempDirectory, "benchmark_stress_chart.bmp");
        try
        {
            BenchmarkStressPattern.SaveToFile(stressBmpPath, 960, 540);
        }
        catch
        {
            stressBmpPath = null;
        }

        var results = new List<BenchmarkResult>();
        try
        {
            foreach (var encoder in candidates)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke($"Iniciando prueba de estrés: {encoder.Name}…");
                var path = Path.Combine(tempDirectory, $"benchmark-{Guid.NewGuid():N}.mkv");
                try
                {
                    results.Add(await MeasureAsync(
                        engine,
                        encoder,
                        bitrateKbps,
                        duration,
                        path,
                        stressBmpPath,
                        targetSceneIds,
                        canvasWidth,
                        canvasHeight,
                        progress,
                        ct).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results.Add(new BenchmarkResult(encoder.Id, encoder.Name, encoder.Hardware, 0, 0, 0, 0, ex.Message));
                }
                finally
                {
                    TryDelete(path);
                }
            }
        }
        finally
        {
            if (stressBmpPath != null)
                TryDelete(stressBmpPath);
        }

        return results;
    }

    private static async Task<BenchmarkResult> MeasureAsync(
        IStreamingEngine engine,
        EncoderCapability encoder,
        int bitrateKbps,
        TimeSpan duration,
        string path,
        string? stressBmpPath,
        IReadOnlyList<Guid>? targetSceneIds,
        int canvasWidth,
        int canvasHeight,
        Action<string>? progress,
        CancellationToken ct)
    {
        var stressSourceId = Guid.NewGuid();
        var sceneItems = new List<(Guid SceneId, Guid ItemId)>();
        var sourceCreated = false;

        // 1. Si existe la carta de ajuste de estrés, crea la fuente y la añade a las escenas visibles
        if (!string.IsNullOrEmpty(stressBmpPath) && File.Exists(stressBmpPath))
        {
            try
            {
                var imageSettings = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["file"] = stressBmpPath,
                    ["unload_when_not_showing"] = false,
                });

                await engine.CreateSourceAsync(
                    new SourceDefinition(stressSourceId, "Benchmark_Stress_Pattern", SourceKind.Image, imageSettings),
                    ct).ConfigureAwait(false);
                sourceCreated = true;

                // Añade la fuente a todas las escenas objetivo para que sea visible en el lienzo y en programa
                if (targetSceneIds != null)
                {
                    foreach (var sceneId in targetSceneIds)
                    {
                        var itemId = Guid.NewGuid();
                        await engine.AddItemAsync(sceneId, itemId, stressSourceId, ct).ConfigureAwait(false);
                        await engine.SetItemVisibleAsync(itemId, true, ct).ConfigureAwait(false);

                        // Centra la carta de estrés inicialmente
                        var initialX = Math.Max(0, (canvasWidth - 960) / 2.0);
                        var initialY = Math.Max(0, (canvasHeight - 540) / 2.0);
                        await engine.SetItemTransformAsync(itemId, Transform.Identity.WithPosition(initialX, initialY), ct).ConfigureAwait(false);

                        sceneItems.Add((sceneId, itemId));
                    }
                }
            }
            catch
            {
                // Si la creación de la fuente de estrés falla, se mide el programa actual sin interrumpir
            }
        }

        var recordingStarted = false;
        try
        {
            await engine.StartRecordingAsync(new RecordingSettings(path, new EncoderSettings(encoder.Id, bitrateKbps)), ct).ConfigureAwait(false);
            recordingStarted = true;

            // Warm-up inicial para estabilizar buffers de Direct3D y el codificador
            await Task.Delay(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
            var before = await engine.GetMetricsAsync(ct).ConfigureAwait(false);
            var cpu = new List<double>();
            var end = DateTime.UtcNow + duration;
            var sw = Stopwatch.StartNew();

            while (DateTime.UtcNow < end)
            {
                var t = sw.Elapsed.TotalSeconds;

                // Movimiento dinámico continuo del patrón de ruido/gráficos por el lienzo:
                // Forza el cálculo de vectores de movimiento y cuantización en cada macrobloque (H.264/HEVC)
                if (sceneItems.Count > 0)
                {
                    var rangeX = Math.Max(40, (canvasWidth - 960) / 2.0);
                    var rangeY = Math.Max(30, (canvasHeight - 540) / 2.0);
                    var posX = (float)((canvasWidth - 960) / 2.0 + Math.Sin(t * 2.8) * rangeX * 0.7);
                    var posY = (float)((canvasHeight - 540) / 2.0 + Math.Cos(t * 3.4) * rangeY * 0.7);
                    var scale = (float)(1.0 + Math.Sin(t * 2.0) * 0.10);
                    var rot = (float)(Math.Sin(t * 2.2) * 6.0);

                    var dynamicTransform = Transform.Identity
                        .WithPosition(posX, posY)
                        .WithScale(scale, scale) with { RotationDegrees = rot };

                    foreach (var (_, itemId) in sceneItems)
                    {
                        try
                        {
                            await engine.SetItemTransformAsync(itemId, dynamicTransform, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Ignora fallos transitorios de transformación durante el ciclo
                        }
                    }
                }

                await Task.Delay(60, ct).ConfigureAwait(false); // ~16 FPS de actualización de movimiento visual
                var currentMetrics = await engine.GetMetricsAsync(ct).ConfigureAwait(false);
                cpu.Add(currentMetrics.CpuUsagePercent);
                progress?.Invoke($"Sometiendo a estrés {encoder.Name}: CPU {currentMetrics.CpuUsagePercent:0}% | FPS {currentMetrics.ActiveFps:0} | {encoder.Codec.ToUpperInvariant()}…");
            }

            var after = await engine.GetMetricsAsync(ct).ConfigureAwait(false);
            var encoded = after.EncodedFrames - before.EncodedFrames;
            var skipped = after.SkippedEncoderFrames - before.SkippedEncoderFrames;
            var rendered = after.TotalFrames - before.TotalFrames;
            var lagged = after.LaggedFrames - before.LaggedFrames;

            return new BenchmarkResult(
                encoder.Id,
                encoder.Name,
                encoder.Hardware,
                encoded,
                Percent(skipped, encoded + skipped),
                Percent(lagged, rendered),
                cpu.Count == 0 ? 0 : cpu.Average());
        }
        finally
        {
            // 1. Desconecta de las escenas los elementos de prueba primero
            foreach (var (_, itemId) in sceneItems)
            {
                try
                {
                    await engine.RemoveItemAsync(itemId, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }

            // 2. Detiene la grabación con seguridad
            if (recordingStarted)
            {
                try
                {
                    await engine.StopRecordingAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }

            // 3. Destruye la fuente de estrés
            if (sourceCreated)
            {
                try
                {
                    await engine.RemoveSourceAsync(stressSourceId, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private static double Percent(uint part, uint total) => total == 0 ? 0 : 100.0 * part / total;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // El muxer puede tardar en soltar el archivo; queda en la carpeta temporal del usuario.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
