<#
.SYNOPSIS
  Captura a PNG una ventana de un proceso (verificación visual sin interacción). Usa PrintWindow: solo el
  contenido de esa ventana, nunca lo que haya encima en la pantalla.
.PARAMETER WindowTitle
  Título exacto de otra ventana del mismo proceso (p. ej. un diálogo). Por defecto, la ventana principal.
#>
param(
    [Parameter(Mandatory)] [int] $ProcessId,
    [Parameter(Mandatory)] [string] $OutputPath,
    [string] $WindowTitle
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SpCapture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public static IntPtr Find(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
[SpCapture]::SetProcessDPIAware() | Out-Null
$handle = if ($WindowTitle) { [SpCapture]::Find([uint32]$ProcessId, $WindowTitle) } else { (Get-Process -Id $ProcessId).MainWindowHandle }
if ($handle -eq [IntPtr]::Zero) { throw "No se encontró la ventana '$WindowTitle' del proceso $ProcessId." }
$r = New-Object SpCapture+RECT
[SpCapture]::GetWindowRect($handle, [ref] $r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
# PrintWindow pinta solo la ventana pedida (aunque esté tapada): nunca captura otras aplicaciones del escritorio.
# PW_RENDERFULLCONTENT (2) incluye el contenido Direct3D de la vista previa.
$hdc = $g.GetHdc()
$ok = [SpCapture]::PrintWindow($handle, $hdc, 2)
$g.ReleaseHdc($hdc)
if (-not $ok) { $g.Dispose(); $bmp.Dispose(); throw 'PrintWindow falló; no se captura la pantalla como alternativa.' }
$bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "Captura: $OutputPath ($w x $h)"
