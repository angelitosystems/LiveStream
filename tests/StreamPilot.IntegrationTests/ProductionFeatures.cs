using StreamPilot.Application.Abstractions;
using StreamPilot.Application.Graphics;
using StreamPilot.Domain.Animation;
using StreamPilot.Domain.Entities;
using StreamPilot.Domain.Graphics;
using StreamPilot.Domain.ValueObjects;
using StreamPilot.Native.Engine;
using Xunit.Abstractions;

namespace StreamPilot.IntegrationTests;

/// <summary>Fase 3 contra libobs real: transiciones con ajustes, filtros, escenas anidadas, animación y gráficos.</summary>
internal sealed class ProductionFeatures(LibObsEngine engine, ITestOutputHelper output)
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Archivos que libobs mantiene abiertos hasta apagarse (el video del stinger).</summary>
    public List<string> FilesToDelete { get; } = [];

    public async Task RunAsync()
    {
        await TypesAsync();
        var (a, b) = await TransitionsAsync();
        await FiltersAsync();
        await NestedScenesAndAnimationAsync(a);
        await GraphicsAsync();
        await engine.RemoveSceneAsync(b, None);
    }

    private async Task TypesAsync()
    {
        var filters = await engine.ListFilterTypesAsync(None);
        output.WriteLine("Filtros: " + string.Join(", ", filters.Select(f => f.Id)));
        foreach (var id in new[] { "gain_filter", "noise_suppress_filter_v2", "compressor_filter", "limiter_filter", "noise_gate_filter",
                     "scroll_filter", "color_filter_v2", "chroma_key_filter_v2" })
            Assert.Contains(filters, f => f.Id == id);
        Assert.True(filters.Single(f => f.Id == "gain_filter").HasAudio);
        Assert.True(filters.Single(f => f.Id == "color_filter_v2").HasVideo);

        var transitions = await engine.ListTransitionTypesAsync(None);
        output.WriteLine("Transiciones: " + string.Join(", ", transitions.Select(t => t.Id)));
        foreach (var id in new[] { "cut_transition", "fade_transition", "swipe_transition", "slide_transition", "fade_to_color_transition",
                     "wipe_transition", "obs_stinger_transition" })
            Assert.Contains(transitions, t => t.Id == id);
    }

    private async Task<(Guid, Guid)> TransitionsAsync()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var colorA = Guid.NewGuid();
        await engine.CreateSceneAsync(a, "Producción A", None);
        await engine.CreateSceneAsync(b, "Producción B", None);
        await engine.CreateSourceAsync(new SourceDefinition(colorA, "Fondo A", SourceKind.ColorSource,
            """{"color":4282664004,"width":1280,"height":720}"""), None);
        await engine.AddItemAsync(a, Guid.NewGuid(), colorA, None);
        await engine.SetProgramSceneAsync(a, None);

        var cases = new[]
        {
            new TransitionSettings("wipe_transition", 300, """{"luma_image":"clock.png","luma_softness":0.1,"luma_invert":false}"""),
            new TransitionSettings("slide_transition", 300, """{"direction":"up"}"""),
            new TransitionSettings("swipe_transition", 300, """{"direction":"right","swipe_in":true}"""),
            new TransitionSettings("fade_to_color_transition", 300, """{"color":4278190080,"switch_point":50}"""),
        };
        var target = b;
        foreach (var transition in cases)
        {
            await engine.SetTransitionAsync(transition, None);
            await engine.SetProgramSceneAsync(target, None);
            await Task.Delay(450);
            target = target == a ? b : a;
        }

        // Stinger con un video real: se graba un clip corto del programa y se usa como cortinilla.
        var stinger = Path.Combine(Path.GetTempPath(), "StreamPilotTests", $"stinger-{Guid.NewGuid():N}.mkv");
        await engine.StartRecordingAsync(new RecordingSettings(stinger, new EncoderSettings("obs_x264", 2000)), None);
        await Task.Delay(1500);
        await engine.StopRecordingAsync(None);
        Assert.True(new FileInfo(stinger).Length > 10_000);
        await engine.SetTransitionAsync(new TransitionSettings("obs_stinger_transition", 0,
            $$"""{"path":"{{stinger.Replace("\\", "/")}}","transition_point":400,"tp_type":0}"""), None);
        await engine.SetProgramSceneAsync(target, None);
        await Task.Delay(1800);
        await engine.SetTransitionAsync(new TransitionSettings("cut_transition", 0), None);
        await engine.SetProgramSceneAsync(a, None);
        FilesToDelete.Add(stinger);

        await Assert.ThrowsAsync<NativeEngineException>(() => engine.SetTransitionAsync(new TransitionSettings("no_existe", 300), None));
        output.WriteLine("Transiciones con ajustes y stinger: OK");
        return (a, b);
    }

    private async Task FiltersAsync()
    {
        var mic = Guid.NewGuid();
        await engine.CreateSourceAsync(new SourceDefinition(mic, "Mic filtros", SourceKind.AudioInput, """{"device_id":"default"}"""), None);
        var gain = Guid.NewGuid();
        var noise = Guid.NewGuid();
        var compressor = Guid.NewGuid();
        await engine.AddFilterAsync(mic, gain, "gain_filter", "Ganancia", """{"db":6.0}""", None);
        await engine.AddFilterAsync(mic, noise, "noise_suppress_filter_v2", "Ruido", """{"method":"rnnoise"}""", None);
        await engine.AddFilterAsync(mic, compressor, "compressor_filter", "Compresor", "{}", None);
        await engine.UpdateFilterAsync(gain, """{"db":-3.0}""", None);
        Assert.Contains("-3", await engine.GetSourceSettingsAsync(gain, None));
        var props = await engine.GetSourcePropertiesAsync(compressor, None);
        output.WriteLine("Propiedades del compresor: " + string.Join(", ", props.Select(p => p.Name)));
        Assert.Contains(props, p => p.Name == "ratio");
        await engine.SetFilterEnabledAsync(noise, false, None);
        await engine.RemoveFilterAsync(compressor, None);
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.UpdateFilterAsync(compressor, "{}", None));

        var color = Guid.NewGuid();
        await engine.CreateSourceAsync(new SourceDefinition(color, "Color filtros", SourceKind.ColorSource, """{"width":200,"height":100}"""), None);
        // Un filtro de audio en una fuente sin audio se rechaza.
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.AddFilterAsync(color, Guid.NewGuid(), "gain_filter", "x", "{}", None));
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.AddFilterAsync(color, Guid.NewGuid(), "filtro_inventado", "x", "{}", None));
        var cc = Guid.NewGuid();
        await engine.AddFilterAsync(color, cc, "color_filter_v2", "Color", """{"saturation":-1.0}""", None);

        // Al eliminar la fuente, sus filtros dejan de existir.
        await engine.RemoveSourceAsync(color, None);
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.SetFilterEnabledAsync(cc, false, None));
        await engine.RemoveSourceAsync(mic, None);
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.SetFilterEnabledAsync(gain, false, None));
        output.WriteLine("Filtros de audio y video: OK");
    }

    private async Task NestedScenesAndAnimationAsync(Guid program)
    {
        var inner = Guid.NewGuid();
        var box = Guid.NewGuid();
        await engine.CreateSceneAsync(inner, "Interior", None);
        await engine.CreateSourceAsync(new SourceDefinition(box, "Caja", SourceKind.ColorSource, """{"width":300,"height":120}"""), None);
        await engine.AddItemAsync(inner, Guid.NewGuid(), box, None);

        var nested = Guid.NewGuid();
        await engine.AddItemAsync(program, nested, inner, None);
        // Ni una escena dentro de sí misma ni un ciclo.
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.AddItemAsync(inner, Guid.NewGuid(), inner, None));
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.AddItemAsync(inner, Guid.NewGuid(), program, None));

        var clip = new AnimationClip(
        [
            new AnimationTrack(AnimatedProperty.X, [new Keyframe(0, 0), new Keyframe(0.4, 200, Easing.OutCubic)]),
            new AnimationTrack(AnimatedProperty.ScaleX, [new Keyframe(0, 1), new Keyframe(0.4, 0.5, Easing.OutBack)]),
            new AnimationTrack(AnimatedProperty.Opacity, [new Keyframe(0, 0), new Keyframe(0.4, 1)]),
        ]);
        await engine.PlayAnimationAsync(nested, clip, AnimationFlags.ShowAtStart, None);
        Assert.True(await engine.IsAnimationPlayingAsync(nested, None));
        var mid = await engine.GetItemTransformAsync(nested, None);
        await Task.Delay(900); // la animación avanza con el reloj de fotogramas del motor, no con este hilo
        Assert.False(await engine.IsAnimationPlayingAsync(nested, None));
        var end = await engine.GetItemTransformAsync(nested, None);
        output.WriteLine($"Animación: al inicio x={mid.X:F1}, al final x={end.X:F1} escala={end.ScaleX:F2}");
        Assert.Equal(200, end.X, 0.01);
        Assert.Equal(0.5, end.ScaleX, 0.01);
        Assert.InRange(mid.X, 0, 199);

        // Un clip inválido se rechaza antes de llegar al motor; detener es idempotente.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            engine.PlayAnimationAsync(nested, new AnimationClip([new AnimationTrack(AnimatedProperty.X, [])]), AnimationFlags.None, None));
        await engine.StopAnimationAsync(nested, None);

        // Salida con ocultación al final; luego se elimina la escena anidada y su elemento en programa desaparece.
        await engine.PlayAnimationAsync(nested, new AnimationClip([new AnimationTrack(AnimatedProperty.Y, [new Keyframe(0, 0), new Keyframe(0.2, 720)])]),
            AnimationFlags.HideAtEnd, None);
        await Task.Delay(500);
        await engine.RemoveSceneAsync(inner, None);
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.GetItemTransformAsync(nested, None));
        await engine.RemoveSourceAsync(box, None);
        output.WriteLine("Escenas anidadas y animación por fotogramas clave: OK");
    }

    private async Task GraphicsAsync()
    {
        var project = new StudioProject(Guid.NewGuid(), "Gráficos") { Canvas = new CanvasSettings(1280, 720, 30) };
        await GraphicLayer.CreateOverlayAsync(engine, project, None);

        var graphics = GraphicTemplates.Kinds.Select(k => GraphicTemplates.Create(k, k.ToString(), project.Canvas)).ToList();
        for (var i = 0; i < graphics.Count; i++)
        {
            project.InsertGraphic(graphics[i]);
            await GraphicLayer.CreateAsync(engine, project, graphics[i], i, null, None);
        }

        var lower = graphics.Single(g => g.Kind == GraphicKind.LowerThird);
        var overlayItem = GraphicLayer.OverlayItemId(lower.Id);
        await engine.PlayAnimationAsync(overlayItem, GraphicAnimations.In(lower, project.Canvas), AnimationFlags.ShowAtStart, None);
        await Task.Delay(900);
        var shown = await engine.GetItemTransformAsync(overlayItem, None);
        Assert.Equal(0, shown.X, 0.01);
        Assert.Equal(0, shown.CropRight);

        // Edición en vivo: mismos ids, nuevos textos y colores.
        var edited = lower with
        {
            Fields = new Dictionary<string, string> { ["name"] = "Ana Pérez", ["title"] = "Corresponsal" },
            Style = lower.Style with { PrimaryColor = "#FF1565C0" },
        };
        project.ReplaceGraphic(edited);
        await GraphicLayer.UpdateAsync(engine, project, edited, null, None);
        Assert.Contains("Ana Pérez", await engine.GetSourceSettingsAsync(GraphicLayer.ElementSourceId(lower.Id, "name"), None));

        // El cintillo se desplaza con scroll_filter y queda centrado en su banda.
        var ticker = graphics.Single(g => g.Kind == GraphicKind.Ticker);
        var scroll = await engine.GetSourceSettingsAsync(GraphicLayer.ScrollFilterId(ticker.Id, "items"), None);
        Assert.Contains("\"speed_x\"", scroll);

        await engine.PlayAnimationAsync(overlayItem, GraphicAnimations.Out(edited, project.Canvas), AnimationFlags.HideAtEnd, None);
        await Task.Delay(900);
        Assert.False(await engine.IsAnimationPlayingAsync(overlayItem, None));

        foreach (var g in graphics)
            await GraphicLayer.RemoveAsync(engine, project, g, None);
        await Assert.ThrowsAsync<NativeEngineException>(() => engine.GetItemTransformAsync(overlayItem, None));
        await engine.RemoveSceneAsync(GraphicLayer.OverlaySceneId(project), None);
        output.WriteLine($"Gráficos ({graphics.Count} plantillas) creados, animados, editados en vivo y eliminados: OK");
    }
}
