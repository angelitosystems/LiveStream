using System.Runtime.InteropServices;

namespace LiveStream.Native.Interop;

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
    BufferTooSmall = 10,
    Internal = 99,
}

internal enum SpDeviceKind { Camera = 0, Microphone = 1, AudioOutput = 2, Display = 3 }

internal enum SpLogLevel { Error = 100, Warning = 200, Info = 300, Debug = 400 }

internal enum SpDisplayMode { Program = 0, Scene = 1 }

/// <remarks>
/// Espejo exacto de livestream_bridge.h (ABI v3). Los punteros a cadenas UTF-8 deben mantenerse vivos durante
/// la llamada (ver <see cref="Utf8Pin"/>).
/// </remarks>
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
    public uint AdapterIndex;
    public nint ModuleBinDir;
    public nint ModuleDataDir;
    public nint LibobsDataDir;
    public nint ModuleConfigDir;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpEncoderConfig
{
    public nint VideoEncoderId;
    public uint VideoBitrateKbps;
    public uint AudioBitrateKbps;
    public uint KeyframeIntervalSec;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpRecordingConfig
{
    public nint OutputPath;
    public SpEncoderConfig Encoder;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpStreamConfig
{
    public nint Server;
    public nint Key;
    public SpEncoderConfig Encoder;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpTransform
{
    public float PosX;
    public float PosY;
    public float ScaleX;
    public float ScaleY;
    public float RotationDeg;
    public int CropLeft;
    public int CropTop;
    public int CropRight;
    public int CropBottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpStreamStats
{
    public int Active;
    public int Reconnecting;
    public uint DroppedFrames;
    public uint TotalFrames;
    public ulong TotalBytes;
    public float Congestion;
    public uint VideoBitrateKbps;
    public int DynamicBitrate;
    public int LastStopCode;
    public int SharedEncoder;
    public uint ConnectTimeMs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpMetrics
{
    public double ActiveFps;
    public double AverageFrameRenderMs;
    public double CpuUsagePercent;
    public ulong MemoryBytes;
    public uint TotalFrames;
    public uint LaggedFrames;
    public uint SkippedEncoderFrames;
    public uint EncodedFrames;
    public int IsRecording;
    public int IsStreaming;
    public int StreamReconnecting;
    public uint StreamDroppedFrames;
    public uint StreamTotalFrames;
    public ulong StreamTotalBytes;
    public ulong RecordingTotalBytes;
    public float StreamCongestion;
    public uint StreamVideoBitrateKbps;
    public int StreamDynamicBitrate;
    public uint RenderAdapterIndex;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SpAudioLevels
{
    public fixed float PeakDb[2];
    public fixed float MagnitudeDb[2];
    public uint Channels;
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
        {
            // Puede contener secretos (clave de transmisión): se borra antes de liberar.
            var length = 0;
            while (Marshal.ReadByte(ptr, length) != 0)
                length++;
            for (var i = 0; i < length; i++)
                Marshal.WriteByte(ptr, i, 0);
            Marshal.FreeCoTaskMem(ptr);
        }
        _allocations.Clear();
    }
}
