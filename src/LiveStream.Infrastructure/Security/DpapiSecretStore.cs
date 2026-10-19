using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveStream.Application.Abstractions;

namespace LiveStream.Infrastructure.Security;

/// <summary>
/// Secretos cifrados con DPAPI (ámbito usuario actual). Solo el mismo usuario de Windows en la misma máquina
/// puede descifrarlos. Los valores nunca se registran en logs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = "LiveStream.Secrets.v1"u8.ToArray();

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DpapiSecretStore(string? path = null) =>
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveStream", "secrets.dat");

    public async Task<string?> GetAsync(string name, CancellationToken ct) =>
        (await ReadAllAsync(ct).ConfigureAwait(false)).TryGetValue(name, out var value) ? value : null;

    public Task SetAsync(string name, string value, CancellationToken ct) =>
        MutateAsync(all => all[name] = value, ct);

    public Task DeleteAsync(string name, CancellationToken ct) =>
        MutateAsync(all => all.Remove(name), ct);

    private async Task MutateAsync(Action<Dictionary<string, string>> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadUnlockedAsync(ct).ConfigureAwait(false);
            change(all);
            var plain = JsonSerializer.SerializeToUtf8Bytes(all);
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            CryptographicOperations.ZeroMemory(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await File.WriteAllBytesAsync(temp, cipher, ct).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
            return [];
        var cipher = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
        var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain)) ?? [];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
