using StreamPilot.Application.Abstractions;
using StreamPilot.Domain.Entities;

namespace StreamPilot.Application.Optimization;

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
/// fotogramas descartados por el codificador, retraso de render y CPU. Requiere que no se esté grabando ni emitiendo.
/// </summary>
public static class EncoderBenchmark
{
    /// <summary>Códecs aptos para emitir por RTMP clásico.</summary>
    public static IEnumerable<EncoderCapability> Candidates(IEnumerable<EncoderCapability> encoders) =>
        encoders.Where(e => e.Codec == "h264").OrderBy(e => e.Hardware ? 0 : 1);

    public static async Task<IReadOnlyList<BenchmarkResult>> RunAsync(IStreamingEngine engine, IEnumerable<EncoderCapability> candidates,
        int bitrateKbps, TimeSpan duration, string tempDirectory, Action<string>? progress, CancellationToken ct)
    {
        var start = await engine.GetMetricsAsync(ct).ConfigureAwait(false);
        if (start.IsRecording || start.IsStreaming)
            throw new InvalidOperationException("Detén la grabación y la emisión antes de medir: el benchmark usa el codificador.");
        Directory.CreateDirectory(tempDirectory);

        var results = new List<BenchmarkResult>();
        foreach (var encoder in candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke($"Midiendo {encoder.Name}…");
            var path = Path.Combine(tempDirectory, $"benchmark-{Guid.NewGuid():N}.mkv");
            try
            {
                results.Add(await MeasureAsync(engine, encoder, bitrateKbps, duration, path, ct).ConfigureAwait(false));
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
        return results;
    }

    private static async Task<BenchmarkResult> MeasureAsync(IStreamingEngine engine, EncoderCapability encoder, int bitrateKbps,
        TimeSpan duration, string path, CancellationToken ct)
    {
        await engine.StartRecordingAsync(new RecordingSettings(path, new EncoderSettings(encoder.Id, bitrateKbps)), ct).ConfigureAwait(false);
        try
        {
            // El primer segundo incluye la creación del codificador: se mide después.
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            var before = await engine.GetMetricsAsync(ct).ConfigureAwait(false);
            var cpu = new List<double>();
            var end = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < end)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                cpu.Add((await engine.GetMetricsAsync(ct).ConfigureAwait(false)).CpuUsagePercent);
            }
            var after = await engine.GetMetricsAsync(ct).ConfigureAwait(false);

            var encoded = after.EncodedFrames - before.EncodedFrames;
            var skipped = after.SkippedEncoderFrames - before.SkippedEncoderFrames;
            var rendered = after.TotalFrames - before.TotalFrames;
            var lagged = after.LaggedFrames - before.LaggedFrames;
            return new BenchmarkResult(encoder.Id, encoder.Name, encoder.Hardware, encoded,
                Percent(skipped, encoded + skipped), Percent(lagged, rendered), cpu.Count == 0 ? 0 : cpu.Average());
        }
        finally
        {
            await engine.StopRecordingAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static double Percent(uint part, uint total) => total == 0 ? 0 : 100.0 * part / total;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // El muxer puede tardar en soltar el archivo; queda en la carpeta temporal del usuario.
        }
    }
}
