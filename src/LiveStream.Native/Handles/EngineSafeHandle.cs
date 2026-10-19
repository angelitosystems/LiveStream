using System.Runtime.InteropServices;
using LiveStream.Native.Interop;

namespace LiveStream.Native.Handles;

/// <summary>Propietario del sp_engine nativo. Liberarlo apaga libobs (obs_shutdown).</summary>
internal sealed class EngineSafeHandle : SafeHandle
{
    public EngineSafeHandle() : base(0, ownsHandle: true) { }

    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        NativeMethods.EngineDestroy(handle);
        return true;
    }
}
