using Avalonia;
using LiveStream.Install;

namespace LiveStream.Setup;

// Instalador de LiveStream (por usuario, sin administrador).
//   LiveStream-Setup.exe [/quiet] [/dir <carpeta>] [/noshortcuts] [/waitpid <pid>] [/launch]
// Sin /quiet muestra el asistente; con /quiet instala sin interfaz.
// Códigos de salida: 0 instalado, 1 error, 2 cancelado, 3 LiveStream abierto.
internal static class Program
{
    /// <summary>Opciones de la línea de comandos; el asistente parte de ellas.</summary>
    public static SetupArguments Arguments { get; private set; } = null!;

    /// <summary>Código de salida que fija el asistente al cerrarse.</summary>
    public static int ExitCode { get; set; } = 2;

    [STAThread]
    public static int Main(string[] args)
    {
        Arguments = SetupArguments.Parse(args);
        if (Arguments.Quiet)
            return RunQuiet(Arguments);
        AppBuilder.Configure<SetupApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        return ExitCode;
    }

    private static int RunQuiet(SetupArguments a)
    {
        try
        {
            SetupEngine.Install(new InstallOptions(a.Directory, !a.NoShortcuts, DesktopShortcut: false, a.WaitPid));
            if (a.Launch)
                SetupEngine.Launch(a.Directory);
            return 0;
        }
        catch (SetupBlockedException ex)
        {
            SetupEngine.Log(ex.Message);
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            SetupEngine.Log($"Error: {ex}");
            return 1;
        }
    }
}

internal sealed record SetupArguments(bool Quiet, string Directory, bool NoShortcuts, int? WaitPid, bool Launch)
{
    /// <summary>Actualización lanzada desde la aplicación (espera a que se cierre y la vuelve a abrir).</summary>
    public bool IsUpdate => WaitPid is not null;

    public static SetupArguments Parse(IReadOnlyList<string> argv)
    {
        bool Flag(string name) => argv.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        string? Value(string name)
        {
            var i = argv.ToList().FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null;
        }
        return new SetupArguments(
            Flag("/quiet"),
            Path.GetFullPath(Value("/dir") ?? InstallLayout.RegisteredDirectory ?? InstallLayout.DefaultDirectory).TrimEnd('\\'),
            Flag("/noshortcuts"),
            int.TryParse(Value("/waitpid"), out var pid) ? pid : null,
            Flag("/launch"));
    }
}
