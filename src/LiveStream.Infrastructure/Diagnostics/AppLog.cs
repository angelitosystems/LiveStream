using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace LiveStream.Infrastructure.Diagnostics;

/// <summary>
/// Registro de diagnóstico en archivo (una línea por evento: hora, nivel, categoría, mensaje), rotado por día y
/// limitado a 14 días. Se escribe en segundo plano para no bloquear la UI ni los hilos de libobs. Las URLs de
/// ingesta se recortan antes de escribir para no filtrar claves de transmisión que vengan incrustadas en la ruta.
/// </summary>
public static partial class AppLog
{
    private static readonly BlockingCollection<string> Queue = new(10_000);
    private static readonly Thread Writer;
    private static int _pending;

    public static string Directory { get; } =
        System.IO.Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveStream", "logs")).FullName;

    static AppLog()
    {
        Writer = new Thread(WriteLoop) { IsBackground = true, Name = "LiveStream.AppLog" };
        Writer.Start();
        Prune();
    }

    public static string CurrentFile => Path.Combine(Directory, $"livestream-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string category, string message) => Write("INFO", category, message);
    public static void Warn(string category, string message) => Write("WARN", category, message);
    public static void Error(string category, string message, Exception? ex = null) =>
        Write("ERROR", category, ex is null ? message : $"{message} | {ex}");

    /// <summary>Error previo a la muerte del proceso: espera a que llegue al disco, porque al volver el runtime lo termina.</summary>
    public static void Fatal(string category, string message, Exception? ex)
    {
        Error(category, message, ex);
        Flush(TimeSpan.FromSeconds(2));
    }

    public static void Write(string level, string category, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} [{category}] {Redact(message).ReplaceLineEndings(" ⏎ ")}";
        Interlocked.Increment(ref _pending);
        if (!Queue.TryAdd(line))
            Interlocked.Decrement(ref _pending);
    }

    /// <summary>Espera a que se escriba lo pendiente (al cerrar).</summary>
    public static void Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        // _pending, no Queue.Count: la última línea ya salió de la cola pero puede no estar escrita aún.
        while (Volatile.Read(ref _pending) > 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
    }

    /// <summary>rtmp://servidor/app/CLAVE → rtmp://servidor/app/***</summary>
    public static string Redact(string message) => IngestUrl().Replace(message, m => $"{m.Groups[1].Value}/***");

    [GeneratedRegex(@"(rtmps?://[^\s/]+/[^\s/]+)/[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex IngestUrl();

    private static void WriteLoop()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(CurrentFile, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // El registro nunca debe tumbar la aplicación.
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    private static void Prune()
    {
        try
        {
            foreach (var file in new DirectoryInfo(Directory).GetFiles("livestream-*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-14)))
                file.Delete();
        }
        catch (IOException)
        {
        }
    }
}
