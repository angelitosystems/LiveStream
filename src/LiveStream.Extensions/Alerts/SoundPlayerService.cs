using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace LiveStream.Extensions.Alerts;

/// <summary>
/// Reproductor de audio ultraligero y asíncrono para efectos de sonido y alertas en tiempo real.
/// Utiliza la API nativa de Windows (winmm.dll PlaySound) para reproducción instantánea con latencia cero
/// y soporte sin dependencias externas.
/// </summary>
public static class SoundPlayerService
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_PURGE = 0x0040;

    [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    /// <summary>Indica si el reproductor de sonidos de alertas está silenciado globalmente.</summary>
    public static bool IsMuted { get; set; }

    /// <summary>
    /// Reproduce un archivo de audio asíncronamente en segundo plano.
    /// Si el archivo no existe o ocurre un error, no interrumpe el flujo de la aplicación.
    /// </summary>
    public static void Play(string? filePath)
    {
        if (IsMuted || string.IsNullOrWhiteSpace(filePath))
            return;

        // Si es una ruta relativa o de stock, resolverla en la carpeta de sonidos
        string targetPath = filePath;
        if (!File.Exists(targetPath))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var stockPath = Path.Combine(appData, "LiveStream", "sounds", "stock", Path.GetFileName(filePath));
            if (File.Exists(stockPath))
            {
                targetPath = stockPath;
            }
            else
            {
                var generalPath = Path.Combine(appData, "LiveStream", "sounds", Path.GetFileName(filePath));
                if (File.Exists(generalPath))
                    targetPath = generalPath;
            }
        }

        if (!File.Exists(targetPath))
            return;

        Task.Run(() =>
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    PlaySound(targetPath, IntPtr.Zero, SND_ASYNC | SND_FILENAME);
                }
            }
            catch
            {
                // Manejo silencioso para no afectar la estabilidad del stream
            }
        });
    }

    /// <summary>
    /// Detiene cualquier sonido que se esté reproduciendo actualmente.
    /// </summary>
    public static void Stop()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                PlaySound(null, IntPtr.Zero, SND_PURGE);
            }
        }
        catch (Exception ex) { AppLog.Warn("alertas", $"Detener el sonido: {ex.Message}"); }
    }
}
