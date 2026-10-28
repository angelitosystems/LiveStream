using Avalonia.Layout;
using Avalonia.Media;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LiveStream.Desktop.Components;
using LiveStream.Desktop.Studio;
using LiveStream.Desktop.ViewModels;
using LiveStream.Desktop.Views.Dialogs;

namespace LiveStream.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly GridLength _defaultDockHeight;
    private CanvasEditorController? _editor;
    private bool _syncingSourceSelection;

    public MainWindow()
    {
        InitializeComponent();
        DockHost.RegisterPanel(new StudioDockHost.PanelDefinition("scenes", "Escenas", "Icon.Scenes", ScenesPanel, ScenesHeader));
        DockHost.RegisterPanel(new StudioDockHost.PanelDefinition("sources", "Fuentes", "Icon.Layers", SourcesPanel, SourcesHeader));
        DockHost.RegisterPanel(new StudioDockHost.PanelDefinition("graphics", "Gráficos", "Icon.Graphics", GraphicsPanel, GraphicsHeader));
        DockHost.RegisterPanel(new StudioDockHost.PanelDefinition("mixer", "Mezclador de audio", "Icon.Mixer", MixerPanel, MixerHeader));
        DockHost.RegisterPanel(new StudioDockHost.PanelDefinition("chat", "Chat en vivo", "Icon.Chat", ChatPanel, ChatHeader));
        DockHost.RegisterPanel(new StudioDockHost.PanelDefinition("alerts", "Alertas", "Icon.Alerts", AlertsPanel, AlertsHeader));
        DockHost.RegisterPanel(new StudioDockHost.PanelDefinition("transitions", "Transiciones", "Icon.Transition", TransitionsPanel, TransitionsHeader));
        DockHost.RegisterPanel(new StudioDockHost.PanelDefinition("controls", "Controles", "Icon.Broadcast", ControlsPanel, ControlsHeader));
        DockHost.InitializeLayout();

        _defaultDockHeight = Workspace.RowDefinitions[3].Height;
        Opened += async (_, _) =>
        {
            await Vm.StartAsync();
            RestorePanelLayout();
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainViewModel vm || _editor is not null)
            return;
        _editor = new CanvasEditorController(vm);
        _editor.Attach(Preview);
        _editor.ItemDoubleClicked += async item =>
        {
            var (_, sceneItem) = vm.Project.FindItem(item);
            await StudioDialogs.ShowPropertiesAsync(vm, sceneItem.SourceId);
        };
        vm.CreateSceneRequested += async (_, _) => await Guard("Nueva escena", () => StudioDialogs.ShowCreateSceneAsync(Vm));
        AttachSourceListInteractions();
        new ProgramDisplayController(vm).Attach(Program);
        vm.StudioModeChanged += (_, _) => ApplyStudioLayout(vm.StudioMode);
        vm.Assistant.MessageAdded += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd(),
            Avalonia.Threading.DispatcherPriority.Background);
        vm.Assistant.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AssistantViewModel.IsOpen) && vm.Assistant.IsOpen)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => AssistantInput.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        };
        vm.TransitionNeedsSettings += async (_, type) =>
            await Guard("Transición", () => ProductionDialogs.ShowTransitionSettingsAsync(vm, null, type));
        ApplyStudioLayout(vm.StudioMode);
        vm.Alerts.RequestOpenDesigner += async () =>
            await Guard("Editor de alertas", () => AlertDesignerDialog.ShowAsync(vm, this));
        vm.Alerts.RequestOpenSoundBank += async () =>
            await Guard("Banco de sonidos", () => SoundBankDialog.ShowPickerAsync(vm, this));
    }

    /// <summary>Fuera del modo estudio la vista previa ocupa todo el ancho; dentro, se reparte con el programa.</summary>
    private void ApplyStudioLayout(bool studio)
    {
        CanvasGrid.ColumnDefinitions[2].Width = studio ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _editor?.FitView();
    }

    // ---- Panel de Fuentes: multi-selección y arrastre de archivos --------------------------------

    private void AttachSourceListInteractions()
    {
        SourceList.SelectionChanged += OnSourceSelectionChanged;
        SourceList.AddHandler(Avalonia.Input.Gestures.RightTappedEvent, OnSourceRightTapped);
        SceneList.AddHandler(Avalonia.Input.Gestures.RightTappedEvent, OnSceneRightTapped);
        DragDrop.SetAllowDrop(SourceList, true);
        SourceList.AddHandler(DragDrop.DragOverEvent, (sender, e) =>
        {
            e.DragEffects = e.DataTransfer.TryGetFiles() is { } files && files.Any()
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        });
        SourceList.AddHandler(DragDrop.DropEvent, async (sender, e) =>
        {
            var files = e.DataTransfer.TryGetFiles()?.ToList();
            if (files is not { Count: > 0 })
                return;
            var paths = files.Select(f => f.Path.LocalPath).Where(p => p.Length > 0).ToArray();
            if (paths.Length == 0)
                return;
            await Guard("Importar archivo", () => new CanvasDropController(Vm).ImportFilesAsync(paths, double.NaN, double.NaN));
        });
    }

    private void OnSourceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSourceSelection || sender is not ListBox list)
            return;
        var rows = new List<SourceRowViewModel>();
        if (list.SelectedItems is not { } items)
            return;
        foreach (var item in items)
            if (item is SourceRowViewModel vmRow)
                rows.Add(vmRow);
        if (Vm.SelectedItems.SetEquals(rows.Select(r => r.ItemId)))
            return;
        _syncingSourceSelection = true;
        Vm.SelectRows(rows);
        _syncingSourceSelection = false;
    }

    // Selección al hacer clic derecho sobre una fila sin estar seleccionada.

    private void OnSceneRightTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is ListBox list && e.Source is Visual v &&
            v.FindAncestorOfType<ListBoxItem>(true)?.DataContext is SceneRowViewModel row)
            list.SelectedItem = row;
    }

    private void OnSourceRightTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is ListBox list && e.Source is Visual v &&
            v.FindAncestorOfType<ListBoxItem>(true)?.DataContext is SourceRowViewModel row)
            list.SelectedItem = row;
    }

    private async void OnGoToScene(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedScene is { } row)
            await Guard("Cambiar de escena", () => Vm.SwitchToScene(row));
    }

    private async void OnSceneTransitionRow(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedScene is { } row)
            await Guard("Transición de la escena", () => ProductionDialogs.ShowTransitionSettingsAsync(Vm, row.Id));
    }

    private async void OnDuplicateSource(object? sender, RoutedEventArgs e) =>
        await Vm.DuplicateSourceCommand.ExecuteAsync(null);

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || FocusManager?.GetFocusedElement() is TextBox or NumericUpDown)
            return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Space when ctrl: Vm.Assistant.IsOpen = !Vm.Assistant.IsOpen; break;
            case Key.Z when ctrl: await Vm.UndoCommand.ExecuteAsync(null); break;
            case Key.Y when ctrl: await Vm.RedoCommand.ExecuteAsync(null); break;
            case Key.S when ctrl: await Vm.SaveProjectCommand.ExecuteAsync(null); break;
            case Key.O when ctrl: await Vm.OpenProjectCommand.ExecuteAsync(null); break;
            case Key.E when ctrl: OnEditTransform(null, new RoutedEventArgs()); break;
            case Key.F when ctrl: await Vm.FitToScreenCommand.ExecuteAsync(null); break;
            case Key.R when ctrl: await Vm.ResetTransformCommand.ExecuteAsync(null); break;
            case Key.D0 or Key.NumPad0 when ctrl: _editor?.FitView(); break;
            case Key.OemPlus or Key.Add when ctrl: _editor?.Zoom(1.25); break;
            case Key.OemMinus or Key.Subtract when ctrl: _editor?.Zoom(0.8); break;
            case >= Key.D1 and <= Key.D9 when ctrl: await Vm.SwitchToSceneIndex(e.Key - Key.D1); break;
            case Key.Delete: await Vm.RemoveSourceCommand.ExecuteAsync(null); break;
            case Key.F2: await Vm.RenameSourceCommand.ExecuteAsync(null); break;
            case Key.D when ctrl:
                // Duplicar selección: fuente si hay alguna, si no la escena activa.
                if (Vm.SelectedItems.Count > 0)
                    await Vm.DuplicateSourceCommand.ExecuteAsync(null);
                else if (Vm.SelectedScene is not null)
                    await Vm.DuplicateSceneCommand.ExecuteAsync(null);
                break;
            case Key.L when ctrl && Vm.SelectedSource is not null: await Vm.ToggleLockedCommand.ExecuteAsync(Vm.SelectedSource); break;
            case Key.H when ctrl && Vm.SelectedSource is not null: await Vm.ToggleVisibleCommand.ExecuteAsync(Vm.SelectedSource); break;
            case Key.G when ctrl: await Vm.CenterSelectionCommand.ExecuteAsync(null); break;
            case Key.Left: await Vm.Nudge(-step, 0); break;
            case Key.Right: await Vm.Nudge(step, 0); break;
            case Key.Up: await Vm.Nudge(0, -step); break;
            case Key.Down: await Vm.Nudge(0, step); break;
            default: e.Handled = false; break;
        }
    }

    private async void OnAddSource(object? sender, RoutedEventArgs e) => await Guard("Añadir fuente", () => StudioDialogs.ShowAddSourceAsync(Vm));

    private async void OnProperties(object? sender, RoutedEventArgs e) => await Guard("Propiedades", async () =>
    {
        if (Vm.SelectedSource is { } row)
            await StudioDialogs.ShowPropertiesAsync(Vm, row.SourceId);
    });

    private async void OnSourceDoubleTapped(object? sender, TappedEventArgs e) => OnProperties(sender, e);

    private async void OnEditTransform(object? sender, RoutedEventArgs e) => await Guard("Transformación", async () =>
    {
        if (Vm.SelectedSource is { } row)
            await StudioDialogs.ShowTransformAsync(Vm, row.ItemId);
    });

    private async void OnSourceFilters(object? sender, RoutedEventArgs e) => await Guard("Filtros", async () =>
    {
        if (Vm.SelectedSource is { } row)
            await ProductionDialogs.ShowFiltersAsync(Vm, row.SourceId);
        else
            Vm.StatusMessage = "Selecciona una fuente para ver sus filtros.";
    });

    private async void OnMixerFilters(object? sender, RoutedEventArgs e) => await Guard("Filtros", async () =>
    {
        if ((sender as Control)?.DataContext is MixerChannelViewModel channel)
            await ProductionDialogs.ShowFiltersAsync(Vm, channel.SourceId);
    });

    private async void OnMixerProperties(object? sender, RoutedEventArgs e) => await Guard("Propiedades de audio", async () =>
    {
        if ((sender as Control)?.DataContext is MixerChannelViewModel channel)
            await StudioDialogs.ShowPropertiesAsync(Vm, channel.SourceId);
        else if (Vm.Mixer.FirstOrDefault() is { } first)
            await StudioDialogs.ShowPropertiesAsync(Vm, first.SourceId);
    });

    private async void OnMixerChannelDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MixerChannelViewModel channel)
        {
            await Guard("Propiedades de audio", () => StudioDialogs.ShowPropertiesAsync(Vm, channel.SourceId));
            e.Handled = true;
        }
    }

    private async void OnMixerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is MixerChannelViewModel channel)
        {
            await Guard("Propiedades de audio", () => StudioDialogs.ShowPropertiesAsync(Vm, channel.SourceId));
            e.Handled = true;
            return;
        }

        await Guard("Ajustes de audio", () => StudioDialogs.ShowSettingsAsync(Vm, "Audio"));
        e.Handled = true;
    }

    private async void OnAudioSettingsClick(object? sender, RoutedEventArgs e) =>
        await Guard("Ajustes de audio", () => StudioDialogs.ShowSettingsAsync(Vm, "Audio"));

    private async void OnAddAudioSourceClick(object? sender, RoutedEventArgs e) =>
        await Guard("Añadir fuente de audio", () => StudioDialogs.ShowAddSourceAsync(Vm, "Audio"));

    private async void OnTransitionSettings(object? sender, RoutedEventArgs e) =>
        await Guard("Transición", () => ProductionDialogs.ShowTransitionSettingsAsync(Vm));

    private async void OnSceneTransition(object? sender, RoutedEventArgs e) => await Guard("Transición de la escena", async () =>
    {
        if (Vm.SelectedScene is { } row)
            await ProductionDialogs.ShowTransitionSettingsAsync(Vm, row.Id);
    });

    /// <summary>«Editar gráfico»: abre siempre Diseño Libre, el único editor de gráficos (las plantillas se convierten con confirmación).</summary>
    private async void OnEditGraphic(object? sender, RoutedEventArgs e) => await Guard("Diseño", async () =>
    {
        if (Vm.SelectedGraphic is { } row)
            await OpenDesignerAsync(row.Id);
        else
            Vm.StatusMessage = "Selecciona un gráfico para editarlo.";
    });

    /// <summary>Abre el editor de diseño libre; las plantillas se convierten primero (con confirmación).</summary>
    private async void OnDesignGraphic(object? sender, RoutedEventArgs e) => await Guard("Diseño", async () =>
    {
        if (Vm.SelectedGraphic is not { } row)
        {
            Vm.StatusMessage = "Selecciona un gráfico para diseñarlo.";
            return;
        }
        await OpenDesignerAsync(row.Id);
    });

    /// <summary>Doble clic: siempre el editor de diseño libre (único editor de gráficos).</summary>
    private async void OnGraphicDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Doble clic sobre AIRE o los controles de la fila no abre el editor.
        if (e.Source is not Visual v || v.FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>(includeSelf: true) is not null ||
            v.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (Vm.SelectedGraphic is not { } row)
            return;
        await OpenDesignerAsync(row.Id);
    }

    private async Task OpenDesignerAsync(Guid graphicId)
    {
        if (Vm.Project.FindGraphic(graphicId) is not { } graphic)
            return;
        if (graphic.Kind != Domain.Graphics.GraphicKind.Custom)
        {
            if (!await Vm.Dialogs.ConfirmAsync("Convertir en diseño libre",
                    $"«{graphic.Name}» usa la plantilla «{Application.Graphics.GraphicTemplates.DisplayName(graphic.Kind)}». " +
                    "Convertirlo en diseño libre permite editarlo pieza por pieza en el lienzo; el tipo pasa a ser «Diseño libre» (deshacer con Ctrl+Z). ¿Continuar?",
                    "Diseñar"))
                return;
            if (!await Vm.ConvertToDesignAsync(graphicId))
                return;
        }
        await GraphicDesignerDialog.ShowAsync(Vm, graphicId);
    }

    private async void OnConvertGraphic(object? sender, RoutedEventArgs e) => await Guard("Convertir en diseño libre", async () =>
    {
        if (Vm.SelectedGraphic is not { } row)
        {
            Vm.StatusMessage = "Selecciona un gráfico para convertirlo.";
            return;
        }
        if (!await Vm.Dialogs.ConfirmAsync("Convertir en diseño libre",
                "Las piezas de la plantilla pasan a ser elementos del editor de diseño libre; el tipo pasa a ser «Diseño libre» (deshacer con Ctrl+Z). ¿Continuar?",
                "Convertir"))
            return;
        if (await Vm.ConvertToDesignAsync(row.Id))
            Vm.StatusMessage = "Gráfico convertido en diseño libre.";
    });

    private async void OnExportGraphic(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedGraphic is { } row)
            await Guard("Exportar gráfico", () => Vm.ExportGraphicAsync(row.Id));
    }

    private async void OnAlertDesigner(object? sender, RoutedEventArgs e) =>
        await Guard("Editor de alertas", () => AlertDesignerDialog.ShowAsync(Vm, this));

    private async void OnSoundBank(object? sender, RoutedEventArgs e) =>
        await Guard("Banco de sonidos", () => SoundBankDialog.ShowPickerAsync(Vm, this));

    private async void OnResourceLibrary(object? sender, RoutedEventArgs e) =>
        await Guard("Biblioteca de recursos", () => ResourceLibraryDialog.ShowBrowserAsync(Vm));

    private async void OnMarketplace(object? sender, RoutedEventArgs e) => await Guard("Marketplace", () => MarketplaceDialog.ShowAsync(Vm));

    private async void OnBrandKit(object? sender, RoutedEventArgs e) => await Guard("Kit de marca", () => ProductionDialogs.ShowBrandKitAsync(Vm));

    private async void OnAddGraphicFromTemplate(object? sender, RoutedEventArgs e) =>
        await Guard("Plantillas", () => ProductionDialogs.ShowTemplatePickerAsync(Vm));

    private void OnPerformance(object? sender, RoutedEventArgs e) => PerformanceWindow.Show(Vm);

    /// <summary>Busca, descarga y verifica una actualización firmada; el instalador se ejecuta al cerrar la app.</summary>
    private async void OnCheckUpdates(object? sender, RoutedEventArgs e) => await Guard("Actualizaciones", async () =>
    {
        if (Infrastructure.Updates.UpdateClient.EmbeddedPublicKey() is not { } key)
        {
            await Vm.Dialogs.InfoAsync("Actualizaciones",
                "Esta compilación no incluye la clave pública de publicación, así que no puede verificar actualizaciones. " +
                "Instala las versiones nuevas con su instalador.");
            return;
        }
        if (Vm.Machine.Load().UpdateFeedUrl is not { Length: > 0 } feed || !Uri.TryCreate(feed, UriKind.Absolute, out var feedUri))
        {
            await Vm.Dialogs.InfoAsync("Actualizaciones",
                $"No hay canal de actualizaciones configurado en este equipo («updateFeedUrl» en {Vm.Machine.Path}).");
            return;
        }
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var client = new Infrastructure.Updates.UpdateClient(http, key);
        Vm.StatusMessage = "Buscando actualizaciones…";
        var current = Infrastructure.Updates.UpdateClient.CurrentVersion;
        switch (await client.CheckAsync(feedUri, current, CancellationToken.None))
        {
            case Infrastructure.Updates.UpdateCheckResult.UpToDate up:
                Vm.StatusMessage = $"LiveStream {up.Version} está al día.";
                await Vm.Dialogs.InfoAsync("Actualizaciones", Vm.StatusMessage);
                return;
            case Infrastructure.Updates.UpdateCheckResult.Rejected rejected:
                Vm.StatusMessage = rejected.Reason;
                await Vm.Dialogs.InfoAsync("Actualización rechazada", rejected.Reason);
                return;
            case Infrastructure.Updates.UpdateCheckResult.Available available:
                var m = available.Manifest;
                if (!await Vm.Dialogs.ConfirmAsync("Actualización disponible",
                        $"LiveStream {m.Version} (tienes {current.ToString(3)}). {m.Notes}\n\nSe descargarán {m.Size / 1048576.0:0} MB, se verificará la firma y se instalará al cerrar LiveStream.",
                        "Descargar"))
                    return;
                if (Vm.IsStreaming || Vm.IsRecording)
                {
                    await Vm.Dialogs.InfoAsync("Actualizaciones", "Detén la transmisión y la grabación antes de actualizar.");
                    return;
                }
                var path = await client.DownloadAsync(m, Path.Combine(Path.GetTempPath(), "LiveStream", "updates"),
                    new Progress<double>(p => Vm.StatusMessage = $"Descargando actualización… {p:P0}"), CancellationToken.None);
                Vm.StatusMessage = "Actualización descargada y verificada.";
                if (!await Vm.Dialogs.ConfirmAsync("Instalar actualización", "La firma y el contenido son correctos. ¿Cerrar LiveStream e instalar ahora?", "Instalar"))
                    return;
                Process.Start(new ProcessStartInfo(path, $"/waitpid {Environment.ProcessId} /launch") { UseShellExecute = true });
                Close();
                return;
        }
    });

    private async void OnIntegrations(object? sender, RoutedEventArgs e) => await Guard("Integraciones", () => IntegrationsDialog.ShowAsync(Vm));

    private async void OnDestinations(object? sender, RoutedEventArgs e) => await Guard("Destinos", () => DestinationsDialog.ShowAsync(Vm));

    private async void OnSettings(object? sender, RoutedEventArgs e) => await Guard("Ajustes", () => StudioDialogs.ShowSettingsAsync(Vm));

    private async void OnTtsSettings(object? sender, RoutedEventArgs e) => await Guard("Lectura de chat (TTS)", () => ProductionDialogs.ShowTtsSettingsAsync(Vm));

    private async void OnMonetaryAlerts(object? sender, RoutedEventArgs e) => await Guard("Donaciones y recompensas", () => ProductionDialogs.ShowMonetaryAlertsAsync(Vm));

    /// <summary>Los manejadores async void no pueden propagar errores: se registran y se muestran.</summary>
    private async Task Guard(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Infrastructure.Diagnostics.AppLog.Error("ui", what, ex);
            Vm.StatusMessage = $"{what}: {ex.Message}";
            await Vm.Dialogs.InfoAsync(what, ex.Message);
        }
    }

    private void OnFitView(object? sender, RoutedEventArgs e) => _editor?.FitView();

    private void OnZoomIn(object? sender, RoutedEventArgs e) => _editor?.Zoom(1.25);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => _editor?.Zoom(0.8);

    private void OnResetLayout(object? sender, RoutedEventArgs e)
    {
        DockHost.ResetToDefault();
        Workspace.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        Workspace.RowDefinitions[3].Height = _defaultDockHeight;
    }

    // ---- Distribución de paneles (ajustes del equipo) ---------------------------------------------

    /// <summary>Restaura la altura de la fila de paneles del dock.</summary>
    private void RestorePanelLayout()
    {
        var layout = Vm.Machine.Load();
        if (layout.DockRowHeight is { Length: > 0 } height && GridLength.Parse(height) is { } rowHeight)
            Workspace.RowDefinitions[3].Height = rowHeight;
    }

    private void SavePanelLayout()
    {
        var height = Workspace.RowDefinitions[3].Height.ToString();
        var settings = Vm.Machine.Load();
        if (height == settings.DockRowHeight)
            return;
        Vm.Machine.Save(settings with { DockRowHeight = height });
    }

    private void OnOpenRecordings(object? sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(Vm.Session.NextRecordingPath(DateTime.Now))!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void OnOpenProjects(object? sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(Vm.Session.ProjectPath))!;
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void OnOpenLogs(object? sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Infrastructure.Diagnostics.AppLog.Directory) { UseShellExecute = true });

    private async void OnShortcuts(object? sender, RoutedEventArgs e)
    {
        if (Vm.Dialogs.Owner is not { } owner)
            return;

        var window = StudioDialogs.NewWindow("Atajos de teclado", 720, 560);

        Border CategoryCard(string title, params (string Key, string Description)[] shortcuts)
        {
            var rows = new StackPanel { Spacing = 8 };
            foreach (var (key, desc) in shortcuts)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*"), ColumnSpacing = 16 };
                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#FF232832")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#FF363C48")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(8, 3),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    Child = new TextBlock
                    {
                        Text = key,
                        FontFamily = (FontFamily)Avalonia.Application.Current!.FindResource("Sp.Mono")!,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.Parse("#FF3D8BFF")),
                    },
                };
                var text = new TextBlock
                {
                    Text = desc,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#FFE6E8EB")),
                };
                row.Children.Add(badge);
                row.Children.Add(StudioDialogs.WithColumn(text, 1));
                rows.Children.Add(row);
            }

            return new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FF161A22")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF232832")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontWeight = Avalonia.Media.FontWeight.SemiBold,
                            FontSize = 13,
                            Foreground = new SolidColorBrush(Color.Parse("#FF3D8BFF")),
                        },
                        rows,
                    },
                },
            };
        }

        var canvasTips = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF161A22")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF232832")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Interacciones directas en el Lienzo", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#FF3D8BFF")) },
                    new TextBlock { Text = "• Arrastrar elementos: mueve la posición en el lienzo\n• Tiradores: escalan el elemento (Mantener Mayús para deformar libremente, Alt para recortar bordes)\n• Ctrl + Clic: añade elementos a la selección múltiple\n• Rueda del ratón: zoom de la vista previa · Botón central: encuadre y desplazamiento panorámico\n• Arrastrar y soltar: suelta imágenes, videos o audio desde el Explorador directamente al lienzo o a la lista de fuentes", FontSize = 12, Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                },
            },
        };

        var content = new ScrollViewer
        {
            Padding = new Thickness(16),
            Content = new StackPanel
            {
                Children =
                {
                    CategoryCard("Proyecto y Edición",
                        ("Ctrl + Z", "Deshacer la última acción"),
                        ("Ctrl + Y", "Rehacer la última acción"),
                        ("Ctrl + S", "Guardar el proyecto actual"),
                        ("Ctrl + O", "Abrir un proyecto existente"),
                        ("Ctrl + Espacio", "Abrir o alternar el Asistente de IA")),
                    CategoryCard("Escenas y Fuentes",
                        ("Ctrl + 1 … 9", "Cambiar directamente a la escena N"),
                        ("Ctrl + D", "Duplicar la fuente o escena seleccionada"),
                        ("Supr", "Eliminar la fuente o escena seleccionada"),
                        ("F2", "Renombrar la fuente seleccionada"),
                        ("Ctrl + L", "Bloquear o desbloquear la fuente"),
                        ("Ctrl + H", "Mostrar u ocultar la fuente seleccionada"),
                        ("Ctrl + G", "Centrar la fuente en el lienzo")),
                    CategoryCard("Transformación y Navegación",
                        ("Flechas del teclado", "Mover la selección 1 px (Mayús + Flechas: 10 px)"),
                        ("Ctrl + E", "Abrir editor de transformación"),
                        ("Ctrl + F", "Ajustar la fuente al tamaño del lienzo"),
                        ("Ctrl + R", "Restablecer transformación"),
                        ("Ctrl + 0", "Ajustar vista del lienzo a la ventana"),
                        ("Ctrl + + / Ctrl + -", "Aumentar o reducir zoom de la vista previa")),
                    canvasTips,
                },
            },
        };

        var okBtn = StudioDialogs.Primary("Entendido");
        okBtn.Click += (_, _) => window.Close();

        window.Content = StudioDialogs.Layout(content, okBtn);
        await window.ShowDialog(owner);
    }

    private async void OnAbout(object? sender, RoutedEventArgs e)
    {
        if (Vm.Dialogs.Owner is not { } owner)
            return;

        var window = StudioDialogs.NewWindow("Acerca de LiveStream", 540, 480);
        var icon = new Glyph
        {
            Data = (StreamGeometry)Avalonia.Application.Current!.FindResource("Icon.Broadcast")!,
            Width = 48,
            Height = 48,
            Foreground = new SolidColorBrush(Color.Parse("#FF3D8BFF")),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };

        var titleBlock = new TextBlock
        {
            Text = "LiveStream AI Studio",
            FontSize = 20,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };

        var versionBlock = new TextBlock
        {
            Text = "Versión 0.9.0-preview · 64 bits",
            FontSize = 12,
            Classes = { "muted" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };

        var sysInfo = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1C2028")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF282D36")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Componentes del sistema", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 13 },
                    new TextBlock { Text = $"• Motor de video y composición: libobs 32.2.2 (OBS Project, GPLv2)", FontSize = 12 },
                    new TextBlock { Text = $"• Entorno de interfaz: Avalonia UI 11.2 (XAML multiplataforma)", FontSize = 12 },
                    new TextBlock { Text = $"• Runtime: .NET {Environment.Version} en {Environment.OSVersion}", FontSize = 12 },
                    new TextBlock { Text = $"• Hardware: {Environment.ProcessorCount} hilos lógicos · Modo DPI adaptable", FontSize = 12 },
                },
            },
        };

        var licenseBlock = new TextBlock
        {
            Text = "Diseñado para producción audiovisual y streaming en directo profesional con plugins, gráficos dinámicos y aceleración por GPU.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Classes = { "muted" },
            FontSize = 11,
            Margin = new Thickness(4, 10, 4, 0),
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        };

        var okBtn = StudioDialogs.Primary("Cerrar");
        okBtn.Click += (_, _) => window.Close();

        var content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 8,
            Children =
            {
                icon,
                titleBlock,
                versionBlock,
                sysInfo,
                licenseBlock,
            },
        };

        window.Content = StudioDialogs.Layout(new ScrollViewer { Content = content }, okBtn);
        await window.ShowDialog(owner);
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        try
        {
            SavePanelLayout();
        }
        catch
        {
            // La distribución de paneles no puede impedir el cierre.
        }
        if (DataContext is MainViewModel { IsStreaming: true } || DataContext is MainViewModel { IsRecording: true })
        {
            e.Cancel = true;
            _ = ConfirmCloseAsync();
        }
    }

    private bool _closeConfirmed;

    private async Task ConfirmCloseAsync()
    {
        if (_closeConfirmed)
            return;
        if (!await Vm.Dialogs.ConfirmAsync("Salir", "Hay una transmisión o grabación activa. Se detendrá al salir. ¿Salir igualmente?", "Salir", destructive: true))
            return;
        if (Vm.IsStreaming)
            await Vm.Engine.StopStreamAsync(CancellationToken.None);
        if (Vm.IsRecording)
            await Vm.Engine.StopRecordingAsync(CancellationToken.None);
        Vm.IsStreaming = Vm.IsRecording = false;
        _closeConfirmed = true;
        Close();
    }
}
