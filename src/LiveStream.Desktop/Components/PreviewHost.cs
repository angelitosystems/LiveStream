using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace LiveStream.Desktop.Components;

/// <summary>
/// Ventana hija Win32 donde libobs dibuja con D3D11 (sin copiar frames a bitmaps). Avalonia no recibe eventos de
/// ratón sobre una ventana nativa (problema de "airspace"), así que esta ventana tiene su propio procedimiento y
/// reenvía el ratón como eventos .NET. El teclado se queda en la ventana de Avalonia: la hija nunca toma el foco.
/// </summary>
public sealed class PreviewHost : NativeControlHost
{
    private const string ClassName = "LiveStreamPreview";
    private static readonly object ClassLock = new();
    private static bool _classRegistered;

    private nint _hwnd;
    private GCHandle _self;
    private bool _captured;

    public event Action<nint, int, int>? NativeHandleCreated;
    public event Action<int, int>? NativeHandleResized;
    public event Action? NativeHandleDestroying;

    public event Action<CanvasPointer>? CanvasPointerPressed;
    public event Action<CanvasPointer>? CanvasPointerMoved;
    public event Action<CanvasPointer>? CanvasPointerReleased;
    public event Action<CanvasPointer>? CanvasDoubleClicked;
    public event Action<CanvasPointer>? CanvasWheel;

    /// <summary>Archivos soltados sobre la vista previa (coordenadas de píxel de la ventana nativa).</summary>
    public event Action<string[], int, int>? FilesDropped;

    /// <summary>Cursor que muestra la ventana nativa (lo decide el editor según lo que hay bajo el ratón).</summary>
    public CanvasCursor NativeCursor { get; set; } = CanvasCursor.Arrow;

    public nint NativeHandle => _hwnd;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
            return base.CreateNativeControlCore(parent);

        EnsureClass();
        var (w, h) = PixelSize(Bounds.Size);
        _hwnd = Win32.CreateWindowEx(0, ClassName, null, Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPSIBLINGS | Win32.WS_CLIPCHILDREN,
            0, 0, w, h, parent.Handle, 0, Win32.GetModuleHandle(null), 0);
        if (_hwnd == 0)
            throw new InvalidOperationException($"CreateWindowEx falló ({Marshal.GetLastPInvokeError()}).");
        Win32.DragAcceptFiles(_hwnd, true); // la ventana nativa se traga los arrastres de Avalonia: los acepta aquí

        _self = GCHandle.Alloc(this);
        Win32.SetWindowLongPtr(_hwnd, Win32.GWLP_USERDATA, GCHandle.ToIntPtr(_self));
        NativeHandleCreated?.Invoke(_hwnd, w, h);
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        NativeHandleDestroying?.Invoke();
        if (_hwnd != 0)
        {
            Win32.SetWindowLongPtr(_hwnd, Win32.GWLP_USERDATA, 0);
            Win32.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
        if (_self.IsAllocated)
            _self.Free();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _hwnd != 0)
        {
            var (w, h) = PixelSize(Bounds.Size);
            NativeHandleResized?.Invoke(w, h);
        }
    }

    private (int, int) PixelSize(Size size)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return ((int)Math.Max(1, size.Width * scale), (int)Math.Max(1, size.Height * scale));
    }

    private static unsafe void EnsureClass()
    {
        lock (ClassLock)
        {
            if (_classRegistered)
                return;
            var name = Marshal.StringToHGlobalUni(ClassName); // vive lo que el proceso: la clase queda registrada
            var wc = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                style = Win32.CS_DBLCLKS | Win32.CS_OWNDC,
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WndProc,
                hInstance = Win32.GetModuleHandle(null),
                hCursor = Win32.LoadCursor(0, 32512),
                lpszClassName = name,
            };
            if (Win32.RegisterClassEx(in wc) == 0)
                throw new InvalidOperationException($"RegisterClassEx falló ({Marshal.GetLastPInvokeError()}).");
            _classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            var data = Win32.GetWindowLongPtr(hwnd, Win32.GWLP_USERDATA);
            if (data != 0 && GCHandle.FromIntPtr(data).Target is PreviewHost host && host.Handle(hwnd, msg, wParam, lParam, out var result))
                return result;
        }
        catch
        {
            // Nunca propagar excepciones administradas a través del bucle de mensajes de Win32.
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private bool Handle(nint hwnd, uint msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        switch (msg)
        {
            case Win32.WM_ERASEBKGND:
                result = 1; // libobs pinta todo el área: evita parpadeo
                return true;
            case Win32.WM_SETCURSOR:
                Win32.SetCursor(Win32.LoadCursor(0, NativeCursor switch
                {
                    CanvasCursor.Move => 32646,
                    CanvasCursor.SizeNwse => 32642,
                    CanvasCursor.SizeNesw => 32643,
                    CanvasCursor.SizeWe => 32644,
                    CanvasCursor.SizeNs => 32645,
                    CanvasCursor.Hand => 32649,
                    _ => 32512,
                }));
                result = 1;
                return true;
            case Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN or Win32.WM_MBUTTONDOWN:
                Win32.SetCapture(hwnd);
                _captured = true;
                CanvasPointerPressed?.Invoke(Pointer(wParam, lParam, ButtonOf(msg)));
                return true;
            case Win32.WM_LBUTTONUP or Win32.WM_RBUTTONUP or Win32.WM_MBUTTONUP:
                if (_captured)
                {
                    _captured = false;
                    Win32.ReleaseCapture();
                }
                CanvasPointerReleased?.Invoke(Pointer(wParam, lParam, ButtonOf(msg)));
                return true;
            case Win32.WM_LBUTTONDBLCLK:
                CanvasDoubleClicked?.Invoke(Pointer(wParam, lParam, CanvasButton.Left));
                return true;
            case Win32.WM_MOUSEMOVE:
                CanvasPointerMoved?.Invoke(Pointer(wParam, lParam, CanvasButton.Left));
                return true;
            case Win32.WM_MOUSEWHEEL:
            {
                // Las coordenadas de la rueda llegan en pantalla.
                var pt = new Win32.POINT { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
                Win32.ScreenToClient(hwnd, ref pt);
                var delta = (short)((wParam >> 16) & 0xFFFF);
                CanvasWheel?.Invoke(new CanvasPointer(pt.X, pt.Y, CanvasButton.Middle, Modifiers(wParam), delta));
                return true;
            }
            case Win32.WM_CAPTURECHANGED:
                _captured = false;
                return false;
            case Win32.WM_DROPFILES:
            {
                var hDrop = wParam;
                var count = Win32.DragQueryFileCount(hDrop);
                var pt = new Win32.POINT();
                _ = Win32.DragQueryPoint(hDrop, ref pt);
                var paths = new string[count];
                for (var i = 0u; i < count; i++)
                    paths[i] = Win32.DragQueryFileName(hDrop, i);
                Win32.DragFinish(hDrop);
                FilesDropped?.Invoke(paths, pt.X, pt.Y);
                return true;
            }
            default:
                return false;
        }
    }

    private static CanvasButton ButtonOf(uint msg) => msg switch
    {
        Win32.WM_RBUTTONDOWN or Win32.WM_RBUTTONUP => CanvasButton.Right,
        Win32.WM_MBUTTONDOWN or Win32.WM_MBUTTONUP => CanvasButton.Middle,
        _ => CanvasButton.Left,
    };

    private static CanvasPointer Pointer(nint wParam, nint lParam, CanvasButton button) =>
        new((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF), button, Modifiers(wParam));

    private static CanvasModifiers Modifiers(nint wParam)
    {
        var m = CanvasModifiers.None;
        if ((wParam & Win32.MK_SHIFT) != 0) m |= CanvasModifiers.Shift;
        if ((wParam & Win32.MK_CONTROL) != 0) m |= CanvasModifiers.Control;
        if (Win32.GetKeyState(Win32.VK_MENU) < 0) m |= CanvasModifiers.Alt;
        return m;
    }
}
