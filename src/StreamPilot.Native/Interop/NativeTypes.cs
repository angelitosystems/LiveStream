using System.Runtime.InteropServices;

namespace StreamPilot.Native.Interop;

internal enum SpResult
{
    Ok = 0,
    InvalidArg = 1,
    AlreadyInitialized = 2,
    StartupFailed = 3,
    VideoResetFailed = 4,
    AudioResetFailed = 5,
    NotFound = 6,
    CreateFailed = 7,
    OutputFailed = 8,
    InvalidState = 9,
    Internal = 99,
}

internal enum SpDeviceKind
{
    Camera = 0,
    Microphone = 1,
    AudioOutput = 2,
    Display = 3,
}

internal enum SpLogLevel
{
    Error = 100,
    Warning = 200,
    Info = 300,
    Debug = 400,
}

/// <remarks>Los punteros a cadenas UTF-8 deben mantenerse vivos durante la llamada (ver <see cref="Utf8Pin"/>).</remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct SpEngineConfig
{
    public uint BaseWidth;
    public uint BaseHeight;
    public uint OutputWidth;
    public uint OutputHeight;
    public uint FpsNum;
    public uint FpsDen;
    public uint AudioSampleRate;
    public nint ModuleBinDir;
    public nint ModuleDataDir;
    public nint LibobsDataDir;
    public nint ModuleConfigDir;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpRecordingConfig
{
    public nint OutputPath;
    public uint VideoBitrateKbps;
    public uint AudioBitrateKbps;
    public nint VideoEncoderId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpMetrics
{
    public double ActiveFps;
    public double AverageFrameRenderMs;
    public uint TotalFrames;
    public uint LaggedFrames;
    public uint SkippedEncoderFrames;
    public int IsRecording;
}

/// <summary>Reserva cadenas UTF-8 no administradas y las libera juntas al hacer Dispose.</summary>
internal sealed class Utf8Pin : IDisposable
{
    private readonly List<nint> _allocations = [];

    public nint Add(string? value)
    {
        if (value is null)
            return 0;
        var ptr = Marshal.StringToCoTaskMemUTF8(value);
        _allocations.Add(ptr);
        return ptr;
    }

    public void Dispose()
    {
        foreach (var ptr in _allocations)
            Marshal.FreeCoTaskMem(ptr);
        _allocations.Clear();
    }
}
