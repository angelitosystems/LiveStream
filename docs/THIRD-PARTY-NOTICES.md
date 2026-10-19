# Avisos de terceros

Generado por `scripts/third-party-inventory.ps1` el 2026-10-19. Inventario técnico de lo que se
distribuye con LiveStream; **no es asesoría legal**.

## Nota sobre la GPL

LiveStream se distribuye bajo **GPL-2.0-or-later** (archivo `LICENSE`), la misma licencia que libobs, al que enlaza.
Quien redistribuya el instalador o el paquete portable debe ofrecer también el código fuente correspondiente
(https://github.com/angelitosystems/LiveStream, en el tag de esa versión) bajo la misma licencia.

## Componentes nativos (runtime de libobs)

| Componente | Archivos | Licencia | Estado |
|---|---|---|---|
| libobs / plugins de OBS Studio 32.2.2 | obs.dll, libobs-*.dll, obs-plugins/64bit/*, obs-ffmpeg-mux.exe, obs-*-test.exe, data/ | GPL-2.0-or-later | Confirmado (repositorio obsproject/obs-studio) |
| LiveStream y livestream_bridge (propios) | LiveStream.*.dll, LiveStream.Desktop.exe, livestream_bridge.dll | GPL-2.0-or-later | Ver nota |
| x264 | libx264-164.dll | GPL-2.0-or-later | Confirmado |
| FFmpeg (compilación de OBS) | avcodec, avformat, avutil, avfilter, avdevice, swresample, swscale | LGPL-2.1+ o GPL según la configuración de la compilación de OBS | A confirmar con la configuración de obs-deps |
| libcurl | libcurl.dll | curl (tipo MIT) | Confirmado |
| zlib | zlib.dll | Zlib | Confirmado |
| SRT | srt.dll | MPL-2.0 | Confirmado |
| librist | librist.dll | BSD-2-Clause | Confirmado |
| libdatachannel | datachannel.dll | MPL-2.0 | Confirmado |
| pthreads-win32 | w32-pthreads.dll | LGPL-2.1 (o Apache-2.0 en versiones recientes) | A confirmar la versión incluida |
| Qt, Lua, API del frontend de OBS | — | — | **No se distribuyen** (excluidos del runtime en la Fase 7) |

## Paquetes .NET (licencia según el .nuspec de cada paquete)

| Paquete | Versión | Licencia | Proyecto |
|---|---|---|---|
| Anthropic | 12.58.0 | MIT | https://docs.anthropic.com/claude/reference/ |
| Avalonia | 11.3.22 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.Angle.Windows.Natives | 2.1.25547.20250602 | archivo en el paquete (LICENSE) | https://avaloniaui.net/ |
| Avalonia.BuildServices | 11.3.2 | MIT | https://avaloniaui.net/ |
| Avalonia.Controls.ColorPicker | 11.3.22 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.Desktop | 11.3.22 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.FreeDesktop | 11.3.22 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.Native | 11.3.22 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.Remote.Protocol | 11.3.22 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.Skia | 11.3.22 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.Themes.Fluent | 11.3.22 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.Win32 | 11.3.22 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| Avalonia.X11 | 11.3.22 | MIT | https://avaloniaui.net/?utm_source=nuget&utm_medium=referral&utm_content=project_homepage_link |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | https://github.com/CommunityToolkit/dotnet |
| HarfBuzzSharp | 8.3.1.1 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| HarfBuzzSharp.NativeAssets.Linux | 8.3.1.1 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| HarfBuzzSharp.NativeAssets.macOS | 8.3.1.1 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| HarfBuzzSharp.NativeAssets.WebAssembly | 8.3.1.1 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.1 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| MicroCom.Runtime | 0.11.0 | MIT |  |
| Microsoft.Extensions.AI.Abstractions | 10.5.1 | MIT | https://dot.net/ |
| Microsoft.Extensions.DependencyInjection | 10.0.0 | MIT | https://dot.net/ |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | MIT | https://dot.net/ |
| SkiaSharp | 2.88.9 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| SkiaSharp.NativeAssets.Linux | 2.88.9 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| SkiaSharp.NativeAssets.macOS | 2.88.9 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| SkiaSharp.NativeAssets.WebAssembly | 2.88.9 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| SkiaSharp.NativeAssets.Win32 | 2.88.9 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| System.Security.Cryptography.ProtectedData | 10.0.11 | MIT | https://dot.net/ |
| Tmds.DBus.Protocol | 0.21.3 | MIT |  |

## Runtime de .NET

La aplicación se publica autocontenida con el runtime de .NET (MIT, Microsoft).
