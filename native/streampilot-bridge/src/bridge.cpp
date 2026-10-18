// Implementación del ABI de streampilot_bridge sobre libobs.
// NOTA: aún no compilado contra una versión fijada de libobs (ver docs/fase-0.md).

#include "streampilot_bridge.h"

#include <obs.h>
#include <util/base.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <exception>
#include <map>
#include <string>
#include <thread>

namespace {

thread_local std::string g_last_error;
std::atomic<bool> g_engine_alive{false};
// libobs y sus plugins mantienen estado estático: tras obs_shutdown no se puede volver a iniciar en el
// mismo proceso (OBS Studio tampoco lo hace). Un reinicio del motor implica reiniciar el proceso o
// usar un EngineHost separado.
std::atomic<bool> g_engine_was_shutdown{false};
sp_log_callback g_log_callback = nullptr;
void *g_log_user_data = nullptr;

sp_result fail(sp_result code, const std::string &message)
{
    g_last_error = message;
    return code;
}

void log_handler(int level, const char *format, va_list args, void *)
{
    if (!g_log_callback)
        return;
    char buffer[4096];
    std::vsnprintf(buffer, sizeof(buffer), format, args);
    g_log_callback(static_cast<sp_log_level>(level), buffer, g_log_user_data);
}

bool has_encoder(const char *wanted)
{
    const char *id = nullptr;
    for (size_t i = 0; obs_enum_encoder_types(i, &id); ++i) {
        if (std::strcmp(id, wanted) == 0)
            return true;
    }
    return false;
}

const char *pick_video_encoder(const char *requested)
{
    if (requested && *requested && std::strcmp(requested, "auto") != 0)
        return has_encoder(requested) ? requested : nullptr;

    // Orden de preferencia: hardware (NVIDIA, AMD, Intel) y como último recurso x264 por CPU.
    static const char *preferred[] = {"obs_nvenc_h264_tex", "jim_nvenc", "h264_texture_amf", "obs_qsv11_v2",
                                      "obs_qsv11", "obs_x264"};
    for (const char *id : preferred) {
        if (has_encoder(id))
            return id;
    }
    return nullptr;
}

// libobs no informa por qué falla un efecto base al iniciar el video. Se reproduce la creación
// del dispositivo y de los efectos pidiendo el texto del error para incluirlo en el mensaje.
std::string diagnose_graphics(const char *module)
{
    graphics_t *graphics = nullptr;
    const int rc = gs_create(&graphics, module, 0);
    if (rc != GS_SUCCESS)
        return " | gs_create=" + std::to_string(rc);

    std::string report;
    gs_enter_context(graphics);
    for (const char *name : {"default.effect", "opaque.effect", "solid.effect", "format_conversion.effect",
                             "premultiplied_alpha.effect"}) {
        char *path = obs_find_data_file(name);
        if (!path) {
            report += std::string(" | no encontrado: ") + name;
            continue;
        }
        char *error = nullptr;
        gs_effect_t *effect = gs_effect_create_from_file(path, &error);
        if (!effect)
            report += std::string(" | ") + name + ": " + (error ? error : "sin detalle");
        bfree(error);
        bfree(path);
    }
    gs_leave_context();
    gs_destroy(graphics);
    return report.empty() ? " | efectos base OK" : report;
}

template<typename Fn> sp_result guarded(Fn &&fn)
{
    try {
        return fn();
    } catch (const std::exception &ex) {
        return fail(SP_ERR_INTERNAL, ex.what());
    } catch (...) {
        return fail(SP_ERR_INTERNAL, "Excepción nativa desconocida.");
    }
}

} // namespace

struct sp_engine {
    std::map<std::string, obs_scene_t *> scenes;
    std::map<std::string, obs_source_t *> sources;

    obs_encoder_t *video_encoder = nullptr;
    obs_encoder_t *audio_encoder = nullptr;
    obs_output_t *recording = nullptr;

    obs_display_t *display = nullptr;
    uint32_t display_cx = 0;
    uint32_t display_cy = 0;

    void release_recording()
    {
        obs_output_release(recording);
        obs_encoder_release(video_encoder);
        obs_encoder_release(audio_encoder);
        recording = nullptr;
        video_encoder = nullptr;
        audio_encoder = nullptr;
    }
};

namespace {

void render_preview(void *param, uint32_t cx, uint32_t cy)
{
    (void)param;
    obs_video_info ovi{};
    if (!obs_get_video_info(&ovi) || cx == 0 || cy == 0)
        return;

    // Letterbox: encaja el lienzo base dentro de la ventana conservando la relación de aspecto.
    const double scale = std::min(double(cx) / ovi.base_width, double(cy) / ovi.base_height);
    const int w = int(ovi.base_width * scale);
    const int h = int(ovi.base_height * scale);
    const int x = (int(cx) - w) / 2;
    const int y = (int(cy) - h) / 2;

    gs_viewport_push();
    gs_projection_push();
    gs_ortho(0.0f, float(ovi.base_width), 0.0f, float(ovi.base_height), -100.0f, 100.0f);
    gs_set_viewport(x, y, w, h);
    obs_render_main_texture();
    gs_projection_pop();
    gs_viewport_pop();
}

} // namespace

extern "C" {

uint32_t SP_CALL sp_abi_version(void)
{
    return SP_ABI_VERSION;
}

int32_t SP_CALL sp_last_error(char *buffer, int32_t buffer_size)
{
    const auto required = static_cast<int32_t>(g_last_error.size() + 1);
    if (buffer && buffer_size > 0) {
        const auto n = std::min<int32_t>(buffer_size - 1, required - 1);
        std::memcpy(buffer, g_last_error.data(), n);
        buffer[n] = '\0';
    }
    return required;
}

void SP_CALL sp_set_log_callback(sp_log_callback callback, void *user_data)
{
    g_log_callback = callback;
    g_log_user_data = user_data;
    base_set_log_handler(callback ? log_handler : nullptr, nullptr);
}

sp_result SP_CALL sp_engine_create(const sp_engine_config *config, sp_engine **out_engine)
{
    return guarded([&]() -> sp_result {
        if (!config || !out_engine)
            return fail(SP_ERR_INVALID_ARG, "config y out_engine son obligatorios.");
        *out_engine = nullptr;

        if (g_engine_was_shutdown)
            return fail(SP_ERR_INVALID_STATE,
                        "libobs no admite reiniciarse en el mismo proceso; reinicia la aplicación.");
        bool expected = false;
        if (!g_engine_alive.compare_exchange_strong(expected, true))
            return fail(SP_ERR_ALREADY_INITIALIZED, "libobs ya está inicializado en este proceso.");

        auto abort_startup = [](sp_result code, const char *msg) {
            if (obs_initialized()) {
                obs_shutdown();
                g_engine_was_shutdown = true;
            }
            g_engine_alive = false;
            return fail(code, msg);
        };

        // Obsoleta desde OBS 31 pero sigue exportada y es la única forma de ubicar data/libobs
        // cuando el ejecutable no vive en bin/64bit como en la distribución de OBS.
        if (config->libobs_data_dir) {
#ifdef _MSC_VER
#pragma warning(push)
#pragma warning(disable : 4996)
#endif
            obs_add_data_path(config->libobs_data_dir);
#ifdef _MSC_VER
#pragma warning(pop)
#endif
        }

        // Sin module_config_dir los plugins escriben su configuración en el directorio de trabajo.
        if (!obs_startup("es-ES", config->module_config_dir, nullptr))
            return abort_startup(SP_ERR_STARTUP_FAILED, "obs_startup falló.");

        char *effect = obs_find_data_file("default.effect");
        if (!effect)
            return abort_startup(SP_ERR_STARTUP_FAILED, "No se encontró data/libobs/default.effect.");
        blog(LOG_INFO, "[streampilot] libobs data: %s", effect);
        bfree(effect);

        obs_audio_info oai{};
        oai.samples_per_sec = config->audio_sample_rate ? config->audio_sample_rate : 48000;
        oai.speakers = SPEAKERS_STEREO;
        if (!obs_reset_audio(&oai))
            return abort_startup(SP_ERR_AUDIO_RESET_FAILED, "obs_reset_audio falló.");

        obs_video_info ovi{};
        ovi.graphics_module = "libobs-d3d11.dll";
        ovi.fps_num = config->fps_num ? config->fps_num : 60;
        ovi.fps_den = config->fps_den ? config->fps_den : 1;
        ovi.base_width = config->base_width;
        ovi.base_height = config->base_height;
        ovi.output_width = config->output_width;
        ovi.output_height = config->output_height;
        ovi.output_format = VIDEO_FORMAT_NV12;
        ovi.adapter = 0;
        ovi.gpu_conversion = true;
        ovi.colorspace = VIDEO_CS_709;
        ovi.range = VIDEO_RANGE_PARTIAL;
        ovi.scale_type = OBS_SCALE_BICUBIC;
        const int video = obs_reset_video(&ovi);
        if (video != OBS_VIDEO_SUCCESS) {
            std::string detail = "obs_reset_video falló con código " + std::to_string(video);
            detail += diagnose_graphics(ovi.graphics_module);
            return abort_startup(SP_ERR_VIDEO_RESET_FAILED, detail.c_str());
        }

        // Los módulos se cargan después de inicializar audio y video.
        if (config->module_bin_dir && config->module_data_dir)
            obs_add_module_path(config->module_bin_dir, config->module_data_dir);
        obs_load_all_modules();
        obs_log_loaded_modules();
        obs_post_load_modules();

        *out_engine = new sp_engine();
        return SP_OK;
    });
}

void SP_CALL sp_engine_destroy(sp_engine *engine)
{
    if (!engine)
        return;
    try {
        sp_preview_detach(engine);
        if (engine->recording && obs_output_active(engine->recording))
            obs_output_force_stop(engine->recording);
        engine->release_recording();

        // Igual que el cierre de OBS Studio: quitar la salida de programa, retirar escenas (lo que suelta
        // sus elementos) y fuentes, y esperar a que los hilos de audio/video y la cola de destrucción
        // terminen. Si win-wasapi se descarga con una fuente viva, su hilo de captura accede a código
        // ya liberado y el proceso se cae de forma intermitente.
        obs_set_output_source(0, nullptr);
        for (auto &[id, scene] : engine->scenes) {
            obs_source_t *scene_source = obs_scene_get_source(scene);
            obs_source_remove(scene_source);
            obs_scene_release(scene);
        }
        for (auto &[id, source] : engine->sources) {
            obs_source_remove(source);
            obs_source_release(source);
        }
        delete engine;

        // Dos pasadas: la destrucción de una escena encola la de sus fuentes.
        obs_wait_for_destroy_queue();
        obs_wait_for_destroy_queue();
        obs_shutdown();
        g_engine_was_shutdown = true;
        // libobs sigue registrando mensajes al descargarse (fugas de memoria, DllMain). Si el handler
        // apunta a código administrado y el runtime .NET ya está cerrándose, el proceso se cae.
        base_set_log_handler(nullptr, nullptr);
        g_log_callback = nullptr;
        g_log_user_data = nullptr;
    } catch (...) {
        // El destructor no puede propagar errores a través del ABI.
    }
    g_engine_alive = false;
}

sp_result SP_CALL sp_devices_enumerate(sp_engine *engine, sp_device_callback callback, void *user_data)
{
    return guarded([&]() -> sp_result {
        if (!engine || !callback)
            return fail(SP_ERR_INVALID_ARG, "engine y callback son obligatorios.");

        struct Probe {
            const char *source_type;
            const char *property;
            sp_device_kind kind;
        };
        static const Probe probes[] = {
            {"dshow_input", "video_device_id", SP_DEVICE_CAMERA},
            {"wasapi_input_capture", "device_id", SP_DEVICE_MICROPHONE},
            {"wasapi_output_capture", "device_id", SP_DEVICE_AUDIO_OUTPUT},
            {"monitor_capture", "monitor_id", SP_DEVICE_DISPLAY},
        };

        for (const auto &probe : probes) {
            obs_properties_t *props = obs_get_source_properties(probe.source_type);
            if (!props)
                continue; // El módulo no está cargado: se omite en vez de fallar.
            obs_property_t *list = obs_properties_get(props, probe.property);
            const size_t count = list ? obs_property_list_item_count(list) : 0;
            for (size_t i = 0; i < count; ++i) {
                const char *id = obs_property_list_item_string(list, i);
                const char *name = obs_property_list_item_name(list, i);
                if (id && *id)
                    callback(probe.kind, id, name ? name : id, user_data);
            }
            obs_properties_destroy(props);
        }
        return SP_OK;
    });
}

sp_result SP_CALL sp_scene_create(sp_engine *engine, const char *scene_id, const char *name)
{
    return guarded([&]() -> sp_result {
        if (!engine || !scene_id || !name)
            return fail(SP_ERR_INVALID_ARG, "engine, scene_id y name son obligatorios.");
        if (engine->scenes.count(scene_id))
            return fail(SP_ERR_INVALID_STATE, "La escena ya existe.");

        obs_scene_t *scene = obs_scene_create(name);
        if (!scene)
            return fail(SP_ERR_CREATE_FAILED, "obs_scene_create falló.");
        engine->scenes.emplace(scene_id, scene);
        return SP_OK;
    });
}

sp_result SP_CALL sp_scene_set_program(sp_engine *engine, const char *scene_id)
{
    return guarded([&]() -> sp_result {
        if (!engine || !scene_id)
            return fail(SP_ERR_INVALID_ARG, "engine y scene_id son obligatorios.");
        const auto it = engine->scenes.find(scene_id);
        if (it == engine->scenes.end())
            return fail(SP_ERR_NOT_FOUND, "Escena no encontrada.");
        obs_set_output_source(0, obs_scene_get_source(it->second));
        return SP_OK;
    });
}

sp_result SP_CALL sp_scene_add_source(sp_engine *engine, const char *scene_id, const char *source_id,
                                      const char *source_type, const char *name, const char *settings_json)
{
    return guarded([&]() -> sp_result {
        if (!engine || !scene_id || !source_id || !source_type || !name)
            return fail(SP_ERR_INVALID_ARG, "Faltan argumentos obligatorios.");
        const auto scene = engine->scenes.find(scene_id);
        if (scene == engine->scenes.end())
            return fail(SP_ERR_NOT_FOUND, "Escena no encontrada.");

        obs_source_t *source = nullptr;
        if (const auto existing = engine->sources.find(source_id); existing != engine->sources.end()) {
            source = existing->second;
        } else {
            obs_data_t *settings = settings_json && *settings_json ? obs_data_create_from_json(settings_json)
                                                                   : obs_data_create();
            source = obs_source_create(source_type, name, settings, nullptr);
            obs_data_release(settings);
            if (!source)
                return fail(SP_ERR_CREATE_FAILED, std::string("No se pudo crear la fuente de tipo ") + source_type);
            engine->sources.emplace(source_id, source);
        }

        if (!obs_scene_add(scene->second, source))
            return fail(SP_ERR_CREATE_FAILED, "obs_scene_add falló.");
        return SP_OK;
    });
}

sp_result SP_CALL sp_recording_start(sp_engine *engine, const sp_recording_config *config)
{
    return guarded([&]() -> sp_result {
        if (!engine || !config || !config->output_path)
            return fail(SP_ERR_INVALID_ARG, "engine y output_path son obligatorios.");
        if (engine->recording && obs_output_active(engine->recording))
            return fail(SP_ERR_INVALID_STATE, "Ya hay una grabación activa.");
        engine->release_recording();

        const char *encoder_id = pick_video_encoder(config->video_encoder_id);
        if (!encoder_id)
            return fail(SP_ERR_NOT_FOUND, "No hay ningún codificador de video disponible.");

        obs_data_t *venc_settings = obs_data_create();
        obs_data_set_string(venc_settings, "rate_control", "CBR");
        obs_data_set_int(venc_settings, "bitrate", config->video_bitrate_kbps ? config->video_bitrate_kbps : 6000);
        engine->video_encoder = obs_video_encoder_create(encoder_id, "sp_video", venc_settings, nullptr);
        obs_data_release(venc_settings);

        obs_data_t *aenc_settings = obs_data_create();
        obs_data_set_int(aenc_settings, "bitrate", config->audio_bitrate_kbps ? config->audio_bitrate_kbps : 160);
        engine->audio_encoder = obs_audio_encoder_create("ffmpeg_aac", "sp_audio", aenc_settings, 0, nullptr);
        obs_data_release(aenc_settings);

        obs_data_t *out_settings = obs_data_create();
        obs_data_set_string(out_settings, "path", config->output_path);
        engine->recording = obs_output_create("ffmpeg_muxer", "sp_recording", out_settings, nullptr);
        obs_data_release(out_settings);

        if (!engine->video_encoder || !engine->audio_encoder || !engine->recording) {
            engine->release_recording();
            return fail(SP_ERR_CREATE_FAILED, "No se pudieron crear los codificadores o la salida de grabación.");
        }

        obs_encoder_set_video(engine->video_encoder, obs_get_video());
        obs_encoder_set_audio(engine->audio_encoder, obs_get_audio());
        obs_output_set_video_encoder(engine->recording, engine->video_encoder);
        obs_output_set_audio_encoder(engine->recording, engine->audio_encoder, 0);

        if (!obs_output_start(engine->recording)) {
            const char *err = obs_output_get_last_error(engine->recording);
            std::string message = std::string("obs_output_start falló: ") + (err ? err : "sin detalle");
            engine->release_recording();
            return fail(SP_ERR_OUTPUT_FAILED, message);
        }
        return SP_OK;
    });
}

sp_result SP_CALL sp_recording_stop(sp_engine *engine)
{
    return guarded([&]() -> sp_result {
        if (!engine)
            return fail(SP_ERR_INVALID_ARG, "engine es obligatorio.");
        if (!engine->recording || !obs_output_active(engine->recording))
            return fail(SP_ERR_INVALID_STATE, "No hay ninguna grabación activa.");

        // obs_output_stop es asíncrono: espera a que el muxer vacíe y cierre el archivo.
        obs_output_stop(engine->recording);
        using namespace std::chrono;
        const auto deadline = steady_clock::now() + seconds(10);
        while (obs_output_active(engine->recording) && steady_clock::now() < deadline)
            std::this_thread::sleep_for(milliseconds(50));

        if (obs_output_active(engine->recording)) {
            obs_output_force_stop(engine->recording);
            engine->release_recording();
            return fail(SP_ERR_OUTPUT_FAILED, "La grabación no se detuvo a tiempo; se forzó la parada.");
        }
        engine->release_recording();
        return SP_OK;
    });
}

sp_result SP_CALL sp_metrics_get(sp_engine *engine, sp_metrics *out_metrics)
{
    return guarded([&]() -> sp_result {
        if (!engine || !out_metrics)
            return fail(SP_ERR_INVALID_ARG, "engine y out_metrics son obligatorios.");
        out_metrics->active_fps = obs_get_active_fps();
        out_metrics->average_frame_render_ms = double(obs_get_average_frame_time_ns()) / 1'000'000.0;
        out_metrics->total_frames = obs_get_total_frames();
        out_metrics->lagged_frames = obs_get_lagged_frames();
        out_metrics->skipped_encoder_frames = video_output_get_skipped_frames(obs_get_video());
        out_metrics->is_recording = engine->recording && obs_output_active(engine->recording) ? 1 : 0;
        return SP_OK;
    });
}

sp_result SP_CALL sp_preview_attach(sp_engine *engine, void *native_window, uint32_t width, uint32_t height)
{
    return guarded([&]() -> sp_result {
        if (!engine || !native_window || !width || !height)
            return fail(SP_ERR_INVALID_ARG, "engine, ventana y tamaño son obligatorios.");
        if (engine->display)
            return fail(SP_ERR_INVALID_STATE, "Ya hay una vista previa activa.");

        gs_init_data init{};
        init.cx = width;
        init.cy = height;
        init.format = GS_BGRA;
        init.zsformat = GS_ZS_NONE;
#ifdef _WIN32
        init.window.hwnd = native_window;
#endif
        engine->display = obs_display_create(&init, 0x000000);
        if (!engine->display)
            return fail(SP_ERR_CREATE_FAILED, "obs_display_create falló.");
        engine->display_cx = width;
        engine->display_cy = height;
        obs_display_add_draw_callback(engine->display, render_preview, engine);
        return SP_OK;
    });
}

sp_result SP_CALL sp_preview_resize(sp_engine *engine, uint32_t width, uint32_t height)
{
    return guarded([&]() -> sp_result {
        if (!engine || !engine->display)
            return fail(SP_ERR_INVALID_STATE, "No hay vista previa activa.");
        if (width && height) {
            obs_display_resize(engine->display, width, height);
            engine->display_cx = width;
            engine->display_cy = height;
        }
        return SP_OK;
    });
}

sp_result SP_CALL sp_preview_detach(sp_engine *engine)
{
    return guarded([&]() -> sp_result {
        if (!engine)
            return fail(SP_ERR_INVALID_ARG, "engine es obligatorio.");
        if (engine->display) {
            obs_display_remove_draw_callback(engine->display, render_preview, engine);
            obs_display_destroy(engine->display);
            engine->display = nullptr;
        }
        return SP_OK;
    });
}

} // extern "C"
