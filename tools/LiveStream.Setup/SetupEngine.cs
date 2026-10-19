using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using LiveStream.Install;

namespace LiveStream.Setup;

internal sealed record InstallOptions(string Directory, bool StartMenuShortcut, bool DesktopShortcut, int? WaitPid);

internal sealed record InstallProgress(double Fraction, string Status);

/// <summary>Instalación que el usuario puede resolver (p. ej. LiveStream abierto). Lleva el código de salida.</summary>
internal sealed class SetupBlockedException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Lógica de instalación, compartida por el asistente y el modo /quiet. Extrae el paquete al lado de la carpeta
/// destino y la intercambia: si algo falla, la instalación anterior queda intacta.
/// </summary>
internal static class SetupEngine
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string LogPath { get; } = Path.Combine(Path.GetTempPath(), "LiveStream-Setup.log");

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo("payload.zip") is not null;

    public static void Log(string message) =>
        File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");

    /// <summary>Espacio que ocupará la aplicación instalada (tamaño descomprimido del paquete).</summary>
    public static long InstalledSize()
    {
        using var payload = OpenPayload();
        if (payload is null)
            return 0;
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        return zip.Entries.Sum(e => e.Length);
    }

    public static void Install(InstallOptions options, IProgress<InstallProgress>? progress = null)
    {
        var directory = Path.GetFullPath(options.Directory).TrimEnd('\\');
        void Report(double fraction, string status) => progress?.Report(new InstallProgress(fraction, status));

        Log($"Instalador {Version}: destino {directory}");
        using var payload = OpenPayload()
            ?? throw new SetupBlockedException("Este instalador no contiene la aplicación (se compiló sin paquete). Usa el que genera scripts/package.ps1.", 1);

        // Actualización lanzada desde la propia aplicación: se espera a que se cierre.
        if (options.WaitPid is { } pid)
        {
            Report(0, "Esperando a que se cierre LiveStream…");
            try
            {
                using var running = Process.GetProcessById(pid);
                running.WaitForExit(60_000);
            }
            catch (ArgumentException)
            {
                // Ya había terminado.
            }
        }

        if (InstallLayout.RunningFrom(directory).Count > 0)
            throw new SetupBlockedException("LiveStream está abierto. Ciérralo y vuelve a intentarlo.", 3);

        var staging = directory + ".new";
        var previous = directory + ".old";
        Report(0, "Preparando la carpeta…");
        foreach (var d in new[] { staging, previous })
            if (Directory.Exists(d))
                Directory.Delete(d, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);

        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
            Extract(zip, staging, (done, name) => Report(0.9 * done, $"Copiando {name}"));
        if (!File.Exists(Path.Combine(staging, InstallLayout.ExeName)))
            throw new InvalidDataException("El paquete no contiene LiveStream.Desktop.exe.");
        File.WriteAllText(Path.Combine(staging, InstallLayout.MarkerFile), $"{Version}\n");

        Report(0.92, "Activando la nueva versión…");
        var hadPrevious = Directory.Exists(directory);
        if (hadPrevious)
            Directory.Move(directory, previous);
        try
        {
            Directory.Move(staging, directory);
        }
        catch
        {
            if (hadPrevious)
                Directory.Move(previous, directory); // vuelta atrás
            throw;
        }
        if (hadPrevious)
        {
            try
            {
                Directory.Delete(previous, recursive: true);
            }
            catch (IOException ex)
            {
                Log($"No se pudo borrar la versión anterior ({previous}): {ex.Message}");
            }
        }

        Report(0.96, "Registrando en Windows…");
        InstallLayout.Register(directory, Version, InstallLayout.DirectorySize(directory));
        if (options.StartMenuShortcut)
            InstallLayout.CreateShortcut(directory);
        if (options.DesktopShortcut)
            InstallLayout.CreateShortcut(directory, InstallLayout.DesktopShortcutPath);
        Report(1, "Instalación completada.");
        Log("Instalación completada.");
    }

    public static void Launch(string directory)
    {
        directory = Path.GetFullPath(directory).TrimEnd('\\');
        Process.Start(new ProcessStartInfo(Path.Combine(directory, InstallLayout.ExeName)) { UseShellExecute = true, WorkingDirectory = directory });
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static Stream? OpenPayload() => Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");

    /// <summary>Extrae entrada a entrada para informar del avance; rechaza rutas que salgan de la carpeta.</summary>
    private static void Extract(ZipArchive zip, string destination, Action<double, string> onProgress)
    {
        var root = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
        var total = Math.Max(1, zip.Entries.Sum(e => e.Length));
        long done = 0;
        var lastReport = Stopwatch.StartNew();
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Ruta no válida en el paquete: {entry.FullName}");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
            done += entry.Length;
            if (lastReport.ElapsedMilliseconds >= 50)
            {
                onProgress((double)done / total, entry.Name);
                lastReport.Restart();
            }
        }
        onProgress(1, "");
    }
}
