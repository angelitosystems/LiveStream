using System.Diagnostics;
using LiveStream.Application.Abstractions;
using LiveStream.Domain.Entities;
using LiveStream.Native.Engine;
using Xunit.Abstractions;

namespace LiveStream.IntegrationTests;

/// <summary>
/// Fase 6 contra libobs real: dos destinos simultáneos (servidores RTMP locales con ffmpeg) que comparten
/// codificador, caída de un servidor sin afectar al otro y reconexión automática al volver.
/// </summary>
internal sealed class MultistreamFeatures(LibObsEngine engine, ITestOutputHelper output)
{
    private static readonly CancellationToken None = CancellationToken.None;

    public async Task RunAsync()
    {
        var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d.Trim(), "ffmpeg.exe")).FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            output.WriteLine("ffmpeg no está instalado: se omite la prueba de multistream real.");
            return;
        }

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Null(await engine.GetDestinationStatsAsync(a, None));
        var encoder = new EncoderSettings("obs_x264", 1200);
        Process? serverA = Listen(ffmpeg, 19361);
        var serverB = Listen(ffmpeg, 19362);
        try
        {
            await Task.Delay(1000);
            await engine.StartDestinationAsync(a, new StreamTarget("rtmp://127.0.0.1:19361/live", "a", encoder), None);
            await engine.StartDestinationAsync(b, new StreamTarget("rtmp://127.0.0.1:19362/live", "b", encoder), None);
            await Assert.ThrowsAsync<NativeEngineException>(() =>
                engine.StartDestinationAsync(a, new StreamTarget("rtmp://127.0.0.1:19361/live", "a", encoder), None));

            var sa = await WaitAsync(a, s => s.Active && s.TotalBytes > 50_000, "A no empezó a emitir");
            var sb = await WaitAsync(b, s => s.Active && s.TotalBytes > 50_000, "B no empezó a emitir");
            Assert.True(sa.SharedEncoder && sb.SharedEncoder, "Los dos destinos deberían compartir codificador.");
            var m = await engine.GetMetricsAsync(None);
            Assert.True(m.IsStreaming);
            Assert.True(m.StreamTotalBytes + 200_000 >= sa.TotalBytes + sb.TotalBytes, // el agregado suma todos los destinos
                $"Bytes agregados {m.StreamTotalBytes}, A {sa.TotalBytes}, B {sb.TotalBytes}, frames {m.StreamTotalFrames}");
            output.WriteLine($"Multistream: A {sa.TotalBytes / 1024} KB, B {sb.TotalBytes / 1024} KB, codificador compartido");

            // Cae el servidor A: A entra en reconexión, B sigue emitiendo.
            serverA.Kill(entireProcessTree: true);
            serverA.Dispose();
            serverA = null;
            await WaitAsync(a, s => s.Reconnecting, "A no detectó la caída", TimeSpan.FromSeconds(15));
            var bBefore = (await engine.GetDestinationStatsAsync(b, None))!.TotalBytes;
            await Task.Delay(1500);
            var bAfter = await engine.GetDestinationStatsAsync(b, None);
            Assert.True(bAfter!.Active && !bAfter.Reconnecting && bAfter.TotalBytes > bBefore, "B se vio afectado por la caída de A.");
            output.WriteLine("Caída de A: A reconectando, B sigue emitiendo.");

            // Vuelve el servidor A: libobs reconecta solo (reintento cada 5 s).
            serverA = Listen(ffmpeg, 19361);
            var back = await WaitAsync(a, s => s.Active && !s.Reconnecting, "A no se reconectó", TimeSpan.FromSeconds(25));
            // Tras reconectar, libobs espera al siguiente fotograma clave (cada 2 s) para volver a enviar.
            var bytes = back.TotalBytes;
            await WaitAsync(a, s => s.TotalBytes > bytes + 20_000, "A reconectó pero no envía datos", TimeSpan.FromSeconds(8));
            output.WriteLine("Servidor A de vuelta: reconexión automática verificada.");

            // Bitrate de un destino con codificador compartido: afecta a los dos.
            await engine.SetDestinationBitrateAsync(a, 800, None);
            Assert.Equal(800, (await engine.GetDestinationStatsAsync(b, None))!.VideoBitrateKbps);

            await engine.StopDestinationAsync(a, None);
            await engine.StopDestinationAsync(b, None);
            Assert.False((await engine.GetDestinationStatsAsync(a, None))!.Active);
            Assert.False((await engine.GetMetricsAsync(None)).IsStreaming);
            await Assert.ThrowsAsync<NativeEngineException>(() => engine.StopDestinationAsync(a, None));
        }
        finally
        {
            foreach (var p in new[] { serverA, serverB })
            {
                if (p is { HasExited: false })
                    p.Kill(entireProcessTree: true);
                p?.Dispose();
            }
        }
    }

    private static Process Listen(string ffmpeg, int port) => Process.Start(new ProcessStartInfo(ffmpeg,
        $"-hide_banner -loglevel error -listen 1 -i rtmp://127.0.0.1:{port}/live -f null -")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
    })!;

    private async Task<DestinationStats> WaitAsync(Guid id, Func<DestinationStats, bool> condition, string error, TimeSpan? timeout = null)
    {
        var end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        DestinationStats? s = null;
        while (DateTime.UtcNow < end)
        {
            s = await engine.GetDestinationStatsAsync(id, None);
            if (s is not null && condition(s))
                return s;
            await Task.Delay(200);
        }
        throw new Xunit.Sdk.XunitException($"{error}. Último estado: {s}");
    }
}
