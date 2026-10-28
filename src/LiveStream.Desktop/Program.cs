using Avalonia;
using LiveStream.Infrastructure.Diagnostics;
using LiveStream.Install;

namespace LiveStream.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Desinstalación lanzada desde «Aplicaciones instaladas»: no arranca la interfaz ni el motor.
        if (args.Contains("--uninstall"))
            return Uninstaller.Run(quiet: args.Contains("/quiet"));
        // Antes que Avalonia: así también quedan registrados los fallos al arrancar.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Fatal("app", $"Excepción no controlada (el proceso termina: {e.IsTerminating})", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("app", "Tarea con excepción no observada", e.Exception);
            e.SetObserved();
        };
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
