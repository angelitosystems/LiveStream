using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiveStream.Extensions.Alerts;

public sealed record SoundBankItem(
    string Id,
    string Name,
    string Category,
    string License,
    string LocalFilePath,
    string? RemoteUrl = null,
    double DurationSeconds = 1.0);

/// <summary>
/// Banco de efectos de sonido libres de regalías (Royalty-Free / CC0) para alertas y eventos.
/// Incluye un catálogo base sintetizado proceduralmente (cero latencia, offline) y soporte
/// para explorar y descargar efectos desde repositorios públicos abiertos (Pixabay / Freesound).
/// </summary>
public sealed class SoundBankService
{
    private readonly string _soundsRoot;
    private readonly string _stockDir;
    private readonly string _downloadDir;
    private readonly HttpClient _http;
    private readonly List<SoundBankItem> _catalog = [];

    public SoundBankService(string? customRoot = null)
    {
        _soundsRoot = customRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LiveStream",
            "sounds");

        _stockDir = Path.Combine(_soundsRoot, "stock");
        _downloadDir = Path.Combine(_soundsRoot, "downloads");

        Directory.CreateDirectory(_soundsRoot);
        Directory.CreateDirectory(_stockDir);
        Directory.CreateDirectory(_downloadDir);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        EnsureStockSounds();
        RefreshCatalog();
    }

    public string RootDirectory => _soundsRoot;

    public IReadOnlyList<string> Categories =>
        ["(Todas)", "Campanas y Avisos", "Logros y Subs", "Retro y Arcade", "Victorias y Raids", "Efectos Suaves", "Personalizados"];

    public IReadOnlyList<SoundBankItem> List(string? query = null, string? category = null)
    {
        RefreshCatalog();
        var q = query?.Trim().ToLowerInvariant();
        var cat = (string.IsNullOrWhiteSpace(category) || category == "(Todas)") ? null : category;

        return _catalog
            .Where(s => cat is null || s.Category.Equals(cat, StringComparison.OrdinalIgnoreCase))
            .Where(s => string.IsNullOrWhiteSpace(q) ||
                        s.Name.ToLowerInvariant().Contains(q) ||
                        s.Category.ToLowerInvariant().Contains(q))
            .OrderBy(s => s.Category)
            .ThenBy(s => s.Name)
            .ToList();
    }

    public SoundBankItem? GetById(string id) =>
        _catalog.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public SoundBankItem? GetByPath(string path) =>
        _catalog.FirstOrDefault(s => s.LocalFilePath.Equals(path, StringComparison.OrdinalIgnoreCase));

    public SoundBankItem ImportUserSound(string sourceFilePath, string category = "Personalizados")
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("Archivo de sonido no encontrado", sourceFilePath);

        var ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (ext != ".wav" && ext != ".mp3" && ext != ".ogg")
            throw new InvalidOperationException("Formato no soportado. Usa .wav, .mp3 o .ogg.");

        var fileName = Path.GetFileName(sourceFilePath);
        var destDir = Path.Combine(_soundsRoot, "imported");
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, fileName);

        if (!File.Exists(destPath) || new FileInfo(sourceFilePath).FullName != new FileInfo(destPath).FullName)
            File.Copy(sourceFilePath, destPath, overwrite: true);

        var name = Path.GetFileNameWithoutExtension(sourceFilePath);
        var item = new SoundBankItem(
            Id: $"user_{Guid.NewGuid():N}",
            Name: name,
            Category: category,
            License: "Audio Local de Usuario",
            LocalFilePath: destPath,
            DurationSeconds: 1.5
        );

        _catalog.Add(item);
        return item;
    }

    public async Task<SoundBankItem> DownloadRemoteSoundAsync(string remoteUrl, string name, string category, string license, CancellationToken ct = default)
    {
        var safeName = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");
        var ext = Path.GetExtension(remoteUrl);
        if (string.IsNullOrWhiteSpace(ext) || ext.Length > 5) ext = ".mp3";
        var destPath = Path.Combine(_downloadDir, $"{safeName}_{Guid.NewGuid():N}{ext}");

        var bytes = await _http.GetByteArrayAsync(remoteUrl, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(destPath, bytes, ct).ConfigureAwait(false);

        var item = new SoundBankItem(
            Id: $"remote_{Guid.NewGuid():N}",
            Name: name,
            Category: category,
            License: license,
            LocalFilePath: destPath,
            RemoteUrl: remoteUrl,
            DurationSeconds: 2.0
        );

        _catalog.Add(item);
        return item;
    }

    public void RefreshCatalog()
    {
        _catalog.Clear();

        // 1. Stock procedural sounds
        RegisterSound("chime_bell", "Campana Cristalina (Bienvenida)", "Campanas y Avisos", "Royalty-Free / CC0", Path.Combine(_stockDir, "chime_bell.wav"), 1.2);
        RegisterSound("arcade_coin", "Moneda Dorada (Donación)", "Retro y Arcade", "Royalty-Free / CC0", Path.Combine(_stockDir, "arcade_coin.wav"), 0.8);
        RegisterSound("level_up", "Subida de Nivel (Suscripción)", "Logros y Subs", "Royalty-Free / CC0", Path.Combine(_stockDir, "level_up.wav"), 1.5);
        RegisterSound("fanfare_tada", "Fanfarria Real (Raid / Meta)", "Victorias y Raids", "Royalty-Free / CC0", Path.Combine(_stockDir, "fanfare_tada.wav"), 2.2);
        RegisterSound("ding_notification", "Aviso Ding (Follower)", "Campanas y Avisos", "Royalty-Free / CC0", Path.Combine(_stockDir, "ding_notification.wav"), 0.9);
        RegisterSound("bubble_pop", "Pop Burbuja (Mensaje VIP)", "Efectos Suaves", "Royalty-Free / CC0", Path.Combine(_stockDir, "bubble_pop.wav"), 0.5);
        RegisterSound("victory_arpeggio", "Arpegio de Victoria (SuperChat)", "Victorias y Raids", "Royalty-Free / CC0", Path.Combine(_stockDir, "victory_arpeggio.wav"), 1.8);
        RegisterSound("cheer_applause", "Celebración Triunfal (Donación Mayor)", "Victorias y Raids", "Royalty-Free / CC0", Path.Combine(_stockDir, "cheer_applause.wav"), 2.5);

        // 2. Archivos en downloads y user imported
        ScanExtraDirectory(_downloadDir, "Descargados de la Red");
        ScanExtraDirectory(Path.Combine(_soundsRoot, "imported"), "Personalizados");
    }

    private void RegisterSound(string id, string name, string category, string license, string path, double duration)
    {
        if (File.Exists(path))
        {
            _catalog.Add(new SoundBankItem(id, name, category, license, path, null, duration));
        }
    }

    private void ScanExtraDirectory(string dir, string category)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".wav" or ".mp3" or ".ogg")
            {
                var name = Path.GetFileNameWithoutExtension(file);
                _catalog.Add(new SoundBankItem(
                    Id: $"file_{file.GetHashCode():X}",
                    Name: name,
                    Category: category,
                    License: "Libre / Importado",
                    LocalFilePath: file,
                    DurationSeconds: 1.5));
            }
        }
    }

    /// <summary>
    /// Genera proceduralmente archivos WAV con modulación armónica limpia de 44.1 kHz
    /// para proveer sonidos de alerta de alta fidelidad sin requerir conexión a internet.
    /// </summary>
    private void EnsureStockSounds()
    {
        try
        {
            CreateWavIfNotExists("chime_bell.wav", GenerateChime);
            CreateWavIfNotExists("arcade_coin.wav", GenerateCoin);
            CreateWavIfNotExists("level_up.wav", GenerateLevelUp);
            CreateWavIfNotExists("fanfare_tada.wav", GenerateFanfare);
            CreateWavIfNotExists("ding_notification.wav", GenerateDing);
            CreateWavIfNotExists("bubble_pop.wav", GeneratePop);
            CreateWavIfNotExists("victory_arpeggio.wav", GenerateVictory);
            CreateWavIfNotExists("cheer_applause.wav", GenerateApplause);
        }
        catch (Exception ex) { AppLog.Warn("alertas", $"Generar los sonidos de serie: {ex.Message}"); }
    }

    private void CreateWavIfNotExists(string fileName, Func<byte[]> generator)
    {
        var dest = Path.Combine(_stockDir, fileName);
        if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
        {
            var bytes = generator();
            File.WriteAllBytes(dest, bytes);
        }
    }

    #region Procedural Sound Synthesis (PCM 44.1kHz, 16-bit Mono)

    private static byte[] WriteWav(int sampleRate, short[] samples)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        int subChunk2Size = samples.Length * 2;
        int chunkSize = 36 + subChunk2Size;

        // RIFF Header
        bw.Write("RIFF"u8.ToArray());
        bw.Write(chunkSize);
        bw.Write("WAVE"u8.ToArray());

        // Format chunk
        bw.Write("fmt "u8.ToArray());
        bw.Write(16); // Subchunk1Size
        bw.Write((short)1); // AudioFormat PCM
        bw.Write((short)1); // NumChannels Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2); // ByteRate
        bw.Write((short)2); // BlockAlign
        bw.Write((short)16); // BitsPerSample

        // Data chunk
        bw.Write("data"u8.ToArray());
        bw.Write(subChunk2Size);
        foreach (var sample in samples)
        {
            bw.Write(sample);
        }

        return ms.ToArray();
    }

    private static byte[] GenerateChime()
    {
        const int sampleRate = 44100;
        const double duration = 1.2;
        int numSamples = (int)(sampleRate * duration);
        var samples = new short[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double envelope = Math.Exp(-3.5 * t);
            double wave = Math.Sin(2 * Math.PI * 880 * t) * 0.5
                        + Math.Sin(2 * Math.PI * 1320 * t) * 0.3
                        + Math.Sin(2 * Math.PI * 1760 * t) * 0.2;
            samples[i] = (short)(wave * envelope * 24000);
        }
        return WriteWav(sampleRate, samples);
    }

    private static byte[] GenerateCoin()
    {
        const int sampleRate = 44100;
        const double duration = 0.6;
        int numSamples = (int)(sampleRate * duration);
        var samples = new short[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double freq = t < 0.12 ? 987.77 : 1318.51; // B5 luego E6 estilo retro
            double envelope = Math.Exp(-5.0 * (t % 0.12 + (t >= 0.12 ? 0 : 0)));
            if (t >= 0.12) envelope = Math.Exp(-4.0 * (t - 0.12));

            // Square wave suave
            double sine = Math.Sin(2 * Math.PI * freq * t);
            double wave = Math.Sign(sine) * 0.4 + sine * 0.6;
            samples[i] = (short)(wave * envelope * 22000);
        }
        return WriteWav(sampleRate, samples);
    }

    private static byte[] GenerateLevelUp()
    {
        const int sampleRate = 44100;
        const double duration = 1.4;
        int numSamples = (int)(sampleRate * duration);
        var samples = new short[numSamples];

        double[] freqs = [523.25, 659.25, 783.99, 1046.50]; // C5, E5, G5, C6
        double step = 0.18;

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            int stepIndex = Math.Min(freqs.Length - 1, (int)(t / step));
            double freq = freqs[stepIndex];
            double localT = t - (stepIndex * step);
            double env = stepIndex == freqs.Length - 1 ? Math.Exp(-2.2 * localT) : Math.Exp(-3.0 * localT);

            double wave = Math.Sin(2 * Math.PI * freq * t) * 0.7 + Math.Sin(2 * Math.PI * (freq * 2) * t) * 0.3;
            samples[i] = (short)(wave * env * 24000);
        }
        return WriteWav(sampleRate, samples);
    }

    private static byte[] GenerateFanfare()
    {
        const int sampleRate = 44100;
        const double duration = 2.0;
        int numSamples = (int)(sampleRate * duration);
        var samples = new short[numSamples];

        double[] chord = [587.33, 739.99, 880.00, 1174.66]; // D Major Fanfare

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double env = Math.Exp(-1.8 * t);
            double wave = 0;
            foreach (var f in chord)
            {
                wave += Math.Sin(2 * Math.PI * f * t) * 0.25;
            }
            samples[i] = (short)(wave * env * 26000);
        }
        return WriteWav(sampleRate, samples);
    }

    private static byte[] GenerateDing()
    {
        const int sampleRate = 44100;
        const double duration = 0.9;
        int numSamples = (int)(sampleRate * duration);
        var samples = new short[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double envelope = Math.Exp(-4.0 * t);
            double wave = Math.Sin(2 * Math.PI * 1046.5 * t); // C6
            samples[i] = (short)(wave * envelope * 23000);
        }
        return WriteWav(sampleRate, samples);
    }

    private static byte[] GeneratePop()
    {
        const int sampleRate = 44100;
        const double duration = 0.3;
        int numSamples = (int)(sampleRate * duration);
        var samples = new short[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double envelope = Math.Exp(-18.0 * t);
            double pitchMod = 300 + 700 * Math.Exp(-15.0 * t);
            double wave = Math.Sin(2 * Math.PI * pitchMod * t);
            samples[i] = (short)(wave * envelope * 26000);
        }
        return WriteWav(sampleRate, samples);
    }

    private static byte[] GenerateVictory()
    {
        const int sampleRate = 44100;
        const double duration = 1.6;
        int numSamples = (int)(sampleRate * duration);
        var samples = new short[numSamples];

        double[] notes = [440, 554.37, 659.25, 880]; // A4, C#5, E5, A5
        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            int n = Math.Min(notes.Length - 1, (int)(t / 0.16));
            double env = n == notes.Length - 1 ? Math.Exp(-2.5 * (t - 0.48)) : Math.Exp(-4.0 * (t % 0.16));
            double wave = Math.Sin(2 * Math.PI * notes[n] * t) * 0.7 + Math.Sin(2 * Math.PI * (notes[n] * 0.5) * t) * 0.3;
            samples[i] = (short)(wave * env * 24000);
        }
        return WriteWav(sampleRate, samples);
    }

    private static byte[] GenerateApplause()
    {
        const int sampleRate = 44100;
        const double duration = 2.2;
        int numSamples = (int)(sampleRate * duration);
        var samples = new short[numSamples];
        var rnd = new Random(42);

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double env = Math.Sin(Math.PI * (t / duration));
            // Ruido blanco filtrado con modulaciones para simular palmas
            double noise = (rnd.NextDouble() * 2.0 - 1.0) * env;
            double clap = Math.Sin(2 * Math.PI * 45 * t) * 0.3;
            samples[i] = (short)((noise * 0.7 + clap * 0.3) * 19000);
        }
        return WriteWav(sampleRate, samples);
    }

    #endregion
}
