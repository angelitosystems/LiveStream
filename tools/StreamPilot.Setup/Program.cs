using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using StreamPilot.Install;

// Instalador de StreamPilot (por usuario, sin administrador).
//   StreamPilot-Setup.exe [/quiet] [/dir <carpeta>] [/noshortcuts] [/waitpid <pid>] [/launch]
// Códigos de salida: 0 instalado, 1 error, 2 cancelado, 3 StreamPilot abierto.

var argv = Environment.GetCommandLineArgs().Skip(1).ToList();
bool Flag(string name) => argv.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
string? Value(string name)
{
    var i = argv.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null;
}

var quiet = Flag("/quiet");
var directory = Path.GetFullPath(Value("/dir") ?? InstallLayout.DefaultDirectory).TrimEnd('\\');
var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
var log = Path.Combine(Path.GetTempPath(), "StreamPilot-Setup.log");
void Log(string message) => File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");

try
{
    Log($"Instalador {version}: destino {directory}");
    using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
    if (payload is null)
        return Fail("Este instalador no contiene la aplicación (se compiló sin paquete). Usa el que genera scripts/package.ps1.", 1);

    // Actualización lanzada desde la propia aplicación: se espera a que se cierre.
    if (Value("/waitpid") is { } pidText && int.TryParse(pidText, out var pid))
    {
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

    if (!quiet && !InstallLayout.Ask($"¿Instalar StreamPilot {version} para este usuario?\n\nCarpeta: {directory}\n\n" +
                                     "No requiere permisos de administrador. Tus proyectos y ajustes se conservan."))
        return 2;

    if (InstallLayout.RunningFrom(directory).Count > 0)
        return Fail("StreamPilot está abierto. Ciérralo y vuelve a ejecutar el instalador.", 3);

    // Se extrae al lado y se intercambia: si algo falla, la instalación anterior queda intacta.
    var staging = directory + ".new";
    var previous = directory + ".old";
    foreach (var d in new[] { staging, previous })
        if (Directory.Exists(d))
            Directory.Delete(d, recursive: true);
    Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
    using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        zip.ExtractToDirectory(staging);
    if (!File.Exists(Path.Combine(staging, InstallLayout.ExeName)))
        throw new InvalidDataException("El paquete no contiene StreamPilot.Desktop.exe.");
    File.WriteAllText(Path.Combine(staging, InstallLayout.MarkerFile), $"{version}\n");

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

    InstallLayout.Register(directory, version, InstallLayout.DirectorySize(directory));
    if (!Flag("/noshortcuts"))
        InstallLayout.CreateShortcut(directory);
    Log("Instalación completada.");

    var launch = Flag("/launch") || (!quiet && InstallLayout.Ask($"StreamPilot {version} se ha instalado.\n\n¿Abrirlo ahora?"));
    if (launch)
        Process.Start(new ProcessStartInfo(Path.Combine(directory, InstallLayout.ExeName)) { UseShellExecute = true, WorkingDirectory = directory });
    return 0;
}
catch (Exception ex)
{
    Log($"Error: {ex}");
    return Fail($"No se pudo instalar StreamPilot: {ex.Message}\n\nDetalle en {log}", 1);
}

int Fail(string message, int code)
{
    Log(message);
    if (!quiet)
        InstallLayout.Error(message);
    return code;
}
