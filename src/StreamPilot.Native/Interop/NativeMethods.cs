using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StreamPilot.Native.Handles;

namespace StreamPilot.Native.Interop;

/// <summary>Espejo exacto de native/streampilot-bridge/include/streampilot_bridge.h. Mantener sincronizados.</summary>
internal static unsafe partial class NativeMethods
{
    public const string Library = "streampilot_bridge";
    public const uint ExpectedAbiVersion = 4;

    // ---- General
    [LibraryImport(Library, EntryPoint = "sp_abi_version"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint AbiVersion();

    [LibraryImport(Library, EntryPoint = "sp_last_error"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int LastError(byte* buffer, int bufferSize);

    [LibraryImport(Library, EntryPoint = "sp_set_log_callback"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetLogCallback(delegate* unmanaged[Cdecl]<SpLogLevel, byte*, nint, void> callback, nint userData);

    // ---- Motor
    [LibraryImport(Library, EntryPoint = "sp_engine_create"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult EngineCreate(in SpEngineConfig config, out EngineSafeHandle engine);

    [LibraryImport(Library, EntryPoint = "sp_engine_destroy"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void EngineDestroy(nint engine);

    [LibraryImport(Library, EntryPoint = "sp_metrics_get"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult MetricsGet(EngineSafeHandle engine, out SpMetrics metrics);

    [LibraryImport(Library, EntryPoint = "sp_devices_enumerate"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult DevicesEnumerate(EngineSafeHandle engine, delegate* unmanaged[Cdecl]<SpDeviceKind, byte*, byte*, nint, void> callback, nint userData);

    [LibraryImport(Library, EntryPoint = "sp_video_encoders_enumerate"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult VideoEncodersEnumerate(EngineSafeHandle engine, delegate* unmanaged[Cdecl]<byte*, byte*, byte*, nint, void> callback, nint userData);

    // ---- Escenas
    [LibraryImport(Library, EntryPoint = "sp_scene_create", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SceneCreate(EngineSafeHandle engine, string sceneId, string name);

    [LibraryImport(Library, EntryPoint = "sp_scene_remove", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SceneRemove(EngineSafeHandle engine, string sceneId);

    [LibraryImport(Library, EntryPoint = "sp_scene_rename", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SceneRename(EngineSafeHandle engine, string sceneId, string name);

    [LibraryImport(Library, EntryPoint = "sp_scene_set_program", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SceneSetProgram(EngineSafeHandle engine, string sceneId);

    [LibraryImport(Library, EntryPoint = "sp_transition_set", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult TransitionSet(EngineSafeHandle engine, string typeId, uint durationMs, string settingsJson);

    // ---- Fuentes
    [LibraryImport(Library, EntryPoint = "sp_source_create", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SourceCreate(EngineSafeHandle engine, string sourceId, string sourceType, string name, string settingsJson);

    [LibraryImport(Library, EntryPoint = "sp_source_remove", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SourceRemove(EngineSafeHandle engine, string sourceId);

    [LibraryImport(Library, EntryPoint = "sp_source_rename", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SourceRename(EngineSafeHandle engine, string sourceId, string name);

    [LibraryImport(Library, EntryPoint = "sp_source_update", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SourceUpdate(EngineSafeHandle engine, string sourceId, string settingsJson);

    [LibraryImport(Library, EntryPoint = "sp_source_get_settings", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SourceGetSettings(EngineSafeHandle engine, string sourceId, byte* buffer, int bufferSize, out int required);

    [LibraryImport(Library, EntryPoint = "sp_source_get_properties", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SourceGetProperties(EngineSafeHandle engine, string sourceId, byte* buffer, int bufferSize, out int required);

    [LibraryImport(Library, EntryPoint = "sp_source_get_size", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SourceGetSize(EngineSafeHandle engine, string sourceId, out uint width, out uint height);

    [LibraryImport(Library, EntryPoint = "sp_source_set_output_channel", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SourceSetOutputChannel(EngineSafeHandle engine, string sourceId, uint channel);

    // ---- Audio
    [LibraryImport(Library, EntryPoint = "sp_audio_set_volume", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult AudioSetVolume(EngineSafeHandle engine, string sourceId, float multiplier);

    [LibraryImport(Library, EntryPoint = "sp_audio_set_muted", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult AudioSetMuted(EngineSafeHandle engine, string sourceId, int muted);

    [LibraryImport(Library, EntryPoint = "sp_audio_set_monitoring", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult AudioSetMonitoring(EngineSafeHandle engine, string sourceId, int type);

    [LibraryImport(Library, EntryPoint = "sp_audio_set_sync_offset", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult AudioSetSyncOffset(EngineSafeHandle engine, string sourceId, long offsetMs);

    [LibraryImport(Library, EntryPoint = "sp_audio_get_levels", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult AudioGetLevels(EngineSafeHandle engine, string sourceId, out SpAudioLevels levels);

    // ---- Filtros
    [LibraryImport(Library, EntryPoint = "sp_filter_add", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult FilterAdd(EngineSafeHandle engine, string sourceId, string filterId, string filterType, string name, string settingsJson);

    [LibraryImport(Library, EntryPoint = "sp_filter_remove", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult FilterRemove(EngineSafeHandle engine, string filterId);

    [LibraryImport(Library, EntryPoint = "sp_filter_update", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult FilterUpdate(EngineSafeHandle engine, string filterId, string settingsJson);

    [LibraryImport(Library, EntryPoint = "sp_filter_set_enabled", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult FilterSetEnabled(EngineSafeHandle engine, string filterId, int enabled);

    [LibraryImport(Library, EntryPoint = "sp_filter_types_enumerate"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult FilterTypesEnumerate(EngineSafeHandle engine, delegate* unmanaged[Cdecl]<byte*, byte*, uint, nint, void> callback, nint userData);

    [LibraryImport(Library, EntryPoint = "sp_transition_types_enumerate"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult TransitionTypesEnumerate(EngineSafeHandle engine, delegate* unmanaged[Cdecl]<byte*, byte*, uint, nint, void> callback, nint userData);

    // ---- Animación
    [LibraryImport(Library, EntryPoint = "sp_anim_play", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult AnimPlay(EngineSafeHandle engine, string itemId, string clipJson, int flags);

    [LibraryImport(Library, EntryPoint = "sp_anim_stop", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult AnimStop(EngineSafeHandle engine, string itemId);

    [LibraryImport(Library, EntryPoint = "sp_anim_is_playing", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult AnimIsPlaying(EngineSafeHandle engine, string itemId, out int playing);

    // ---- Elementos
    [LibraryImport(Library, EntryPoint = "sp_item_add", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult ItemAdd(EngineSafeHandle engine, string sceneId, string itemId, string sourceId);

    [LibraryImport(Library, EntryPoint = "sp_item_remove", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult ItemRemove(EngineSafeHandle engine, string itemId);

    [LibraryImport(Library, EntryPoint = "sp_item_set_transform", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult ItemSetTransform(EngineSafeHandle engine, string itemId, in SpTransform transform);

    [LibraryImport(Library, EntryPoint = "sp_item_get_transform", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult ItemGetTransform(EngineSafeHandle engine, string itemId, out SpTransform transform);

    [LibraryImport(Library, EntryPoint = "sp_item_set_visible", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult ItemSetVisible(EngineSafeHandle engine, string itemId, int visible);

    [LibraryImport(Library, EntryPoint = "sp_item_set_locked", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult ItemSetLocked(EngineSafeHandle engine, string itemId, int locked);

    [LibraryImport(Library, EntryPoint = "sp_item_set_order", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult ItemSetOrder(EngineSafeHandle engine, string itemId, int index);

    // ---- Salidas
    [LibraryImport(Library, EntryPoint = "sp_recording_start"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult RecordingStart(EngineSafeHandle engine, in SpRecordingConfig config);

    [LibraryImport(Library, EntryPoint = "sp_recording_stop"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult RecordingStop(EngineSafeHandle engine);

    [LibraryImport(Library, EntryPoint = "sp_stream_start"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult StreamStart(EngineSafeHandle engine, in SpStreamConfig config);

    [LibraryImport(Library, EntryPoint = "sp_stream_stop"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult StreamStop(EngineSafeHandle engine);

    [LibraryImport(Library, EntryPoint = "sp_output_last_stop"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult OutputLastStop(EngineSafeHandle engine, int kind, out int code, byte* buffer, int bufferSize, out int required);

    // ---- Displays
    [LibraryImport(Library, EntryPoint = "sp_display_create", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult DisplayCreate(EngineSafeHandle engine, string displayId, nint nativeWindow, uint width, uint height);

    [LibraryImport(Library, EntryPoint = "sp_display_destroy", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult DisplayDestroy(EngineSafeHandle engine, string displayId);

    [LibraryImport(Library, EntryPoint = "sp_display_resize", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult DisplayResize(EngineSafeHandle engine, string displayId, uint width, uint height);

    [LibraryImport(Library, EntryPoint = "sp_display_set_source", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult DisplaySetSource(EngineSafeHandle engine, string displayId, SpDisplayMode mode, string? sceneId);

    [LibraryImport(Library, EntryPoint = "sp_display_set_view", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult DisplaySetView(EngineSafeHandle engine, string displayId, float zoom, float offsetX, float offsetY);

    [LibraryImport(Library, EntryPoint = "sp_display_set_overlay", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult DisplaySetOverlay(EngineSafeHandle engine, string displayId, nint* selectedIds, int selectedCount,
        float* guidesX, int guidesXCount, float* guidesY, int guidesYCount);

    public static string GetLastError()
    {
        var required = LastError(null, 0);
        if (required <= 1)
            return "Error nativo sin detalle.";
        var buffer = new byte[required];
        fixed (byte* p = buffer)
            LastError(p, buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, required - 1);
    }

    public delegate SpResult BufferCall(byte* buffer, int size, out int required);

    /// <summary>Patrón de dos llamadas: primero se pide el tamaño, luego se llena el búfer.</summary>
    public static SpResult ReadString(BufferCall call, out string value)
    {
        value = string.Empty;
        var rc = call(null, 0, out var required);
        if (rc != SpResult.Ok)
            return rc;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new byte[Math.Max(required, 1)];
            fixed (byte* p = buffer)
                rc = call(p, buffer.Length, out required);
            if (rc == SpResult.BufferTooSmall)
                continue; // el valor creció entre llamadas
            if (rc == SpResult.Ok)
                value = System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Max(required - 1, 0));
            return rc;
        }
        return rc;
    }
}
