using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using StreamPilot.Application.Abstractions;
using StreamPilot.Application.Sources;
using StreamPilot.Application.Studio;
using StreamPilot.Desktop.Components;
using StreamPilot.Desktop.ViewModels;
using StreamPilot.Domain.Entities;
using StreamPilot.Domain.ValueObjects;
using StreamPilot.Native.Engine;

namespace StreamPilot.Desktop.Views.Dialogs;

/// <summary>Diálogos del estudio. Todas las modificaciones salen como comandos del <c>CommandExecutor</c>.</summary>
public static class StudioDialogs
{
    private static Window NewWindow(string title, double width, double height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        MinWidth = 420,
        MinHeight = 300,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
    };

    private static Grid Layout(Control body, params Button[] buttons)
    {
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 10) };
        foreach (var b in buttons)
        {
            b.MinWidth = 96;
            footer.Children.Add(b);
        }
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(body);
        var footerBorder = new Border { Child = footer, BorderBrush = new SolidColorBrush(Color.Parse("#FF2F343B")), BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(footerBorder, 1);
        grid.Children.Add(footerBorder);
        return grid;
    }

    private static Button Primary(string text)
    {
        var b = new Button { Content = text, IsDefault = true };
        b.Classes.Add("primary");
        return b;
    }

    // ---- Añadir fuente ---------------------------------------------------------------------------

    public static async Task ShowAddSourceAsync(MainViewModel vm)
    {
        if (vm.EditSceneId is not { } sceneId || vm.Dialogs.Owner is not { } owner)
            return;

        var window = NewWindow("Añadir fuente", 760, 520);
        var available = SourceCatalog.Entries.Where(e => SourceTypeMap.IsAvailable(e.Kind)).ToList();
        var kinds = new ListBox { ItemsSource = available.Select(e => e.Label).ToList(), SelectedIndex = 0, Classes = { "studioList" } };
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        var name = new TextBox();
        var deviceLabel = new TextBlock { Text = "Dispositivo" };
        var devices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var createNew = new RadioButton { Content = "Crear nueva", IsChecked = true, GroupName = "mode" };
        var useExisting = new RadioButton { Content = "Usar una fuente existente (comparte la captura, no la duplica)", GroupName = "mode" };
        var existing = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };

        var allDevices = vm.EngineReady ? await vm.Engine.ListDevicesAsync(CancellationToken.None) : [];
        List<CaptureDevice> currentDevices = [];
        List<Source> currentExisting = [];

        void OnKindChanged()
        {
            var entry = available[Math.Max(0, kinds.SelectedIndex)];
            description.Text = entry.Description;
            name.Text = NextSourceName(vm, entry.DefaultName);
            var deviceKind = DeviceKindFor(entry.Kind);
            currentDevices = deviceKind is { } k ? allDevices.Where(d => d.Kind == k).ToList() : [];
            // win-capture nombra los monitores como ": 1920x1080 @ 0,0 (Monitor principal)" cuando no tienen nombre propio.
            devices.ItemsSource = currentDevices.Select(d => d.Name.TrimStart(':', ' ')).ToList();
            devices.SelectedIndex = currentDevices.Count > 0 ? 0 : -1;
            deviceLabel.IsVisible = devices.IsVisible = deviceKind is not null;
            currentExisting = vm.Project.Sources.Where(s => s.Kind == entry.Kind).ToList();
            existing.ItemsSource = currentExisting.Select(s => s.Name).ToList();
            existing.SelectedIndex = currentExisting.Count > 0 ? 0 : -1;
            useExisting.IsEnabled = currentExisting.Count > 0;
            if (!useExisting.IsEnabled)
                createNew.IsChecked = true;
        }
        kinds.SelectionChanged += (_, _) => OnKindChanged();
        OnKindChanged();

        var form = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(16),
            Children =
            {
                description, createNew,
                new TextBlock { Text = "Nombre" }, name, deviceLabel, devices,
                useExisting, existing, error,
            },
        };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*") };
        body.Children.Add(new Border { Child = kinds, Padding = new Thickness(8), Background = new SolidColorBrush(Color.Parse("#FF17191D")) });
        var right = new ScrollViewer { Content = form };
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        var ok = Primary("Añadir");
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        Guid? createdSource = null;
        ok.Click += async (_, _) =>
        {
            var entry = available[Math.Max(0, kinds.SelectedIndex)];
            if (useExisting.IsChecked == true && existing.SelectedIndex >= 0)
            {
                var source = currentExisting[existing.SelectedIndex];
                var result = await vm.Run(new AddExistingSourceCommand(sceneId, source.Id));
                if (result.Success) window.Close(); else error.Text = result.Error;
                return;
            }
            var settings = new JsonObject();
            if (devices.SelectedIndex >= 0 && devices.SelectedIndex < currentDevices.Count)
                settings[DeviceSettingKey(entry.Kind)!] = currentDevices[devices.SelectedIndex].Id;
            if (entry.Kind == SourceKind.Text)
                settings["text"] = "Texto";
            if (entry.Kind == SourceKind.ColorSource)
            {
                settings["width"] = vm.Project.Canvas.Width;
                settings["height"] = vm.Project.Canvas.Height;
            }
            var command = new AddSourceCommand(sceneId, string.IsNullOrWhiteSpace(name.Text) ? entry.DefaultName : name.Text.Trim(), entry.Kind, settings.ToJsonString());
            var added = await vm.Run(command);
            if (!added.Success)
            {
                error.Text = added.Error;
                return;
            }
            createdSource = command.SourceId;
            vm.SelectFromCanvas(command.ItemId, false);
            window.Close();
        };
        window.Content = Layout(body, cancel, ok);
        await window.ShowDialog(owner);

        if (createdSource is { } created)
            await ShowPropertiesAsync(vm, created, justCreated: true);
    }

    private static string NextSourceName(MainViewModel vm, string baseName)
    {
        if (vm.Project.Sources.All(s => s.Name != baseName))
            return baseName;
        for (var i = 2; ; i++)
            if (vm.Project.Sources.All(s => s.Name != $"{baseName} {i}"))
                return $"{baseName} {i}";
    }

    private static CaptureDeviceKind? DeviceKindFor(SourceKind kind) => kind switch
    {
        SourceKind.VideoCaptureDevice => CaptureDeviceKind.Camera,
        SourceKind.AudioInput => CaptureDeviceKind.Microphone,
        SourceKind.AudioOutput => CaptureDeviceKind.AudioOutput,
        SourceKind.DisplayCapture => CaptureDeviceKind.Display,
        _ => null,
    };

    private static string? DeviceSettingKey(SourceKind kind) => kind switch
    {
        SourceKind.VideoCaptureDevice => "video_device_id",
        SourceKind.AudioInput or SourceKind.AudioOutput => "device_id",
        SourceKind.DisplayCapture => "monitor_id",
        _ => null,
    };

    // ---- Propiedades -----------------------------------------------------------------------------

    public static async Task ShowPropertiesAsync(MainViewModel vm, Guid sourceId, bool justCreated = false)
    {
        if (vm.Dialogs.Owner is not { } owner || vm.Project.FindSource(sourceId) is not { } source)
            return;
        if (!vm.EngineReady)
        {
            await vm.Dialogs.InfoAsync("Propiedades", "Las propiedades se leen del motor, que no está iniciado.");
            return;
        }

        var original = await vm.Engine.GetSourceSettingsAsync(sourceId, CancellationToken.None);
        var settings = JsonNode.Parse(original) as JsonObject ?? [];
        var window = NewWindow($"Propiedades — {source.Name}", 680, 640);
        var nameBox = new TextBox { Text = source.Name };
        var editor = new PropertyEditor();
        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };

        async Task Rebuild()
        {
            var properties = await vm.Engine.GetSourcePropertiesAsync(sourceId, CancellationToken.None);
            editor.Build(properties, settings, vm.Dialogs);
        }

        // Vista previa en vivo: los cambios van al motor con un pequeño retardo; el dominio solo cambia al aceptar.
        CancellationTokenSource? pending = null;
        editor.SettingsChanged += (current, structural) =>
        {
            pending?.Cancel();
            var cts = pending = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(150, cts.Token);
                    await vm.Engine.UpdateSourceSettingsAsync(sourceId, current.ToJsonString(), CancellationToken.None);
                    if (structural)
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(Rebuild);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => status.Text = ex.Message);
                }
            });
        };
        await Rebuild();

        var body = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = $"{SourceCatalog.Label(source.Kind)}", Classes = { "muted" } },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("180,*"),
                        ColumnSpacing = 12,
                        Children = { new TextBlock { Text = "Nombre", VerticalAlignment = VerticalAlignment.Center }, WithColumn(nameBox, 1) },
                    },
                    editor,
                    status,
                },
            },
        };

        var accepted = false;
        var ok = Primary("Aceptar");
        var cancel = new Button { Content = justCreated ? "Usar valores por defecto" : "Cancelar", IsCancel = true };
        ok.Click += (_, _) =>
        {
            accepted = true;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.Content = Layout(body, cancel, ok);
        await window.ShowDialog(owner);
        pending?.Cancel();

        if (accepted)
        {
            var json = editor.Settings.ToJsonString();
            if (json != original)
                await vm.Run(new UpdateSourceSettingsCommand(sourceId, json, previousSettingsJson: original));
            if (!string.IsNullOrWhiteSpace(nameBox.Text) && nameBox.Text.Trim() != source.Name)
                await vm.Run(new RenameSourceCommand(sourceId, nameBox.Text.Trim()));
        }
        else
        {
            await vm.Engine.UpdateSourceSettingsAsync(sourceId, original, CancellationToken.None);
        }
    }

    private static Control WithColumn(Control c, int column)
    {
        Grid.SetColumn(c, column);
        return c;
    }

    // ---- Transformación --------------------------------------------------------------------------

    public static async Task ShowTransformAsync(MainViewModel vm, Guid itemId)
    {
        if (vm.Dialogs.Owner is not { } owner)
            return;
        var (_, item) = vm.Project.FindItem(itemId);
        var t = item.Transform;
        NumericUpDown Num(double value, double min, double max, double step, string format = "0.##") =>
            new() { Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, FormatString = format, Width = 150 };

        var x = Num(t.X, -100_000, 100_000, 1);
        var y = Num(t.Y, -100_000, 100_000, 1);
        var sx = Num(t.ScaleX * 100, -10_000, 10_000, 1);
        var sy = Num(t.ScaleY * 100, -10_000, 10_000, 1);
        var rot = Num(t.RotationDegrees, -360, 360, 1);
        var cl = Num(t.CropLeft, 0, 100_000, 1, "0");
        var ct = Num(t.CropTop, 0, 100_000, 1, "0");
        var cr = Num(t.CropRight, 0, 100_000, 1, "0");
        var cb = Num(t.CropBottom, 0, 100_000, 1, "0");
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("160,Auto,24,160,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"), RowSpacing = 8, Margin = new Thickness(16) };
        void Add(string label, Control control, int row, int col)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row); Grid.SetColumn(text, col);
            Grid.SetRow(control, row); Grid.SetColumn(control, col + 1);
            grid.Children.Add(text);
            grid.Children.Add(control);
        }
        Add("Posición X (px)", x, 0, 0); Add("Posición Y (px)", y, 0, 3);
        Add("Escala X (%)", sx, 1, 0); Add("Escala Y (%)", sy, 1, 3);
        Add("Giro (grados)", rot, 2, 0);
        Add("Recorte izquierda", cl, 3, 0); Add("Recorte superior", ct, 3, 3);
        Add("Recorte derecha", cr, 4, 0); Add("Recorte inferior", cb, 4, 3);

        var window = NewWindow("Editar transformación", 680, 360);
        var ok = Primary("Aplicar");
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        ok.Click += async (_, _) =>
        {
            double V(NumericUpDown n) => (double)(n.Value ?? 0);
            var updated = new StreamPilot.Domain.ValueObjects.Transform(V(x), V(y), V(sx) / 100, V(sy) / 100, V(rot), (int)V(cl), (int)V(ct), (int)V(cr), (int)V(cb));
            var result = await vm.Run(new SetItemTransformCommand(itemId, updated));
            if (result.Success) window.Close(); else error.Text = result.Error;
        };
        window.Content = Layout(new StackPanel { Children = { grid, new Border { Child = error, Padding = new Thickness(16, 0) } } }, cancel, ok);
        await window.ShowDialog(owner);
    }

    // ---- Ajustes ---------------------------------------------------------------------------------

    public static async Task ShowSettingsAsync(MainViewModel vm)
    {
        if (vm.Dialogs.Owner is not { } owner)
            return;
        var project = vm.Project;
        var encoders = vm.EngineReady ? await vm.Engine.ListVideoEncodersAsync(CancellationToken.None) : [];
        var encoderIds = new List<string> { "auto" }.Concat(encoders.Select(e => e.Id)).ToList();
        var encoderNames = new List<string> { "Automático (el mejor disponible)" }.Concat(encoders.Select(e => $"{e.Name} — {e.Codec.ToUpperInvariant()}")).ToList();
        ComboBox EncoderBox(string current) => new() { ItemsSource = encoderNames, SelectedIndex = Math.Max(0, encoderIds.IndexOf(current)), HorizontalAlignment = HorizontalAlignment.Stretch };
        NumericUpDown Num(int value, int min, int max, int step) => new() { Value = value, Minimum = min, Maximum = max, Increment = step, FormatString = "0", Width = 160, HorizontalAlignment = HorizontalAlignment.Left };

        // Transmisión
        var server = new TextBox { Text = project.Stream.Server, Watermark = "rtmp://servidor/app" };
        var hasKey = await vm.Session.HasStreamKeyAsync();
        var key = new TextBox { PasswordChar = '•', Watermark = hasKey ? "Guardada (deja vacío para conservarla)" : "Clave de transmisión" };
        var streamEnc = EncoderBox(project.Stream.EffectiveEncoder.VideoEncoderId);
        var streamVideo = Num(project.Stream.EffectiveEncoder.VideoBitrateKbps, 200, 150_000, 500);
        var streamAudio = Num(project.Stream.EffectiveEncoder.AudioBitrateKbps, 32, 512, 32);
        var keyframe = Num(project.Stream.EffectiveEncoder.KeyframeIntervalSec, 0, 20, 1);

        // Grabación
        var folder = new TextBox { Text = project.Recording.Folder, Watermark = Path.GetDirectoryName(vm.Session.NextRecordingPath(DateTime.Now)) };
        var browse = new Button { Content = "Examinar…" };
        browse.Click += async (_, _) => { if (await vm.Dialogs.PickFolderAsync("Carpeta de grabación") is { } f) folder.Text = f; };
        var container = new ComboBox { ItemsSource = RecordingPreferences.SupportedContainers, SelectedItem = project.Recording.Container };
        var recEnc = EncoderBox(project.Recording.EffectiveEncoder.VideoEncoderId);
        var recVideo = Num(project.Recording.EffectiveEncoder.VideoBitrateKbps, 200, 150_000, 1000);

        // Video
        var width = Num(project.Canvas.Width, 2, 7680, 2);
        var height = Num(project.Canvas.Height, 2, 7680, 2);
        var fps = new ComboBox { ItemsSource = new[] { 24, 25, 30, 48, 50, 60 }, SelectedItem = project.Canvas.Fps };

        StackPanel Form(params (string Label, Control Control)[] rows)
        {
            var panel = new StackPanel { Spacing = 10, Margin = new Thickness(16) };
            foreach (var (label, control) in rows)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), ColumnSpacing = 12 };
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(WithColumn(control, 1));
                panel.Children.Add(row);
            }
            return panel;
        }

        var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { folder, WithColumn(browse, 1) } };
        var tabs = new TabControl
        {
            Margin = new Thickness(8),
            ItemsSource = new[]
            {
                new TabItem { Header = "Transmisión", Content = new StackPanel { Children = {
                    Form(("Servidor RTMP/RTMPS", server), ("Clave de transmisión", key), ("Codificador", streamEnc),
                        ("Bitrate de video (kb/s)", streamVideo), ("Bitrate de audio (kb/s)", streamAudio), ("Fotograma clave cada (s)", keyframe)),
                    new TextBlock { Margin = new Thickness(16, 0), TextWrapping = TextWrapping.Wrap, Classes = { "muted" },
                        Text = "La clave se guarda cifrada para tu usuario de Windows (DPAPI) y nunca se escribe en el proyecto ni en los registros. " +
                               "YouTube, Twitch, Facebook y otros servicios publican su URL RTMP/RTMPS y la clave en su panel de directo." } } } },
                new TabItem { Header = "Grabación", Content = Form(("Carpeta", folderRow), ("Formato", container), ("Codificador", recEnc), ("Bitrate de video (kb/s)", recVideo)) },
                new TabItem { Header = "Video", Content = new StackPanel { Children = {
                    Form(("Ancho del lienzo", width), ("Alto del lienzo", height), ("FPS", fps)),
                    new TextBlock { Margin = new Thickness(16, 0), TextWrapping = TextWrapping.Wrap, Classes = { "muted" },
                        Text = "Usa 1080 x 1920 para formato vertical. El lienzo se aplica al reiniciar StreamPilot (limitación de libobs)." } } } },
            },
        };

        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0) };
        var window = NewWindow("Ajustes", 720, 560);
        var ok = Primary("Guardar");
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        var canvasChanged = false;
        ok.Click += async (_, _) =>
        {
            int I(NumericUpDown n) => (int)(n.Value ?? 0);
            var stream = project.Stream with
            {
                Server = server.Text ?? string.Empty,
                Encoder = new EncoderSettings(encoderIds[Math.Max(0, streamEnc.SelectedIndex)], I(streamVideo), I(streamAudio), I(keyframe)),
            };
            var recording = new RecordingPreferences(folder.Text?.Trim() ?? string.Empty, container.SelectedItem as string ?? "mkv",
                new EncoderSettings(encoderIds[Math.Max(0, recEnc.SelectedIndex)], I(recVideo), project.Recording.EffectiveEncoder.AudioBitrateKbps));
            var result = await vm.Run(new SetOutputSettingsCommand(stream, recording));
            if (!result.Success)
            {
                error.Text = result.Error;
                return;
            }
            if (!string.IsNullOrEmpty(key.Text))
                await vm.Session.SetStreamKeyAsync(key.Text);

            var canvas = new CanvasSettings(I(width), I(height), fps.SelectedItem is int f ? f : 30);
            if (canvas != project.Canvas)
            {
                var canvasResult = await vm.Run(new SetCanvasCommand(canvas));
                if (!canvasResult.Success)
                {
                    error.Text = canvasResult.Error;
                    return;
                }
                canvasChanged = true;
            }
            window.Close();
        };
        window.Content = Layout(new DockPanel { Children = { WithDock(error, Dock.Bottom), tabs } }, cancel, ok);
        await window.ShowDialog(owner);

        if (canvasChanged && await vm.Dialogs.ConfirmAsync("Reiniciar", "El nuevo lienzo se aplica al reiniciar StreamPilot. ¿Reiniciar ahora?", "Reiniciar"))
            await vm.RelaunchWithProjectAsync(vm.Session.ProjectPath);
    }

    private static Control WithDock(Control c, Dock dock)
    {
        DockPanel.SetDock(c, dock);
        return c;
    }
}
