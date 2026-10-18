/*
 * streampilot_bridge — ABI C estable entre StreamPilot (.NET) y libobs.
 *
 * Reglas:
 *  - Todas las cadenas son UTF-8 terminadas en NUL. El llamador conserva su propiedad.
 *  - El único handle opaco es sp_engine. Escenas y fuentes se identifican por el GUID
 *    textual (36 caracteres) que asigna el dominio .NET.
 *  - Ninguna función lanza excepciones; todas devuelven sp_result. El detalle del último
 *    error del hilo actual se obtiene con sp_last_error().
 *  - Las funciones NO son seguras para llamadas concurrentes sobre el mismo sp_engine.
 *    El lado .NET las serializa en un único hilo.
 *  - Incrementar SP_ABI_VERSION ante cualquier cambio incompatible.
 */
#ifndef STREAMPILOT_BRIDGE_H
#define STREAMPILOT_BRIDGE_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef SP_BRIDGE_EXPORTS
#    define SP_API __declspec(dllexport)
#  else
#    define SP_API __declspec(dllimport)
#  endif
#  define SP_CALL __cdecl
#else
#  define SP_API __attribute__((visibility("default")))
#  define SP_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define SP_ABI_VERSION 2u

typedef enum sp_result {
    SP_OK = 0,
    SP_ERR_INVALID_ARG = 1,
    SP_ERR_ALREADY_INITIALIZED = 2,
    SP_ERR_STARTUP_FAILED = 3,
    SP_ERR_VIDEO_RESET_FAILED = 4,
    SP_ERR_AUDIO_RESET_FAILED = 5,
    SP_ERR_NOT_FOUND = 6,
    SP_ERR_CREATE_FAILED = 7,
    SP_ERR_OUTPUT_FAILED = 8,
    SP_ERR_INVALID_STATE = 9,
    SP_ERR_INTERNAL = 99
} sp_result;

typedef enum sp_device_kind {
    SP_DEVICE_CAMERA = 0,
    SP_DEVICE_MICROPHONE = 1,
    SP_DEVICE_AUDIO_OUTPUT = 2,
    SP_DEVICE_DISPLAY = 3
} sp_device_kind;

typedef enum sp_log_level {
    SP_LOG_ERROR = 100,
    SP_LOG_WARNING = 200,
    SP_LOG_INFO = 300,
    SP_LOG_DEBUG = 400
} sp_log_level;

typedef struct sp_engine sp_engine;

typedef struct sp_engine_config {
    uint32_t base_width;
    uint32_t base_height;
    uint32_t output_width;
    uint32_t output_height;
    uint32_t fps_num;
    uint32_t fps_den;
    uint32_t audio_sample_rate;
    const char *module_bin_dir;   /* p. ej. <app>/obs-plugins/64bit */
    const char *module_data_dir;  /* p. ej. <app>/data/obs-plugins/%module% */
    const char *libobs_data_dir;  /* p. ej. <app>/data/libobs */
    const char *module_config_dir; /* configuración que escriben los plugins, p. ej. %APPDATA%/StreamPilot/plugin_config */
} sp_engine_config;

typedef struct sp_recording_config {
    const char *output_path;      /* .mkv recomendado: tolera cierres abruptos */
    uint32_t video_bitrate_kbps;
    uint32_t audio_bitrate_kbps;
    const char *video_encoder_id; /* NULL o "auto": elige el mejor disponible */
} sp_recording_config;

typedef struct sp_metrics {
    double active_fps;
    double average_frame_render_ms;
    uint32_t total_frames;
    uint32_t lagged_frames;
    uint32_t skipped_encoder_frames;
    int32_t is_recording;
} sp_metrics;

typedef void(SP_CALL *sp_device_callback)(sp_device_kind kind, const char *id, const char *name, void *user_data);
typedef void(SP_CALL *sp_log_callback)(sp_log_level level, const char *message, void *user_data);

SP_API uint32_t SP_CALL sp_abi_version(void);

/* Copia el último mensaje de error del hilo actual. Devuelve los bytes necesarios (incluido el NUL). */
SP_API int32_t SP_CALL sp_last_error(char *buffer, int32_t buffer_size);

/* Debe llamarse antes de sp_engine_create. El callback puede invocarse desde cualquier hilo. */
SP_API void SP_CALL sp_set_log_callback(sp_log_callback callback, void *user_data);

/* libobs es un singleton por proceso: solo puede existir un sp_engine a la vez y, una vez destruido,
 * no puede crearse otro en el mismo proceso (devuelve SP_ERR_INVALID_STATE). */
SP_API sp_result SP_CALL sp_engine_create(const sp_engine_config *config, sp_engine **out_engine);
SP_API void SP_CALL sp_engine_destroy(sp_engine *engine);

SP_API sp_result SP_CALL sp_devices_enumerate(sp_engine *engine, sp_device_callback callback, void *user_data);

SP_API sp_result SP_CALL sp_scene_create(sp_engine *engine, const char *scene_id, const char *name);
SP_API sp_result SP_CALL sp_scene_set_program(sp_engine *engine, const char *scene_id);

/* source_type es un id de libobs ("dshow_input", "wasapi_input_capture", "monitor_capture", ...). */
SP_API sp_result SP_CALL sp_scene_add_source(sp_engine *engine, const char *scene_id, const char *source_id,
                                             const char *source_type, const char *name, const char *settings_json);

SP_API sp_result SP_CALL sp_recording_start(sp_engine *engine, const sp_recording_config *config);
SP_API sp_result SP_CALL sp_recording_stop(sp_engine *engine);

SP_API sp_result SP_CALL sp_metrics_get(sp_engine *engine, sp_metrics *out_metrics);

/* Vista previa en una ventana nativa (HWND en Windows). Una sola vista previa por motor en el spike. */
SP_API sp_result SP_CALL sp_preview_attach(sp_engine *engine, void *native_window, uint32_t width, uint32_t height);
SP_API sp_result SP_CALL sp_preview_resize(sp_engine *engine, uint32_t width, uint32_t height);
SP_API sp_result SP_CALL sp_preview_detach(sp_engine *engine);

#ifdef __cplusplus
}
#endif

#endif /* STREAMPILOT_BRIDGE_H */
