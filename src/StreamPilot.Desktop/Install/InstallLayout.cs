using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace StreamPilot.Install;

/// <summary>
/// Instalación por usuario (sin permisos de administrador). Lo comparten el instalador y la desinstalación de la
/// aplicación. Los datos del usuario (%APPDATA%\StreamPilot: proyectos, ajustes, claves cifradas) nunca se tocan.
/// </summary>
internal static class InstallLayout
{
    public const string AppName = "StreamPilot";
    public const string Publisher = "StreamPilot";
    public const string ExeName = "StreamPilot.Desktop.exe";
    /// <summary>Marca que el instalador deja en la carpeta: solo una carpeta marcada y registrada se puede borrar.</summary>
    public const string MarkerFile = "streampilot.install";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\StreamPilot";

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{AppName}.lnk");

    public static string? RegisteredDirectory
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") as string;
        }
    }

    public static void Register(string directory, string version, long sizeBytes)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        var exe = Path.Combine(directory, ExeName);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", directory);
        key.SetValue("DisplayIcon", exe);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall /quiet");
        key.SetValue("EstimatedSize", (int)(sizeBytes / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    public static void Unregister() => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

    /// <summary>Acceso del menú Inicio mediante el objeto COM de Windows Script Host.</summary>
    public static void CreateShortcut(string directory)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell no disponible.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = Path.Combine(directory, ExeName);
            link.WorkingDirectory = directory;
            link.Description = "Producción y transmisión en directo";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    public static void RemoveShortcut()
    {
        if (File.Exists(ShortcutPath))
            File.Delete(ShortcutPath);
    }

    /// <summary>¿Es una carpeta instalada por el instalador (marcada y registrada)? Protege carpetas de desarrollo.</summary>
    public static bool IsInstalledDirectory(string directory) =>
        File.Exists(Path.Combine(directory, MarkerFile)) &&
        RegisteredDirectory is { } registered &&
        string.Equals(Path.GetFullPath(registered).TrimEnd('\\'), Path.GetFullPath(directory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Procesos de StreamPilot que se ejecutan desde la carpeta (impiden reemplazarla o borrarla).</summary>
    public static IReadOnlyList<Process> RunningFrom(string directory, int? exceptPid = null)
    {
        var full = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            try
            {
                if (p.Id != exceptPid && p.MainModule?.FileName is { } path && path.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                    list.Add(p);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Proceso de otro usuario: no es nuestro.
            }
        }
        return list;
    }

    public static long DirectorySize(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    // ---- Diálogos nativos (el instalador no carga ningún framework de interfaz) ----------------------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    private const uint MbOk = 0x0, MbYesNo = 0x4, MbIconInformation = 0x40, MbIconQuestion = 0x20, MbIconError = 0x10;

    public static bool Ask(string text) => MessageBoxW(IntPtr.Zero, text, AppName, MbYesNo | MbIconQuestion) == 6;

    public static void Info(string text) => MessageBoxW(IntPtr.Zero, text, AppName, MbOk | MbIconInformation);

    public static void Error(string text) => MessageBoxW(IntPtr.Zero, text, AppName, MbOk | MbIconError);
}
