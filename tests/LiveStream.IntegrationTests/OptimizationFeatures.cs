using System.Diagnostics;
using LiveStream.Application.Abstractions;
using LiveStream.Domain.Entities;
using LiveStream.Native.Engine;
using Xunit.Abstractions;

namespace LiveStream.IntegrationTests;

/// <summary>Fase 5 contra libobs real: adaptadores, métricas de emisión y bitrate dinámico con un servidor RTMP local.</summary>
internal sealed class OptimizationFeatures(LibObsEngine engine, ITestOutputHelper output)
{
    private static readonly CancellationToken None = CancellationToken.None;

    public async Task RunAsync()
    {
        var adapters = LibObsEngine.ListAdapters();
        output.WriteLine("Adaptadores: " + string.Join(" | ", adapters.Select(a => $"{a.Index}: {a.Name} ({a.Vendor}, {a.DedicatedMemoryBytes >> 20} MB)")));
        Assert.NotEmpty(adapters);
        Assert.Equal(Enumerable.Range(0, adapters.Count), adapters.Select(a => a.Index));
        Assert.Equal(0, (await engine.GetMetricsAsync(None)).RenderAdapterIndex);

        Assert.True(await engine.SupportsDynamicBitrateAsync("obs_x264", None));
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.SupportsDynamicBitrateAsync("no_existe", None));
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.SetStreamVideoBitrateAsync(1000, None)); // sin emisión

        await DynamicBitrateAgainstLocalRtmpAsync();
    }

    private async Task DynamicBitrateAgainstLocalRtmpAsync()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            output.WriteLine("ffmpeg no está instalado: se omite la emisión real a un servidor RTMP local.");
            return;
        }

        // ffmpeg -listen actúa como servidor RTMP y descarta lo que recibe.
        using var server = Process.Start(new ProcessStartInfo(ffmpeg,
            "-hide_banner -loglevel error -listen 1 -i rtmp://127.0.0.1:19350/live/prueba -f null -")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        })!;
        try
        {
            await Task.Delay(1000);
            await engine.StartStreamAsync(new StreamTarget("rtmp://127.0.0.1:19350/live", "prueba", new EncoderSettings("obs_x264", 1500)), None);
            EngineMetrics m = await engine.GetMetricsAsync(None);
            for (var i = 0; i < 50 && !(m.IsStreaming && m.StreamTotalBytes > 0); i++)
            {
                await Task.Delay(100);
                m = await engine.GetMetricsAsync(None);
            }
            Assert.True(m.IsStreaming, "La emisión al servidor local no arrancó.");
            Assert.Equal(1500, m.StreamVideoBitrateKbps);
            Assert.True(m.StreamDynamicBitrate);
            Assert.InRange(m.StreamCongestion, 0, 1);

            await engine.SetStreamVideoBitrateAsync(900, None);
            await Task.Delay(1500);
            m = await engine.GetMetricsAsync(None);
            output.WriteLine($"Emisión local: bitrate {m.StreamVideoBitrateKbps} kb/s, congestión {m.StreamCongestion:0.00}, " +
                             $"perdidos {m.StreamDroppedFrames}/{m.StreamTotalFrames}, {m.StreamTotalBytes / 1024} KB enviados");
            Assert.True(m.IsStreaming, "Cambiar el bitrate cortó la emisión.");
            Assert.Equal(900, m.StreamVideoBitrateKbps);

            await engine.StopStreamAsync(None);
            Assert.Equal(0, (await engine.GetLastStopAsync(OutputKind.Stream, None)).Code);
        }
        finally
        {
            if (!server.HasExited)
                server.Kill(entireProcessTree: true);
        }
    }

    private static string? FindFfmpeg() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir.Trim(), "ffmpeg.exe"))
            .FirstOrDefault(File.Exists);
}
