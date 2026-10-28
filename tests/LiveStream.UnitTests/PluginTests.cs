using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStream.Application.Assistant;
using LiveStream.Application.Studio;
using LiveStream.Infrastructure.Diagnostics;
using LiveStream.Infrastructure.Persistence;
using LiveStream.Infrastructure.Plugins;
using LiveStream.Infrastructure.Security;
using LiveStream.PluginSdk;

namespace LiveStream.UnitTests;

/// <summary>SDK, instalación verificada y aislamiento de plugins en procesos reales (el plugin de ejemplo).</summary>
public sealed class PluginTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ls-plugins-").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private StudioSession _session = null!;

    private string PublicKey => _key.ExportSubjectPublicKeyInfoPem();
    private string PrivateKey => _key.ExportPkcs8PrivateKeyPem();

    public async Task InitializeAsync()
    {
        _session = new StudioSession(new FakeStreamingEngine(), new JsonProjectRepository(_dir), new DpapiSecretStore(Path.Combine(_dir, "s.dat")),
            StudioSession.CreateDefaultProject("Plugins"), Path.Combine(_dir, "p.spproj"), TimeSpan.FromHours(1));
        await _session.StartEngineAsync();
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        _key.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private PluginStore Store(bool developerMode = false) =>
        new(new Version(0, 9, 0), PublicKey, Path.Combine(_dir, "install"), Path.Combine(_dir, "data")) { DeveloperMode = developerMode };

    /// <summary>La salida compilada del plugin de ejemplo, con su manifiesto opcionalmente modificado.</summary>
    private string ExampleSource(Func<JsonObject, JsonObject>? edit = null, string project = "sdk/examples/HelloPlugin")
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "LiveStream.sln")))
            root = Path.GetDirectoryName(root)!;
        var exe = Directory.GetFiles(Path.Combine(root, project, "bin"), Path.GetFileName(project) + ".exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        var copy = Path.Combine(_dir, "src-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(exe)!))
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        if (edit is not null)
        {
            var manifestPath = Path.Combine(copy, PluginManifest.FileName);
            File.WriteAllText(manifestPath, edit(JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject()).ToJsonString());
        }
        return copy;
    }

    private (string Package, PackageSignature Signature) PackAndSign(string source)
    {
        var package = Path.Combine(_dir, Guid.NewGuid().ToString("N") + PluginPackage.Extension);
        PluginPackage.Pack(source, package);
        return (package, PackageSignature.Create(package, PrivateKey));
    }

    private AssistantToolbox Toolbox(InstalledPlugin _) => new(_session, new AssistantTests.FakeOutputs(), _ => Task.FromResult(false));

    // ---- SDK ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"id":"Com.Bad","name":"x","version":"1.0.0","apiVersion":1,"entry":"a.exe"}""", "id")]
    [InlineData("""{"id":"com.ok.x","name":"x","version":"1.0","apiVersion":1,"entry":"a.exe"}""", "version")]
    [InlineData("""{"id":"com.ok.x","name":"x","version":"1.0.0","apiVersion":99,"entry":"a.exe"}""", "apiVersion")]
    [InlineData("""{"id":"com.ok.x","name":"x","version":"1.0.0","apiVersion":1,"entry":"..\\evil.exe"}""", "entry")]
    [InlineData("""{"id":"com.ok.x","name":"x","version":"1.0.0","apiVersion":1,"entry":"a.dll"}""", "entry")]
    [InlineData("""{"id":"com.ok.x","name":"x","version":"1.0.0","apiVersion":1,"entry":"a.exe","permissions":["files.all"]}""", "Permiso")]
    public void Manifest_RejectsInvalidFields(string json, string expected) =>
        Assert.Contains(expected, Assert.Throws<PluginValidationException>(() => PluginManifest.Parse(json)).Message);

    [Fact]
    public void Pack_IsDeterministic_AndTheSignatureCatchesAnyChange()
    {
        var source = ExampleSource();
        var (a, signature) = PackAndSign(source);
        var (b, _) = PackAndSign(source);
        Assert.Equal(PluginPackage.Sha256(a), PluginPackage.Sha256(b));
        Assert.Null(signature.VerifyPackage(a, PublicKey));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.NotNull(signature.VerifyPackage(a, other.ExportSubjectPublicKeyInfoPem()));
        File.AppendAllText(a, "x");
        Assert.NotNull(signature.VerifyPackage(a, PublicKey));
    }

    [Fact]
    public void Package_WithPathsOutsideItsFolder_IsRejectedBeforeExtracting()
    {
        var package = Path.Combine(_dir, "evil.lsplugin");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("plugin.json").Open()))
                w.Write("""{"id":"com.evil.x","name":"x","version":"1.0.0","apiVersion":1,"entry":"a.exe"}""");
            zip.CreateEntry("a.exe");
            zip.CreateEntry("../../outside.txt");
        }
        Assert.Throws<PluginValidationException>(() => PluginPackage.Extract(package, Path.Combine(_dir, "x")));
        Assert.False(File.Exists(Path.Combine(_dir, "outside.txt")));
    }

    // ---- Instalación --------------------------------------------------------------------------

    [Fact]
    public void Install_RequiresAValidSignature_UnlessDeveloperMode()
    {
        var (package, signature) = PackAndSign(ExampleSource());
        Assert.Contains("no está firmado", Assert.Throws<PluginValidationException>(() => Store().Install(package, null, "test", true)).Message);

        var forged = signature with { Version = "9.9.9" };
        Assert.Throws<PluginValidationException>(() => Store().Install(package, forged, "test", true));

        var installed = Store().Install(package, signature, "test", true);
        Assert.True(installed.Record.Signed);
        Assert.True(File.Exists(installed.EntryPath));
        Assert.StartsWith(Path.Combine(_dir, "install"), installed.Directory);

        var dev = Store(developerMode: true).Install(PackAndSign(ExampleSource()).Package, null, "dev", true);
        Assert.False(dev.Record.Signed);
    }

    [Fact]
    public void Dependencies_AreCheckedOnInstall_Enable_AndUninstall()
    {
        var store = Store();
        var dependent = ExampleSource(m =>
        {
            m["id"] = "com.angelitosystems.dependent";
            m["dependencies"] = new JsonObject { ["com.angelitosystems.hello"] = "1.0.0" };
            return m;
        });
        var (depPackage, depSignature) = PackAndSign(dependent);
        Assert.Contains("Instálalo primero", Assert.Throws<PluginValidationException>(() => store.Install(depPackage, depSignature, "t", true)).Message);

        var (basePackage, baseSignature) = PackAndSign(ExampleSource());
        store.Install(basePackage, baseSignature, "t", true);
        store.Install(depPackage, depSignature, "t", true);
        Assert.Throws<PluginValidationException>(() => store.Uninstall("com.angelitosystems.hello"));
        Assert.Throws<PluginValidationException>(() => store.SetEnabled("com.angelitosystems.hello", false));

        store.Uninstall("com.angelitosystems.dependent");
        store.Uninstall("com.angelitosystems.hello");
        Assert.Empty(store.List());
    }

    [Fact]
    public void TooNewPlugins_AreRejected()
    {
        var (package, signature) = PackAndSign(ExampleSource(m => { m["minAppVersion"] = "99.0.0"; return m; }));
        Assert.Contains("Requiere LiveStream", Assert.Throws<PluginValidationException>(() => Store().Verify(package, signature)).Message);
    }

    [Fact]
    public void Settings_AreTypedAndLimitedToTheDeclaredKeys()
    {
        var store = Store();
        var (package, signature) = PackAndSign(ExampleSource());
        var plugin = store.Install(package, signature, "t", false);
        Assert.Equal("Hola", store.LoadSettings(plugin)["greeting"]);
        store.SaveSettings(plugin, new Dictionary<string, string> { ["greeting"] = "Bienvenidos" });
        Assert.Equal("Bienvenidos", store.LoadSettings(plugin)["greeting"]);
        Assert.Throws<PluginValidationException>(() => store.SaveSettings(plugin, new Dictionary<string, string> { ["apiKey"] = "x" }));
    }

    [Fact]
    public async Task An_update_that_does_not_start_is_rolled_back_to_the_working_version()
    {
        await using var manager = new PluginManager(Store(), Toolbox);
        var (v1, s1) = PackAndSign(ExampleSource());
        var installed = await manager.InstallAsync(v1, s1, "test", CancellationToken.None);
        Assert.Equal(PluginRunState.Running, manager.Host(installed.Id)!.State);

        // 1.0.1 con un ejecutable que termina sin hablar el protocolo: no supera initialize.
        var broken = ExampleSource(m => { m["version"] = "1.0.1"; return m; });
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), Path.Combine(broken, "HelloPlugin.exe"), overwrite: true);
        var (v2, s2) = PackAndSign(broken);

        var error = await Assert.ThrowsAsync<PluginValidationException>(() => manager.InstallAsync(v2, s2, "test", CancellationToken.None));

        Assert.Contains("Se restauró la 1.0.0", error.Message);
        Assert.Equal("1.0.0", manager.Store.Find(installed.Id)!.Manifest.Version);
        Assert.Equal(PluginRunState.Running, manager.Host(installed.Id)!.State);
        Assert.False(Directory.Exists(Path.Combine(_dir, "install", $".previous-{installed.Id}")));
    }

    [Fact]
    public async Task A_working_update_discards_the_previous_version()
    {
        await using var manager = new PluginManager(Store(), Toolbox);
        var (v1, s1) = PackAndSign(ExampleSource());
        var installed = await manager.InstallAsync(v1, s1, "test", CancellationToken.None);
        var (v2, s2) = PackAndSign(ExampleSource(m => { m["version"] = "1.0.1"; return m; }));

        await manager.InstallAsync(v2, s2, "test", CancellationToken.None);

        Assert.Equal("1.0.1", manager.Store.Find(installed.Id)!.Manifest.Version);
        Assert.Equal(PluginRunState.Running, manager.Host(installed.Id)!.State);
        Assert.False(Directory.Exists(Path.Combine(_dir, "install", $".previous-{installed.Id}")));
    }

    // ---- Proceso aislado ----------------------------------------------------------------------

    [Fact]
    public async Task Plugin_RunsInItsOwnProcess_UsesTheApi_AndIsRestartedAfterACrash()
    {
        var store = Store();
        var (package, signature) = PackAndSign(ExampleSource());
        await using var manager = new PluginManager(store, Toolbox);
        await manager.InstallAsync(package, signature, "test", CancellationToken.None);
        var host = manager.Host("com.angelitosystems.hello")!;
        Assert.Equal(PluginRunState.Running, host.State);
        var firstPid = host.ProcessId!.Value;
        Assert.NotEqual(Environment.ProcessId, firstPid);

        manager.Publish(PluginProtocol.StudioChangedEvent);
        Assert.True(await LogEventually("com.angelitosystems.hello", "escena(s)"), "el plugin no consultó el estado");

        // Un cierre inesperado no afecta a LiveStream: el plugin vuelve a arrancar solo.
        Process.GetProcessById(firstPid).Kill();
        for (var i = 0; i < 100 && (host.State != PluginRunState.Running || host.ProcessId is not { } pid || pid == firstPid); i++)
            await Task.Delay(100);
        Assert.Equal(PluginRunState.Running, host.State);
        Assert.NotEqual(firstPid, host.ProcessId);

        await manager.SetEnabledAsync("com.angelitosystems.hello", false, CancellationToken.None);
        Assert.Null(manager.Host("com.angelitosystems.hello"));
        Assert.True(Process.GetProcesses().All(p => p.Id != firstPid));
    }

    [Fact]
    public async Task Plugin_CannotUseToolsBeyondItsPermissions()
    {
        var (package, signature) = PackAndSign(ExampleSource());
        var plugin = Store().Install(package, signature, "t", false);
        await using var host = new PluginHost(plugin, Toolbox(plugin), () => new Dictionary<string, string>(), Path.Combine(_dir, "d"));

        var list = await host.HandleAsync(PluginProtocol.ToolsList, null, CancellationToken.None);
        Assert.All(list!["tools"]!.AsArray(), t => Assert.Equal(PluginPermissions.StudioRead, t!["permission"]!.GetValue<string>()));

        var denied = await Assert.ThrowsAsync<JsonRpcException>(() => host.HandleAsync(PluginProtocol.ToolsCall,
            new JsonObject { ["name"] = "create_scene", ["arguments"] = new JsonObject { ["name"] = "X" } }, CancellationToken.None));
        Assert.Equal(JsonRpcException.PermissionDenied, denied.Code);
        Assert.DoesNotContain(_session.Project.Scenes, s => s.Name == "X");
    }

    [Fact]
    public async Task Marketplace_LocalRepository_ListsOnlySignedPackages_AndDownloadsVerified()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_dir, "repo")).FullName;
        var (package, signature) = PackAndSign(ExampleSource());
        File.Copy(package, Path.Combine(repo, "hello.lsplugin"));
        File.WriteAllText(Path.Combine(repo, "hello.lsplugin" + PackageSignature.FileSuffix), signature.ToJson());
        File.Copy(package, Path.Combine(repo, "unsigned.lsplugin"));

        var client = new MarketplaceClient(new HttpClient(), PublicKey, new Version(0, 9, 0));
        var catalog = client.GetLocal(repo);
        var entry = Assert.Single(catalog.Entries);
        Assert.Equal(1, catalog.Rejected);
        Assert.Equal(["studio.read"], entry.Permissions);

        var downloaded = await client.DownloadAsync(entry, CancellationToken.None);
        Assert.Null(entry.PackageSignature.VerifyPackage(downloaded, PublicKey));
        File.Delete(downloaded);
    }

    [Fact]
    public async Task Marketplace_RemoteCatalog_DropsTamperedEntries_AndRejectsAlteredDownloads()
    {
        var (package, signature) = PackAndSign(ExampleSource());
        var manifest = PluginPackage.Inspect(package);
        var good = CatalogEntry.From(manifest, signature, "https://market.test/v1/packages/hello/1.0.0", DateTimeOffset.UtcNow);
        var tampered = good with { Id = "com.angelitosystems.other", Name = "Otro" };
        var catalog = new Catalog(Catalog.CurrentFormat, DateTimeOffset.UtcNow, [good, tampered]);
        var bytes = await File.ReadAllBytesAsync(package);
        var serveAltered = false;
        var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/catalog" => new HttpResponseMessage { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(catalog, PluginManifest.Json)) },
            _ => new HttpResponseMessage { Content = new ByteArrayContent(serveAltered ? [.. bytes[..^1], (byte)(bytes[^1] ^ 0xFF)] : bytes) },
        }));

        var client = new MarketplaceClient(http, PublicKey, new Version(0, 9, 0));
        var remote = await client.GetRemoteAsync(new Uri("https://market.test"), CancellationToken.None);
        Assert.Equal("com.angelitosystems.hello", Assert.Single(remote.Entries).Id);
        Assert.Equal(1, remote.Rejected);

        var ok = await client.DownloadAsync(remote.Entries[0], CancellationToken.None);
        File.Delete(ok);
        serveAltered = true;
        await Assert.ThrowsAsync<PluginValidationException>(() => client.DownloadAsync(remote.Entries[0], CancellationToken.None));
        await Assert.ThrowsAsync<PluginValidationException>(() => client.GetRemoteAsync(new Uri("http://market.test"), CancellationToken.None));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    private static async Task<bool> LogEventually(string pluginId, string text)
    {
        for (var i = 0; i < 100; i++)
        {
            AppLog.Flush(TimeSpan.FromSeconds(1));
            if (File.Exists(AppLog.CurrentFile))
            {
                using var stream = new FileStream(AppLog.CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (new StreamReader(stream).ReadToEnd().Split('\n').Any(l => l.Contains($"plugin:{pluginId}") && l.Contains(text)))
                    return true;
            }
            await Task.Delay(100);
        }
        return false;
    }
}
