# Fase 0 — Decisiones y pendientes

## Hecho (sprint 1, pasos 1, 3 y 4)

- Solución `StreamPilot.sln` (.NET 10, x64, gestión central de paquetes, warnings como errores).
- `Domain`: `StudioProject`, `Scene`, `SceneItem`, `Source`, `Transform`, `CaptureDevice`.
- `Application`: puerto `IStreamingEngine`, `CommandExecutor` serializado con Undo/Redo, comandos de escena.
- `native/streampilot-bridge`: ABI C v1 (`streampilot_bridge.h`) e implementación sobre libobs (**aún sin compilar**).
- `Native`: `LibraryImport` + `EngineSafeHandle` + `NativeDispatcher` (todas las llamadas a libobs en un único hilo).
- `Desktop`: pantalla del spike (inicializar → escena cámara+mic → vista previa en HWND → grabar 30 s).
- Tests: 9 pruebas unitarias (comandos, serialización, layout de structs del ABI, dispatcher).

## Pendiente antes de cerrar la Fase 0

1. **Licencia (bloqueante para distribución comercial).** libobs es GPLv2+. Un binario que enlaza libobs
   (incluido el puente) debe distribuirse bajo GPL con su código fuente. Opciones a evaluar con asesoría legal:
   - Publicar la app de escritorio como GPL y monetizar servicios (relay, cuentas, IA en la nube).
   - Aislar libobs en un `EngineHost` GPL independiente comunicado por IPC (no garantiza por sí solo
     evitar la GPL; depende de cómo se acople y distribuya).
2. **Fijar versión de libobs.** Elegir un tag de `obsproject/obs-studio` y documentarlo aquí.
3. **Toolchain C++.** Instalar CMake ≥ 3.28 y el workload "Desarrollo de escritorio con C++" de VS 2022/2026
   (CMake no está en el PATH de esta máquina).
4. **Validar la vista previa** `NativeControlHost` + `obs_display` (riesgo principal de la tabla de riesgos).
5. Confirmar encoders disponibles en el hardware objetivo (NVENC / AMF / QSV / x264).

## Cómo continuar

```powershell
./scripts/build-native.ps1 -ObsTag <tag>   # compila libobs + plugins + puente en artifacts/obs-runtime
dotnet build StreamPilot.sln
dotnet run --project src/StreamPilot.Desktop
```
