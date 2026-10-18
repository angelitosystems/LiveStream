using StreamPilot.Application.Abstractions;

namespace StreamPilot.Application.Optimization;

/// <summary>Codificador de video disponible y lo que sabe hacer.</summary>
public sealed record EncoderCapability(string Id, string Name, string Codec, bool Hardware, string Vendor, bool DynamicBitrate)
{
    /// <summary>Clasifica por id de libobs: obs_nvenc_*, obs_qsv11*, *amf*, obs_x264...</summary>
    public static (bool Hardware, string Vendor) Classify(string id) => id switch
    {
        _ when id.Contains("nvenc", StringComparison.OrdinalIgnoreCase) => (true, "NVIDIA"),
        _ when id.Contains("qsv", StringComparison.OrdinalIgnoreCase) => (true, "Intel"),
        _ when id.Contains("amf", StringComparison.OrdinalIgnoreCase) || id.Contains("amd", StringComparison.OrdinalIgnoreCase) => (true, "AMD"),
        _ => (false, "CPU"),
    };
}

public sealed record HardwareInfo(
    string CpuName,
    int LogicalCores,
    double MemoryGb,
    IReadOnlyList<GraphicsAdapter> Adapters,
    IReadOnlyList<EncoderCapability> Encoders,
    int RenderAdapterIndex);

/// <summary>Detección del hardware real del equipo (CPU, memoria, GPUs y codificadores del runtime).</summary>
public interface IHardwareProbe
{
    Task<HardwareInfo> ProbeAsync(CancellationToken ct);
}
