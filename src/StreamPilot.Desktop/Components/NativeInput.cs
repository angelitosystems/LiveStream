using System.Runtime.InteropServices;

namespace StreamPilot.Desktop.Components;

public enum CanvasButton { Left, Right, Middle }

[Flags]
public enum CanvasModifiers { None = 0, Shift = 1, Control = 2, Alt = 4 }

public enum CanvasCursor { Arrow, Move, SizeNwse, SizeNesw, SizeWe, SizeNs, Hand }

/// <summary>Evento de ratón en coordenadas de píxel de la ventana nativa de la vista previa.</summary>
public readonly record struct CanvasPointer(double X, double Y, CanvasButton Button, CanvasModifiers Modifiers, int WheelDelta = 0);

internal static partial class Win32
{
    public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    public const uint CS_DBLCLKS = 0x0008, CS_OWNDC = 0x0020;
    public const uint WM_DESTROY = 0x0002, WM_ERASEBKGND = 0x0014, WM_SETCURSOR = 0x0020, WM_MOUSEMOVE = 0x0200,
        WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204,
        WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A, WM_CAPTURECHANGED = 0x0215;
    public const int GWLP_USERDATA = -21;
    public const int MK_SHIFT = 0x4, MK_CONTROL = 0x8;
    public const int VK_MENU = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(in WNDCLASSEXW wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetCapture")]
    public static partial nint SetCapture(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseCapture")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursor(nint instance, nint cursorName);

    [LibraryImport("user32.dll", EntryPoint = "SetCursor")]
    public static partial nint SetCursor(nint cursor);

    [LibraryImport("user32.dll", EntryPoint = "GetKeyState")]
    public static partial short GetKeyState(int key);

    [LibraryImport("user32.dll", EntryPoint = "ScreenToClient")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hwnd, ref POINT point);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? name);
}
