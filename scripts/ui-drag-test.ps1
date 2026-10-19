<#
.SYNOPSIS
  Prueba funcional del editor de lienzo: arrastra un elemento enviando mensajes de ratón Win32 a la ventana nativa de
  vista previa y verifica en el proyecto autoguardado que la transformación cambió, y que Ctrl+Z la revierte.
.PARAMETER FromCanvas / ToCanvas
  Puntos en coordenadas del lienzo (x,y).
#>
param(
    [Parameter(Mandatory)] [int] $ProcessId,
    [Parameter(Mandatory)] [string] $ProjectPath,
    [Parameter(Mandatory)] [string] $ItemName,
    [double[]] $FromCanvas = @(300, 950),
    [double[]] $ToCanvas = @(700, 500),
    [int] $CanvasWidth = 1920,
    [int] $CanvasHeight = 1080
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SpUi {
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public static IntPtr FindPreview(IntPtr parent) {
        IntPtr found = IntPtr.Zero; int bestArea = 0;
        EnumChildWindows(parent, (h, l) => {
            var sb = new StringBuilder(64); GetClassName(h, sb, 64);
            RECT r; GetClientRect(h, out r);
            int area = (r.Right - r.Left) * (r.Bottom - r.Top);
            if (sb.ToString() == "LiveStreamPreview" && IsWindowVisible(h) && area > bestArea) { found = h; bestArea = area; }
            return true; }, IntPtr.Zero);
        return found;
    }
}
'@

function Get-ItemTransform {
    $p = Get-Content $ProjectPath -Raw | ConvertFrom-Json
    $source = $p.sources | Where-Object name -eq $ItemName
    foreach ($scene in $p.scenes) { foreach ($i in $scene.items) { if ($i.sourceId -eq $source.id) { return $i.transform } } }
}

# Coordenadas en píxeles físicos, como las recibe la app (PerMonitorV2).
[SpUi]::SetProcessDPIAware() | Out-Null
$main = (Get-Process -Id $ProcessId).MainWindowHandle
$preview = [SpUi]::FindPreview($main)
if ($preview -eq [IntPtr]::Zero) { throw 'No se encontró la ventana de vista previa.' }
$rect = New-Object SpUi+RECT
[SpUi]::GetClientRect($preview, [ref] $rect) | Out-Null
$w = $rect.Right; $h = $rect.Bottom
$scale = [Math]::Min($w / $CanvasWidth, $h / $CanvasHeight)
$ox = ($w - $CanvasWidth * $scale) / 2; $oy = ($h - $CanvasHeight * $scale) / 2
function To-View([double[]] $c) { return @([int]($c[0] * $scale + $ox), [int]($c[1] * $scale + $oy)) }
function LParam([int[]] $p) { return [IntPtr](($p[1] -shl 16) -bor ($p[0] -band 0xFFFF)) }

$before = Get-ItemTransform
Write-Host "Vista previa ${w}x${h}, escala $([Math]::Round($scale,4)). Antes: x=$($before.x) y=$($before.y)"

$from = To-View $FromCanvas; $to = To-View $ToCanvas
[SpUi]::SendMessage($preview, 0x0200, [IntPtr]0, (LParam $from)) | Out-Null       # WM_MOUSEMOVE
[SpUi]::SendMessage($preview, 0x0201, [IntPtr]1, (LParam $from)) | Out-Null       # WM_LBUTTONDOWN
for ($i = 1; $i -le 10; $i++) {
    $pt = @([int]($from[0] + ($to[0] - $from[0]) * $i / 10), [int]($from[1] + ($to[1] - $from[1]) * $i / 10))
    [SpUi]::SendMessage($preview, 0x0200, [IntPtr]1, (LParam $pt)) | Out-Null
    Start-Sleep -Milliseconds 30
}
[SpUi]::SendMessage($preview, 0x0202, [IntPtr]0, (LParam $to)) | Out-Null           # WM_LBUTTONUP
Start-Sleep -Seconds 4   # autoguardado (2 s) + margen
$after = Get-ItemTransform
Write-Host "Después del arrastre: x=$($after.x) y=$($after.y)"

[SpUi]::SetForegroundWindow($main) | Out-Null
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait('^z')
Start-Sleep -Seconds 4
$undone = Get-ItemTransform
Write-Host "Después de Ctrl+Z: x=$($undone.x) y=$($undone.y)"

$moved = [Math]::Abs($after.x - $before.x) -gt 50 -or [Math]::Abs($after.y - $before.y) -gt 50
$restored = [Math]::Abs($undone.x - $before.x) -lt 0.01 -and [Math]::Abs($undone.y - $before.y) -lt 0.01
Write-Host "Arrastre aplicado y guardado: $moved · Deshacer restauró: $restored"
if (-not ($moved -and $restored)) { exit 1 }
