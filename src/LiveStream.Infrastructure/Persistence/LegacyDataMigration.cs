namespace LiveStream.Infrastructure.Persistence;

/// <summary>
/// El producto se llamó «StreamPilot» antes de llamarse LiveStream. En el primer arranque se mueve
/// %APPDATA%\StreamPilot (proyectos, ajustes del equipo, plantillas y claves cifradas) a %APPDATA%\LiveStream, y se
/// corrige la ruta del último proyecto abierto. Si ya existe la carpeta nueva no se toca nada.
/// </summary>
public static class LegacyDataMigration
{
    public const string LegacyName = "StreamPilot";

    /// <summary>Devuelve un mensaje si migró algo; null si no había nada que migrar.</summary>
    public static string? Run(string? appDataRoot = null)
    {
        var appData = appDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var legacy = Path.Combine(appData, LegacyName);
        var current = Path.Combine(appData, "LiveStream");
        if (!Directory.Exists(legacy) || Directory.Exists(current))
            return null;

        Directory.Move(legacy, current);
        var lastOpened = Path.Combine(current, "last-project.txt");
        if (File.Exists(lastOpened))
        {
            var path = File.ReadAllText(lastOpened).Trim();
            if (path.StartsWith(legacy + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(lastOpened, current + path[legacy.Length..]);
        }
        return $"Datos de {LegacyName} trasladados a {current}.";
    }
}
