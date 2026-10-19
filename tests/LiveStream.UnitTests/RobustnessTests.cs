using System.Text.Json;
using LiveStream.Application.Commands;
using LiveStream.Application.Filters;
using LiveStream.Application.Graphics;
using LiveStream.Application.Scenes;
using LiveStream.Application.Sources;
using LiveStream.Application.Streaming;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using LiveStream.Domain.ValueObjects;
using LiveStream.Infrastructure.Persistence;

namespace LiveStream.UnitTests;

/// <summary>
/// Fase 7: robustez. Secuencias aleatorias de comandos (propiedades de deshacer/rehacer) y archivos de proyecto
/// corruptos. Las semillas son fijas: un fallo se reproduce siempre igual.
/// </summary>
public sealed class RobustnessTests
{
    private static readonly SourceKind[] Kinds = [SourceKind.Text, SourceKind.ColorSource, SourceKind.Image, SourceKind.AudioInput];

    private static IStudioCommand? RandomCommand(Random r, StudioProject p)
    {
        T Pick<T>(IReadOnlyList<T> list) => list[r.Next(list.Count)];
        var scenes = p.Scenes;
        var items = scenes.SelectMany(s => s.Items).ToList();
        var name = $"N{r.Next(1000)}";
        switch (r.Next(17))
        {
            case 0: return new CreateSceneCommand(name);
            case 1 when scenes.Count > 1: return new RemoveSceneCommand(Pick(scenes).Id);
            case 2 when scenes.Count > 0: return new RenameSceneCommand(Pick(scenes).Id, name);
            case 3 when scenes.Count > 0: return new DuplicateSceneCommand(Pick(scenes).Id);
            case 4 when scenes.Count > 0: return new MoveSceneCommand(Pick(scenes).Id, r.Next(-1, scenes.Count + 2));
            case 5 when scenes.Count > 0:
            {
                var transform = new Transform(r.Next(-50, 1900), r.Next(-50, 1000), 0.5 + r.NextDouble(), 0.5 + r.NextDouble(), r.Next(-180, 180))
                    .WithCrop(r.Next(5), 0, 0, 0);
                return new AddSourceCommand(Pick(scenes).Id, name, Kinds[r.Next(Kinds.Length)], "{}", transform);
            }
            case 6 when items.Count > 0: return new RemoveItemCommand(Pick(items).Id);
            case 7 when items.Count > 0:
                return new SetItemTransformCommand(Pick(items).Id, new Transform(r.Next(2000), r.Next(1100), r.NextDouble() * 3, r.NextDouble() * 3, r.Next(360)));
            case 8 when items.Count > 0: return new SetItemVisibleCommand(Pick(items).Id, r.Next(2) == 0);
            case 9 when items.Count > 0: return new MoveItemCommand(Pick(items).Id, r.Next(-1, 6));
            case 10 when p.Sources.Count > 0: return new RenameSourceCommand(Pick(p.Sources).Id, name);
            case 11 when p.Sources.Count > 0:
            {
                var s = Pick(p.Sources);
                return new AddFilterCommand(s.Id, s.IsAudioOnly ? "gain_filter" : "color_filter_v2", name);
            }
            case 12 when p.Sources.Any(s => s.Filters.Count > 0):
            {
                var s = Pick(p.Sources.Where(x => x.Filters.Count > 0).ToList());
                var f = Pick(s.Filters);
                return r.Next(3) switch
                {
                    0 => new RemoveFilterCommand(s.Id, f.Id),
                    1 => new SetFilterEnabledCommand(s.Id, f.Id, r.Next(2) == 0),
                    _ => new MoveFilterCommand(s.Id, f.Id, r.Next(0, 4)),
                };
            }
            case 13:
                return new AddGraphicCommand(GraphicTemplates.Create(Enum.GetValues<GraphicKind>()[r.Next(8)], name, p.Canvas));
            case 14 when p.Graphics.Count > 0:
            {
                var g = Pick(p.Graphics);
                return r.Next(3) switch
                {
                    0 => new RemoveGraphicCommand(g.Id),
                    1 => new MoveGraphicCommand(g.Id, r.Next(0, 5)),
                    _ => new UpdateGraphicCommand(g with { X = r.Next(-100, 1900), Name = name }),
                };
            }
            case 15 when scenes.Count > 0:
                return new SetSceneTransitionCommand(Pick(scenes).Id, r.Next(2) == 0 ? null : new TransitionSettings("fade_transition", r.Next(0, 3000)));
            case 16:
                return p.Destinations.Count > 0 && r.Next(2) == 0
                    ? new RemoveDestinationCommand(Pick(p.Destinations).Id, _ => false)
                    : new AddDestinationCommand(StreamDestination.Create(name, StreamPlatform.Custom, $"rtmp://{name.ToLowerInvariant()}.example/app"));
            default: return null;
        }
    }

    public static IEnumerable<object[]> Seeds => Enumerable.Range(0, 40).Select(i => new object[] { 1 + i * 7919 });

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task RandomCommandSequences_UndoAllRestoresTheStart_RedoAllRestoresTheEnd(int seed)
    {
        var r = new Random(seed);
        var engine = new FakeStreamingEngine();
        var project = new StudioProject(Guid.NewGuid(), "Fuzz");
        var executor = new CommandExecutor(new StudioContext(project, engine));
        await GraphicLayer.CreateOverlayAsync(engine, project, CancellationToken.None);
        Assert.True((await executor.ExecuteAsync(new CreateSceneCommand("Base"))).Success);
        executor.ClearHistory();

        var initial = ProjectSerializer.Serialize(project);
        var engineInitial = (engine.Scenes.Count, engine.Sources.Count, engine.Items.Count, engine.Filters.Count);
        var snapshots = new Stack<(string State, string Command)>();
        var applied = 0;

        for (var step = 0; step < 150; step++)
        {
            if (RandomCommand(r, project) is not { } command)
                continue;
            var before = ProjectSerializer.Serialize(project);
            var result = await executor.ExecuteAsync(command);
            if (!result.Success)
            {
                Assert.Equal(before, ProjectSerializer.Serialize(project)); // un comando rechazado no deja rastro
                continue;
            }
            snapshots.Push((before, command.Description));
            applied++;
        }
        Assert.True(applied > 50, $"Solo se aplicaron {applied} comandos: el generador no ejercita lo suficiente.");
        var final = ProjectSerializer.Serialize(project);

        // Deshacer paso a paso: cada estado intermedio coincide con el que había antes de ese comando.
        while (executor.CanUndo)
        {
            Assert.True((await executor.UndoAsync()).Success);
            var (expected, description) = snapshots.Pop();
            var actual = ProjectSerializer.Serialize(project);
            if (expected != actual)
                Assert.Fail($"Deshacer «{description}» no restauró el estado anterior. " + Diff(expected, actual));
        }
        Assert.Equal(initial, ProjectSerializer.Serialize(project));
        Assert.Equal(engineInitial, (engine.Scenes.Count, engine.Sources.Count, engine.Items.Count, engine.Filters.Count));

        while (executor.CanRedo)
            Assert.True((await executor.RedoAsync()).Success);
        Assert.Equal(final, ProjectSerializer.Serialize(project));

        // El motor refleja exactamente el dominio: mismos elementos de escena y filtros.
        Assert.Equal(project.Scenes.Sum(s => s.Items.Count), engine.Items.Count(i => project.Scenes.Any(s => s.Id == i.Value.SceneId)));
        Assert.Equal(project.Sources.Sum(s => s.Filters.Count), engine.Filters.Count(f => project.Sources.Any(s => s.Id == f.Value.SourceId)));
    }

    [Fact]
    public void CorruptProjectFiles_FailCleanly_NeverWithAnUnexpectedException()
    {
        var project = StudioSession_DefaultWithContent();
        var json = ProjectSerializer.Serialize(project);
        var r = new Random(2026);
        var loaded = 0;
        for (var i = 0; i < 2000; i++)
        {
            var chars = json.ToCharArray().ToList();
            switch (i % 4)
            {
                case 0: chars = chars.Take(r.Next(chars.Count)).ToList(); break; // archivo truncado
                case 1:
                    for (var k = 0; k < 1 + r.Next(5); k++)
                        chars[r.Next(chars.Count)] = "{}[]\",:0x-tn"[r.Next(12)];
                    break;
                case 2: chars.RemoveRange(r.Next(chars.Count - 10), r.Next(1, 10)); break;
                default:
                    // Cambio de tipo plausible: un número donde va un texto o al revés.
                    var text = new string(chars.ToArray());
                    text = r.Next(2) == 0 ? text.Replace("\"name\": \"", "\"name\": 1, \"x\": \"") : text.Replace("\"volume\": 1", "\"volume\": \"alto\"");
                    chars = text.ToList();
                    break;
            }
            try
            {
                var p = ProjectSerializer.Deserialize(new string(chars.ToArray()));
                Assert.NotNull(p.Name);
                loaded++;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException
                                           or FormatException or NotSupportedException)
            {
                // Esperado: el repositorio lo informa y recurre a la copia .bak.
            }
        }
        Assert.True(loaded > 0, "Ninguna mutación inocua se leyó: el lector es demasiado estricto.");
    }

    private static string Diff(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Min(e.Length, a.Length); i++)
            if (e[i] != a[i])
                return $"Línea {i}: esperado «{e[i].Trim()}», obtenido «{a[i].Trim()}». Contexto: {string.Join(" ", e.Skip(Math.Max(0, i - 6)).Take(7).Select(x => x.Trim()))}";
        return $"Longitudes {e.Length} / {a.Length}";
    }

    private static StudioProject StudioSession_DefaultWithContent()
    {
        var project = Application.Studio.StudioSession.CreateDefaultProject("Corrupto");
        var scene = project.Scenes[0];
        var src = new Source(Guid.NewGuid(), "Texto", SourceKind.Text, """{"text":"hola"}""");
        src.InsertFilter(new SourceFilter(Guid.NewGuid(), "Color", "color_filter_v2"), 0);
        project.AddSource(src);
        scene.AddItem(src.Id, new Transform(10, 20, 1, 1, 0));
        project.InsertGraphic(GraphicTemplates.Create(GraphicKind.Scoreboard, "Marcador", project.Canvas));
        project.InsertDestination(StreamDestination.Create("YouTube", StreamPlatform.YouTube, "rtmp://a.rtmp.youtube.com/live2"));
        return project;
    }
}
