using System.Runtime.InteropServices;
using LiveStream.Native.Engine;
using LiveStream.Native.Interop;

namespace LiveStream.UnitTests;

public sealed class NativeInteropTests
{
    // Tamaños esperados en x64 según livestream_bridge.h. Si cambian, el ABI se rompió.
    [Fact]
    public void StructLayouts_MatchCHeader()
    {
        Assert.Equal(8 * 4 + 4 * 8, Marshal.SizeOf<SpEngineConfig>());     // 8 uint32 + 4 punteros
        Assert.Equal(24, Marshal.SizeOf<SpEncoderConfig>());                // puntero + 3 uint32 + relleno
        Assert.Equal(8 + 24, Marshal.SizeOf<SpRecordingConfig>());
        Assert.Equal(16 + 24, Marshal.SizeOf<SpStreamConfig>());
        Assert.Equal(5 * 4 + 4 * 4, Marshal.SizeOf<SpTransform>());
        Assert.Equal(5 * 4, Marshal.SizeOf<SpAudioLevels>());
        Assert.Equal(104, Marshal.SizeOf<SpMetrics>());
    }

    [Fact]
    public void ProbeBridge_ReportsMissingDllInsteadOfThrowing()
    {
        // En CI sin el puente compilado debe devolver false con un mensaje útil.
        var ok = LibObsEngine.TryProbeBridge(out var error);
        Assert.True(ok || error.Length > 0);
    }

    [Fact]
    public async Task NativeDispatcher_RunsEverythingOnOneThread()
    {
        using var dispatcher = new NativeDispatcher("test");
        var ids = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => dispatcher.InvokeAsync(() => Environment.CurrentManagedThreadId)));
        Assert.Single(ids.Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, ids[0]);
    }

    [Fact]
    public async Task NativeDispatcher_PropagatesExceptions()
    {
        using var dispatcher = new NativeDispatcher("test");
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(() => throw new InvalidOperationException()));
    }

    [Fact]
    public void SourceTypeMap_CoversEveryKind()
    {
        foreach (var kind in Enum.GetValues<LiveStream.Domain.Entities.SourceKind>())
            Assert.False(string.IsNullOrEmpty(SourceTypeMap.ToObsId(kind)));
    }
}
