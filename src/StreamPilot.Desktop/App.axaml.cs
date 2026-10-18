using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using StreamPilot.Application.Studio;
using StreamPilot.Desktop.Services;
using StreamPilot.Desktop.ViewModels;
using StreamPilot.Desktop.Views;
using StreamPilot.Domain.Entities;
using StreamPilot.Infrastructure.Diagnostics;
using StreamPilot.Infrastructure.Persistence;
using StreamPilot.Infrastructure.Security;
using StreamPilot.Native.Engine;

namespace StreamPilot.Desktop;

public sealed class App : Avalonia.Application
{
    private MainViewModel? _vm;
    private LibObsEngine? _engine;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppLog.Info("app", $"StreamPilot iniciado ({Environment.OSVersion}, {Environment.ProcessorCount} hilos)");
            LibObsEngine.Log += (level, message) => AppLog.Write(level.ToUpperInvariant(), "libobs", message);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("app", "Excepción no controlada", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("app", "Tarea con excepción no observada", e.Exception);

            var repository = new JsonProjectRepository();
            var (project, path, warning) = LoadProject(repository, desktop.Args ?? []);
            _engine = new LibObsEngine();
            var session = new StudioSession(_engine, repository, new DpapiSecretStore(), project, path);
            _vm = new MainViewModel(session, _engine, new DialogService()) { StatusMessage = warning ?? $"Proyecto: {path}" };
            desktop.MainWindow = new MainWindow { DataContext = _vm };

            // Bloquea a propósito: el proyecto se guarda y libobs se apaga antes de que termine el proceso.
            // Se ejecuta en el pool de hilos: así ninguna continuación intenta volver al hilo de UI que espera.
            desktop.Exit += (_, _) =>
            {
                AppLog.Info("app", "Cerrando: guardando proyecto");
                var shutdown = Task.Run(async () =>
                {
                    await _vm.DisposeAsync();
                    AppLog.Info("app", "Apagando motor");
                    await _engine.DisposeAsync();
                });
                if (!shutdown.Wait(TimeSpan.FromSeconds(20)))
                    AppLog.Error("app", "El apagado superó 20 s; se fuerza la salida");
                AppLog.Info("app", "Cerrado");
                AppLog.Flush(TimeSpan.FromSeconds(2));
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>--project &lt;ruta&gt; o, si no, el último proyecto abierto; si no existe ninguno, uno nuevo por defecto.</summary>
    private static (StudioProject Project, string Path, string? Warning) LoadProject(JsonProjectRepository repository, string[] args)
    {
        var index = Array.IndexOf(args, "--project");
        var requested = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : repository.LastOpenedPath;
        if (requested is not null)
        {
            try
            {
                if (repository.LoadAsync(requested, CancellationToken.None).GetAwaiter().GetResult() is { } loaded)
                {
                    repository.LastOpenedPath = requested;
                    return (loaded, requested, null);
                }
            }
            catch (Exception ex)
            {
                var fresh = StudioSession.CreateDefaultProject();
                return (fresh, repository.DefaultPathFor(fresh), $"No se pudo abrir {requested}: {ex.Message}. Se creó un proyecto nuevo.");
            }
        }
        var project = StudioSession.CreateDefaultProject();
        return (project, repository.DefaultPathFor(project), null);
    }
}
