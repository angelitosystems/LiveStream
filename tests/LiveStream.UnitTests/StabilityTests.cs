using System.Collections.Concurrent;
using LiveStream.Application.Graphics;
using LiveStream.Application.Studio;
using LiveStream.Extensions.Chat;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using LiveStream.Domain.ValueObjects;
using LiveStream.Infrastructure.Persistence;
using LiveStream.Infrastructure.Security;

namespace LiveStream.UnitTests;

/// <summary>Causas de cierres inesperados: el proyecto solo se modifica en el hilo que creó la sesión (la UI).</summary>
public sealed class StabilityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sp-stability-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Contexto que acumula lo publicado hasta que la prueba lo ejecuta, como haría el dispatcher.</summary>
    private sealed class QueuedContext : SynchronizationContext
    {
        public ConcurrentQueue<(SendOrPostCallback, object?)> Posted { get; } = new();

        public override void Post(SendOrPostCallback d, object? state) => Posted.Enqueue((d, state));

        public void Pump()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (Posted.TryDequeue(out var work))
                    work.Item1(work.Item2);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    private StudioSession CreateSession(QueuedContext ui)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var project = StudioSession.CreateDefaultProject("Stability", new CanvasSettings(1920, 1080, 60));
            return new StudioSession(new FakeStreamingEngine(), new JsonProjectRepository(_dir),
                new DpapiSecretStore(Path.Combine(_dir, "s.dat")), project, Path.Combine(_dir, "p.lsproj"));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task Chat_from_a_connector_thread_updates_the_overlay_graphic_only_on_the_owner_thread()
    {
        var ui = new QueuedContext();
        var session = CreateSession(ui);
        session.Extensions.Add(new ChatExtension());
        await session.Extensions.StartAsync();
        var overlay = GraphicTemplates.Create(GraphicKind.ChatOverlay, "Chat", session.Project.Canvas);
        session.Project.InsertGraphic(overlay);

        // Los conectores entregan mensajes en hilos del pool.
        var chat = session.Extensions.Get<UnifiedChatService>()!;
        await Task.Run(() => chat.AddMessage(new ChatMessage("1", StreamPlatform.Twitch, "Ana", "u1", "hola", DateTime.UtcNow)));

        Assert.NotEqual("Ana: hola", session.Project.GetGraphic(overlay.Id).Fields["line1"]);
        ui.Pump();
        Assert.Equal("Ana: hola", session.Project.GetGraphic(overlay.Id).Fields["line1"]);
        await session.Extensions.DisposeAsync();
    }

    [Fact]
    public void A_failing_owner_thread_action_is_logged_instead_of_thrown()
    {
        var ui = new QueuedContext();
        var session = CreateSession(ui);

        session.OnOwnerThread("prueba", () => throw new InvalidOperationException("boom"));
        ui.Pump();
    }
}
