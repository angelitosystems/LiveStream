using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using LiveStream.Application.Extensions;
using LiveStream.Desktop.ViewModels;
using LiveStream.Infrastructure.Diagnostics;
using LiveStream.Infrastructure.Plugins;
using LiveStream.Infrastructure.Updates;
using LiveStream.PluginSdk;
using static LiveStream.Desktop.Views.Dialogs.StudioDialogs;

namespace LiveStream.Desktop.Views.Dialogs;

/// <summary>
/// Extensiones y plugins en una sola ventana. Las extensiones integradas (alertas, chat, TTS…) se activan o desactivan.
/// Los plugins de terceros (catálogo remoto o carpeta local, cada uno en su proceso) se instalan con verificación y
/// aceptación de permisos, y se pueden activar, desactivar, actualizar (uno o todos), configurar y desinstalar.
/// </summary>
public static class MarketplaceDialog
{
    private sealed record Row(string Id, string Name, CatalogEntry? Available, InstalledPlugin? Installed, ExtensionStatus? BuiltIn = null)
    {
        public bool HasUpdate => Available is not null && Installed is not null
            && PluginVersion.Parse(Available.Version) > PluginVersion.Parse(Installed.Manifest.Version);

        public string Version => BuiltIn?.Manifest.Version.ToString(3) ?? Installed?.Manifest.Version ?? Available!.Version;
        public string Author => BuiltIn is not null ? "LiveStream" : Installed?.Manifest.Author ?? Available?.Author ?? "";
        public string Category => BuiltIn is not null ? "Integrada" : Installed?.Manifest.Category ?? Available?.Category ?? "";
        public string? Description => BuiltIn?.Manifest.Description ?? Installed?.Manifest.Description ?? Available?.Description;
    }

    private const string Blue = "#FF3D8BFF", Green = "#FF34D399", Amber = "#FFF5A524", Red = "#FFEF4444", Grey = "#FF8A93A6";

    public static async Task ShowAsync(MainViewModel vm)
    {
        if (vm.Dialogs.Owner is not { } owner)
            return;
        var manager = vm.Plugins;
        var extensions = vm.Session.Extensions;
        var client = new MarketplaceClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, PluginTrust.EmbeddedPublicKey(), UpdateClient.CurrentVersion);
        var window = NewWindow("Extensiones y plugins", 1000, 700);

        var search = new TextBox { Watermark = "Buscar por nombre, autor, categoría o etiqueta", MinWidth = 300 };
        var filter = new ComboBox { MinWidth = 210 };
        var source = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        var list = new ListBox { Classes = { "studioList" }, Width = 360 };
        var detailPanel = new StackPanel { Spacing = 12 };
        var settingsPanel = new StackPanel { Spacing = 6 };
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        var install = Primary("Instalar");
        var toggle = new Button { Content = "Desactivar" };
        var uninstall = new Button { Content = "Desinstalar" };
        var saveSettings = new Button { Content = "Guardar ajustes" };
        var updateAll = Primary("Actualizar todo");
        var busy = new ProgressBar { IsIndeterminate = true, IsVisible = false, Height = 3, Margin = new Thickness(0, 0, 0, 6) };

        IReadOnlyList<CatalogEntry> catalog = [];
        List<Row> all = [];
        List<Row> rows = [];
        Row? selected = null;
        Dictionary<string, Func<string>> settingInputs = [];

        (string Text, string Color) StateOf(Row r) =>
            r.BuiltIn is { } b ? b.State switch
            {
                ExtensionState.Running => ("Activa", Green),
                ExtensionState.Disabled => ("Desactivada", Grey),
                ExtensionState.Faulted => ("Error", Red),
                ExtensionState.Blocked => ("En espera de una dependencia", Amber),
                _ => ("Cargando", Blue),
            }
            : r.Installed is null ? ("Disponible", Blue)
            : manager.Host(r.Id) is { State: PluginRunState.Failed } ? ("Error", Red)
            : r.HasUpdate ? ($"Actualización {r.Available!.Version}", Amber)
            : !r.Installed.Record.Enabled ? ("Desactivado", Grey)
            : manager.Host(r.Id)?.State switch
            {
                PluginRunState.Running => ("En marcha", Green),
                PluginRunState.Starting => ("Iniciando", Blue),
                _ => ("Detenido", Grey),
            };

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

        // Rebuild reasigna los elementos del filtro y de la lista, y sus eventos volverían a llamarlo.
        var rebuilding = false;

        void Rebuild()
        {
            if (rebuilding)
                return;
            rebuilding = true;
            try
            {
                RebuildCore();
            }
            finally
            {
                rebuilding = false;
            }
            ShowSelected();
        }

        void RebuildCore()
        {
            var installed = manager.Store.List().ToDictionary(p => p.Id);
            all = [.. extensions.Statuses.Select(s => new Row($"ext:{s.Manifest.Id}", s.Manifest.Name, null, null, s))
                .Concat(catalog.Select(e => new Row(e.Id, e.Name, e, installed.GetValueOrDefault(e.Id))))
                .Concat(installed.Values.Where(p => catalog.All(e => e.Id != p.Id)).Select(p => new Row(p.Id, p.Manifest.Name, null, p)))];
            var updates = all.Count(r => r.HasUpdate);
            var filterIndex = Math.Max(filter.SelectedIndex, 0);
            filter.ItemsSource = new[]
            {
                $"Todo ({all.Count})", $"Integradas ({all.Count(r => r.BuiltIn is not null)})",
                $"Plugins instalados ({all.Count(r => r.Installed is not null)})", $"Actualizaciones ({updates})",
            };
            filter.SelectedIndex = filterIndex;
            updateAll.Content = $"Actualizar todo ({updates})";
            updateAll.IsVisible = updates > 1;

            var text = search.Text?.Trim() ?? "";
            rows = [.. all.Where(r => filterIndex switch
                {
                    1 => r.BuiltIn is not null,
                    2 => r.Installed is not null,
                    3 => r.HasUpdate,
                    _ => true,
                })
                .Where(r => text.Length == 0 || Matches(r, text))
                .OrderByDescending(r => r.HasUpdate)
                .ThenByDescending(r => r.BuiltIn is not null)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
            var keep = selected?.Id;
            list.ItemsSource = rows;
            list.SelectedIndex = Math.Max(rows.FindIndex(r => r.Id == keep), rows.Count > 0 ? 0 : -1);
        }

        list.ItemTemplate = new FuncDataTemplate<Row>((r, _) =>
        {
            if (r is null)
                return new TextBlock();
            var (state, color) = StateOf(r);
            var name = new TextBlock { Text = r.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var version = new TextBlock { Text = r.Version, Classes = { "muted" }, Margin = new Thickness(8, 0, 0, 0) };
            var top = new DockPanel { Children = { version, name } };
            DockPanel.SetDock(version, Dock.Right);
            var meta = new TextBlock
            {
                Text = string.Join("  ·  ", new[] { r.Author, r.Category }.Where(s => s.Length > 0)),
                Classes = { "muted" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            return new StackPanel { Spacing = 3, Margin = new Thickness(2, 4), Children = { top, meta, Badge(state, color) } };
        });

        void ShowSelected()
        {
            selected = list.SelectedIndex >= 0 && list.SelectedIndex < rows.Count ? rows[list.SelectedIndex] : null;
            detailPanel.Children.Clear();
            settingsPanel.Children.Clear();
            settingInputs = [];
            if (selected is null)
            {
                detailPanel.Children.Add(new TextBlock
                {
                    Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
                    Text = all.Count == 0 ? "No hay plugins todavía. Configura un catálogo con «Origen…» o instala un paquete .lsplugin." : "Ningún plugin coincide con la búsqueda.",
                });
                install.IsVisible = toggle.IsVisible = uninstall.IsVisible = saveSettings.IsVisible = false;
                return;
            }
            var r = selected;
            if (r.BuiltIn is { } builtIn)
            {
                ShowBuiltIn(r, builtIn);
                return;
            }
            var m = r.Installed?.Manifest;
            var e = r.Available;
            var (state, color) = StateOf(r);

            var badges = new WrapPanel { Children = { Badge(state, color) } };
            if (r.Installed is { } i)
                badges.Children.Add(i.Record.Signed ? Badge("Firmado", Green) : Badge("Sin firma (desarrollador)", Red));
            else if (e is not null)
                badges.Children.Add(Badge("Firmado por el catálogo", Green));
            detailPanel.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = r.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"{(r.Author.Length > 0 ? r.Author : "Autor desconocido")}  ·  {r.Category}  ·  versión {r.Version}", Classes = { "muted" } },
                    badges,
                },
            });
            detailPanel.Children.Add(new SelectableTextBlock { Text = m?.Description ?? e?.Description, TextWrapping = TextWrapping.Wrap });

            if (manager.Host(r.Id) is { State: PluginRunState.Failed, LastError: { } failure })
            {
                var logs = new Button { Content = "Ver registro" };
                logs.Click += (_, _) => Process.Start(new ProcessStartInfo(AppLog.Directory) { UseShellExecute = true });
                detailPanel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse(Red), 0.12), CornerRadius = new CornerRadius(6), Padding = new Thickness(10),
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new SelectableTextBlock { Text = failure, TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = "LiveStream sigue funcionando: el plugin corre en su propio proceso. «Activar» lo vuelve a intentar.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                            logs,
                        },
                    },
                });
            }

            var permissions = m?.PermissionList ?? e?.Permissions ?? [];
            detailPanel.Children.Add(Section("Permisos", permissions.Count == 0
                ? "Ninguno: no usa la API del estudio."
                : string.Join("\n", permissions.Select(p => $"•  {PluginPermissions.Descriptions.GetValueOrDefault(p, p)}"))));

            var compat = new List<string> { $"LiveStream {m?.MinAppVersion ?? e?.MinAppVersion} o posterior  ·  API {m?.ApiVersion ?? e?.ApiVersion}" };
            if ((m?.Dependencies ?? e?.Dependencies) is { Count: > 0 } deps)
                compat.Add("Necesita: " + string.Join(", ", deps.Select(d => $"{d.Key} {d.Value}+")));
            if (r.Installed is { } inst)
                compat.Add($"Instalada {inst.Manifest.Version} desde {inst.Record.Source}, el {inst.Record.InstalledAt.LocalDateTime:d}");
            if (e is not null)
                compat.Add($"En el catálogo: {e.Version}, publicada el {e.PublishedAt.LocalDateTime:d} ({Math.Max(1, e.Size / 1024)} KB)");
            if ((m?.Homepage ?? e?.Homepage) is { Length: > 0 } home)
                compat.Add($"Documentación: {home}");
            if ((m?.Tags ?? e?.Tags) is { Count: > 0 } tags)
                compat.Add("Etiquetas: " + string.Join(", ", tags));
            detailPanel.Children.Add(Section("Detalles", string.Join("\n", compat)));
            if ((e?.Changelog ?? m?.Changelog) is { Length: > 0 } changes)
                detailPanel.Children.Add(Section(r.HasUpdate ? $"Novedades de la {e!.Version}" : "Cambios", changes));

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

        string NameOf(string id) => extensions.Statuses.FirstOrDefault(s => s.Manifest.Id == id)?.Manifest.Name ?? id;

        string Dependents(string id) =>
            string.Join(", ", extensions.Statuses.Where(s => s.Manifest.Dependencies.Contains(id)).Select(s => s.Manifest.Name));

        void ShowBuiltIn(Row r, ExtensionStatus s)
        {
            var (state, color) = StateOf(r);
            var m = s.Manifest;
            detailPanel.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = m.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"Integrada en LiveStream  ·  versión {r.Version}", Classes = { "muted" } },
                    new WrapPanel { Children = { Badge(state, color), Badge("Integrada", Blue) } },
                },
            });
            detailPanel.Children.Add(new SelectableTextBlock { Text = m.Description, TextWrapping = TextWrapping.Wrap });
            if (s.Error is { } failure)
                detailPanel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse(s.State == ExtensionState.Faulted ? Red : Amber), 0.12),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(10),
                    Child = new SelectableTextBlock { Text = failure, TextWrapping = TextWrapping.Wrap },
                });
            detailPanel.Children.Add(Section("Permisos", m.Permissions.Count == 0
                ? "Ninguno."
                : string.Join("\n", m.Permissions.Select(p => $"•  {ExtensionPermissionText(p)}"))));
            var dependents = Dependents(m.Id);
            detailPanel.Children.Add(Section("Dependencias",
                $"Depende de: {(m.Dependencies.Count == 0 ? "nada" : string.Join(", ", m.Dependencies.Select(NameOf)))}\n" +
                $"Necesitan esta: {(dependents.Length == 0 ? "ninguna" : dependents)}"));
            detailPanel.Children.Add(new TextBlock
            {
                Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
                Text = "Se activa y desactiva sin reiniciar. Desactivada, su panel y sus opciones de menú desaparecen hasta que la " +
                       "vuelvas a activar. Si falla, se detiene sola y el resto del estudio sigue funcionando.",
            });
            install.IsVisible = uninstall.IsVisible = saveSettings.IsVisible = false;
            toggle.IsVisible = true;
            toggle.Content = s.Enabled ? "Desactivar" : "Activar";
            toggle.IsEnabled = m.CanDisable || !s.Enabled;
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
            var current = manager.Store.Find(manifest.Id);
            // Una actualización firmada que no pide permisos nuevos no necesita otro consentimiento (como en los navegadores).
            var samePermissions = current is not null && signature is not null && manifest.PermissionList.All(current.Manifest.PermissionList.Contains);
            if (!samePermissions && !await ConsentAsync(manifest, signature is not null, current is not null))
                return;
            status.Text = $"Instalando «{manifest.Name}» {manifest.Version}…";
            var installed = await manager.InstallAsync(package, signature, origin, CancellationToken.None);
            var state = manager.Host(installed.Id);
            status.Text = state?.State == PluginRunState.Running
                ? $"«{installed.Manifest.Name}» {installed.Manifest.Version} instalado y en marcha."
                : $"«{installed.Manifest.Name}» instalado, pero no arrancó: {state?.LastError}";
        }

        async Task InstallEntryAsync(CatalogEntry entry)
        {
            status.Text = $"Descargando y verificando «{entry.Name}» {entry.Version}…";
            var package = await client.DownloadAsync(entry, CancellationToken.None);
            try
            {
                await InstallPackageAsync(package, entry.PackageSignature, entry.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "catálogo" : "repositorio local");
            }
            finally
            {
                File.Delete(package);
            }
        }

        async Task Guarded(Func<Task> action)
        {
            error.Text = "";
            busy.IsVisible = true;
            foreach (var button in new[] { install, toggle, uninstall, saveSettings, updateAll })
                button.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is PluginValidationException or IOException or HttpRequestException or TaskCanceledException
                                           or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                                           or KeyNotFoundException)
            {
                error.Text = ex.Message;
                status.Text = "";
                AppLog.Warn("marketplace", ex.Message);
            }
            finally
            {
                busy.IsVisible = false;
                foreach (var button in new[] { install, toggle, uninstall, saveSettings, updateAll })
                    button.IsEnabled = true;
            }
            Rebuild();
        }

        install.Click += async (_, _) => await Guarded(async () =>
        {
            if (selected?.Available is { } entry)
                await InstallEntryAsync(entry);
        });
        updateAll.Click += async (_, _) => await Guarded(async () =>
        {
            // Una a una: si una falla (o vuelve a su versión anterior), las demás siguen.
            var failures = new List<string>();
            var pending = all.Where(r => r.HasUpdate).Select(r => r.Available!).ToList();
            foreach (var entry in pending)
            {
                try
                {
                    await InstallEntryAsync(entry);
                }
                catch (Exception ex) when (ex is PluginValidationException or IOException or HttpRequestException or TaskCanceledException
                                               or UnauthorizedAccessException or InvalidDataException)
                {
                    AppLog.Warn("marketplace", $"{entry.Id}: {ex.Message}");
                    failures.Add($"{entry.Name}: {ex.Message}");
                }
            }
            status.Text = $"{pending.Count - failures.Count} de {pending.Count} actualizados.";
            if (failures.Count > 0)
                throw new PluginValidationException(string.Join("\n", failures));
        });
        toggle.Click += async (_, _) => await Guarded(async () =>
        {
            if (selected?.BuiltIn is { } ext)
            {
                var dependents = Dependents(ext.Manifest.Id);
                if (ext.Enabled && dependents.Length > 0 &&
                    !await vm.Dialogs.ConfirmAsync("Desactivar extensión",
                        $"Al desactivar «{ext.Manifest.Name}» también se detienen: {dependents}. ¿Continuar?", "Desactivar"))
                    return;
                await extensions.SetEnabledAsync(ext.Manifest.Id, !ext.Enabled);
                status.Text = $"«{ext.Manifest.Name}» {(ext.Enabled ? "desactivada" : "activada")}.";
                return;
            }
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
            var folder = machine.PluginRepository;
            if (folder is { Length: > 0 })
            {
                if (await vm.Dialogs.ConfirmAsync("Repositorio local", $"Se usa además la carpeta local «{folder}». ¿Dejar de usarla?", "Quitar carpeta"))
                    folder = null;
            }
            else if (await vm.Dialogs.ConfirmAsync("Repositorio local", "¿Quieres usar además una carpeta local con paquetes .lsplugin firmados?", "Elegir carpeta"))
            {
                folder = await vm.Dialogs.PickFolderAsync("Repositorio local de plugins");
            }
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
        filter.SelectionChanged += (_, _) =>
        {
            if (filter.SelectedIndex >= 0)
                Rebuild();
        };
        list.SelectionChanged += (_, _) => ShowSelected();
        EventHandler onChanged = (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        manager.Changed += onChanged;
        extensions.Changed += onChanged;
        window.Closed += (_, _) =>
        {
            manager.Changed -= onChanged;
            extensions.Changed -= onChanged;
        };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var topRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { updateAll, reload, origin } };
        DockPanel.SetDock(topRight, Dock.Right);
        top.Children.Add(topRight);
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { search, filter } });
        var detailPane = new ScrollViewer
        {
            Margin = new Thickness(16, 0, 0, 0),
            Content = new StackPanel
            {
                Spacing = 14,
                Children = { detailPanel, settingsPanel, new WrapPanel { Children = { install, toggle, saveSettings, uninstall } } },
            },
        };
        foreach (var button in new[] { install, toggle, saveSettings })
            button.Margin = new Thickness(0, 0, 8, 0);
        var body = new DockPanel { Margin = new Thickness(16) };
        var footer = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0), Children = { source, developer, error, status } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(busy, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(list, Dock.Left);
        body.Children.Add(top);
        body.Children.Add(busy);
        body.Children.Add(footer);
        body.Children.Add(list);
        body.Children.Add(detailPane);

        var close = new Button { Content = "Cerrar", IsCancel = true };
        close.Click += (_, _) => window.Close();
        window.Content = Layout(body, fromFile, close);
        window.Opened += async (_, _) => await Guarded(LoadCatalogAsync);
        Rebuild();
        await window.ShowDialog(owner);
    }

    private static Border Badge(string text, string color) => new()
    {
        Background = new SolidColorBrush(Color.Parse(color), 0.16),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1),
        Margin = new Thickness(0, 0, 6, 0),
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(color)) },
    };

    private static StackPanel Section(string title, string body) => new()
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
            new SelectableTextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
        },
    };

    private static bool Matches(Row r, string text)
    {
        bool Has(string? s) => s?.Contains(text, StringComparison.CurrentCultureIgnoreCase) == true;
        var m = r.Installed?.Manifest;
        var e = r.Available;
        return Has(r.Name) || Has(r.Author) || Has(r.Category) || Has(r.Description) || (m?.Tags ?? e?.Tags ?? []).Any(Has);
    }

    private static string ExtensionPermissionText(string permission) => permission switch
    {
        ExtensionPermissions.Studio => "Proyecto, escenas y gráficos",
        ExtensionPermissions.Secrets => "Credenciales cifradas (claves de plataformas)",
        ExtensionPermissions.Network => "Conexiones de red",
        ExtensionPermissions.Audio => "Reproducir audio",
        _ => permission,
    };
}
