using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamPilot.Infrastructure.Updates;

namespace StreamPilot.UnitTests;

/// <summary>Fase 7: actualizaciones firmadas (ECDSA P-256) servidas desde un servidor HTTP local.</summary>
public sealed class UpdateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sp-upd-").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly HttpListener _server = new();
    private readonly Dictionary<string, byte[]> _files = [];
    private readonly string _base;

    public UpdateTests()
    {
        var port = 20000 + Random.Shared.Next(20000);
        _base = $"http://127.0.0.1:{port}/";
        _server.Prefixes.Add(_base);
        _server.Start();
        _ = Task.Run(async () =>
        {
            while (_server.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _server.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }
                var name = ctx.Request.Url!.AbsolutePath.Trim('/');
                if (_files.TryGetValue(name, out var body))
                {
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }
                ctx.Response.Close();
            }
        });
    }

    public void Dispose()
    {
        _server.Stop();
        _key.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private UpdateClient Client(ECDsa? key = null) => new(new HttpClient(), (key ?? _key).ExportSubjectPublicKeyInfoPem());

    private UpdateManifest Sign(string version, string url, byte[] installer, ECDsa? key = null)
    {
        var unsigned = new UpdateManifest(version, url, installer.Length, Convert.ToHexStringLower(SHA256.HashData(installer)), "notas", "");
        var signature = (key ?? _key).SignData(Encoding.UTF8.GetBytes(unsigned.SignedText), HashAlgorithmName.SHA256);
        return unsigned with { Signature = Convert.ToBase64String(signature) };
    }

    private void Publish(UpdateManifest manifest, byte[] installer, byte[]? served = null)
    {
        _files["update.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest);
        _files["setup.exe"] = served ?? installer;
    }

    private static readonly byte[] Installer = RandomNumberGenerator.GetBytes(300_000);

    [Fact]
    public async Task NewerSignedRelease_IsOffered_DownloadedAndVerified()
    {
        var manifest = Sign("0.8.0", _base + "setup.exe", Installer);
        Publish(manifest, Installer);
        var client = Client();

        var result = await client.CheckAsync(new Uri(_base + "update.json"), new Version(0, 7, 0), CancellationToken.None);
        var available = Assert.IsType<UpdateCheckResult.Available>(result);
        var progress = new List<double>();
        var path = await client.DownloadAsync(available.Manifest, _dir, new Progress<double>(progress.Add), CancellationToken.None);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(path));

        Assert.IsType<UpdateCheckResult.UpToDate>(await client.CheckAsync(new Uri(_base + "update.json"), new Version(0, 8, 0), CancellationToken.None));
        Assert.IsType<UpdateCheckResult.UpToDate>(await client.CheckAsync(new Uri(_base + "update.json"), new Version(1, 0, 0), CancellationToken.None));
    }

    [Fact]
    public async Task TamperedManifest_OrForeignKey_IsRejectedBeforeDownloading()
    {
        var manifest = Sign("0.8.0", _base + "setup.exe", Installer);
        Publish(manifest with { Version = "9.9.9" }, Installer); // alguien sube la versión sin poder firmar
        var rejected = Assert.IsType<UpdateCheckResult.Rejected>(await Client().CheckAsync(new Uri(_base + "update.json"), new Version(0, 7, 0), CancellationToken.None));
        Assert.Contains("firma", rejected.Reason);

        Publish(manifest with { Url = "https://otro.example/malware.exe" }, Installer);
        Assert.IsType<UpdateCheckResult.Rejected>(await Client().CheckAsync(new Uri(_base + "update.json"), new Version(0, 7, 0), CancellationToken.None));

        using var foreign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Publish(Sign("0.8.0", _base + "setup.exe", Installer, foreign), Installer);
        Assert.IsType<UpdateCheckResult.Rejected>(await Client().CheckAsync(new Uri(_base + "update.json"), new Version(0, 7, 0), CancellationToken.None));

        Publish(manifest with { Signature = "" }, Installer);
        Assert.IsType<UpdateCheckResult.Rejected>(await Client().CheckAsync(new Uri(_base + "update.json"), new Version(0, 7, 0), CancellationToken.None));
    }

    [Fact]
    public async Task AlteredOrOversizedInstaller_IsDeletedAndRejected()
    {
        var manifest = Sign("0.8.0", _base + "setup.exe", Installer);
        var altered = (byte[])Installer.Clone();
        altered[1000] ^= 0xFF;
        Publish(manifest, Installer, altered);
        var client = Client();
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(manifest, _dir, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir));

        Publish(manifest, Installer, [.. Installer, .. new byte[50_000]]);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(manifest, _dir, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task InsecureChannel_IsRejected()
    {
        var result = await Client().CheckAsync(new Uri("http://updates.example.com/update.json"), new Version(0, 7, 0), CancellationToken.None);
        Assert.Contains("HTTPS", Assert.IsType<UpdateCheckResult.Rejected>(result).Reason);
    }

    [Fact]
    public void ManifestSignedByTheReleaseScript_VerifiesInTheApp()
    {
        var pwsh = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d.Trim(), "pwsh.exe")).FirstOrDefault(File.Exists);
        Assert.True(pwsh is not null, "Los scripts de publicación requieren PowerShell 7 (pwsh) en el PATH.");

        var privateKey = Path.Combine(_dir, "private.pem");
        File.WriteAllText(privateKey, _key.ExportPkcs8PrivateKeyPem());
        var installer = Path.Combine(_dir, "setup.exe");
        File.WriteAllBytes(installer, Installer);
        var output = Path.Combine(_dir, "update.json");
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../scripts/sign-update.ps1"));
        using var p = Process.Start(new ProcessStartInfo(pwsh!, $"-NoProfile -File \"{script}\" -Installer \"{installer}\" -Version 0.8.0 " +
            $"-Url https://descargas.example.com/setup.exe -PrivateKey \"{privateKey}\" -Output \"{output}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        p.WaitForExit(60_000);
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(output))!;
        Assert.True(Client().Verify(manifest));
        Assert.False(Client().Verify(manifest with { Size = manifest.Size + 1 }));
    }
}
