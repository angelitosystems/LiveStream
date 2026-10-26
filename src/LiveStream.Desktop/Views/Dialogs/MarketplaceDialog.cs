using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LiveStream.Desktop.ViewModels;
using LiveStream.Infrastructure.Diagnostics;
using LiveStream.Infrastructure.Plugins;
using LiveStream.Infrastructure.Updates;
using LiveStream.PluginSdk;
using static LiveStream.Desktop.Views.Dialogs.StudioDialogs;

namespace LiveStream.Desktop.Views.Dialogs;

/// <summary>
/// Marketplace: catálogo (remoto y/o carpeta local), instalación verificada con aceptación de permisos, activar,
/// desactivar, desinstalar, actualizaciones y ajustes de cada plugin.
/// </summary>
public static class MarketplaceDialog
{
    private sealed record Row(string Id, string Name, CatalogEntry? Available, InstalledPlugin? Installed)
    {
        public bool HasUpdate => Available is not null && Installed is not null
            && PluginVersion.Parse(Available.Version) > PluginVersion.Parse(Installed.Manifest.Version);
    }

    private static readonly string[] Filters = ["Todos", "Instalados", "Actualizaciones disponibles"];

    public static async Task ShowAsync(MainViewModel vm)
    {
        if (vm.Dialogs.Owner is not { } owner)
            return;
        var manager = vm.Plugins;
        var client = new MarketplaceClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, PluginTrust.EmbeddedPublicKey(), UpdateClient.CurrentVersion);
        var window = NewWindow("Marketplace de plugins", 980, 680);

        var search = new TextBox { Watermark = "Buscar por nombre, autor, categoría o etiqueta", MinWidth = 320 };
        var filter = new ComboBox { ItemsSource = Filters, SelectedIndex = 0, MinWidth = 200 };
        var source = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        var list = new ListBox { Classes = { "studioList" }, Width = 380 };
        var details = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        var settingsPanel = new StackPanel { Spacing = 6 };
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        var install = Primary("Instalar");
        var toggle = new Button { Content = "Desactivar" };
        var uninstall = new Button { Content = "Desinstalar" };
        var saveSettings = new Button { Content = "Guardar ajustes" };

        IReadOnlyList<CatalogEntry> catalog = [];
        List<Row> rows = [];
        Row? selected = null;
        Dictionary<string, Func<string>> settingInputs = [];

        async Task LoadCatalogAsync()
        {
            var machine = vm.Machine.Load();
            manager.Store.DeveloperMode = machine.PluginDeveloperMode;
            var entries = new List<CatalogEntry>();
            var notes = new List<string>();
            try
            {
                if (Uri.TryCreate(machine.PluginCatalogUrl, UriKind.Absolute, out var url))
                {
                    var remote = await client.GetRemoteAsync(url, CancellationToken.None);
                    entries.AddRange(remote.Entries);
                    notes.Add($"Catálogo: {url.Host}{(remote.Rejected > 0 ? $" ({remote.Rejected} entradas descartadas por firma o compatibilidad)" : "")}");
                }
                if (machine.PluginRepository is { Length: > 0 } folder && Directory.Exists(folder))
                {
                    var local = client.GetLocal(folder);
                    entries.AddRange(local.Entries.Where(l => entries.All(e => e.Id != l.Id)));
                    notes.Add($"Repositorio local: {folder}{(local.Rejected > 0 ? $" ({local.Rejected} paquetes sin firma válida)" : "")}");
                }
                if (notes.Count == 0)
                    notes.Add("Sin catálogo configurado: pulsa «Origen…» para indicar la URL del marketplace o una carpeta local.");
                if (PluginTrust.EmbeddedPublicKey() is null)
                    notes.Add("Esta compilación no tiene clave de publicador: solo se pueden instalar plugins sin firma en modo desarrollador.");
                error.Text = "";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PluginValidationException or IOException)
            {
                error.Text = $"No se pudo cargar el catálogo: {ex.Message}";
            }
            catalog = entries;
            source.Text = string.Join("  ·  ", notes);
            Rebuild();
        }

        void Rebuild()
        {
            var installed = manager.Store.List().ToDictionary(p => p.Id);
            var all = catalog.Select(e => new Row(e.Id, e.Name, e, installed.GetValueOrDefault(e.Id)))
                .Concat(installed.Values.Where(p => catalog.All(e => e.Id != p.Id)).Select(p => new Row(p.Id, p.Manifest.Name, null, p)));
            var text = search.Text?.Trim() ?? "";
            rows = all.Where(r => filter.SelectedIndex switch
                {
                    1 => r.Installed is not null,
                    2 => r.HasUpdate,
                    _ => true,
                })
                .Where(r => text.Length == 0 || Matches(r, text))
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var keep = selected?.Id;
            list.ItemsSource = rows.Select(Label).ToList();
            list.SelectedIndex = Math.Max(rows.FindIndex(r => r.Id == keep), rows.Count > 0 ? 0 : -1);
            ShowSelected();
        }

        string Label(Row r)
        {
            var version = r.Installed?.Manifest.Version ?? r.Available!.Version;
            var state = r.Installed is null ? "Disponible"
                : manager.Host(r.Id) is { State: PluginRunState.Failed } ? "Error"
                : r.HasUpdate ? $"Actualización a {r.Available!.Version}"
                : r.Installed.Record.Enabled ? "Activo" : "Desactivado";
            return $"{r.Name}   {version}   ·   {state}";
        }

        void ShowSelected()
        {
            selected = list.SelectedIndex >= 0 && list.SelectedIndex < rows.Count ? rows[list.SelectedIndex] : null;
            settingsPanel.Children.Clear();
            settingInputs = [];
            if (selected is null)
            {
                details.Text = rows.Count == 0 ? "No hay plugins que mostrar." : "";
                install.IsVisible = toggle.IsVisible = uninstall.IsVisible = saveSettings.IsVisible = false;
                return;
            }
            var r = selected;
            var m = r.Installed?.Manifest;
            var e = r.Available;
            var sb = new StringBuilder();
            sb.AppendLine(r.Name);
            var author = m?.Author ?? e?.Author;
            sb.AppendLine($"{(string.IsNullOrEmpty(author) ? "Autor desconocido" : author)}  ·  {m?.Category ?? e?.Category}");
            sb.AppendLine();
            sb.AppendLine(m?.Description ?? e?.Description);
            sb.AppendLine();
            if (r.Installed is { } i)
                sb.AppendLine($"Instalada: {i.Manifest.Version} ({(i.Record.Signed ? "firmada" : "SIN FIRMA, modo desarrollador")}, {i.Record.Source})");
            if (e is not null)
                sb.AppendLine($"En el catálogo: {e.Version}  ·  publicada {e.PublishedAt.LocalDateTime:d}  ·  {e.Size / 1024} KB");
            sb.AppendLine($"Compatible desde LiveStream {m?.MinAppVersion ?? e?.MinAppVersion}  ·  API {m?.ApiVersion ?? e?.ApiVersion}");
            var deps = m?.Dependencies ?? e?.Dependencies;
            if (deps is { Count: > 0 })
                sb.AppendLine("Necesita: " + string.Join(", ", deps.Select(d => $"{d.Key} {d.Value}+")));
            sb.AppendLine("Permisos:");
            foreach (var p in m?.PermissionList ?? e?.Permissions ?? [])
                sb.AppendLine($"  • {PluginPermissions.Descriptions.GetValueOrDefault(p, p)}");
            if ((m?.Tags ?? e?.Tags) is { Count: > 0 } tags)
                sb.AppendLine("Etiquetas: " + string.Join(", ", tags));
            if ((m?.Homepage ?? e?.Homepage) is { Length: > 0 } home)
                sb.AppendLine($"Documentación: {home}");
            if ((e?.Changelog ?? m?.Changelog) is { Length: > 0 } changes)
                sb.AppendLine().AppendLine("Cambios:").AppendLine(changes);
            if (manager.Host(r.Id) is { } host)
                sb.AppendLine().AppendLine($"Estado: {host.State}{(host.LastError is { } err ? $" — {err}" : "")}");
            details.Text = sb.ToString();

            install.IsVisible = e is not null && (r.Installed is null || r.HasUpdate);
            install.Content = r.Installed is null ? "Instalar" : $"Actualizar a {e?.Version}";
            toggle.IsVisible = uninstall.IsVisible = r.Installed is not null;
            toggle.Content = r.Installed?.Record.Enabled == true && manager.Host(r.Id)?.State != PluginRunState.Failed ? "Desactivar" : "Activar";

            if (r.Installed is { } plugin && plugin.Manifest.Settings is { Count: > 0 } declared)
            {
                var values = manager.Store.LoadSettings(plugin);
                settingsPanel.Children.Add(new TextBlock { Text = "Ajustes", FontWeight = FontWeight.SemiBold });
                foreach (var setting in declared)
                {
                    Control input;
                    if (setting.Type == "bool")
                    {
                        var box = new CheckBox { Content = setting.Label, IsChecked = values.GetValueOrDefault(setting.Key) == "true" };
                        settingInputs[setting.Key] = () => box.IsChecked == true ? "true" : "false";
                        input = box;
                    }
                    else
                    {
                        var box = new TextBox { Text = values.GetValueOrDefault(setting.Key, setting.Default) };
                        settingInputs[setting.Key] = () => box.Text ?? "";
                        input = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = setting.Label }, box } };
                    }
                    settingsPanel.Children.Add(input);
                }
            }
            saveSettings.IsVisible = settingInputs.Count > 0;
        }

        async Task<bool> ConsentAsync(PluginManifest manifest, bool signed, bool update)
        {
            var permissions = manifest.PermissionList.Count == 0
                ? "  • Ninguno (no usa la API del estudio)"
                : string.Join("\n", manifest.PermissionList.Select(p => $"  • {PluginPermissions.Descriptions[p]}"));
            return await vm.Dialogs.ConfirmAsync(update ? "Actualizar plugin" : "Instalar plugin",
                $"«{manifest.Name}» {manifest.Version}{(manifest.Author.Length > 0 ? $" de {manifest.Author}" : "")}\n\n" +
                $"Permisos que pide:\n{permissions}\n\n" +
                (signed ? "Firmado por el publicador de confianza de LiveStream." : "SIN FIRMA: solo instálalo si lo has creado tú (modo desarrollador).") +
                "\n\nLos plugins son programas que se ejecutan en un proceso aparte con tus permisos de usuario de Windows. " +
                "LiveStream solo controla lo que pueden hacer en el estudio. Instala solo plugins de confianza.",
                update ? "Actualizar" : "Instalar", destructive: !signed);
        }

        async Task InstallPackageAsync(string package, PackageSignature? signature, string origin)
        {
            var manifest = manager.Store.Verify(package, signature);
            var update = manager.Store.Find(manifest.Id) is not null;
            if (!await ConsentAsync(manifest, signature is not null, update))
                return;
            status.Text = "Instalando…";
            var installed = await manager.InstallAsync(package, signature, origin, CancellationToken.None);
            var state = manager.Host(installed.Id);
            status.Text = state?.State == PluginRunState.Running
                ? $"«{installed.Manifest.Name}» {installed.Manifest.Version} instalado y en marcha."
                : $"«{installed.Manifest.Name}» instalado, pero no arrancó: {state?.LastError}";
        }

        async Task Guarded(Func<Task> action)
        {
            error.Text = "";
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is PluginValidationException or IOException or HttpRequestException or TaskCanceledException
                                           or UnauthorizedAccessException or InvalidDataException)
            {
                error.Text = ex.Message;
                status.Text = "";
                AppLog.Warn("marketplace", ex.Message);
            }
            Rebuild();
        }

        install.Click += async (_, _) => await Guarded(async () =>
        {
            if (selected?.Available is not { } entry)
                return;
            status.Text = "Descargando y verificando…";
            var package = await client.DownloadAsync(entry, CancellationToken.None);
            try
            {
                await InstallPackageAsync(package, entry.PackageSignature, entry.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "catálogo" : "repositorio local");
            }
            finally
            {
                File.Delete(package);
            }
        });
        toggle.Click += async (_, _) => await Guarded(async () =>
        {
            if (selected?.Installed is not { } plugin)
                return;
            var enable = !(plugin.Record.Enabled && manager.Host(plugin.Id)?.State != PluginRunState.Failed);
            await manager.SetEnabledAsync(plugin.Id, enable, CancellationToken.None);
            status.Text = enable ? $"«{plugin.Manifest.Name}» activado." : $"«{plugin.Manifest.Name}» desactivado.";
        });
        uninstall.Click += async (_, _) => await Guarded(async () =>
        {
            if (selected?.Installed is not { } plugin
                || !await vm.Dialogs.ConfirmAsync("Desinstalar plugin", $"¿Desinstalar «{plugin.Manifest.Name}» y borrar sus ajustes y datos?", "Desinstalar", destructive: true))
                return;
            await manager.UninstallAsync(plugin.Id);
            status.Text = $"«{plugin.Manifest.Name}» desinstalado.";
        });
        saveSettings.Click += async (_, _) => await Guarded(async () =>
        {
            if (selected?.Installed is not { } plugin)
                return;
            await manager.SaveSettingsAsync(plugin, settingInputs.ToDictionary(kv => kv.Key, kv => kv.Value()));
            status.Text = "Ajustes guardados y enviados al plugin.";
        });

        var fromFile = new Button { Content = "Instalar desde archivo…" };
        fromFile.Click += async (_, _) => await Guarded(async () =>
        {
            if (await vm.Dialogs.PickFileAsync("Instalar plugin", "Plugins de LiveStream (*.lsplugin)") is not { } path)
                return;
            var sigPath = path + PackageSignature.FileSuffix;
            var signature = File.Exists(sigPath) ? PackageSignature.FromJson(await File.ReadAllTextAsync(sigPath)) : null;
            await InstallPackageAsync(path, signature, "archivo");
        });

        var origin = new Button { Content = "Origen…" };
        origin.Click += async (_, _) => await Guarded(async () =>
        {
            var machine = vm.Machine.Load();
            if (await vm.Dialogs.PromptAsync("Catálogo remoto", "URL HTTPS del marketplace (vacío = ninguno):", machine.PluginCatalogUrl ?? "") is not { } url)
                return;
            url = url.Trim();
            if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttps && !parsed.IsLoopback)))
                throw new PluginValidationException("La URL del catálogo debe empezar por https://");
            var folder = await vm.Dialogs.ConfirmAsync("Repositorio local", "¿Quieres usar además una carpeta local con paquetes .lsplugin firmados?", "Elegir carpeta")
                ? await vm.Dialogs.PickFolderAsync("Repositorio local de plugins") : machine.PluginRepository;
            vm.Machine.Save(machine with { PluginCatalogUrl = url.Length > 0 ? url : null, PluginRepository = folder });
            await LoadCatalogAsync();
        });

        var developer = new CheckBox { Content = "Modo desarrollador (permitir plugins sin firma)", IsChecked = vm.Machine.Load().PluginDeveloperMode };
        developer.IsCheckedChanged += async (_, _) =>
        {
            if (developer.IsChecked == true && !await vm.Dialogs.ConfirmAsync("Modo desarrollador",
                    "Permite instalar plugins sin firma. Úsalo solo para probar plugins que estás desarrollando.", "Activar", destructive: true))
            {
                developer.IsChecked = false;
                return;
            }
            vm.Machine.Save(vm.Machine.Load() with { PluginDeveloperMode = developer.IsChecked == true });
            manager.Store.DeveloperMode = developer.IsChecked == true;
        };

        var reload = new Button { Content = "Recargar" };
        reload.Click += async (_, _) => await Guarded(LoadCatalogAsync);
        search.TextChanged += (_, _) => Rebuild();
        filter.SelectionChanged += (_, _) => Rebuild();
        list.SelectionChanged += (_, _) => ShowSelected();
        EventHandler onChanged = (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        manager.Changed += onChanged;
        window.Closed += (_, _) => manager.Changed -= onChanged;

        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8),
            Children = { search, filter, reload, origin },
        };
        var detailPane = new ScrollViewer
        {
            Margin = new Thickness(16, 0, 0, 0),
            Content = new StackPanel { Spacing = 10, Children = { details, settingsPanel, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { install, toggle, saveSettings, uninstall } } } },
        };
        var body = new DockPanel { Margin = new Thickness(16) };
        var footer = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0), Children = { source, developer, error, status } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(list, Dock.Left);
        body.Children.Add(top);
        body.Children.Add(footer);
        body.Children.Add(list);
        body.Children.Add(detailPane);

        var close = new Button { Content = "Cerrar", IsCancel = true };
        close.Click += (_, _) => window.Close();
        window.Content = Layout(body, fromFile, close);
        window.Opened += async (_, _) => await Guarded(LoadCatalogAsync);
        await window.ShowDialog(owner);
    }

    private static bool Matches(Row r, string text)
    {
        bool Has(string? s) => s?.Contains(text, StringComparison.CurrentCultureIgnoreCase) == true;
        var m = r.Installed?.Manifest;
        var e = r.Available;
        return Has(r.Name) || Has(m?.Author ?? e?.Author) || Has(m?.Category ?? e?.Category) || Has(m?.Description ?? e?.Description)
            || (m?.Tags ?? e?.Tags ?? []).Any(Has);
    }
}
