// Implementación del ABI de streampilot_bridge sobre libobs 32.2.2.

#include "streampilot_bridge.h"

#include <obs.h>
#include <obs-audio-controls.h>
#include <graphics/matrix4.h>
#include <graphics/vec3.h>
#include <util/base.h>
#include <util/platform.h>

#ifdef _WIN32
#include <windows.h>
#include <rtworkq.h>
#endif

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <exception>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {

thread_local std::string g_last_error;
std::atomic<bool> g_engine_alive{false};
// libobs y sus plugins mantienen estado estático: tras obs_shutdown no se puede volver a iniciar en el
// mismo proceso (OBS Studio tampoco lo hace). Un reinicio del motor implica reiniciar el proceso o
// usar un EngineHost separado.
std::atomic<bool> g_engine_was_shutdown{false};
sp_log_callback g_log_callback = nullptr;
void *g_log_user_data = nullptr;

constexpr float kSilenceDb = -100.0f;

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

sp_result copy_out(const std::string &text, char *buffer, int32_t buffer_size, int32_t *required)
{
    const auto needed = static_cast<int32_t>(text.size() + 1);
    if (required)
        *required = needed;
    if (!buffer || buffer_size < needed)
        return buffer ? fail(SP_ERR_BUFFER_TOO_SMALL, "Búfer insuficiente.") : SP_OK;
    std::memcpy(buffer, text.c_str(), needed);
    return SP_OK;
}

void json_escape(std::string &out, const char *text)
{
    out += '"';
    for (const char *p = text ? text : ""; *p; ++p) {
        const auto c = static_cast<unsigned char>(*p);
        switch (c) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if (c < 0x20) {
                char hex[8];
                std::snprintf(hex, sizeof(hex), "\\u%04x", c);
                out += hex;
            } else {
                out += static_cast<char>(c);
            }
        }
    }
    out += '"';
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

bool input_type_exists(const char *wanted)
{
    const char *id = nullptr;
    for (size_t i = 0; obs_enum_input_types(i, &id); ++i) {
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
    // La validación real de cada encoder contra el hardware corresponde al optimizador (Fase 5).
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
std::string diagnose_graphics(const char *module, uint32_t adapter)
{
    graphics_t *graphics = nullptr;
    const int rc = gs_create(&graphics, module, adapter);
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

float to_db(float value)
{
    return std::isfinite(value) ? std::max(value, kSilenceDb) : kSilenceDb;
}

// ---- Medidor de audio -------------------------------------------------------------------------

struct audio_meter {
    obs_volmeter_t *volmeter = nullptr;
    std::mutex mutex;
    float peak[2] = {kSilenceDb, kSilenceDb};
    float magnitude[2] = {kSilenceDb, kSilenceDb};
    uint32_t channels = 0;

    static void on_update(void *param, const float magnitude_in[MAX_AUDIO_CHANNELS],
                          const float peak_in[MAX_AUDIO_CHANNELS], const float[MAX_AUDIO_CHANNELS])
    {
        auto *self = static_cast<audio_meter *>(param);
        const int channels = std::min(2, obs_volmeter_get_nr_channels(self->volmeter));
        std::lock_guard lock(self->mutex);
        self->channels = static_cast<uint32_t>(std::max(channels, 0));
        for (int i = 0; i < 2; ++i) {
            const bool used = i < channels;
            self->peak[i] = used ? to_db(peak_in[i]) : kSilenceDb;
            self->magnitude[i] = used ? to_db(magnitude_in[i]) : kSilenceDb;
        }
    }

    ~audio_meter()
    {
        if (volmeter) {
            obs_volmeter_remove_callback(volmeter, on_update, this);
            obs_volmeter_destroy(volmeter);
        }
    }
};

// ---- Salidas ----------------------------------------------------------------------------------

struct output_set {
    obs_output_t *output = nullptr;
    obs_encoder_t *video_encoder = nullptr;
    obs_encoder_t *audio_encoder = nullptr;
    obs_service_t *service = nullptr;

    std::mutex stop_mutex;
    int32_t last_stop_code = 0;
    std::string last_stop_message;

    static void on_stop(void *param, calldata_t *data)
    {
        auto *self = static_cast<output_set *>(param);
        const auto code = static_cast<int32_t>(calldata_int(data, "code"));
        const char *error = self->output ? obs_output_get_last_error(self->output) : nullptr;
        std::lock_guard lock(self->stop_mutex);
        self->last_stop_code = code;
        self->last_stop_message = error ? error : "";
    }

    bool active() const { return output && obs_output_active(output); }

    void release()
    {
        if (output)
            signal_handler_disconnect(obs_output_get_signal_handler(output), "stop", on_stop, this);
        obs_output_release(output);
        obs_encoder_release(video_encoder);
        obs_encoder_release(audio_encoder);
        obs_service_release(service);
        output = nullptr;
        video_encoder = nullptr;
        audio_encoder = nullptr;
        service = nullptr;
    }
};

// ---- Displays ---------------------------------------------------------------------------------

struct display_state {
    struct sp_engine *engine = nullptr;
    obs_display_t *display = nullptr;

    std::mutex mutex; // protege todo lo de abajo: el hilo gráfico lo lee en cada frame
    sp_display_mode mode = SP_DISPLAY_PROGRAM;
    obs_source_t *scene_source = nullptr; // referencia propia en modo SCENE
    float zoom = 0.0f;
    float offset_x = 0.0f;
    float offset_y = 0.0f;
    std::vector<obs_sceneitem_t *> selection; // referencias propias
    std::vector<float> guides_x;
    std::vector<float> guides_y;

    void release_refs()
    {
        std::vector<obs_sceneitem_t *> items;
        obs_source_t *scene = nullptr;
        {
            std::lock_guard lock(mutex);
            items.swap(selection);
            scene = scene_source;
            scene_source = nullptr;
        }
        for (auto *item : items)
            obs_sceneitem_release(item);
        if (scene) {
            obs_source_dec_showing(scene);
            obs_source_release(scene);
        }
    }
};

void draw_solid(const vec4 &color, gs_draw_mode mode, const std::vector<vec3> &points)
{
    gs_effect_t *solid = obs_get_base_effect(OBS_EFFECT_SOLID);
    gs_effect_set_vec4(gs_effect_get_param_by_name(solid, "color"), &color);
    while (gs_effect_loop(solid, "Solid")) {
        gs_render_start(true);
        for (const auto &p : points)
            gs_vertex2f(p.x, p.y);
        gs_render_stop(mode);
    }
}

void render_display(void *param, uint32_t cx, uint32_t cy)
{
    auto *state = static_cast<display_state *>(param);
    obs_video_info ovi{};
    if (!obs_get_video_info(&ovi) || cx == 0 || cy == 0)
        return;

    std::lock_guard lock(state->mutex);
    const float bw = float(ovi.base_width);
    const float bh = float(ovi.base_height);

    float scale, ox, oy;
    if (state->zoom <= 0.0f) {
        scale = std::min(float(cx) / bw, float(cy) / bh);
        ox = (float(cx) - bw * scale) / 2.0f;
        oy = (float(cy) - bh * scale) / 2.0f;
    } else {
        scale = state->zoom;
        ox = state->offset_x;
        oy = state->offset_y;
    }

    gs_viewport_push();
    gs_projection_push();
    gs_set_viewport(0, 0, int(cx), int(cy));
    // Proyección de píxeles de ventana a coordenadas del lienzo.
    gs_ortho(-ox / scale, (float(cx) - ox) / scale, -oy / scale, (float(cy) - oy) / scale, -100.0f, 100.0f);

    // Fondo del lienzo para distinguirlo del área fuera del encuadre.
    vec4 canvas_bg;
    vec4_set(&canvas_bg, 0.0f, 0.0f, 0.0f, 1.0f);
    draw_solid(canvas_bg, GS_TRISTRIP, {{0, 0, 0}, {bw, 0, 0}, {0, bh, 0}, {bw, bh, 0}});

    if (state->mode == SP_DISPLAY_SCENE && state->scene_source)
        obs_source_video_render(state->scene_source);
    else
        obs_render_main_texture();

    // Superposición del editor.
    const float px = 1.0f / scale; // un píxel de pantalla en unidades del lienzo
    if (!state->guides_x.empty() || !state->guides_y.empty()) {
        vec4 guide;
        vec4_set(&guide, 1.0f, 0.25f, 0.55f, 0.9f);
        for (float x : state->guides_x)
            draw_solid(guide, GS_LINES, {{x, 0, 0}, {x, bh, 0}});
        for (float y : state->guides_y)
            draw_solid(guide, GS_LINES, {{0, y, 0}, {bw, y, 0}});
    }

    vec4 outline;
    vec4_set(&outline, 0.29f, 0.56f, 1.0f, 1.0f);
    for (auto *item : state->selection) {
        if (!obs_sceneitem_visible(item))
            continue;
        matrix4 box;
        obs_sceneitem_get_box_transform(item, &box);
        vec3 corners[4];
        const float unit[4][2] = {{0, 0}, {1, 0}, {1, 1}, {0, 1}};
        for (int i = 0; i < 4; ++i) {
            vec3_set(&corners[i], unit[i][0], unit[i][1], 0.0f);
            vec3_transform(&corners[i], &corners[i], &box);
        }
        draw_solid(outline, GS_LINESTRIP, {corners[0], corners[1], corners[2], corners[3], corners[0]});

        // Tiradores de tamaño constante en pantalla: esquinas y puntos medios.
        const float h = 4.0f * px;
        vec3 handles[8] = {corners[0], corners[1], corners[2], corners[3]};
        for (int i = 0; i < 4; ++i) {
            const vec3 &a = corners[i];
            const vec3 &b = corners[(i + 1) % 4];
            vec3_set(&handles[4 + i], (a.x + b.x) / 2.0f, (a.y + b.y) / 2.0f, 0.0f);
        }
        for (const auto &c : handles)
            draw_solid(outline, GS_TRISTRIP,
                       {{c.x - h, c.y - h, 0}, {c.x + h, c.y - h, 0}, {c.x - h, c.y + h, 0}, {c.x + h, c.y + h, 0}});
    }

    gs_projection_pop();
    gs_viewport_pop();
}

} // namespace

struct sp_engine {
    std::map<std::string, obs_scene_t *> scenes;
    std::map<std::string, obs_source_t *> sources;
    std::map<std::string, obs_sceneitem_t *> items; // referencias propias
    std::map<std::string, std::unique_ptr<audio_meter>> meters;
    std::map<std::string, std::unique_ptr<display_state>> displays;
    std::map<uint32_t, std::string> output_channels; // canal global -> source_id

    obs_source_t *transition = nullptr;
    uint32_t transition_duration_ms = 300;
    obs_source_t *program_scene = nullptr; // referencia débil: la propiedad está en scenes

    output_set recording;
    output_set stream;

    os_cpu_usage_info_t *cpu = nullptr;

    obs_source_t *find_source(const char *id)
    {
        const auto it = sources.find(id ? id : "");
        return it == sources.end() ? nullptr : it->second;
    }

    obs_scene_t *find_scene(const char *id)
    {
        const auto it = scenes.find(id ? id : "");
        return it == scenes.end() ? nullptr : it->second;
    }

    obs_sceneitem_t *find_item(const char *id)
    {
        const auto it = items.find(id ? id : "");
        return it == items.end() ? nullptr : it->second;
    }

    void forget_items_where(bool (*match)(obs_sceneitem_t *, void *), void *param)
    {
        for (auto it = items.begin(); it != items.end();) {
            if (match(it->second, param)) {
                obs_sceneitem_release(it->second);
                it = items.erase(it);
            } else {
                ++it;
            }
        }
    }
};

namespace {

sp_result require(sp_engine *engine)
{
    return engine ? SP_OK : fail(SP_ERR_INVALID_ARG, "engine es obligatorio.");
}

#define SP_REQUIRE(cond, message)                              \
    do {                                                       \
        if (!(cond))                                           \
            return fail(SP_ERR_INVALID_ARG, (message));        \
    } while (false)

obs_data_t *data_from_json(const char *json)
{
    return json && *json ? obs_data_create_from_json(json) : obs_data_create();
}

sp_result start_output(sp_engine *engine, output_set &set, const char *output_type, const char *name_prefix,
                       const sp_encoder_config &enc, obs_data_t *output_settings, obs_service_t *service)
{
    const char *encoder_id = pick_video_encoder(enc.video_encoder_id);
    if (!encoder_id) {
        obs_service_release(service);
        return fail(SP_ERR_NOT_FOUND, "No hay ningún codificador de video disponible.");
    }

    obs_data_t *venc = obs_data_create();
    obs_data_set_string(venc, "rate_control", "CBR");
    obs_data_set_int(venc, "bitrate", enc.video_bitrate_kbps ? enc.video_bitrate_kbps : 6000);
    if (enc.keyframe_interval_sec)
        obs_data_set_int(venc, "keyint_sec", enc.keyframe_interval_sec);
    obs_data_t *aenc = obs_data_create();
    obs_data_set_int(aenc, "bitrate", enc.audio_bitrate_kbps ? enc.audio_bitrate_kbps : 160);
    if (service)
        obs_service_apply_encoder_settings(service, venc, aenc); // límites de la plataforma

    const std::string prefix(name_prefix);
    set.video_encoder = obs_video_encoder_create(encoder_id, (prefix + "_video").c_str(), venc, nullptr);
    set.audio_encoder = obs_audio_encoder_create("ffmpeg_aac", (prefix + "_audio").c_str(), aenc, 0, nullptr);
    set.output = obs_output_create(output_type, prefix.c_str(), output_settings, nullptr);
    set.service = service;
    obs_data_release(venc);
    obs_data_release(aenc);

    if (!set.video_encoder || !set.audio_encoder || !set.output) {
        set.release();
        return fail(SP_ERR_CREATE_FAILED, "No se pudieron crear los codificadores o la salida.");
    }

    {
        std::lock_guard lock(set.stop_mutex);
        set.last_stop_code = 0;
        set.last_stop_message.clear();
    }
    signal_handler_connect(obs_output_get_signal_handler(set.output), "stop", output_set::on_stop, &set);

    obs_encoder_set_video(set.video_encoder, obs_get_video());
    obs_encoder_set_audio(set.audio_encoder, obs_get_audio());
    obs_output_set_video_encoder(set.output, set.video_encoder);
    obs_output_set_audio_encoder(set.output, set.audio_encoder, 0);
    if (set.service)
        obs_output_set_service(set.output, set.service);

    if (!obs_output_start(set.output)) {
        const char *err = obs_output_get_last_error(set.output);
        const std::string message = std::string("obs_output_start falló: ") + (err ? err : "sin detalle");
        set.release();
        return fail(SP_ERR_OUTPUT_FAILED, message);
    }
    (void)engine;
    return SP_OK;
}

sp_result stop_output(output_set &set, const char *what)
{
    if (!set.active())
        return fail(SP_ERR_INVALID_STATE, std::string("No hay ") + what + " activa.");

    // obs_output_stop es asíncrono: espera a que el muxer o la conexión terminen.
    obs_output_stop(set.output);
    using namespace std::chrono;
    const auto deadline = steady_clock::now() + seconds(10);
    while (set.active() && steady_clock::now() < deadline)
        std::this_thread::sleep_for(milliseconds(50));

    if (set.active()) {
        obs_output_force_stop(set.output);
        set.release();
        return fail(SP_ERR_OUTPUT_FAILED, std::string("La ") + what + " no se detuvo a tiempo; se forzó la parada.");
    }
    set.release();
    return SP_OK;
}

void set_display_selection(display_state &state, std::vector<obs_sceneitem_t *> items)
{
    std::vector<obs_sceneitem_t *> old;
    {
        std::lock_guard lock(state.mutex);
        old.swap(state.selection);
        state.selection = std::move(items);
    }
    for (auto *item : old)
        obs_sceneitem_release(item);
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

// ---- Motor --------------------------------------------------------------------------------------

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

        auto abort_startup = [](sp_result code, const std::string &msg) {
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
        ovi.adapter = config->adapter_index;
        ovi.gpu_conversion = true;
        ovi.colorspace = VIDEO_CS_709;
        ovi.range = VIDEO_RANGE_PARTIAL;
        ovi.scale_type = OBS_SCALE_BICUBIC;
        const int video = obs_reset_video(&ovi);
        if (video != OBS_VIDEO_SUCCESS)
            return abort_startup(SP_ERR_VIDEO_RESET_FAILED, "obs_reset_video falló con código " +
                                                                std::to_string(video) +
                                                                diagnose_graphics(ovi.graphics_module, ovi.adapter));

#ifdef _WIN32
        // win-wasapi usa las colas de trabajo en tiempo real (RTWQ) de Windows si la plataforma está
        // iniciada; si no, registra un error y cae a su hilo de captura clásico (mayor latencia).
        if (FAILED(RtwqStartup()))
            blog(LOG_WARNING, "[streampilot] RtwqStartup falló; WASAPI usará su hilo de captura clásico.");
#endif
        // Los módulos se cargan después de inicializar audio y video.
        if (config->module_bin_dir && config->module_data_dir)
            obs_add_module_path(config->module_bin_dir, config->module_data_dir);
        obs_load_all_modules();
        obs_log_loaded_modules();
        obs_post_load_modules();

        auto *engine = new sp_engine();
        engine->cpu = os_cpu_usage_info_start();
        // Transición inicial: corte. El programa siempre se emite a través de una transición.
        engine->transition = obs_source_create_private("cut_transition", "sp_transition", nullptr);
        if (engine->transition)
            obs_set_output_source(0, engine->transition);
        *out_engine = engine;
        return SP_OK;
    });
}

void SP_CALL sp_engine_destroy(sp_engine *engine)
{
    if (!engine)
        return;
    try {
        for (auto &[id, display] : engine->displays) {
            obs_display_remove_draw_callback(display->display, render_display, display.get());
            obs_display_destroy(display->display);
            display->release_refs();
        }
        engine->displays.clear();

        if (engine->recording.active())
            obs_output_force_stop(engine->recording.output);
        engine->recording.release();
        if (engine->stream.active())
            obs_output_force_stop(engine->stream.output);
        engine->stream.release();

        // Igual que el cierre de OBS Studio: quitar las salidas globales, retirar escenas (lo que suelta
        // sus elementos) y fuentes, y esperar a que los hilos de audio/video y la cola de destrucción
        // terminen. Si win-wasapi se descarga con una fuente viva, su hilo de captura accede a código
        // ya liberado y el proceso se cae de forma intermitente.
        for (uint32_t channel = 0; channel < MAX_CHANNELS; ++channel)
            obs_set_output_source(channel, nullptr);
        obs_source_release(engine->transition);
        engine->meters.clear();
        for (auto &[id, item] : engine->items)
            obs_sceneitem_release(item);
        engine->items.clear();
        for (auto &[id, scene] : engine->scenes) {
            obs_source_remove(obs_scene_get_source(scene));
            obs_scene_release(scene);
        }
        for (auto &[id, source] : engine->sources) {
            obs_source_remove(source);
            obs_source_release(source);
        }
        if (engine->cpu)
            os_cpu_usage_info_destroy(engine->cpu);
        delete engine;

        // Dos pasadas: la destrucción de una escena encola la de sus fuentes.
        obs_wait_for_destroy_queue();
        obs_wait_for_destroy_queue();
        obs_shutdown();
        g_engine_was_shutdown = true;
#ifdef _WIN32
        RtwqShutdown();
#endif
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

sp_result SP_CALL sp_metrics_get(sp_engine *engine, sp_metrics *m)
{
    return guarded([&]() -> sp_result {
        if (require(engine) || !m)
            return fail(SP_ERR_INVALID_ARG, "engine y out_metrics son obligatorios.");
        *m = {};
        m->active_fps = obs_get_active_fps();
        m->average_frame_render_ms = double(obs_get_average_frame_time_ns()) / 1'000'000.0;
        m->cpu_usage_percent = engine->cpu ? os_cpu_usage_info_query(engine->cpu) : 0.0;
        m->memory_bytes = os_get_proc_resident_size();
        m->total_frames = obs_get_total_frames();
        m->lagged_frames = obs_get_lagged_frames();
        video_t *video = obs_get_video();
        m->skipped_encoder_frames = video_output_get_skipped_frames(video);
        m->encoded_frames = video_output_get_total_frames(video);
        m->is_recording = engine->recording.active() ? 1 : 0;
        m->is_streaming = engine->stream.active() ? 1 : 0;
        if (engine->recording.output)
            m->recording_total_bytes = obs_output_get_total_bytes(engine->recording.output);
        if (engine->stream.output) {
            m->stream_reconnecting = obs_output_reconnecting(engine->stream.output) ? 1 : 0;
            m->stream_dropped_frames = static_cast<uint32_t>(obs_output_get_frames_dropped(engine->stream.output));
            m->stream_total_frames = static_cast<uint32_t>(obs_output_get_total_frames(engine->stream.output));
            m->stream_total_bytes = obs_output_get_total_bytes(engine->stream.output);
        }
        return SP_OK;
    });
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

sp_result SP_CALL sp_video_encoders_enumerate(sp_engine *engine, sp_encoder_callback callback, void *user_data)
{
    return guarded([&]() -> sp_result {
        if (!engine || !callback)
            return fail(SP_ERR_INVALID_ARG, "engine y callback son obligatorios.");
        const char *id = nullptr;
        for (size_t i = 0; obs_enum_encoder_types(i, &id); ++i) {
            if (obs_get_encoder_type(id) != OBS_ENCODER_VIDEO)
                continue;
            const uint32_t caps = obs_get_encoder_caps(id);
            if (caps & (OBS_ENCODER_CAP_DEPRECATED | OBS_ENCODER_CAP_INTERNAL))
                continue;
            const char *name = obs_encoder_get_display_name(id);
            const char *codec = obs_get_encoder_codec(id);
            callback(id, name ? name : id, codec ? codec : "", user_data);
        }
        return SP_OK;
    });
}

// ---- Escenas ------------------------------------------------------------------------------------

sp_result SP_CALL sp_scene_create(sp_engine *engine, const char *scene_id, const char *name)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && scene_id && name, "engine, scene_id y name son obligatorios.");
        if (engine->scenes.count(scene_id))
            return fail(SP_ERR_INVALID_STATE, "La escena ya existe.");
        obs_scene_t *scene = obs_scene_create(name);
        if (!scene)
            return fail(SP_ERR_CREATE_FAILED, "obs_scene_create falló.");
        engine->scenes.emplace(scene_id, scene);
        return SP_OK;
    });
}

sp_result SP_CALL sp_scene_remove(sp_engine *engine, const char *scene_id)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && scene_id, "engine y scene_id son obligatorios.");
        obs_scene_t *scene = engine->find_scene(scene_id);
        if (!scene)
            return fail(SP_ERR_NOT_FOUND, "Escena no encontrada.");
        obs_source_t *source = obs_scene_get_source(scene);
        if (source == engine->program_scene)
            return fail(SP_ERR_INVALID_STATE, "No se puede eliminar la escena que está en programa.");

        engine->forget_items_where(
            [](obs_sceneitem_t *item, void *p) { return obs_sceneitem_get_scene(item) == static_cast<obs_scene_t *>(p); },
            scene);
        obs_source_remove(source);
        obs_scene_release(scene);
        engine->scenes.erase(scene_id);
        return SP_OK;
    });
}

sp_result SP_CALL sp_scene_rename(sp_engine *engine, const char *scene_id, const char *name)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && scene_id && name, "engine, scene_id y name son obligatorios.");
        obs_scene_t *scene = engine->find_scene(scene_id);
        if (!scene)
            return fail(SP_ERR_NOT_FOUND, "Escena no encontrada.");
        obs_source_set_name(obs_scene_get_source(scene), name);
        return SP_OK;
    });
}

sp_result SP_CALL sp_scene_set_program(sp_engine *engine, const char *scene_id)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && scene_id, "engine y scene_id son obligatorios.");
        obs_scene_t *scene = engine->find_scene(scene_id);
        if (!scene)
            return fail(SP_ERR_NOT_FOUND, "Escena no encontrada.");
        obs_source_t *target = obs_scene_get_source(scene);
        if (!engine->transition) {
            obs_set_output_source(0, target);
        } else if (!engine->program_scene) {
            obs_transition_set(engine->transition, target); // primer programa: sin animación
        } else {
            obs_transition_start(engine->transition, OBS_TRANSITION_MODE_AUTO,
                                 engine->transition_duration_ms, target);
        }
        engine->program_scene = target;
        return SP_OK;
    });
}

sp_result SP_CALL sp_transition_set(sp_engine *engine, const char *type_id, uint32_t duration_ms)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && type_id, "engine y type_id son obligatorios.");
        obs_source_t *transition = obs_source_create_private(type_id, "sp_transition", nullptr);
        if (!transition)
            return fail(SP_ERR_NOT_FOUND, std::string("Transición no disponible: ") + type_id);
        if (engine->program_scene)
            obs_transition_set(transition, engine->program_scene);
        obs_set_output_source(0, transition);
        obs_source_release(engine->transition);
        engine->transition = transition;
        engine->transition_duration_ms = duration_ms;
        return SP_OK;
    });
}

// ---- Fuentes ------------------------------------------------------------------------------------

sp_result SP_CALL sp_source_create(sp_engine *engine, const char *source_id, const char *source_type,
                                   const char *name, const char *settings_json)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && source_id && source_type && name, "Faltan argumentos obligatorios.");
        if (engine->sources.count(source_id))
            return fail(SP_ERR_INVALID_STATE, "La fuente ya existe.");
        // Con un id desconocido libobs crea una fuente marcador vacía en lugar de fallar.
        if (!input_type_exists(source_type))
            return fail(SP_ERR_NOT_FOUND, std::string("Tipo de fuente no disponible: ") + source_type);

        obs_data_t *settings = data_from_json(settings_json);
        obs_source_t *source = obs_source_create(source_type, name, settings, nullptr);
        obs_data_release(settings);
        if (!source)
            return fail(SP_ERR_CREATE_FAILED, std::string("No se pudo crear la fuente de tipo ") + source_type);
        engine->sources.emplace(source_id, source);

        if (obs_source_get_output_flags(source) & OBS_SOURCE_AUDIO) {
            auto meter = std::make_unique<audio_meter>();
            meter->volmeter = obs_volmeter_create(OBS_FADER_LOG);
            obs_volmeter_attach_source(meter->volmeter, source);
            obs_volmeter_add_callback(meter->volmeter, audio_meter::on_update, meter.get());
            engine->meters.emplace(source_id, std::move(meter));
        }
        return SP_OK;
    });
}

sp_result SP_CALL sp_source_remove(sp_engine *engine, const char *source_id)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && source_id, "engine y source_id son obligatorios.");
        obs_source_t *source = engine->find_source(source_id);
        if (!source)
            return fail(SP_ERR_NOT_FOUND, "Fuente no encontrada.");

        engine->meters.erase(source_id);
        for (auto it = engine->output_channels.begin(); it != engine->output_channels.end();) {
            if (it->second == source_id) {
                obs_set_output_source(it->first, nullptr);
                it = engine->output_channels.erase(it);
            } else {
                ++it;
            }
        }
        engine->forget_items_where(
            [](obs_sceneitem_t *item, void *p) { return obs_sceneitem_get_source(item) == static_cast<obs_source_t *>(p); },
            source);
        obs_source_remove(source); // las escenas sueltan sus elementos al recibir la señal
        obs_source_release(source);
        engine->sources.erase(source_id);
        return SP_OK;
    });
}

sp_result SP_CALL sp_source_rename(sp_engine *engine, const char *source_id, const char *name)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && source_id && name, "engine, source_id y name son obligatorios.");
        obs_source_t *source = engine->find_source(source_id);
        if (!source)
            return fail(SP_ERR_NOT_FOUND, "Fuente no encontrada.");
        obs_source_set_name(source, name);
        return SP_OK;
    });
}

sp_result SP_CALL sp_source_update(sp_engine *engine, const char *source_id, const char *settings_json)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && source_id, "engine y source_id son obligatorios.");
        obs_source_t *source = engine->find_source(source_id);
        if (!source)
            return fail(SP_ERR_NOT_FOUND, "Fuente no encontrada.");
        obs_data_t *settings = data_from_json(settings_json);
        obs_source_update(source, settings);
        obs_data_release(settings);
        return SP_OK;
    });
}

sp_result SP_CALL sp_source_get_settings(sp_engine *engine, const char *source_id, char *buffer,
                                         int32_t buffer_size, int32_t *required)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && source_id, "engine y source_id son obligatorios.");
        obs_source_t *source = engine->find_source(source_id);
        if (!source)
            return fail(SP_ERR_NOT_FOUND, "Fuente no encontrada.");
        obs_data_t *settings = obs_source_get_settings(source);
        const char *json = obs_data_get_json_with_defaults(settings);
        const std::string text = json ? json : "{}";
        obs_data_release(settings);
        return copy_out(text, buffer, buffer_size, required);
    });
}

namespace {

const char *property_type_name(obs_property_type type)
{
    switch (type) {
    case OBS_PROPERTY_BOOL: return "bool";
    case OBS_PROPERTY_INT: return "int";
    case OBS_PROPERTY_FLOAT: return "float";
    case OBS_PROPERTY_TEXT: return "text";
    case OBS_PROPERTY_PATH: return "path";
    case OBS_PROPERTY_LIST: return "list";
    case OBS_PROPERTY_COLOR: return "color";
    case OBS_PROPERTY_COLOR_ALPHA: return "color_alpha";
    case OBS_PROPERTY_FONT: return "font";
    case OBS_PROPERTY_BUTTON: return "button";
    case OBS_PROPERTY_EDITABLE_LIST: return "editable_list";
    case OBS_PROPERTY_FRAME_RATE: return "frame_rate";
    case OBS_PROPERTY_GROUP: return "group";
    default: return "unknown";
    }
}

void append_properties(std::string &out, obs_properties_t *props)
{
    out += '[';
    bool first = true;
    for (obs_property_t *p = obs_properties_first(props); p; obs_property_next(&p)) {
        if (!obs_property_visible(p))
            continue;
        const obs_property_type type = obs_property_get_type(p);
        if (!first)
            out += ',';
        first = false;

        out += "{\"name\":";
        json_escape(out, obs_property_name(p));
        out += ",\"description\":";
        json_escape(out, obs_property_description(p));
        out += ",\"type\":";
        json_escape(out, property_type_name(type));
        out += ",\"enabled\":";
        out += obs_property_enabled(p) ? "true" : "false";
        if (const char *doc = obs_property_long_description(p)) {
            out += ",\"tooltip\":";
            json_escape(out, doc);
        }

        char num[64];
        switch (type) {
        case OBS_PROPERTY_INT:
            std::snprintf(num, sizeof(num), ",\"min\":%d,\"max\":%d,\"step\":%d", obs_property_int_min(p),
                          obs_property_int_max(p), obs_property_int_step(p));
            out += num;
            out += obs_property_int_type(p) == OBS_NUMBER_SLIDER ? ",\"slider\":true" : "";
            break;
        case OBS_PROPERTY_FLOAT:
            std::snprintf(num, sizeof(num), ",\"min\":%g,\"max\":%g,\"step\":%g", obs_property_float_min(p),
                          obs_property_float_max(p), obs_property_float_step(p));
            out += num;
            out += obs_property_float_type(p) == OBS_NUMBER_SLIDER ? ",\"slider\":true" : "";
            break;
        case OBS_PROPERTY_TEXT: {
            const obs_text_type tt = obs_property_text_type(p);
            out += tt == OBS_TEXT_MULTILINE ? ",\"multiline\":true" : "";
            out += tt == OBS_TEXT_PASSWORD ? ",\"password\":true" : "";
            out += tt == OBS_TEXT_INFO ? ",\"info\":true" : "";
            break;
        }
        case OBS_PROPERTY_PATH:
            out += ",\"pathType\":";
            json_escape(out, obs_property_path_type(p) == OBS_PATH_DIRECTORY ? "directory"
                             : obs_property_path_type(p) == OBS_PATH_FILE_SAVE ? "save"
                                                                                : "file");
            out += ",\"filter\":";
            json_escape(out, obs_property_path_filter(p));
            break;
        case OBS_PROPERTY_LIST: {
            const obs_combo_format format = obs_property_list_format(p);
            out += ",\"format\":";
            json_escape(out, format == OBS_COMBO_FORMAT_INT     ? "int"
                             : format == OBS_COMBO_FORMAT_FLOAT ? "float"
                             : format == OBS_COMBO_FORMAT_BOOL  ? "bool"
                                                                : "string");
            out += obs_property_list_type(p) == OBS_COMBO_TYPE_EDITABLE ? ",\"editable\":true" : "";
            out += ",\"items\":[";
            const size_t count = obs_property_list_item_count(p);
            for (size_t i = 0; i < count; ++i) {
                if (i)
                    out += ',';
                out += "{\"name\":";
                json_escape(out, obs_property_list_item_name(p, i));
                out += ",\"value\":";
                if (format == OBS_COMBO_FORMAT_INT) {
                    std::snprintf(num, sizeof(num), "%lld", (long long)obs_property_list_item_int(p, i));
                    out += num;
                } else if (format == OBS_COMBO_FORMAT_FLOAT) {
                    std::snprintf(num, sizeof(num), "%g", obs_property_list_item_float(p, i));
                    out += num;
                } else if (format == OBS_COMBO_FORMAT_BOOL) {
                    out += obs_property_list_item_bool(p, i) ? "true" : "false";
                } else {
                    json_escape(out, obs_property_list_item_string(p, i));
                }
                out += obs_property_list_item_disabled(p, i) ? ",\"disabled\":true}" : "}";
            }
            out += ']';
            break;
        }
        case OBS_PROPERTY_GROUP:
            out += ",\"properties\":";
            append_properties(out, obs_property_group_content(p));
            break;
        default:
            break;
        }
        out += '}';
    }
    out += ']';
}

} // namespace

sp_result SP_CALL sp_source_get_properties(sp_engine *engine, const char *source_id, char *buffer,
                                           int32_t buffer_size, int32_t *required)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && source_id, "engine y source_id son obligatorios.");
        obs_source_t *source = engine->find_source(source_id);
        if (!source)
            return fail(SP_ERR_NOT_FOUND, "Fuente no encontrada.");
        obs_properties_t *props = obs_source_properties(source);
        std::string text;
        if (props) {
            append_properties(text, props);
            obs_properties_destroy(props);
        } else {
            text = "[]";
        }
        return copy_out(text, buffer, buffer_size, required);
    });
}

sp_result SP_CALL sp_source_get_size(sp_engine *engine, const char *source_id, uint32_t *width, uint32_t *height)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && source_id && width && height, "Faltan argumentos obligatorios.");
        obs_source_t *source = engine->find_source(source_id);
        if (!source)
            return fail(SP_ERR_NOT_FOUND, "Fuente no encontrada.");
        *width = obs_source_get_width(source);
        *height = obs_source_get_height(source);
        return SP_OK;
    });
}

sp_result SP_CALL sp_source_set_output_channel(sp_engine *engine, const char *source_id, uint32_t channel)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && source_id, "engine y source_id son obligatorios.");
        SP_REQUIRE(channel < MAX_CHANNELS, "Canal fuera de rango.");
        obs_source_t *source = engine->find_source(source_id);
        if (!source)
            return fail(SP_ERR_NOT_FOUND, "Fuente no encontrada.");
        // El canal 0 es el programa (transición); los globales de audio empiezan en 1.
        for (auto it = engine->output_channels.begin(); it != engine->output_channels.end();) {
            if (it->second == source_id) {
                obs_set_output_source(it->first, nullptr);
                it = engine->output_channels.erase(it);
            } else {
                ++it;
            }
        }
        if (channel > 0) {
            obs_set_output_source(channel, source);
            engine->output_channels[channel] = source_id;
        }
        return SP_OK;
    });
}

// ---- Audio --------------------------------------------------------------------------------------

sp_result SP_CALL sp_audio_set_volume(sp_engine *engine, const char *source_id, float multiplier)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && source_id, "engine y source_id son obligatorios.");
        SP_REQUIRE(multiplier >= 0.0f && multiplier <= 20.0f, "Volumen fuera de rango (0..20).");
        obs_source_t *source = engine->find_source(source_id);
        if (!source)
            return fail(SP_ERR_NOT_FOUND, "Fuente no encontrada.");
        obs_source_set_volume(source, multiplier);
        return SP_OK;
    });
}

sp_result SP_CALL sp_audio_set_muted(sp_engine *engine, const char *source_id, int32_t muted)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && source_id, "engine y source_id son obligatorios.");
        obs_source_t *source = engine->find_source(source_id);
        if (!source)
            return fail(SP_ERR_NOT_FOUND, "Fuente no encontrada.");
        obs_source_set_muted(source, muted != 0);
        return SP_OK;
    });
}

sp_result SP_CALL sp_audio_set_monitoring(sp_engine *engine, const char *source_id, sp_monitoring type)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && source_id, "engine y source_id son obligatorios.");
        obs_source_t *source = engine->find_source(source_id);
        if (!source)
            return fail(SP_ERR_NOT_FOUND, "Fuente no encontrada.");
        obs_source_set_monitoring_type(source, static_cast<obs_monitoring_type>(type));
        return SP_OK;
    });
}

sp_result SP_CALL sp_audio_set_sync_offset(sp_engine *engine, const char *source_id, int64_t offset_ms)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && source_id, "engine y source_id son obligatorios.");
        obs_source_t *source = engine->find_source(source_id);
        if (!source)
            return fail(SP_ERR_NOT_FOUND, "Fuente no encontrada.");
        obs_source_set_sync_offset(source, offset_ms * 1'000'000);
        return SP_OK;
    });
}

sp_result SP_CALL sp_audio_get_levels(sp_engine *engine, const char *source_id, sp_audio_levels *levels)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && source_id && levels, "Faltan argumentos obligatorios.");
        const auto it = engine->meters.find(source_id);
        if (it == engine->meters.end())
            return fail(SP_ERR_NOT_FOUND, "La fuente no tiene audio.");
        std::lock_guard lock(it->second->mutex);
        for (int i = 0; i < 2; ++i) {
            levels->peak_db[i] = it->second->peak[i];
            levels->magnitude_db[i] = it->second->magnitude[i];
        }
        levels->channels = it->second->channels;
        return SP_OK;
    });
}

// ---- Elementos de escena ------------------------------------------------------------------------

sp_result SP_CALL sp_item_add(sp_engine *engine, const char *scene_id, const char *item_id, const char *source_id)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && scene_id && item_id && source_id, "Faltan argumentos obligatorios.");
        if (engine->items.count(item_id))
            return fail(SP_ERR_INVALID_STATE, "El elemento ya existe.");
        obs_scene_t *scene = engine->find_scene(scene_id);
        obs_source_t *source = engine->find_source(source_id);
        if (!scene || !source)
            return fail(SP_ERR_NOT_FOUND, "Escena o fuente no encontrada.");
        obs_sceneitem_t *item = obs_scene_add(scene, source);
        if (!item)
            return fail(SP_ERR_CREATE_FAILED, "obs_scene_add falló.");
        obs_sceneitem_addref(item);
        obs_sceneitem_set_alignment(item, OBS_ALIGN_LEFT | OBS_ALIGN_TOP);
        engine->items.emplace(item_id, item);
        return SP_OK;
    });
}

sp_result SP_CALL sp_item_remove(sp_engine *engine, const char *item_id)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && item_id, "engine e item_id son obligatorios.");
        obs_sceneitem_t *item = engine->find_item(item_id);
        if (!item)
            return fail(SP_ERR_NOT_FOUND, "Elemento no encontrado.");
        obs_sceneitem_remove(item);
        obs_sceneitem_release(item);
        engine->items.erase(item_id);
        return SP_OK;
    });
}

sp_result SP_CALL sp_item_set_transform(sp_engine *engine, const char *item_id, const sp_transform *t)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && item_id && t, "Faltan argumentos obligatorios.");
        obs_sceneitem_t *item = engine->find_item(item_id);
        if (!item)
            return fail(SP_ERR_NOT_FOUND, "Elemento no encontrado.");
        SP_REQUIRE(t->crop_left >= 0 && t->crop_top >= 0 && t->crop_right >= 0 && t->crop_bottom >= 0,
                   "El recorte no puede ser negativo.");

        vec2 pos, scale;
        vec2_set(&pos, t->pos_x, t->pos_y);
        vec2_set(&scale, t->scale_x, t->scale_y);
        const obs_sceneitem_crop crop{t->crop_left, t->crop_top, t->crop_right, t->crop_bottom};
        // Defer evita que el hilo gráfico renderice un estado intermedio de la transformación.
        obs_sceneitem_defer_update_begin(item);
        obs_sceneitem_set_pos(item, &pos);
        obs_sceneitem_set_scale(item, &scale);
        obs_sceneitem_set_rot(item, t->rotation_deg);
        obs_sceneitem_set_crop(item, &crop);
        obs_sceneitem_defer_update_end(item);
        return SP_OK;
    });
}

sp_result SP_CALL sp_item_get_transform(sp_engine *engine, const char *item_id, sp_transform *t)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && item_id && t, "Faltan argumentos obligatorios.");
        obs_sceneitem_t *item = engine->find_item(item_id);
        if (!item)
            return fail(SP_ERR_NOT_FOUND, "Elemento no encontrado.");
        vec2 pos, scale;
        obs_sceneitem_get_pos(item, &pos);
        obs_sceneitem_get_scale(item, &scale);
        obs_sceneitem_crop crop{};
        obs_sceneitem_get_crop(item, &crop);
        *t = {pos.x, pos.y, scale.x, scale.y, obs_sceneitem_get_rot(item), crop.left, crop.top, crop.right,
              crop.bottom};
        return SP_OK;
    });
}

sp_result SP_CALL sp_item_set_visible(sp_engine *engine, const char *item_id, int32_t visible)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && item_id, "engine e item_id son obligatorios.");
        obs_sceneitem_t *item = engine->find_item(item_id);
        if (!item)
            return fail(SP_ERR_NOT_FOUND, "Elemento no encontrado.");
        obs_sceneitem_set_visible(item, visible != 0);
        return SP_OK;
    });
}

sp_result SP_CALL sp_item_set_locked(sp_engine *engine, const char *item_id, int32_t locked)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && item_id, "engine e item_id son obligatorios.");
        obs_sceneitem_t *item = engine->find_item(item_id);
        if (!item)
            return fail(SP_ERR_NOT_FOUND, "Elemento no encontrado.");
        obs_sceneitem_set_locked(item, locked != 0);
        return SP_OK;
    });
}

sp_result SP_CALL sp_item_set_order(sp_engine *engine, const char *item_id, int32_t index)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && item_id && index >= 0, "Argumentos inválidos.");
        obs_sceneitem_t *item = engine->find_item(item_id);
        if (!item)
            return fail(SP_ERR_NOT_FOUND, "Elemento no encontrado.");
        obs_sceneitem_set_order_position(item, index);
        return SP_OK;
    });
}

// ---- Salidas ------------------------------------------------------------------------------------

sp_result SP_CALL sp_recording_start(sp_engine *engine, const sp_recording_config *config)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && config && config->output_path, "engine y output_path son obligatorios.");
        if (engine->recording.active())
            return fail(SP_ERR_INVALID_STATE, "Ya hay una grabación activa.");
        engine->recording.release();

        obs_data_t *settings = obs_data_create();
        obs_data_set_string(settings, "path", config->output_path);
        const sp_result rc =
            start_output(engine, engine->recording, "ffmpeg_muxer", "sp_recording", config->encoder, settings, nullptr);
        obs_data_release(settings);
        return rc;
    });
}

sp_result SP_CALL sp_recording_stop(sp_engine *engine)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine, "engine es obligatorio.");
        return stop_output(engine->recording, "grabación");
    });
}

sp_result SP_CALL sp_stream_start(sp_engine *engine, const sp_stream_config *config)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && config && config->server && config->key, "engine, server y key son obligatorios.");
        SP_REQUIRE(std::strncmp(config->server, "rtmp://", 7) == 0 || std::strncmp(config->server, "rtmps://", 8) == 0,
                   "El servidor debe empezar por rtmp:// o rtmps://.");
        if (engine->stream.active())
            return fail(SP_ERR_INVALID_STATE, "Ya hay una transmisión activa.");
        engine->stream.release();

        obs_data_t *service_settings = obs_data_create();
        obs_data_set_string(service_settings, "server", config->server);
        obs_data_set_string(service_settings, "key", config->key);
        obs_service_t *service = obs_service_create("rtmp_custom", "sp_service", service_settings, nullptr);
        obs_data_release(service_settings);
        if (!service)
            return fail(SP_ERR_CREATE_FAILED, "No se pudo crear el servicio RTMP.");

        obs_data_t *settings = obs_data_create();
        const sp_result rc =
            start_output(engine, engine->stream, "rtmp_output", "sp_stream", config->encoder, settings, service);
        obs_data_release(settings);
        if (rc == SP_OK)
            obs_output_set_reconnect_settings(engine->stream.output, 20, 5);
        return rc;
    });
}

sp_result SP_CALL sp_stream_stop(sp_engine *engine)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine, "engine es obligatorio.");
        return stop_output(engine->stream, "transmisión");
    });
}

sp_result SP_CALL sp_output_last_stop(sp_engine *engine, sp_output_kind kind, int32_t *code, char *buffer,
                                      int32_t buffer_size, int32_t *required)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && code, "engine y code son obligatorios.");
        output_set &set = kind == SP_OUTPUT_STREAM ? engine->stream : engine->recording;
        std::string message;
        {
            std::lock_guard lock(set.stop_mutex);
            *code = set.last_stop_code;
            message = set.last_stop_message;
        }
        return copy_out(message, buffer, buffer_size, required);
    });
}

// ---- Displays -----------------------------------------------------------------------------------

sp_result SP_CALL sp_display_create(sp_engine *engine, const char *display_id, void *native_window, uint32_t width,
                                    uint32_t height)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && display_id && native_window && width && height, "Faltan argumentos obligatorios.");
        if (engine->displays.count(display_id))
            return fail(SP_ERR_INVALID_STATE, "El display ya existe.");

        gs_init_data init{};
        init.cx = width;
        init.cy = height;
        init.format = GS_BGRA;
        init.zsformat = GS_ZS_NONE;
#ifdef _WIN32
        init.window.hwnd = native_window;
#endif
        auto state = std::make_unique<display_state>();
        state->engine = engine;
        state->display = obs_display_create(&init, 0x1a1a1e);
        if (!state->display)
            return fail(SP_ERR_CREATE_FAILED, "obs_display_create falló.");
        obs_display_add_draw_callback(state->display, render_display, state.get());
        engine->displays.emplace(display_id, std::move(state));
        return SP_OK;
    });
}

sp_result SP_CALL sp_display_destroy(sp_engine *engine, const char *display_id)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && display_id, "engine y display_id son obligatorios.");
        const auto it = engine->displays.find(display_id);
        if (it == engine->displays.end())
            return SP_OK; // idempotente: la ventana puede destruirse después del motor
        obs_display_remove_draw_callback(it->second->display, render_display, it->second.get());
        obs_display_destroy(it->second->display);
        it->second->release_refs();
        engine->displays.erase(it);
        return SP_OK;
    });
}

sp_result SP_CALL sp_display_resize(sp_engine *engine, const char *display_id, uint32_t width, uint32_t height)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && display_id, "engine y display_id son obligatorios.");
        const auto it = engine->displays.find(display_id);
        if (it == engine->displays.end())
            return fail(SP_ERR_NOT_FOUND, "Display no encontrado.");
        if (width && height)
            obs_display_resize(it->second->display, width, height);
        return SP_OK;
    });
}

sp_result SP_CALL sp_display_set_source(sp_engine *engine, const char *display_id, sp_display_mode mode,
                                        const char *scene_id)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && display_id, "engine y display_id son obligatorios.");
        const auto it = engine->displays.find(display_id);
        if (it == engine->displays.end())
            return fail(SP_ERR_NOT_FOUND, "Display no encontrado.");

        obs_source_t *scene_source = nullptr;
        if (mode == SP_DISPLAY_SCENE) {
            obs_scene_t *scene = engine->find_scene(scene_id);
            if (!scene)
                return fail(SP_ERR_NOT_FOUND, "Escena no encontrada.");
            scene_source = obs_source_get_ref(obs_scene_get_source(scene));
            // Una escena que se ve en un display está "mostrándose": sin esto libobs no activa sus fuentes
            // (p. ej. la captura de pantalla no se inicializa) cuando la escena no está en programa.
            obs_source_inc_showing(scene_source);
        }
        obs_source_t *old = nullptr;
        {
            std::lock_guard lock(it->second->mutex);
            old = it->second->scene_source;
            it->second->scene_source = scene_source;
            it->second->mode = mode;
        }
        if (old) {
            obs_source_dec_showing(old);
            obs_source_release(old);
        }
        return SP_OK;
    });
}

sp_result SP_CALL sp_display_set_view(sp_engine *engine, const char *display_id, float zoom, float offset_x,
                                      float offset_y)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && display_id && zoom >= 0.0f && zoom <= 32.0f, "Argumentos inválidos.");
        const auto it = engine->displays.find(display_id);
        if (it == engine->displays.end())
            return fail(SP_ERR_NOT_FOUND, "Display no encontrado.");
        std::lock_guard lock(it->second->mutex);
        it->second->zoom = zoom;
        it->second->offset_x = offset_x;
        it->second->offset_y = offset_y;
        return SP_OK;
    });
}

sp_result SP_CALL sp_display_set_overlay(sp_engine *engine, const char *display_id,
                                         const char *const *selected_item_ids, int32_t selected_count,
                                         const float *guides_x, int32_t guides_x_count, const float *guides_y,
                                         int32_t guides_y_count)
{
    return guarded([&]() -> sp_result {
        SP_REQUIRE(engine && display_id && selected_count >= 0 && guides_x_count >= 0 && guides_y_count >= 0,
                   "Argumentos inválidos.");
        const auto it = engine->displays.find(display_id);
        if (it == engine->displays.end())
            return fail(SP_ERR_NOT_FOUND, "Display no encontrado.");

        std::vector<obs_sceneitem_t *> items;
        for (int32_t i = 0; i < selected_count; ++i) {
            if (obs_sceneitem_t *item = engine->find_item(selected_item_ids[i])) {
                obs_sceneitem_addref(item);
                items.push_back(item);
            }
        }
        set_display_selection(*it->second, std::move(items));
        std::lock_guard lock(it->second->mutex);
        it->second->guides_x.assign(guides_x, guides_x + (guides_x ? guides_x_count : 0));
        it->second->guides_y.assign(guides_y, guides_y + (guides_y ? guides_y_count : 0));
        return SP_OK;
    });
}

} // extern "C"
