using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LiveStream.Application.Streaming;
using LiveStream.Desktop.ViewModels;
using LiveStream.Domain.Entities;
using static LiveStream.Desktop.Views.Dialogs.StudioDialogs;

namespace LiveStream.Desktop.Views.Dialogs;

/// <summary>
/// Destinos de emisión (multistream): lista con el estado en vivo de cada uno, inicio y parada por destino, editor
/// por plataforma con prueba de conexión y el registro de eventos del supervisor (caídas, reconexiones, errores).
/// </summary>
public static class DestinationsDialog
{
    private static Window? _open;

    public static Task ShowAsync(MainViewModel vm)
    {
        if (_open is not null)
        {
            _open.Activate();
            return Task.CompletedTask;
        }
        if (vm.Dialogs.Owner is not { } owner)
            return Task.CompletedTask;

        var window = NewWindow("Destinos de emisión", 940, 680);
        _open = window;
        var rows = new StackPanel { Spacing = 6 };
        var log = new StackPanel { Spacing = 2 };
        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };

        static IBrush Color(DestinationState s) => new SolidColorBrush(Avalonia.Media.Color.Parse(s switch
        {
            DestinationState.Live => "#FF30A46C",
            DestinationState.Reconnecting or DestinationState.Connecting => "#FFF5A524",
            DestinationState.Failed => "#FFE5484D",
            _ => "#FF9AA1AB",
        }));

        static string StateText(DestinationState s) => s switch
        {
            DestinationState.Live => "EN DIRECTO",
            DestinationState.Connecting => "CONECTANDO",
            DestinationState.Reconnecting => "RECONECTANDO",
            DestinationState.Failed => "CAÍDO",
            _ => "DETENIDO",
        };

        void Render()
        {
            rows.Children.Clear();
            var statuses = vm.Session.Destinations.Statuses.ToDictionary(s => s.Id);
            if (vm.Project.Destinations.Count == 0)
                rows.Children.Add(new TextBlock { Text = "No hay destinos. Pulsa «Añadir destino» para emitir a YouTube, Twitch, Facebook, un relay propio…", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });

            foreach (var d in vm.Project.Destinations)
            {
                statuses.TryGetValue(d.Id, out var s);
                var state = s?.State ?? DestinationState.Idle;
                var live = state is DestinationState.Live or DestinationState.Reconnecting or DestinationState.Connecting;

                var enabled = new CheckBox { IsChecked = d.Enabled, VerticalAlignment = VerticalAlignment.Center, IsEnabled = !live };
                ToolTip.SetTip(enabled, "Incluir en «Iniciar transmisión»");
                Avalonia.Automation.AutomationProperties.SetName(enabled, $"Incluir {d.Name} al iniciar la transmisión");
                enabled.IsCheckedChanged += async (_, _) => await vm.Run(new UpdateDestinationCommand(d with { Enabled = enabled.IsChecked == true }));

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = $"{d.Name}  ·  {PlatformCatalog.For(d.Platform).Name}", FontWeight = FontWeight.SemiBold });
                var detail = live && s is not null
                    ? $"{s.MeasuredKbps} kb/s enviados (codificador {s.EncoderKbps} kb/s{(s.SharedEncoder ? ", compartido" : "")}) · pérdidas {s.DropPercent:0.0}% · " +
                      $"en directo {s.Uptime:hh\\:mm\\:ss}{(s.Reconnections > 0 ? $" · {s.Reconnections} reconexión(es)" : "")}"
                    : s?.Error ?? (d.Encoder is { } own ? $"Codificador propio: {own.VideoEncoderId} a {own.VideoBitrateKbps} kb/s" : "Usa el codificador común");
                info.Children.Add(new TextBlock { Text = detail, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });

                var chip = new Border
                {
                    Background = Color(state), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = StateText(state), FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
                };

                var startStop = new Button { Content = live ? "Detener" : "Iniciar", MinWidth = 80, IsEnabled = vm.EngineReady };
                if (live)
                    startStop.Classes.Add("live");
                startStop.Click += async (_, _) =>
                {
                    try
                    {
                        if (live)
                        {
                            if (await vm.Dialogs.ConfirmAsync("Detener destino", $"¿Dejar de emitir a «{d.Name}»? El resto de destinos sigue.", "Detener", destructive: true))
                                await vm.Session.StopDestinationAsync(d.Id);
                        }
                        else if (await vm.Dialogs.ConfirmAsync("Emitir a un destino", $"Se emitirá en directo a «{d.Name}».", "Transmitir"))
                        {
                            await vm.Session.StartDestinationAsync(d.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        status.Text = ex.Message;
                    }
                    await vm.Session.Destinations.TickAsync(DateTime.UtcNow);
                };
                var edit = new Button { Content = "Editar", IsEnabled = !live };
                edit.Click += async (_, _) => await EditAsync(vm, d);
                var remove = new Button { Content = "Eliminar", IsEnabled = !live };
                remove.Click += async (_, _) =>
                {
                    if (await vm.Dialogs.ConfirmAsync("Eliminar destino", $"¿Eliminar «{d.Name}»? Se puede deshacer; la clave guardada se conserva hasta entonces.", "Eliminar", destructive: true))
                        await vm.Run(new RemoveDestinationCommand(d.Id, vm.Session.Destinations.IsLive));
                };

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { chip, startStop, edit, remove } };
                var row = new DockPanel();
                DockPanel.SetDock(enabled, Dock.Left);
                DockPanel.SetDock(buttons, Dock.Right);
                enabled.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(enabled);
                row.Children.Add(buttons);
                row.Children.Add(info);
                rows.Children.Add(new Border { Child = row, Padding = new Thickness(10), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#FF1E2126")) });
            }

            log.Children.Clear();
            foreach (var e in vm.Session.Destinations.Events.AsEnumerable().Reverse().Take(40))
            {
                var line = new SelectableTextBlock { Text = $"{e.Time:HH:mm:ss}  {e.Destination}: {e.Message}", FontSize = 12, TextWrapping = TextWrapping.Wrap };
                // Solo los errores llevan color propio; asignar null anularía el color heredado del tema y el texto no se vería.
                if (e.IsError)
                    line.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFF5A524"));
                log.Children.Add(line);
            }
            if (log.Children.Count == 0)
                log.Children.Add(new TextBlock { Text = "Sin eventos todavía.", Classes = { "muted" } });
        }

        var add = Primary("Añadir destino");
        add.IsDefault = false;
        add.Click += async (_, _) => await EditAsync(vm, null);

        void OnChanged(object? s, EventArgs e) => Dispatcher.UIThread.Post(Render);
        vm.Session.Destinations.Changed += OnChanged;
        vm.Executor.StateChanged += OnChanged;
        window.Closed += (_, _) =>
        {
            vm.Session.Destinations.Changed -= OnChanged;
            vm.Executor.StateChanged -= OnChanged;
            _open = null;
        };

        var body = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "«Iniciar transmisión» emite a la vez a todos los destinos marcados. Si uno falla o pierde la conexión, los demás siguen; " +
                               "libobs reintenta la conexión cada 5 s (hasta 20 veces). Los destinos con los mismos ajustes comparten codificador.",
                        Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
                    },
                    add, status, rows,
                    new TextBlock { Text = "Registro", FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) },
                    log,
                },
            },
        };
        var close = Primary("Cerrar");
        close.Click += (_, _) => window.Close();
        window.Content = Layout(body, close);
        Render();
        window.Show(owner);
        return Task.CompletedTask;
    }

    /// <summary>Crear (destination null) o editar un destino.</summary>
    private static async Task EditAsync(MainViewModel vm, StreamDestination? destination)
    {
        if (vm.Dialogs.Owner is not { } owner)
            return;
        var isNew = destination is null;
        var window = NewWindow(isNew ? "Nuevo destino" : $"Destino — {destination!.Name}", 640, 600);

        var platforms = PlatformCatalog.All.ToList();
        var platform = new ComboBox
        {
            ItemsSource = platforms.Select(p => p.Name).ToList(),
            SelectedIndex = Math.Max(0, platforms.FindIndex(p => p.Platform == (destination?.Platform ?? StreamPlatform.YouTube))),
            MinWidth = 260,
        };
        var help = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        var name = new TextBox { Text = destination?.Name ?? "" };
        var server = new TextBox { Text = destination?.Server ?? "", Watermark = "rtmp://… o rtmps://…" };
        var hasKey = destination is not null && await vm.Session.HasDestinationKeyAsync(destination.Id);
        var key = new TextBox { PasswordChar = '•', Watermark = hasKey ? "Guardada (deja vacío para conservarla)" : "Clave de transmisión" };
        Avalonia.Automation.AutomationProperties.SetName(key, "Clave de transmisión");

        var encoders = vm.EngineReady ? await vm.Engine.ListVideoEncodersAsync(CancellationToken.None) : [];
        var encoderIds = new List<string> { "auto" }.Concat(encoders.Select(e => e.Id)).ToList();
        var ownEncoder = new CheckBox { Content = "Codificador propio (no comparte con otros destinos)", IsChecked = destination?.Encoder is not null };
        var encoderBox = new ComboBox
        {
            ItemsSource = new List<string> { "Automático" }.Concat(encoders.Select(e => e.Name)).ToList(),
            SelectedIndex = Math.Max(0, encoderIds.IndexOf(destination?.Encoder?.VideoEncoderId ?? vm.Project.Stream.EffectiveEncoder.VideoEncoderId)),
            MinWidth = 260,
        };
        var bitrate = new NumericUpDown
        {
            Value = destination?.Encoder?.VideoBitrateKbps ?? vm.Project.Stream.EffectiveEncoder.VideoBitrateKbps,
            Minimum = 200, Maximum = 150_000, Increment = 250, FormatString = "0", Width = 160, HorizontalAlignment = HorizontalAlignment.Left,
        };
        void SyncEncoder() => encoderBox.IsEnabled = bitrate.IsEnabled = ownEncoder.IsChecked == true;
        ownEncoder.IsCheckedChanged += (_, _) => SyncEncoder();
        SyncEncoder();

        var previousDefault = "";
        void OnPlatform()
        {
            var entry = platforms[Math.Max(0, platform.SelectedIndex)];
            help.Text = entry.Help;
            if (string.IsNullOrWhiteSpace(server.Text) || server.Text == previousDefault)
                server.Text = entry.DefaultServer;
            previousDefault = entry.DefaultServer;
            if (string.IsNullOrWhiteSpace(name.Text) || platforms.Any(p => p.Name == name.Text))
                name.Text = entry.Platform is StreamPlatform.Custom or StreamPlatform.Relay ? name.Text : entry.Name;
        }
        platform.SelectionChanged += (_, _) => OnPlatform();
        previousDefault = platforms[Math.Max(0, platform.SelectedIndex)].DefaultServer;
        help.Text = platforms[Math.Max(0, platform.SelectedIndex)].Help;
        if (isNew)
            OnPlatform();

        var probeResult = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var probe = new Button { Content = "Probar conexión" };
        probe.Click += async (_, _) =>
        {
            probe.IsEnabled = false;
            probeResult.Text = "Probando…";
            var r = await ConnectivityProbe.TestAsync(server.Text ?? "", TimeSpan.FromSeconds(5));
            probeResult.Text = (r.Reachable ? "✓ " : "✗ ") + r.Message + (r.Reachable ? " La clave solo se comprueba al emitir." : "");
            probeResult.Foreground = r.Reachable ? new SolidColorBrush(Avalonia.Media.Color.Parse("#FF30A46C")) : Brushes.IndianRed;
            probe.IsEnabled = true;
        };
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };

        Grid Row(string label, Control control)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), ColumnSpacing = 12 };
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(WithColumn(control, 1));
            return g;
        }

        var body = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    Row("Plataforma", platform), help, Row("Nombre", name), Row("Servidor", server), Row("Clave", key),
                    new TextBlock { Text = "La clave se guarda cifrada para tu usuario de Windows (DPAPI), separada de la de los demás destinos; nunca va al proyecto ni a los registros.", Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { probe, probeResult } },
                    ownEncoder, Row("Codificador", encoderBox), Row("Bitrate de video (kb/s)", bitrate),
                    error,
                },
            },
        };

        var save = Primary("Guardar");
        save.Click += async (_, _) =>
        {
            var entry = platforms[Math.Max(0, platform.SelectedIndex)];
            EncoderSettings? encoder = ownEncoder.IsChecked == true
                ? vm.Project.Stream.EffectiveEncoder with { VideoEncoderId = encoderIds[Math.Max(0, encoderBox.SelectedIndex)], VideoBitrateKbps = (int)(bitrate.Value ?? 4500) }
                : null;
            var updated = destination is null
                ? StreamDestination.Create(name.Text?.Trim() ?? "", entry.Platform, server.Text?.Trim() ?? "", encoder)
                : destination with { Name = name.Text?.Trim() ?? "", Platform = entry.Platform, Server = server.Text?.Trim() ?? "", Encoder = encoder };
            var result = isNew
                ? await vm.Run(new AddDestinationCommand(updated))
                : await vm.Run(new UpdateDestinationCommand(updated));
            if (!result.Success)
            {
                error.Text = result.Error;
                return;
            }
            if (!string.IsNullOrEmpty(key.Text))
                await vm.Session.SetDestinationKeyAsync(updated.Id, key.Text.Trim());
            key.Text = "";
            window.Close();
        };
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        window.Content = Layout(body, cancel, save);
        await window.ShowDialog(owner);
    }
}
