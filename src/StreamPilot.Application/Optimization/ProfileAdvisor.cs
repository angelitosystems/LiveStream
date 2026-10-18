using StreamPilot.Domain.Entities;

namespace StreamPilot.Application.Optimization;

public enum RecommendationImpact
{
    /// <summary>Se aplica a la próxima grabación o emisión.</summary>
    NextOutput,
    /// <summary>Requiere reiniciar la aplicación (adaptador gráfico, lienzo).</summary>
    Restart,
    /// <summary>Solo informa.</summary>
    Info,
}

public sealed record Recommendation(string Id, string Title, string Detail, RecommendationImpact Impact);

/// <summary>Perfil propuesto. Los campos null no cambian.</summary>
public sealed record OptimizationPlan(
    IReadOnlyList<Recommendation> Items,
    EncoderSettings? Stream,
    EncoderSettings? Recording,
    int? RenderAdapterIndex,
    CanvasSettings? Canvas);

/// <summary>
/// Recomendador determinista de perfiles a partir del hardware detectado y del benchmark. No aplica nada: las
/// propuestas se aplican solo cuando el usuario las autoriza.
/// </summary>
public static class ProfileAdvisor
{
    /// <summary>Bitrate de emisión H.264 recomendado según resolución y FPS (kb/s).</summary>
    public static int StreamBitrateFor(CanvasSettings canvas)
    {
        var lines = Math.Min(canvas.Width, canvas.Height); // el lado corto: 1080 en horizontal y en vertical
        var high = canvas.Fps > 30;
        return lines switch
        {
            <= 720 => high ? 4500 : 3000,
            <= 1080 => high ? 6000 : 4500,
            <= 1440 => high ? 9000 : 7500,
            _ => high ? 15000 : 12000,
        };
    }

    public static OptimizationPlan Advise(HardwareInfo hardware, IReadOnlyList<BenchmarkResult> benchmark, StudioProject project)
    {
        var items = new List<Recommendation>();
        EncoderSettings? stream = null, recording = null;
        int? adapter = null;
        CanvasSettings? canvas = null;

        var healthy = benchmark.Where(b => b.Healthy).ToList();
        var best = healthy.Where(b => b.Hardware).OrderBy(b => b.CpuPercent).FirstOrDefault()
                   ?? healthy.Where(b => !b.Hardware && b.CpuPercent < 60).OrderBy(b => b.CpuPercent).FirstOrDefault();

        if (benchmark.Count == 0)
        {
            items.Add(new("benchmark", "Mide el rendimiento", "Ejecuta el benchmark para elegir el codificador con datos reales de este equipo.", RecommendationImpact.Info));
        }
        else if (best is null)
        {
            items.Add(new("overload", "Ningún codificador aguanta este lienzo",
                $"Con {project.Canvas.Width}x{project.Canvas.Height} a {project.Canvas.Fps} FPS todos descartaron fotogramas o retrasaron el render.",
                RecommendationImpact.Info));
            canvas = Lighter(project.Canvas);
            if (canvas is not null)
                items.Add(new("canvas", $"Bajar el lienzo a {canvas.Width}x{canvas.Height} a {canvas.Fps} FPS",
                    "Reduce el trabajo de render y de codificación. Requiere reiniciar la aplicación.", RecommendationImpact.Restart));
        }
        else
        {
            var bitrate = StreamBitrateFor(project.Canvas);
            var current = project.Stream.EffectiveEncoder;
            stream = current with { VideoEncoderId = best.EncoderId, VideoBitrateKbps = bitrate, KeyframeIntervalSec = 2 };
            if (stream != current)
                items.Add(new("stream", $"Emitir con {best.EncoderName} a {bitrate} kb/s",
                    $"En el benchmark: {best.SkippedPercent:0.#}% de fotogramas descartados, CPU media {best.CpuPercent:0}%." +
                    (best.Hardware ? " Al codificar en la GPU, la CPU queda libre para juegos y capturas." : ""),
                    RecommendationImpact.NextOutput));
            else
                stream = null;

            var currentRec = project.Recording.EffectiveEncoder;
            var recBitrate = Math.Min(50_000, bitrate * 5 / 2);
            recording = currentRec with { VideoEncoderId = best.EncoderId, VideoBitrateKbps = recBitrate };
            if (recording != currentRec)
                items.Add(new("recording", $"Grabar con {best.EncoderName} a {recBitrate} kb/s",
                    "La grabación local admite más calidad que la emisión.", RecommendationImpact.NextOutput));
            else
                recording = null;

            // Codificar en una GPU dedicada mientras se renderiza en la integrada obliga a copiar cada fotograma.
            var vendor = EncoderCapability.Classify(best.EncoderId).Vendor;
            var discrete = hardware.Adapters.FirstOrDefault(a => a.IsDiscrete && a.Vendor == vendor);
            if (best.Hardware && discrete is not null && discrete.Index != hardware.RenderAdapterIndex)
            {
                adapter = discrete.Index;
                items.Add(new("adapter", $"Renderizar con {discrete.Name}",
                    $"Ahora se renderiza con «{hardware.Adapters.FirstOrDefault(a => a.Index == hardware.RenderAdapterIndex)?.Name}». " +
                    "Renderizar y codificar en la misma GPU evita copias entre tarjetas. En portátiles con gráficos híbridos la " +
                    "captura de pantalla puede necesitar el método «Windows 10+»; si algo falla, vuelve al adaptador anterior. Requiere reiniciar.",
                    RecommendationImpact.Restart));
            }
        }

        if (hardware.MemoryGb is > 0 and < 8)
            items.Add(new("memory", "Poca memoria", $"El equipo tiene {hardware.MemoryGb:0.#} GB. Cierra otras aplicaciones durante el directo.", RecommendationImpact.Info));
        return new OptimizationPlan(items, stream, recording, adapter, canvas);
    }

    /// <summary>Siguiente escalón más ligero: primero FPS, luego resolución.</summary>
    public static CanvasSettings? Lighter(CanvasSettings c)
    {
        if (c.Fps > 30)
            return c with { Fps = 30 };
        var shortSide = Math.Min(c.Width, c.Height);
        if (shortSide > 720)
        {
            var scale = 720.0 / shortSide;
            int Even(double v) => (int)Math.Round(v / 2) * 2;
            return c with { Width = Even(c.Width * scale), Height = Even(c.Height * scale) };
        }
        return null;
    }
}
