using LiveStream.Application.Audio;
using LiveStream.Application.Commands;
using LiveStream.Application.Filters;
using LiveStream.Application.Scenes;
using LiveStream.Application.Sources;
using LiveStream.Domain.Entities;
using LiveStream.Domain.ValueObjects;
using LiveStream.Infrastructure.Persistence;

namespace LiveStream.UnitTests;

public sealed class FilterAndAudioControlTests
{
    private readonly FakeStreamingEngine _engine = new();
    private readonly StudioProject _project = new(Guid.NewGuid(), "Audio & Filter Test");
    private readonly CommandExecutor _executor;

    public FilterAndAudioControlTests()
    {
        _executor = new CommandExecutor(new StudioContext(_project, _engine));
    }

    [Fact]
    public void Source_AudioCapabilities_AccuratelyReflectSourceKind()
    {
        var cam = new Source(Guid.NewGuid(), "Webcam", SourceKind.VideoCaptureDevice);
        Assert.True(cam.SupportsAudio);
        Assert.False(cam.IsAudioOnly);
        Assert.True(cam.AudioCaptureEnabled);
        Assert.True(cam.HasActiveAudio);

        cam.SetAudioCaptureEnabled(false);
        Assert.False(cam.AudioCaptureEnabled);
        Assert.False(cam.HasActiveAudio);

        var mic = new Source(Guid.NewGuid(), "Micrófono USB", SourceKind.AudioInput);
        Assert.True(mic.SupportsAudio);
        Assert.True(mic.IsAudioOnly);
        Assert.True(mic.HasActiveAudio);

        var img = new Source(Guid.NewGuid(), "Logo PNG", SourceKind.Image);
        Assert.False(img.SupportsAudio);
        Assert.False(img.IsAudioOnly);
        Assert.False(img.HasActiveAudio);
    }

    [Fact]
    public async Task SetAudioCaptureEnabledCommand_TogglesAndUndoes_Correctly()
    {
        var sceneCmd = new CreateSceneCommand("Principal");
        await _executor.ExecuteAsync(sceneCmd);
        var sceneId = sceneCmd.CreatedSceneId!.Value;

        var addCam = new AddSourceCommand(sceneId, "Cámara", SourceKind.VideoCaptureDevice);
        await _executor.ExecuteAsync(addCam);
        var camId = addCam.SourceId!.Value;
        var cam = _project.GetSource(camId);

        Assert.True(cam.AudioCaptureEnabled);

        // Desactivar captura de audio
        var disableCmd = new SetAudioCaptureEnabledCommand(camId, false);
        var res = await _executor.ExecuteAsync(disableCmd);
        Assert.True(res.Success);
        Assert.False(cam.AudioCaptureEnabled);
        Assert.False(cam.HasActiveAudio);

        // Deshacer (Undo) restaura la captura
        var undoRes = await _executor.UndoAsync();
        Assert.True(undoRes.Success);
        Assert.True(cam.AudioCaptureEnabled);
        Assert.True(cam.HasActiveAudio);
    }

    [Fact]
    public async Task DuplicateFilterCommand_ClonesSettingsAndName_WithUndo()
    {
        var sceneCmd = new CreateSceneCommand("Escena Filtros");
        await _executor.ExecuteAsync(sceneCmd);
        var sceneId = sceneCmd.CreatedSceneId!.Value;

        var addMic = new AddSourceCommand(sceneId, "Micrófono", SourceKind.AudioInput);
        await _executor.ExecuteAsync(addMic);
        var micId = addMic.SourceId!.Value;

        var addFilter = new AddFilterCommand(micId, "compressor_filter", "Compresor Principal", """{"ratio":3.5,"threshold":-16.0}""");
        await _executor.ExecuteAsync(addFilter);
        var filterId = addFilter.FilterId!.Value;

        // Duplicar el filtro existente
        var dupCmd = new DuplicateFilterCommand(micId, filterId, "Compresor Principal Copia");
        var dupRes = await _executor.ExecuteAsync(dupCmd);
        Assert.True(dupRes.Success);

        var mic = _project.GetSource(micId);
        Assert.Equal(2, mic.Filters.Count);
        var copy = mic.Filters[1];
        Assert.Equal("Compresor Principal Copia", copy.Name);
        Assert.Equal("compressor_filter", copy.TypeId);
        Assert.Equal("""{"ratio":3.5,"threshold":-16.0}""", copy.SettingsJson);

        // Deshacer elimina la copia
        var undo = await _executor.UndoAsync();
        Assert.True(undo.Success);
        Assert.Single(mic.Filters);
    }

    [Fact]
    public void FilterPresetStore_BuiltInPresets_HaveValidFilters()
    {
        var presets = FilterPresetStore.GetAllPresets();
        Assert.NotEmpty(presets);

        var audioPresets = FilterPresetStore.GetPresetsForSource(isAudio: true);
        var videoPresets = FilterPresetStore.GetPresetsForSource(isAudio: false);

        Assert.NotEmpty(audioPresets);
        Assert.NotEmpty(videoPresets);

        foreach (var preset in presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
            Assert.False(string.IsNullOrWhiteSpace(preset.Description));
            Assert.NotEmpty(preset.Filters);

            foreach (var f in preset.Filters)
            {
                Assert.NotNull(FilterCatalog.Find(f.TypeId));
                Assert.Null(FilterCatalog.ValidateJson(f.SettingsJson));
            }
        }
    }

    [Fact]
    public void FilterPresetStore_ExportAndImport_RoundTripsJson()
    {
        var original = new List<FilterPresetItem>
        {
            new("Supresión", "noise_suppress_filter_v2", """{"method":"rnnoise"}"""),
            new("Limitador", "limiter_filter", """{"threshold":-1.5}""")
        };

        var json = FilterPresetStore.ExportFiltersToJson(original);
        Assert.Contains("noise_suppress_filter_v2", json);

        var imported = FilterPresetStore.ImportFiltersFromJson(json);
        Assert.NotNull(imported);
        Assert.Equal(2, imported.Count);
        Assert.Equal("Supresión", imported[0].Name);
        Assert.Equal("noise_suppress_filter_v2", imported[0].TypeId);
    }

    [Fact]
    public void FilterPresetStore_CustomPresets_CanSaveAndRemove()
    {
        var customName = "Mi Preset Personalizado " + Guid.NewGuid();
        var filters = new List<FilterPresetItem>
        {
            new("Color", "color_filter_v2", """{"brightness":0.1}""")
        };

        FilterPresetStore.SaveCustomPreset(customName, "Descripción prueba", false, filters);

        var all = FilterPresetStore.GetAllPresets();
        var found = all.FirstOrDefault(p => p.Name == customName);
        Assert.NotNull(found);
        Assert.False(found.IsBuiltIn);
        Assert.Single(found.Filters);

        var deleted = FilterPresetStore.DeleteCustomPreset(customName);
        Assert.True(deleted);

        var afterDelete = FilterPresetStore.GetAllPresets();
        Assert.DoesNotContain(afterDelete, p => p.Name == customName);
    }

    [Fact]
    public void FilterCatalog_AllEntries_AreValidAndCategorized()
    {
        Assert.NotEmpty(FilterCatalog.All);
        foreach (var entry in FilterCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.TypeId));
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
            Assert.StartsWith("Icon.", entry.IconKey);
            Assert.Null(FilterCatalog.ValidateJson(entry.DefaultSettingsJson));
            Assert.False(string.IsNullOrWhiteSpace(FilterCatalog.CategoryName(entry.Category)));
        }
    }

    [Fact]
    public void ProjectSerializer_PreservesAudioCaptureEnabled_RoundTrip()
    {
        var project = new StudioProject(Guid.NewGuid(), "RoundTrip Test");
        var cam = new Source(Guid.NewGuid(), "Cámara USB", SourceKind.VideoCaptureDevice);
        cam.SetAudioCaptureEnabled(false);
        project.AddSource(cam);

        var json = ProjectSerializer.Serialize(project);
        var restored = ProjectSerializer.Deserialize(json);

        var restoredCam = restored.FindSource(cam.Id);
        Assert.NotNull(restoredCam);
        Assert.False(restoredCam.AudioCaptureEnabled);
        Assert.False(restoredCam.HasActiveAudio);
    }
}
