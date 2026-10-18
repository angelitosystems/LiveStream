# Estado real del proyecto

Actualizado al cierre de la **Fase 1 — Auditoría y estabilización**. Solo se marca como hecho lo que se
verificó contra libobs real en esta máquina (i7-13650HX, Intel UHD + RTX 4050 Laptop, Windows 11).

## Fase 1 — completada

| Funcionalidad | Estado | Cómo se verificó |
|---|---|---|
| Compilar el puente nativo contra libobs 32.2.2 | Hecho | `scripts/build-native.ps1`, MSVC `/W4 /WX` sin avisos |
| Inicializar y apagar libobs | Hecho | Prueba de integración, 10 ejecuciones seguidas sin fallos |
| Enumerar cámaras, micrófonos, salidas de audio y pantallas | Hecho | 9 dispositivos detectados |
| Crear escena con captura de pantalla + micrófono | Hecho | Prueba de integración |
| Grabar MKV con audio y video | Hecho | 5 s a 30 FPS, 0 frames con lag, ~1,5 MB |
| Vista previa real en Avalonia | Hecho | `NativeControlHost` + `obs_display`; webcam en vivo, captura de pantalla de la app |
| Cerrar y volver a abrir la app | Hecho | 3 ciclos de arranque/cierre con código de salida 0 |
| Rechazo controlado del reinicio en el mismo proceso | Hecho | Prueba de integración (error claro en lugar de crash) |

### Problemas encontrados y corregidos

1. **Rutas con `\`**: el preprocesador de efectos de libobs solo reconoce `/` al resolver `#include`;
   los shaders base no compilaban y `obs_reset_video` devolvía -1 sin explicación. `LibObsEngine.ObsPath` normaliza las rutas.
2. **COM**: el hilo nativo debe ser STA (libobs llama `CoInitializeEx(COINIT_APARTMENTTHREADED)`).
3. **Plugins de UI Qt** (`frontend-tools`, `obs-websocket`, `obs-vst`, `decklink-*-ui`, `aja-output-ui`) cuelgan el proceso
   sin la interfaz de OBS: el runtime solo incluye una lista explícita de plugins del motor.
4. **Crash intermitente al apagar** con micrófono WASAPI: las fuentes deben retirarse (`obs_source_remove`) y la cola de
   destrucción vaciarse antes de `obs_shutdown`, como hace OBS Studio al cerrar.
5. **Callback de log**: se desconecta tras `obs_shutdown`; libobs sigue registrando al descargarse.
6. **Rutas relativas de plugins**: win-capture busca sus hooks en `..\..\data`; la app fija un directorio de trabajo ancla
   (`obs-anchor/64bit`) para reproducir la estructura de OBS.

## Limitaciones conocidas

- **Reinicio del motor**: libobs no puede reiniciarse dentro del mismo proceso. Reconfigurar video (resolución/FPS) en
  caliente sí es posible con `obs_reset_video` mientras no haya salidas activas; reiniciar el motor completo requerirá un
  `EngineHost` en proceso aparte.
- **GPU de render**: libobs usa el adaptador 0 (Intel UHD en este portátil). NVENC en modo textura falla y hace fallback a
  modo memoria. La selección de adaptador y encoder con prueba real es trabajo de la Fase 5.
- **Fuentes de navegador**: `obs-browser` (CEF) no está incluido todavía.
- **RTWQ**: win-wasapi registra `RtwqLockSharedWorkQueue failed` y usa su hilo de captura clásico; funciona.
- **Runtime de desarrollo**: el modo `Prebuilt` toma los binarios de una instalación local de OBS 32.2.2 y los copia a
  `artifacts/`; la app no depende de OBS instalado al ejecutarse. Para distribución hay que usar el modo `Source`
  (compilar libobs desde el tag), aún no validado.
- **Licencia (bloqueante para vender)**: libobs es GPLv2+. Un binario que lo enlaza debe distribuirse bajo GPL con su
  código. Hay que decidir el modelo con asesoría legal antes de distribuir (p. ej. app GPL + servicios de pago).

## Pendiente por fase

- **Fase 2 — Estudio profesional**: shell tipo OBS (menús, barra lateral, paneles acoplables), editor visual del lienzo
  (arrastrar, escalar, recortar, rotar, guías, zoom), gestión completa de escenas/fuentes en el puente, mezclador con
  medidores (volmeter de libobs), persistencia SQLite con autoguardado, transmisión RTMP.
- **Fases 3–7**: sin empezar (ver `PLAN_StreamPilot_AI.md`).

## Cómo ejecutar

```powershell
./scripts/build-native.ps1          # una vez: runtime libobs + puente en artifacts/obs-runtime
dotnet build StreamPilot.sln
dotnet test StreamPilot.sln         # unitarias + integración contra libobs real (requiere GPU y escritorio)
dotnet run --project src/StreamPilot.Desktop -- --autostart
```
