using System.Diagnostics;

namespace LiveStream.Install;

/// <summary>
/// Desinstalación (LiveStream.Desktop.exe --uninstall [/quiet]). Quita el acceso, la entrada de «Aplicaciones
/// instaladas» y la carpeta del programa; conserva proyectos, ajustes y claves del usuario.
/// </summary>
internal static class Uninstaller
{
    public static int Run(bool quiet)
    {
        var directory = AppContext.BaseDirectory.TrimEnd('\\');
        if (!InstallLayout.IsInstalledDirectory(directory))
        {
            if (!quiet)
                InstallLayout.Error($"Esta copia de LiveStream ({directory}) no se instaló con el instalador; no se desinstala nada.");
            return 4;
        }
        if (!quiet && !InstallLayout.Ask(
                "¿Desinstalar LiveStream?\n\nTus proyectos, ajustes y claves guardadas se conservan en %APPDATA%\\LiveStream."))
            return 2;
        if (InstallLayout.RunningFrom(directory, Environment.ProcessId).Count > 0)
        {
            if (!quiet)
                InstallLayout.Error("LiveStream está abierto. Ciérralo y vuelve a desinstalar.");
            return 3;
        }

        InstallLayout.RemoveShortcut();
        InstallLayout.Unregister();
        // El ejecutable no puede borrar su propia carpeta: cmd la borra en cuanto este proceso termina.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{directory}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
        if (!quiet)
            InstallLayout.Info("LiveStream se ha desinstalado.");
        return 0;
    }
}
