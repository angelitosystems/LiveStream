using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LiveStream.Application.Studio;
using LiveStream.Desktop.Services;
using LiveStream.Desktop.ViewModels;
using LiveStream.Desktop.Views;
using LiveStream.Domain.Entities;
using LiveStream.Extensions;
using LiveStream.Infrastructure.Diagnostics;
using LiveStream.Infrastructure.Persistence;
using LiveStream.Infrastructure.Security;
using LiveStream.Native.Engine;

namespace LiveStream.Desktop;

public sealed class App : Avalonia.Application
{
    private MainViewModel? _vm;
    private LibObsEngine? _engine;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppLog.Info("app", $"LiveStream iniciado ({Environment.OSVersion}, {Environment.ProcessorCount} hilos)");
            LiveStream.Application.AppLog.InfoAction = AppLog.Info;
            LiveStream.Application.AppLog.WarnAction = AppLog.Warn;
            LiveStream.Application.AppLog.ErrorAction = AppLog.Error;
            LibObsEngine.Log += (level, message) => AppLog.Write(level.ToUpperInvariant(), "libobs", message);
            // Los async void de la UI relanzan sus errores en el dispatcher: se registran con su origen y la app sigue.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                AppLog.Error("ui", $"Error no controlado en el hilo de la interfaz (en {Origin(e.Exception)})", e.Exception);
                e.Handled = true;
                if (_vm is not null)
                    _vm.StatusMessage = $"Error inesperado: {e.Exception.Message} (detalles en el registro)";
            };

            try
            {
                if (LegacyDataMigration.Run() is { } migrated)
                    AppLog.Info("app", migrated);
            }
            catch (IOException ex)
            {
                AppLog.Error("app", "No se pudieron trasladar los datos del nombre anterior", ex);
            }
            var repository = new JsonProjectRepository();
            var (project, path, warning) = LoadProject(repository, desktop.Args ?? []);
            _engine = new LibObsEngine();
            var secrets = new DpapiSecretStore();
            var session = new StudioSession(_engine, repository, secrets, project, path, extensionState: new JsonExtensionStateStore());
            // Composición: el núcleo no conoce las funcionalidades; se registran aquí y la UI resuelve sus servicios.
            BuiltInExtensions.AddTo(session.Extensions);
            session.Extensions.Register();
            _vm = new MainViewModel(session, _engine, new DialogService(), secrets) { StatusMessage = warning ?? $"Proyecto: {path}" };
            desktop.MainWindow = new MainWindow { DataContext = _vm };
            _ = StartExtensionsAsync(session);

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

    /// <summary>Cada extensión que falla queda aislada en el host; esto solo registra un fallo del propio host.</summary>
    private static async Task StartExtensionsAsync(StudioSession session)
    {
        try
        {
            await session.Extensions.StartAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("extensiones", "Arrancar las extensiones", ex);
        }
    }

    private static string Origin(Exception ex) =>
        ex.TargetSite is { } site ? $"{site.DeclaringType?.FullName}.{site.Name}" : "origen desconocido";

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
