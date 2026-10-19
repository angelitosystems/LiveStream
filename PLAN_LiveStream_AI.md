# Plan de desarrollo — LiveStream AI

**Estado:** en desarrollo — ver [docs/ESTADO.md](docs/ESTADO.md) para el avance real por fase.
**Plataforma inicial:** Windows x64
**Objetivo:** aplicación independiente de producción audiovisual y transmisiones en vivo, con interfaz propia, motor `libobs`, asistente de IA y optimización adaptativa.

## 1. Visión y alcance

LiveStream AI permite crear escenas, capturar pantalla/juegos/cámaras, mezclar audio, grabar y transmitir. Un asistente interpreta instrucciones naturales y ejecuta operaciones validadas sobre el estudio. El optimizador recomienda perfiles según el hardware, las condiciones de red y las métricas del motor. Incluye producción televisiva: transiciones profesionales, lower thirds, titulares, tickers, plantillas y animaciones por keyframes.

### Principios

- **Offline-first:** edición y grabación local sin requerir el backend.
- **Motor probado:** usar `libobs` en vez de reimplementar captura, mezcla y codificación.
- **Separación de responsabilidades:** la interfaz, la IA y el optimizador llaman a una única capa de casos de uso (`CommandExecutor`).
- **Operaciones reversibles:** comandos con validación, historial y Undo/Redo donde sea posible.
- **Seguridad:** claves de transmisión fuera de logs, prompts y bases de datos en texto plano.
- **Sin funcionalidad simulada:** nada se marca como terminado si no funciona contra el motor real.

## 2. Stack

| Área | Elección |
|---|---|
| Escritorio | C# / .NET 10, Avalonia UI 11.3, XAML, MVVM |
| Estado/comandos | CommunityToolkit.Mvvm, inyección de dependencias |
| Motor | libobs 32.2.2 (C/C++) |
| Interoperabilidad | DLL puente `livestream_bridge` con ABI C, `LibraryImport`, `SafeHandle` |
| Configuración local | SQLite y archivos de proyecto versionados |
| IA | LLM mediante tool calling con esquemas validados |
| Optimización | C# + telemetría de libobs + APIs nativas |
| Backend opcional | NestJS, TypeORM, PostgreSQL |
| Relay multistream | SRS o MediaMTX, sujeto a prueba técnica |
| Distribución | Instalador Windows, actualizaciones firmadas |
| Pruebas | xUnit (unitarias + integración contra libobs real) |

## 3. Arquitectura

```text
Avalonia Desktop (Views / ViewModels)
                |
         Application Layer
     (commands, queries, events)
       /       |         \
  AI Agent  Optimizer  Persistence / Cloud
       \       |         /
          IStreamingEngine
                |
      C# Native Adapter (LibObsEngine + NativeDispatcher STA)
                |
      C ABI / C++ Bridge DLL (livestream_bridge)
                |
             libobs
                |
       Encoder / Output / Relay
```

Reglas de dependencias:

1. `Domain` no depende de infraestructura.
2. `Application` depende de `Domain` y declara interfaces.
3. `Infrastructure`, `Native`, `AI` y `Optimizer` implementan interfaces de `Application`.
4. `Desktop` compone los servicios y no maneja punteros nativos.
5. Todas las llamadas a libobs pasan por un único hilo STA (`NativeDispatcher`).
6. libobs no admite reiniciarse en el mismo proceso: reiniciar el motor = reiniciar el proceso o usar un `EngineHost` separado por IPC (evaluar en Fase 7).

## 4. Fases

| Fase | Contenido | Gate |
|---|---|---|
| 1. Auditoría y estabilización | Motor libobs real: init, dispositivos, escena, vista previa, grabación, cierre | Ciclo completo estable y repetible |
| 2. Estudio profesional | Shell tipo OBS (menús, barra lateral, paneles acoplables), editor visual del lienzo, escenas, fuentes, mezclador, persistencia, grabación y transmisión RTMP | Transmisión y grabación estables en pruebas prolongadas |
| 3. Producción televisiva | Transiciones (corte, fundido, barrido, stinger), lower thirds, titulares, tickers, plantillas con datos dinámicos, keyframes sincronizados con el reloj del motor | Gráficos actualizables en directo sin reconstruir la escena |
| 4. Asistente IA | Catálogo de herramientas tipadas (`scene.*`, `source.*`, `transition.*`, `graphic.*`, `audio.*`, `stream.*`), permisos y confirmaciones | El asistente crea y modifica escenas sin acceso directo a libobs |
| 5. Optimización | Detección real de CPU/GPU/encoders, benchmark, perfiles, controlador con histéresis y rollback, panel de auditoría | Mejoras medibles o ausencia de regresiones |
| 6. Multistream | Destinos directos y relay (SRS/MediaMTX), credenciales seguras, reconexión, estadísticas por destino | Reconexión y supervisión verificadas |
| 7. Calidad y distribución | Pruebas de estrés, instalador, actualizaciones firmadas, revisión de licencias | Instalación limpia y estable |

## 5. Asistente IA — reglas

- Esquemas estrictos para argumentos y autorización por herramienta.
- Confirmación antes de emitir públicamente, detener una transmisión activa o eliminar datos.
- Sin acceso del LLM a terminal, punteros nativos, secretos ni archivos arbitrarios.
- Validación de estado antes y después de cada acción; el modelo propone, la capa de aplicación ejecuta.

## 6. Optimizador — reglas

- Monitoreo con medias móviles, umbrales e histéresis.
- Ajustes en vivo solo cuando el encoder/protocolo lo permita; los disruptivos (resolución, FPS, encoder) requieren autorización.
- La IA explica y recomienda; un controlador determinista decide los ajustes críticos.

## 7. Multistream — reglas

- No prometer acceso universal: TikTok y otras plataformas exigen permisos o elegibilidad.
- Credenciales del relay separadas y protegidas en tránsito y reposo.

## 8. Riesgos principales

| Riesgo | Mitigación |
|---|---|
| Integración gráfica Avalonia/libobs | **Resuelto en Fase 1** con `NativeControlHost` + `obs_display` |
| Crashes y ownership nativo | ABI reducida, `SafeHandle`, hilo único, pruebas de ciclo de vida repetidas |
| Licencia GPL y distribución comercial | Revisión legal antes de distribuir (ver docs/ESTADO.md) |
| Restricciones de plataformas | Conectores específicos y pruebas de elegibilidad |
| Cambios peligrosos por IA | Validación, permisos, confirmación y comandos reversibles |
| Ajustes inestables durante directo | Histéresis, cambios seguros y rollback |
