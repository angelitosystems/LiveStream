using System.Text.Json.Nodes;
using LiveStream.Application.Assistant;
using LiveStream.PluginSdk;

namespace LiveStream.Infrastructure.Plugins;

/// <summary>Ciclo de vida de los plugins: arranca los activos, instala/actualiza/desinstala y reparte los eventos.</summary>
public sealed class PluginManager(PluginStore store, Func<InstalledPlugin, AssistantToolbox> toolboxFor) : IAsyncDisposable
{
    private readonly Dictionary<string, PluginHost> _hosts = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PluginStore Store { get; } = store;

    /// <summary>Cambió el estado de algún plugin (para refrescar la interfaz).</summary>
    public event EventHandler? Changed;

    public PluginHost? Host(string id)
    {
        lock (_hosts)
            return _hosts.GetValueOrDefault(id);
    }

    public async Task StartEnabledAsync(CancellationToken ct)
    {
        foreach (var plugin in Store.List().Where(p => p.Record.Enabled))
            await StartAsync(plugin, ct).ConfigureAwait(false);
    }

    /// <summary>Instala o actualiza un paquete ya verificado y aceptado por el usuario.</summary>
    public Task<InstalledPlugin> InstallAsync(string packagePath, PackageSignature? signature, string source, CancellationToken ct) =>
        ExclusiveAsync(async () =>
        {
            var manifest = Store.Verify(packagePath, signature);
            await StopAsync(manifest.Id).ConfigureAwait(false);
            var installed = Store.Install(packagePath, signature, source, enable: true);
            await StartAsync(installed, ct).ConfigureAwait(false);
            return installed;
        });

    public Task UninstallAsync(string id) => ExclusiveAsync(async () =>
    {
        await StopAsync(id).ConfigureAwait(false);
        Store.Uninstall(id);
        return true;
    });

    public Task SetEnabledAsync(string id, bool enabled, CancellationToken ct) => ExclusiveAsync(async () =>
    {
        Store.SetEnabled(id, enabled);
        if (enabled)
            await StartAsync(Store.Find(id)!, ct).ConfigureAwait(false);
        else
            await StopAsync(id).ConfigureAwait(false);
        return true;
    });

    public Task SaveSettingsAsync(InstalledPlugin plugin, IReadOnlyDictionary<string, string> values)
    {
        Store.SaveSettings(plugin, values);
        return Host(plugin.Id)?.NotifyAsync(PluginProtocol.SettingsChanged, new JsonObject { ["settings"] = Host(plugin.Id)!.SettingsNode() })
            ?? Task.CompletedTask;
    }

    /// <summary>Envía un evento a todos los plugins en marcha (sin esperar a que lo procesen).</summary>
    public void Publish(string name)
    {
        List<PluginHost> hosts;
        lock (_hosts)
            hosts = [.. _hosts.Values];
        foreach (var host in hosts)
            _ = host.NotifyAsync(PluginProtocol.Event, new JsonObject { ["name"] = name });
    }

    private async Task StartAsync(InstalledPlugin plugin, CancellationToken ct)
    {
        await StopAsync(plugin.Id).ConfigureAwait(false);
        var host = new PluginHost(plugin, toolboxFor(plugin), () => Store.LoadSettings(plugin), Store.DataDirectory(plugin.Id));
        host.StateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        lock (_hosts)
            _hosts[plugin.Id] = host;
        await host.StartAsync(ct).ConfigureAwait(false);
    }

    private async Task StopAsync(string id)
    {
        PluginHost? host;
        lock (_hosts)
        {
            if (_hosts.Remove(id, out host) is false)
                return;
        }
        await host.DisposeAsync().ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<T> ExclusiveAsync<T>(Func<Task<T>> action)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<string> ids;
        lock (_hosts)
            ids = [.. _hosts.Keys];
        foreach (var id in ids)
            await StopAsync(id).ConfigureAwait(false);
    }
}
