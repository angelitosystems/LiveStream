/*
 * streampilot_bridge — ABI C estable entre StreamPilot (.NET) y libobs.
 *
 * Reglas:
 *  - Todas las cadenas son UTF-8 terminadas en NUL. El llamador conserva su propiedad.
 *  - El único handle opaco es sp_engine. Escenas, fuentes, elementos de escena y displays se
 *    identifican por el GUID textual (36 caracteres) que asigna el dominio .NET.
 *  - Ninguna función lanza excepciones; todas devuelven sp_result. El detalle del último
 *    error del hilo actual se obtiene con sp_last_error().
 *  - Las funciones NO son seguras para llamadas concurrentes sobre el mismo sp_engine.
 *    El lado .NET las serializa en un único hilo (STA).
 *  - Las funciones que devuelven texto escriben en un búfer del llamador y devuelven en
 *    *required el tamaño necesario (incluido el NUL); si el búfer es pequeño devuelven
 *    SP_ERR_BUFFER_TOO_SMALL sin escribir.
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

#define SP_ABI_VERSION 3u

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
    SP_ERR_BUFFER_TOO_SMALL = 10,
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

typedef enum sp_display_mode {
    SP_DISPLAY_PROGRAM = 0, /* textura final del programa (lo que se emite) */
    SP_DISPLAY_SCENE = 1    /* una escena concreta, p. ej. la de vista previa en modo estudio */
} sp_display_mode;

typedef enum sp_output_kind {
    SP_OUTPUT_RECORDING = 0,
    SP_OUTPUT_STREAM = 1
} sp_output_kind;

typedef enum sp_monitoring {
    SP_MONITORING_NONE = 0,
    SP_MONITORING_MONITOR_ONLY = 1,
    SP_MONITORING_MONITOR_AND_OUTPUT = 2
} sp_monitoring;

typedef struct sp_engine sp_engine;

typedef struct sp_engine_config {
    uint32_t base_width;
    uint32_t base_height;
    uint32_t output_width;
    uint32_t output_height;
    uint32_t fps_num;
    uint32_t fps_den;
    uint32_t audio_sample_rate;
    uint32_t adapter_index;        /* adaptador D3D11 para renderizar */
    const char *module_bin_dir;    /* NULL: rutas por defecto de libobs (relativas al directorio de trabajo) */
    const char *module_data_dir;
    const char *libobs_data_dir;   /* p. ej. <app>/data/libobs/ */
    const char *module_config_dir; /* configuración que escriben los plugins */
} sp_engine_config;

typedef struct sp_encoder_config {
    const char *video_encoder_id; /* NULL o "auto": elige el mejor disponible */
    uint32_t video_bitrate_kbps;
    uint32_t audio_bitrate_kbps;
    uint32_t keyframe_interval_sec; /* 0: valor del encoder */
} sp_encoder_config;

typedef struct sp_recording_config {
    const char *output_path; /* el contenedor se deduce de la extensión (.mkv recomendado) */
    sp_encoder_config encoder;
} sp_recording_config;

typedef struct sp_stream_config {
    const char *server; /* rtmp:// o rtmps:// */
    const char *key;    /* nunca se registra en logs */
    sp_encoder_config encoder;
} sp_stream_config;

typedef struct sp_transform {
    float pos_x, pos_y;     /* esquina superior izquierda, en píxeles del lienzo */
    float scale_x, scale_y;
    float rotation_deg;
    int32_t crop_left, crop_top, crop_right, crop_bottom;
} sp_transform;

typedef struct sp_metrics {
    double active_fps;
    double average_frame_render_ms;
    double cpu_usage_percent;
    uint64_t memory_bytes;
    uint32_t total_frames;
    uint32_t lagged_frames;
    uint32_t skipped_encoder_frames;
    uint32_t encoded_frames;
    int32_t is_recording;
    int32_t is_streaming;
    int32_t stream_reconnecting;
    uint32_t stream_dropped_frames;
    uint32_t stream_total_frames;
    uint64_t stream_total_bytes;
    uint64_t recording_total_bytes;
} sp_metrics;

typedef struct sp_audio_levels {
    float peak_db[2];      /* -inf (=-100) .. 0 */
    float magnitude_db[2];
    uint32_t channels;
} sp_audio_levels;

typedef void(SP_CALL *sp_device_callback)(sp_device_kind kind, const char *id, const char *name, void *user_data);
typedef void(SP_CALL *sp_encoder_callback)(const char *id, const char *name, const char *codec, void *user_data);
typedef void(SP_CALL *sp_log_callback)(sp_log_level level, const char *message, void *user_data);

SP_API uint32_t SP_CALL sp_abi_version(void);
SP_API int32_t SP_CALL sp_last_error(char *buffer, int32_t buffer_size);
SP_API void SP_CALL sp_set_log_callback(sp_log_callback callback, void *user_data);

/* ---- Motor ------------------------------------------------------------------------------------
 * libobs es un singleton por proceso: solo puede existir un sp_engine a la vez y, una vez destruido,
 * no puede crearse otro en el mismo proceso (devuelve SP_ERR_INVALID_STATE). */
SP_API sp_result SP_CALL sp_engine_create(const sp_engine_config *config, sp_engine **out_engine);
SP_API void SP_CALL sp_engine_destroy(sp_engine *engine);
SP_API sp_result SP_CALL sp_metrics_get(sp_engine *engine, sp_metrics *out_metrics);
SP_API sp_result SP_CALL sp_devices_enumerate(sp_engine *engine, sp_device_callback callback, void *user_data);
SP_API sp_result SP_CALL sp_video_encoders_enumerate(sp_engine *engine, sp_encoder_callback callback, void *user_data);

/* ---- Escenas ---------------------------------------------------------------------------------- */
SP_API sp_result SP_CALL sp_scene_create(sp_engine *engine, const char *scene_id, const char *name);
SP_API sp_result SP_CALL sp_scene_remove(sp_engine *engine, const char *scene_id);
SP_API sp_result SP_CALL sp_scene_rename(sp_engine *engine, const char *scene_id, const char *name);
/* Cambia la escena de programa con la transición configurada (corte si no hay ninguna). */
SP_API sp_result SP_CALL sp_scene_set_program(sp_engine *engine, const char *scene_id);
/* type_id: "cut_transition", "fade_transition", "swipe_transition", "slide_transition", ... */
SP_API sp_result SP_CALL sp_transition_set(sp_engine *engine, const char *type_id, uint32_t duration_ms);

/* ---- Fuentes (compartibles entre escenas) ------------------------------------------------------ */
/* source_type es un id de libobs ("dshow_input", "wasapi_input_capture", "monitor_capture", ...). */
SP_API sp_result SP_CALL sp_source_create(sp_engine *engine, const char *source_id, const char *source_type,
                                          const char *name, const char *settings_json);
SP_API sp_result SP_CALL sp_source_remove(sp_engine *engine, const char *source_id);
SP_API sp_result SP_CALL sp_source_rename(sp_engine *engine, const char *source_id, const char *name);
SP_API sp_result SP_CALL sp_source_update(sp_engine *engine, const char *source_id, const char *settings_json);
SP_API sp_result SP_CALL sp_source_get_settings(sp_engine *engine, const char *source_id, char *buffer,
                                                int32_t buffer_size, int32_t *required);
/* Describe las propiedades editables en JSON: [{name, description, type, value?, items?, min?, max?, step?}]. */
SP_API sp_result SP_CALL sp_source_get_properties(sp_engine *engine, const char *source_id, char *buffer,
                                                  int32_t buffer_size, int32_t *required);
SP_API sp_result SP_CALL sp_source_get_size(sp_engine *engine, const char *source_id, uint32_t *width,
                                            uint32_t *height);
/* Canal de salida global (1..5) para fuentes que suenan en todas las escenas, p. ej. audio de escritorio
 * y micrófono. channel 0 quita la fuente de los canales globales. */
SP_API sp_result SP_CALL sp_source_set_output_channel(sp_engine *engine, const char *source_id, uint32_t channel);

/* ---- Audio ------------------------------------------------------------------------------------ */
SP_API sp_result SP_CALL sp_audio_set_volume(sp_engine *engine, const char *source_id, float multiplier);
SP_API sp_result SP_CALL sp_audio_set_muted(sp_engine *engine, const char *source_id, int32_t muted);
SP_API sp_result SP_CALL sp_audio_set_monitoring(sp_engine *engine, const char *source_id, sp_monitoring type);
SP_API sp_result SP_CALL sp_audio_set_sync_offset(sp_engine *engine, const char *source_id, int64_t offset_ms);
/* Últimos niveles medidos (actualizados por libobs ~cada 50 ms). Sin audio: SP_ERR_NOT_FOUND. */
SP_API sp_result SP_CALL sp_audio_get_levels(sp_engine *engine, const char *source_id, sp_audio_levels *levels);

/* ---- Elementos de escena ---------------------------------------------------------------------- */
SP_API sp_result SP_CALL sp_item_add(sp_engine *engine, const char *scene_id, const char *item_id,
                                     const char *source_id);
SP_API sp_result SP_CALL sp_item_remove(sp_engine *engine, const char *item_id);
SP_API sp_result SP_CALL sp_item_set_transform(sp_engine *engine, const char *item_id, const sp_transform *transform);
SP_API sp_result SP_CALL sp_item_get_transform(sp_engine *engine, const char *item_id, sp_transform *transform);
SP_API sp_result SP_CALL sp_item_set_visible(sp_engine *engine, const char *item_id, int32_t visible);
SP_API sp_result SP_CALL sp_item_set_locked(sp_engine *engine, const char *item_id, int32_t locked);
/* Posición en la pila de la escena: 0 = abajo. */
SP_API sp_result SP_CALL sp_item_set_order(sp_engine *engine, const char *item_id, int32_t index);

/* ---- Salidas ---------------------------------------------------------------------------------- */
SP_API sp_result SP_CALL sp_recording_start(sp_engine *engine, const sp_recording_config *config);
SP_API sp_result SP_CALL sp_recording_stop(sp_engine *engine);
SP_API sp_result SP_CALL sp_stream_start(sp_engine *engine, const sp_stream_config *config);
SP_API sp_result SP_CALL sp_stream_stop(sp_engine *engine);
/* Código y mensaje de la última detención de una salida (0 = detención normal). */
SP_API sp_result SP_CALL sp_output_last_stop(sp_engine *engine, sp_output_kind kind, int32_t *code, char *buffer,
                                             int32_t buffer_size, int32_t *required);

/* ---- Displays (ventanas nativas, HWND en Windows) --------------------------------------------- */
SP_API sp_result SP_CALL sp_display_create(sp_engine *engine, const char *display_id, void *native_window,
                                           uint32_t width, uint32_t height);
SP_API sp_result SP_CALL sp_display_destroy(sp_engine *engine, const char *display_id);
SP_API sp_result SP_CALL sp_display_resize(sp_engine *engine, const char *display_id, uint32_t width,
                                           uint32_t height);
/* scene_id solo se usa con SP_DISPLAY_SCENE. */
SP_API sp_result SP_CALL sp_display_set_source(sp_engine *engine, const char *display_id, sp_display_mode mode,
                                               const char *scene_id);
/* zoom 0 = ajustar a la ventana. offset en píxeles de la ventana. */
SP_API sp_result SP_CALL sp_display_set_view(sp_engine *engine, const char *display_id, float zoom, float offset_x,
                                             float offset_y);
/* Superposición del editor: contorno y tiradores de los elementos seleccionados y guías de alineación
 * (coordenadas del lienzo). Se dibuja en el hilo gráfico de libobs sobre la imagen. */
SP_API sp_result SP_CALL sp_display_set_overlay(sp_engine *engine, const char *display_id,
                                                const char *const *selected_item_ids, int32_t selected_count,
                                                const float *guides_x, int32_t guides_x_count, const float *guides_y,
                                                int32_t guides_y_count);

#ifdef __cplusplus
}
#endif

#endif /* STREAMPILOT_BRIDGE_H */
