// Diagnóstico del subsistema gráfico de libobs: crea el dispositivo D3D11 y compila los efectos
// base mostrando el error exacto. Uso: sp_diag.exe <ruta a data/libobs>
#include <obs.h>
#include <util/base.h>

#include <cstdarg>
#include <cstdio>
#include <string>

static void log_handler(int level, const char *fmt, va_list args, void *)
{
    std::fprintf(stdout, "[%d] ", level);
    std::vfprintf(stdout, fmt, args);
    std::fputc('\n', stdout);
}

int main(int argc, char **argv)
{
    if (argc < 2) {
        std::fprintf(stderr, "uso: sp_diag <data/libobs>\n");
        return 2;
    }
    base_set_log_handler(log_handler, nullptr);

    graphics_t *graphics = nullptr;
    int rc = gs_create(&graphics, "libobs-d3d11.dll", 0);
    std::printf("gs_create -> %d\n", rc);
    if (rc != GS_SUCCESS)
        return 1;

    gs_enter_context(graphics);
    const char *effects[] = {"default.effect", "opaque.effect", "solid.effect", "format_conversion.effect",
                             "premultiplied_alpha.effect"};
    int failures = 0;
    for (const char *name : effects) {
        std::string path = std::string(argv[1]) + "/" + name;
        char *error = nullptr;
        gs_effect_t *effect = gs_effect_create_from_file(path.c_str(), &error);
        std::printf("%s -> %s\n", name, effect ? "OK" : "FALLO");
        if (error) {
            std::printf("  %s\n", error);
            bfree(error);
        }
        failures += effect ? 0 : 1;
    }
    gs_leave_context();
    gs_destroy(graphics);

    // Secuencia completa de arranque, igual que sp_engine_create.
#pragma warning(suppress : 4996)
    obs_add_data_path((std::string(argv[1]) + "/").c_str());
    if (!obs_startup("es-ES", nullptr, nullptr))
        return 10;
    obs_audio_info oai{};
    oai.samples_per_sec = 48000;
    oai.speakers = SPEAKERS_STEREO;
    std::printf("reset_audio -> %d\n", obs_reset_audio(&oai));
    obs_video_info ovi{};
    ovi.graphics_module = "libobs-d3d11.dll";
    ovi.fps_num = 30;
    ovi.fps_den = 1;
    ovi.base_width = ovi.output_width = 1280;
    ovi.base_height = ovi.output_height = 720;
    ovi.output_format = VIDEO_FORMAT_NV12;
    ovi.gpu_conversion = true;
    ovi.colorspace = VIDEO_CS_709;
    ovi.range = VIDEO_RANGE_PARTIAL;
    ovi.scale_type = OBS_SCALE_BICUBIC;
    int video = obs_reset_video(&ovi);
    std::printf("reset_video -> %d\n", video);
    if (argc >= 4) {
        obs_add_module_path(argv[2], argv[3]);
        obs_load_all_modules();
        obs_post_load_modules();
        std::printf("modules loaded\n");
    }
    std::fflush(stdout);
    obs_shutdown();
    std::printf("shutdown ok\n");
    return failures + (video == OBS_VIDEO_SUCCESS ? 0 : 100);
}
