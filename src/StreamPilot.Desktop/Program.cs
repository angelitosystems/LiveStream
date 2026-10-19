using Avalonia;
using StreamPilot.Install;

namespace StreamPilot.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Desinstalación lanzada desde «Aplicaciones instaladas»: no arranca la interfaz ni el motor.
        if (args.Contains("--uninstall"))
            return Uninstaller.Run(quiet: args.Contains("/quiet"));
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
