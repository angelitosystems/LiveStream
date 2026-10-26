using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LiveStream.Desktop.Studio;
using LiveStream.Desktop.ViewModels;
using LiveStream.Desktop.Views.Dialogs;

namespace LiveStream.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly GridLength[] _defaultDockColumns;
    private readonly GridLength _defaultDockHeight;
    private CanvasEditorController? _editor;

    public MainWindow()
    {
        InitializeComponent();
        _defaultDockColumns = Docks.ColumnDefinitions.Select(c => c.Width).ToArray();
        _defaultDockHeight = Workspace.RowDefinitions[3].Height;
        Opened += async (_, _) => await Vm.StartAsync();
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
    }

    /// <summary>Fuera del modo estudio la vista previa ocupa todo el ancho; dentro, se reparte con el programa.</summary>
    private void ApplyStudioLayout(bool studio)
    {
        CanvasGrid.ColumnDefinitions[2].Width = studio ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _editor?.FitView();
    }

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

    private async void OnTransitionSettings(object? sender, RoutedEventArgs e) =>
        await Guard("Transición", () => ProductionDialogs.ShowTransitionSettingsAsync(Vm));

    private async void OnSceneTransition(object? sender, RoutedEventArgs e) => await Guard("Transición de la escena", async () =>
    {
        if (Vm.SelectedScene is { } row)
            await ProductionDialogs.ShowTransitionSettingsAsync(Vm, row.Id);
    });

    private async void OnEditGraphic(object? sender, RoutedEventArgs e) => await Guard("Gráfico", async () =>
    {
        if (Vm.SelectedGraphic is { } row)
            await ProductionDialogs.ShowGraphicEditorAsync(Vm, row.Id);
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

    /// <summary>Doble clic: diseñador para los diseños libres y formulario para las plantillas.</summary>
    private async void OnGraphicDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Doble clic sobre AIRE o los controles de la fila no abre el editor.
        if (e.Source is not Visual v || v.FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>(includeSelf: true) is not null ||
            v.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (Vm.SelectedGraphic is not { } row)
            return;
        if (row.Kind == Domain.Graphics.GraphicKind.Custom)
            await Guard("Diseño", () => GraphicDesignerDialog.ShowAsync(Vm, row.Id));
        else
            await Guard("Gráfico", () => ProductionDialogs.ShowGraphicEditorAsync(Vm, row.Id));
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

    private async void OnDestinations(object? sender, RoutedEventArgs e) => await Guard("Destinos", () => DestinationsDialog.ShowAsync(Vm));

    private async void OnSettings(object? sender, RoutedEventArgs e) => await Guard("Ajustes", () => StudioDialogs.ShowSettingsAsync(Vm));

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
        for (var i = 0; i < _defaultDockColumns.Length; i++)
            Docks.ColumnDefinitions[i].Width = _defaultDockColumns[i];
        Workspace.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        Workspace.RowDefinitions[3].Height = _defaultDockHeight;
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

    private async void OnShortcuts(object? sender, RoutedEventArgs e) => await Vm.Dialogs.InfoAsync("Atajos de teclado",
        "Ctrl+Z / Ctrl+Y: deshacer / rehacer\n" +
        "Ctrl+S: guardar · Ctrl+O: abrir proyecto\n" +
        "Ctrl+1…9: cambiar a la escena N\n" +
        "Supr: eliminar · F2: renombrar fuente\n" +
        "Flechas: mover 1 px (Shift: 10 px)\n" +
        "Ctrl+E: editar transformación · Ctrl+F: ajustar a pantalla · Ctrl+R: restablecer\n" +
        "Ctrl+0: ajustar lienzo · Ctrl++ / Ctrl+-: zoom\n\n" +
        "En el lienzo: arrastra para mover; los tiradores escalan (Shift deforma, Alt recorta); " +
        "Ctrl+clic añade a la selección; rueda para zoom; botón central para desplazar.");

    private async void OnAbout(object? sender, RoutedEventArgs e) => await Vm.Dialogs.InfoAsync("Acerca de LiveStream",
        "LiveStream AI — estudio de producción y transmisión en vivo.\n\n" +
        "Motor de video: libobs 32.2.2 (OBS Project), licencia GPLv2 o posterior.\n" +
        "Interfaz: Avalonia UI. Proyecto en desarrollo (Fase 2).");

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
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
