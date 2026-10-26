using System.Text.Json;
using LiveStream.Application.Commands;
using LiveStream.Application.Filters;
using LiveStream.Application.Graphics;
using LiveStream.Application.Scenes;
using LiveStream.Application.Sources;
using LiveStream.Domain.Animation;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using LiveStream.Infrastructure.Persistence;
using LiveStream.Native.Engine;

namespace LiveStream.UnitTests;

/// <summary>Fase 3: filtros, transiciones por escena, gráficos y animación.</summary>
public sealed class ProductionTests
{
    private readonly FakeStreamingEngine _engine = new();
    private readonly StudioProject _project = new(Guid.NewGuid(), "Test") { Canvas = new CanvasSettings(1920, 1080, 30) };
    private readonly CommandExecutor _executor;

    public ProductionTests()
    {
        _executor = new CommandExecutor(new StudioContext(_project, _engine));
        // Lo que haría la carga del proyecto.
        _ = GraphicLayer.CreateOverlayAsync(_engine, _project, CancellationToken.None);
    }

    private async Task Ok(IStudioCommand command)
    {
        var result = await _executor.ExecuteAsync(command);
        Assert.True(result.Success, result.Error);
    }

    private async Task<Guid> SceneAsync(string name)
    {
        var command = new CreateSceneCommand(name);
        await Ok(command);
        return command.CreatedSceneId!.Value;
    }

    // ---- Filtros --------------------------------------------------------------------------------

    [Fact]
    public async Task Filters_AddUpdateDisableMoveRemove_WithUndo()
    {
        var scene = await SceneAsync("A");
        var add = new AddSourceCommand(scene, "Mic", SourceKind.AudioInput);
        await Ok(add);
        var mic = _project.GetSource(add.SourceId!.Value);

        var gate = new AddFilterCommand(mic.Id, "noise_gate_filter", "Puerta");
        var comp = new AddFilterCommand(mic.Id, "compressor_filter", "Compresor");
        await Ok(gate);
        await Ok(comp);
        Assert.Equal(["Puerta", "Compresor"], mic.Filters.Select(f => f.Name));
        Assert.Equal(2, _engine.Filters.Count);

        await Ok(new UpdateFilterSettingsCommand(mic.Id, comp.FilterId!.Value, """{"ratio":4.0}"""));
        Assert.Equal("""{"ratio":4.0}""", _engine.Filters[comp.FilterId!.Value].SettingsJson);
        await Ok(new SetFilterEnabledCommand(mic.Id, gate.FilterId!.Value, false));
        Assert.False(_engine.Filters[gate.FilterId!.Value].Enabled);

        await Ok(new MoveFilterCommand(mic.Id, comp.FilterId!.Value, 0));
        Assert.Equal(["Compresor", "Puerta"], mic.Filters.Select(f => f.Name));
        // El motor se reconstruye conservando ajustes y estado de cada filtro.
        Assert.False(_engine.Filters[gate.FilterId!.Value].Enabled);
        Assert.Equal("""{"ratio":4.0}""", _engine.Filters[comp.FilterId!.Value].SettingsJson);

        await Ok(new RemoveFilterCommand(mic.Id, gate.FilterId!.Value));
        Assert.Single(mic.Filters);
        Assert.True((await _executor.UndoAsync()).Success);
        Assert.Equal(2, mic.Filters.Count);
        Assert.True(_engine.Filters.ContainsKey(gate.FilterId!.Value));

        Assert.True((await _executor.UndoAsync()).Success); // mover
        Assert.Equal(["Puerta", "Compresor"], mic.Filters.Select(f => f.Name));
    }

    [Fact]
    public async Task Filters_RejectAudioFilterOnVideoOnlySource_AndUnknownTypes()
    {
        var scene = await SceneAsync("A");
        var add = new AddSourceCommand(scene, "Color", SourceKind.ColorSource);
        await Ok(add);
        var id = add.SourceId!.Value;

        Assert.False((await _executor.ExecuteAsync(new AddFilterCommand(id, "gain_filter", "G"))).Success);
        Assert.False((await _executor.ExecuteAsync(new AddFilterCommand(id, "inventado", "X"))).Success);
        Assert.False((await _executor.ExecuteAsync(new AddFilterCommand(id, "color_filter_v2", "C", "no json"))).Success);
        await Ok(new AddFilterCommand(id, "chroma_key_filter_v2", "Croma"));
    }

    [Fact]
    public async Task RemovingSource_AndUndo_RestoresItsFilters()
    {
        var scene = await SceneAsync("A");
        var add = new AddSourceCommand(scene, "Mic", SourceKind.AudioInput);
        await Ok(add);
        var filter = new AddFilterCommand(add.SourceId!.Value, "gain_filter", "Ganancia", """{"db":3.0}""");
        await Ok(filter);

        await Ok(new RemoveItemCommand(_project.Scenes[0].Items[0].Id));
        Assert.Empty(_engine.Filters);
        Assert.True((await _executor.UndoAsync()).Success);
        Assert.Equal("""{"db":3.0}""", _engine.Filters[filter.FilterId!.Value].SettingsJson);
    }

    // ---- Transiciones ---------------------------------------------------------------------------

    [Fact]
    public async Task SceneTransitionOverride_IsUsedWhenEnteringThatScene()
    {
        var a = await SceneAsync("A");
        var b = await SceneAsync("B");
        await Ok(new SetTransitionCommand(new TransitionSettings("fade_transition", 300)));
        var slide = new TransitionSettings("slide_transition", 500, """{"direction":"up"}""");
        await Ok(new SetSceneTransitionCommand(b, slide));

        await Ok(new ActivateSceneCommand(b));
        Assert.Equal(slide, _engine.Transition);
        await Ok(new ActivateSceneCommand(a));
        Assert.Equal(new TransitionSettings("fade_transition", 300), _engine.Transition);

        // Sin cambio de transición no se recrea en el motor.
        var calls = _engine.Calls.Count(c => c == "transition");
        await Ok(new ActivateSceneCommand(a));
        Assert.Equal(calls, _engine.Calls.Count(c => c == "transition"));

        Assert.True((await _executor.UndoAsync()).Success); // quita la transición de B
        Assert.Null(_project.GetScene(b).TransitionOverride);
    }

    [Fact]
    public async Task Transitions_ValidateSettings()
    {
        await SceneAsync("A");
        Assert.False((await _executor.ExecuteAsync(new SetTransitionCommand(new TransitionSettings("obs_stinger_transition", 0, "{}")))).Success);
        Assert.False((await _executor.ExecuteAsync(new SetTransitionCommand(
            new TransitionSettings("obs_stinger_transition", 0, """{"path":"C:/no/existe.webm"}""")))).Success);
        Assert.False((await _executor.ExecuteAsync(new SetTransitionCommand(new TransitionSettings("slide_transition", 300, """{"direction":"diagonal"}""")))).Success);
        Assert.False((await _executor.ExecuteAsync(new SetTransitionCommand(new TransitionSettings("fade_transition", 300, "[1]")))).Success);
        await Ok(new SetTransitionCommand(new TransitionSettings("wipe_transition", 800, """{"luma_image":"clock.png"}""")));
    }

    // ---- Gráficos -------------------------------------------------------------------------------

    [Fact]
    public void Templates_ProduceSameElementKeys_WhateverTheFields()
    {
        foreach (var kind in GraphicTemplates.Kinds)
        {
            var g = GraphicTemplates.Create(kind, kind.ToString(), _project.Canvas);
            Assert.Null(g.Validate());
            Assert.Null(GraphicTemplates.ValidateFields(g));
            var keys = GraphicTemplates.Layout(g, _project.Canvas).Elements.Select(e => e.Key).ToList();
            var empty = g with { Fields = new Dictionary<string, string>(), Scale = 2, X = 10 };
            Assert.Equal(keys, GraphicTemplates.Layout(empty, _project.Canvas).Elements.Select(e => e.Key));
            Assert.Equal(keys.Count, keys.Distinct().Count());

            // Dentro del lienzo por defecto (horizontal y vertical).
            foreach (var canvas in new[] { _project.Canvas, new CanvasSettings(1080, 1920, 30) })
            {
                var placed = GraphicTemplates.Create(kind, "x", canvas);
                var (l, t, r, b) = GraphicTemplates.Layout(placed, canvas).Bounds;
                Assert.True(l >= 0 && t >= 0 && b <= canvas.Height + 0.5, $"{kind} fuera del lienzo {canvas.Width}x{canvas.Height}");
                Assert.True(r <= canvas.Width + 0.5, $"{kind} se sale por la derecha en {canvas.Width}x{canvas.Height}");
            }
        }
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1080, 1920)]
    [InlineData(1280, 720)]
    public void Templates_DefaultPositions_DoNotOverlapForATypicalShow(int width, int height)
    {
        var canvas = new CanvasSettings(width, height, 30);
        var show = new[] { GraphicKind.LiveBadge, GraphicKind.Scoreboard, GraphicKind.Countdown, GraphicKind.LowerThird, GraphicKind.Ticker }
            .Select(k => (Kind: k, Box: GraphicTemplates.Layout(GraphicTemplates.Create(k, "x", canvas), canvas).Bounds)).ToList();
        foreach (var a in show)
            foreach (var b in show.Where(b => b.Kind > a.Kind))
            {
                var overlap = a.Box.Left < b.Box.Right && b.Box.Left < a.Box.Right && a.Box.Top < b.Box.Bottom && b.Box.Top < a.Box.Bottom;
                Assert.False(overlap, $"{a.Kind} y {b.Kind} se tapan en {width}x{height}");
            }
    }

    [Fact]
    public async Task Graphics_AddUpdateMoveRemove_SyncNestedScenesInEngine()
    {
        var lower = GraphicTemplates.Create(GraphicKind.LowerThird, "Rótulo", _project.Canvas);
        var badge = GraphicTemplates.Create(GraphicKind.LiveBadge, "Mosca", _project.Canvas);
        await Ok(new AddGraphicCommand(lower));
        await Ok(new AddGraphicCommand(badge));

        var overlay = GraphicLayer.OverlaySceneId(_project);
        Assert.Equal(GraphicLayer.OverlayChannel, _engine.Channels[overlay]);
        Assert.Equal((overlay, lower.Id), _engine.Items[GraphicLayer.OverlayItemId(lower.Id)]);
        Assert.False(_engine.Visible[GraphicLayer.OverlayItemId(lower.Id)]); // oculto hasta lanzarlo
        var nameSource = GraphicLayer.ElementSourceId(lower.Id, "name");
        Assert.Contains("Nombre Apellido", _engine.Sources[nameSource].SettingsJson);

        // Nombre duplicado y tipo cambiado se rechazan.
        Assert.False((await _executor.ExecuteAsync(new AddGraphicCommand(GraphicTemplates.Create(GraphicKind.Clock, "Rótulo", _project.Canvas)))).Success);
        Assert.False((await _executor.ExecuteAsync(new UpdateGraphicCommand(lower with { Kind = GraphicKind.Clock }))).Success);

        var edited = lower with { Fields = new Dictionary<string, string> { ["name"] = "Ana", ["title"] = "Directora" }, X = 300 };
        await Ok(new UpdateGraphicCommand(edited));
        Assert.Contains("\"Ana\"", _engine.Sources[nameSource].SettingsJson);
        Assert.Equal(300 + 40, _engine.Transforms[GraphicLayer.ElementItemId(lower.Id, "name")].X, 0.5);
        Assert.True((await _executor.UndoAsync()).Success);
        Assert.Contains("Nombre Apellido", _engine.Sources[nameSource].SettingsJson);

        await Ok(new MoveGraphicCommand(badge.Id, 0));
        Assert.Equal(badge.Id, _project.Graphics[0].Id);

        var sourcesBefore = _engine.Sources.Count;
        await Ok(new RemoveGraphicCommand(lower.Id));
        Assert.False(_engine.Scenes.ContainsKey(lower.Id));
        Assert.False(_engine.Items.ContainsKey(GraphicLayer.OverlayItemId(lower.Id)));
        Assert.True(_engine.Sources.Count < sourcesBefore);
        Assert.True((await _executor.UndoAsync()).Success);
        Assert.True(_engine.Scenes.ContainsKey(lower.Id));
        Assert.Equal(1, _project.IndexOfGraphic(lower.Id));
    }

    [Fact]
    public async Task GraphicsService_TakeInOut_AutoHide_AndLiveData()
    {
        var alert = GraphicTemplates.Create(GraphicKind.Alert, "Alerta", _project.Canvas) with { AutoHideSeconds = 0.3, AnimationMs = 100 };
        var dataFile = Path.Combine(Directory.CreateTempSubdirectory("sp-data-").FullName, "marcador.json");
        var score = GraphicTemplates.Create(GraphicKind.Scoreboard, "Marcador", _project.Canvas) with { DataFile = dataFile };
        await Ok(new AddGraphicCommand(alert));
        await Ok(new AddGraphicCommand(score));

        await using var service = new GraphicsService(_executor);
        service.Start();
        var item = GraphicLayer.OverlayItemId(alert.Id);
        await service.TakeInAsync(alert.Id);
        Assert.True(service.IsOnAir(alert.Id));
        Assert.Equal(AnimationFlags.ShowAtStart, _engine.Animations[item].Flags);

        // Salida automática tras animación + 0,3 s.
        for (var i = 0; i < 30 && service.IsOnAir(alert.Id); i++)
            await Task.Delay(100);
        Assert.False(service.IsOnAir(alert.Id));
        Assert.Equal(AnimationFlags.HideAtEnd, _engine.Animations[item].Flags);

        // Datos en vivo: un programa externo escribe el marcador.
        await File.WriteAllTextAsync(dataFile, """{"homeScore": 2, "awayScore": "1", "otro": "ignorado"}""");
        var homeScore = GraphicLayer.ElementSourceId(score.Id, "homeScore");
        for (var i = 0; i < 40 && !_engine.Sources[homeScore].SettingsJson.Contains("\"2\""); i++)
            await Task.Delay(100);
        Assert.Contains("\"2\"", _engine.Sources[homeScore].SettingsJson);
        Assert.Equal("1", _project.GetGraphic(score.Id).Field("awayScore"));
        Assert.False(_executor.CanUndo && _executor.UndoDescription == "Datos en vivo del gráfico");
    }

    [Fact]
    public async Task LowerThird_Logo_IsAnImagePiece_FittedInsideTheSameWidth()
    {
        var plain = GraphicTemplates.Create(GraphicKind.LowerThird, "Rótulo", _project.Canvas);
        var withLogo = plain with { Fields = new Dictionary<string, string>(plain.Fields) { ["logo"] = @"C:\logos\iglesia.png" } };

        var before = GraphicTemplates.Layout(plain, _project.Canvas);
        var after = GraphicTemplates.Layout(withLogo, _project.Canvas);
        var logo = after.Elements.Single(e => e.Key == "logo");
        Assert.Equal(GraphicElementKind.Image, logo.Kind);
        Assert.Equal(before.Bounds.Right, after.Bounds.Right, 3);
        Assert.True(after.Elements.Single(e => e.Key == "name").X > logo.X + logo.Width);

        await Ok(new AddGraphicCommand(withLogo));
        var source = _engine.Sources[GraphicLayer.ElementSourceId(withLogo.Id, "logo")];
        Assert.Equal(SourceKind.Image, source.Kind);
        Assert.Contains("iglesia.png", source.SettingsJson);
    }

    [Fact]
    public async Task GraphicsService_Countdown_RunsAndShowsFinishedText()
    {
        var countdown = GraphicTemplates.Create(GraphicKind.Countdown, "Cuenta", _project.Canvas) with
        {
            Fields = new Dictionary<string, string> { ["label"] = "EMPEZAMOS", ["duration"] = "1", ["finished"] = "¡YA!" },
        };
        await Ok(new AddGraphicCommand(countdown));
        await using var service = new GraphicsService(_executor);
        var time = GraphicLayer.ElementSourceId(countdown.Id, "time");

        Assert.Equal(TimeSpan.FromSeconds(1), service.GetCountdown(countdown.Id).Remaining);
        service.StartCountdown(countdown.Id);
        Assert.True(service.GetCountdown(countdown.Id).Running);
        for (var i = 0; i < 30 && !_engine.Sources[time].SettingsJson.Contains("¡YA!"); i++)
            await Task.Delay(100);
        Assert.Contains("¡YA!", JsonDocument.Parse(_engine.Sources[time].SettingsJson).RootElement.GetProperty("text").GetString());
        Assert.False(service.GetCountdown(countdown.Id).Running);

        service.ResetCountdown(countdown.Id);
        Assert.Equal(TimeSpan.FromSeconds(1), service.GetCountdown(countdown.Id).Remaining);
    }

    [Theory]
    [InlineData("19:30", "2026-10-25 19:00", 30 * 60)]       // más tarde hoy
    [InlineData("19:30", "2026-10-25 19:45", -15 * 60)]      // acaba de pasar: terminada
    [InlineData("01:00", "2026-10-25 23:00", 2 * 3600)]      // mañana de madrugada
    public void Countdown_EndsAt_CountsToTheNextTimeOnTheClock(string endsAt, string now, int expectedSeconds)
    {
        var until = GraphicTemplates.UntilEndsAt(GraphicTemplates.ParseEndsAt(endsAt)!.Value, DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), until);
    }

    [Fact]
    public async Task Countdown_WithEndsAt_RunsFromTheSystemClock_AndRejectsBadTimes()
    {
        var endsAt = TimeOnly.FromDateTime(DateTime.Now.AddMinutes(10));
        var countdown = GraphicTemplates.Create(GraphicKind.Countdown, "Evento", _project.Canvas) with
        {
            Fields = new Dictionary<string, string> { ["duration"] = "05:00", ["endsAt"] = endsAt.ToString("HH:mm:ss") },
        };
        await Ok(new AddGraphicCommand(countdown));
        await using var service = new GraphicsService(_executor);

        var status = service.GetCountdown(countdown.Id);
        Assert.True(status.Running);
        Assert.InRange(status.Remaining, TimeSpan.FromMinutes(9.9), TimeSpan.FromMinutes(10));
        Assert.NotNull(GraphicTemplates.ValidateFields(countdown with { Fields = new Dictionary<string, string> { ["duration"] = "05:00", ["endsAt"] = "25:99" } }));
    }

    [Fact]
    public void Animations_InEndsAtBase_OutLeavesGraphicOffscreenOrTransparent()
    {
        foreach (var anim in Enum.GetValues<GraphicAnimation>())
        {
            var g = GraphicTemplates.Create(GraphicKind.LowerThird, "x", _project.Canvas) with { AnimationIn = anim, AnimationOut = anim };
            var clipIn = GraphicAnimations.In(g, _project.Canvas);
            Assert.Null(clipIn.Validate());
            foreach (var track in clipIn.Tracks)
            {
                var end = track.Keys[^1].Value;
                var expected = track.Property switch
                {
                    AnimatedProperty.ScaleX or AnimatedProperty.ScaleY or AnimatedProperty.Opacity => 1.0,
                    _ => 0.0,
                };
                Assert.Equal(expected, end, 6);
            }
            Assert.Null(GraphicAnimations.Out(g, _project.Canvas).Validate());
        }
    }

    [Fact]
    public void AnimationClipJson_UsesBridgeFormat()
    {
        var clip = new AnimationClip([new AnimationTrack(AnimatedProperty.CropRight, [new Keyframe(0, 1.5), new Keyframe(0.25, 0, Easing.InOutCubic)])], Delay: 0.1);
        Assert.Equal("""{"delay":0.1,"duration":0,"tracks":[{"p":"crop_right","k":[{"t":0,"v":1.5,"e":"linear"},{"t":0.25,"v":0,"e":"in_out_cubic"}]}]}""",
            AnimationClipJson.Serialize(clip));
    }

    [Fact]
    public void GraphicColor_ConvertsToLibobsAbgr()
    {
        Assert.Equal(0xFF0000FFu, GraphicColor.ToAbgr("#FF0000"));
        Assert.Equal(0x80FF0000u, GraphicColor.ToAbgr("#800000FF"));
        Assert.False(GraphicColor.TryParse("rojo", out _));
        var text = GraphicLayer.SettingsJson(new GraphicElement("t", GraphicElementKind.Text, 0, 0, 100, 40, "#80FFFFFF", "Hola", 20), new GraphicStyle());
        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(0xFFFFFF, root.GetProperty("color").GetInt64());
        Assert.Equal(50, root.GetProperty("opacity").GetInt32());
    }

    [Fact]
    public void Serializer_V3_RoundTripsFiltersSceneTransitionsAndGraphics()
    {
        var scene = _project.AddScene("A");
        scene.TransitionOverride = new TransitionSettings("wipe_transition", 900, """{"luma_image":"clock.png"}""");
        var source = new Source(Guid.NewGuid(), "Mic", SourceKind.AudioInput);
        source.InsertFilter(new SourceFilter(Guid.NewGuid(), "Ruido", "noise_suppress_filter_v2", """{"method":"rnnoise"}""", enabled: false), 0);
        _project.AddSource(source);
        var g = GraphicTemplates.Create(GraphicKind.Ticker, "Cintillo", _project.Canvas) with { DataFile = @"C:\datos\cintillo.json" };
        _project.InsertGraphic(g);

        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(_project));

        Assert.Equal(scene.TransitionOverride, copy.GetScene(scene.Id).TransitionOverride);
        var filter = Assert.Single(copy.GetSource(source.Id).Filters);
        Assert.Equal(("Ruido", "noise_suppress_filter_v2", false), (filter.Name, filter.TypeId, filter.Enabled));
        Assert.Contains("rnnoise", filter.SettingsJson);
        var g2 = Assert.Single(copy.Graphics);
        Assert.Equal((g.Kind, g.Name, g.X, g.Y, g.AnimationIn, g.DataFile), (g2.Kind, g2.Name, g2.X, g2.Y, g2.AnimationIn, g2.DataFile));
        Assert.Equal(g.Fields, g2.Fields);
        Assert.Equal(g.Style, g2.Style);
    }
}

public sealed class GraphicTemplateLibraryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sp-tpl-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task SaveListInstantiateDelete()
    {
        var library = new GraphicTemplateLibrary(_dir);
        var canvas = new CanvasSettings();
        var g = GraphicTemplates.Create(GraphicKind.LowerThird, "Rótulo azul", canvas) with
        {
            Style = new GraphicStyle(PrimaryColor: "#FF1565C0"),
            DataFile = @"C:\datos\x.json",
        };
        await library.SaveAsync("Rótulo: azul", g, CancellationToken.None);

        var info = Assert.Single(await library.ListAsync(CancellationToken.None));
        Assert.Equal(("Rótulo: azul", GraphicKind.LowerThird), (info.Name, info.Kind));

        var copy = await library.InstantiateAsync("Rótulo: azul", CancellationToken.None);
        Assert.NotEqual(g.Id, copy.Id);
        Assert.Null(copy.DataFile);
        Assert.Equal(g.Style, copy.Style);
        Assert.Equal(g.Fields, copy.Fields);

        File.WriteAllText(Path.Combine(_dir, "rota.spgfx"), "{no es json");
        Assert.Single(await library.ListAsync(CancellationToken.None));

        await library.DeleteAsync("Rótulo: azul", CancellationToken.None);
        Assert.Empty(await library.ListAsync(CancellationToken.None));
    }
}
