using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace StreamPilot.Desktop.Components;

/// <summary>
/// Hospeda una ventana hija nativa (HWND) donde libobs dibuja la vista previa con D3D11.
/// Prototipo de la Fase 0: la alternativa a evaluar es una superficie D3D compartida.
/// </summary>
public sealed class PreviewHost : NativeControlHost
{
    public event Action<nint, uint, uint>? NativeHandleCreated;
    public event Action<uint, uint>? NativeHandleResized;
    public event Action? NativeHandleDestroying;

    private nint _handle;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _handle = handle.Handle;
        var (w, h) = PixelSize(Bounds.Size);
        NativeHandleCreated?.Invoke(_handle, w, h);
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        NativeHandleDestroying?.Invoke();
        _handle = 0;
        base.DestroyNativeControlCore(control);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _handle != 0)
        {
            var (w, h) = PixelSize(Bounds.Size);
            NativeHandleResized?.Invoke(w, h);
        }
    }

    private (uint, uint) PixelSize(Size size)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return ((uint)Math.Max(1, size.Width * scale), (uint)Math.Max(1, size.Height * scale));
    }
}
