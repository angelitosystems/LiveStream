using Microsoft.Win32;
using StreamPilot.Application.Abstractions;
using StreamPilot.Application.Optimization;

namespace StreamPilot.Native.Engine;

/// <summary>Detección real del hardware: CPU (registro de Windows), memoria, adaptadores DXGI y codificadores de libobs.</summary>
public sealed class HardwareProbe(IStreamingEngine engine) : IHardwareProbe
{
    public async Task<HardwareInfo> ProbeAsync(CancellationToken ct)
    {
        var encoders = new List<EncoderCapability>();
        var renderAdapter = 0;
        if (engine.IsInitialized)
        {
            renderAdapter = (await engine.GetMetricsAsync(ct).ConfigureAwait(false)).RenderAdapterIndex;
            foreach (var e in await engine.ListVideoEncodersAsync(ct).ConfigureAwait(false))
            {
                var (hardware, vendor) = EncoderCapability.Classify(e.Id);
                var dynamic = await engine.SupportsDynamicBitrateAsync(e.Id, ct).ConfigureAwait(false);
                encoders.Add(new EncoderCapability(e.Id, e.Name, e.Codec, hardware, vendor, dynamic));
            }
        }
        return new HardwareInfo(CpuName(), Environment.ProcessorCount, MemoryGb(), LibObsEngine.ListAdapters(), encoders, renderAdapter);
    }

    private static string CpuName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU desconocida";
    }

    private static double MemoryGb() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024;
}
