using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LiveStream.Application.Optimization;
using LiveStream.Application.Studio;
using LiveStream.Desktop.ViewModels;
using LiveStream.Native.Engine;
using static LiveStream.Desktop.Views.Dialogs.StudioDialogs;

namespace LiveStream.Desktop.Views.Dialogs;

/// <summary>
/// Rendimiento: hardware detectado, benchmark real, perfil recomendado (se aplica solo si el usuario lo autoriza),
/// estado del bitrate adaptativo y registro de auditoría con reversión.
/// </summary>
public static class PerformanceWindow
{
    private static Window? _open;

    public static void Show(MainViewModel vm)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        if (vm.Dialogs.Owner is not { } owner)
            return;

        var window = NewWindow("Rendimiento y optimización", 900, 760);
        _open = window;

        var hardwareText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Text = "Detectando hardware…" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var adaptive = new CheckBox { Content = "Bitrate adaptativo durante la emisión", IsChecked = vm.Optimizer.AdaptiveEnabled };
        var restore = new Button { Content = "Restaurar bitrate configurado" };
        var benchButton = Primary("Ejecutar benchmark");
        benchButton.IsDefault = false;
        var benchStatus = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        var benchTable = new StackPanel { Spacing = 2 };
        var recommendations = new StackPanel { Spacing = 8 };
        var log = new StackPanel { Spacing = 6 };

        HardwareInfo? hardware = null;
        IReadOnlyList<BenchmarkResult> results = [];

        TextBlock Section(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(0, 12, 0, 2) };

        void RenderRecommendations()
        {
            recommendations.Children.Clear();
            if (hardware is null)
                return;
            var plan = ProfileAdvisor.Advise(hardware, results, vm.Project);
            foreach (var item in plan.Items)
            {
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = item.Detail, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
                recommendations.Children.Add(new Border { Child = row, Padding = new Thickness(10), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.Parse("#FF1E2126")) });
            }

            var buttons = new WrapPanel();
            if (plan.Stream is not null || plan.Recording is not null)
            {
                var apply = Primary("Aplicar perfil de salida");
                apply.IsDefault = false;
                apply.Margin = new Thickness(0, 0, 8, 0);
                apply.Click += async (_, _) =>
                {
                    if (await vm.Dialogs.ConfirmAsync("Aplicar perfil", "Se cambiarán el codificador y el bitrate de la emisión y la grabación (a partir de la próxima). Podrás revertirlo desde el registro.", "Aplicar"))
                    {
                        await vm.Optimizer.ApplyOutputsAsync(plan);
                        RenderRecommendations();
                    }
                };
                buttons.Children.Add(apply);
            }
            if (plan.RenderAdapterIndex is { } adapter)
            {
                var name = hardware.Adapters.First(a => a.Index == adapter).Name;
                var change = new Button { Content = $"Renderizar con {name} y reiniciar", Margin = new Thickness(0, 0, 8, 0) };
                change.Click += async (_, _) =>
                {
                    if (!await vm.Dialogs.ConfirmAsync("Cambiar adaptador gráfico", $"LiveStream se reiniciará usando «{name}». Si el motor no arranca con él, volverá solo al adaptador anterior.", "Reiniciar"))
                        return;
                    var previous = vm.Machine.Load();
                    vm.Machine.Save(previous with { RenderAdapterIndex = adapter });
                    vm.Optimizer.Log(OptimizationEventKind.Applied, $"Adaptador gráfico: {name}", $"Antes: adaptador {previous.RenderAdapterIndex}. Se aplica al reiniciar.");
                    await vm.RelaunchWithProjectAsync(vm.Session.ProjectPath);
                };
                buttons.Children.Add(change);
            }
            if (plan.Canvas is { } canvas)
            {
                var lighter = new Button { Content = $"Usar {canvas.Width}x{canvas.Height} a {canvas.Fps} FPS y reiniciar" };
                lighter.Click += async (_, _) =>
                {
                    if (!await vm.Dialogs.ConfirmAsync("Cambiar lienzo", "La aplicación se reiniciará con el lienzo más ligero. Las fuentes conservan su posición en píxeles.", "Reiniciar"))
                        return;
                    if ((await vm.Run(new SetCanvasCommand(canvas))).Success)
                    {
                        vm.Optimizer.Log(OptimizationEventKind.Applied, "Lienzo más ligero", $"{canvas.Width}x{canvas.Height} a {canvas.Fps} FPS.");
                        await vm.RelaunchWithProjectAsync(vm.Session.ProjectPath);
                    }
                };
                buttons.Children.Add(lighter);
            }
            if (buttons.Children.Count > 0)
                recommendations.Children.Add(buttons);
        }

        void RenderLog()
        {
            log.Children.Clear();
            var events = vm.Optimizer.Events.Reverse().Take(60).ToList();
            if (events.Count == 0)
                log.Children.Add(new TextBlock { Text = "Sin decisiones todavía. Aquí aparece cada ajuste con su motivo.", Classes = { "muted" } });
            foreach (var e in events)
            {
                var color = e.Kind switch
                {
                    OptimizationEventKind.Warning => "#FFF5A524",
                    OptimizationEventKind.Adjusted => "#FF3D8BFF",
                    OptimizationEventKind.Applied => "#FF30A46C",
                    _ => "#FF9AA1AB",
                };
                var row = new DockPanel();
                if (e.RevertId is { } id && vm.Optimizer.CanRevert(id))
                {
                    var revert = new Button { Content = "Revertir", VerticalAlignment = VerticalAlignment.Top };
                    revert.Click += async (_, _) => await vm.Optimizer.RevertAsync(id);
                    DockPanel.SetDock(revert, Dock.Right);
                    row.Children.Add(revert);
                }
                row.Children.Add(new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{e.Time:HH:mm:ss}  {e.Title}", Foreground = new SolidColorBrush(Color.Parse(color)), FontWeight = FontWeight.SemiBold },
                        new SelectableTextBlock { Text = e.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                    },
                });
                log.Children.Add(row);
            }
        }

        void RenderStatus()
        {
            var s = vm.Optimizer.Status;
            status.Text = s.Active
                ? $"Emitiendo a {s.CurrentKbps} kb/s (objetivo {s.TargetKbps}). Pérdidas {s.DropRate:P1} · congestión {s.Congestion:P0} · render con retraso {s.RenderLagRate:P1} · descartes al codificar {s.EncoderSkipRate:P1}."
                : $"Sin emisión. Render con retraso {s.RenderLagRate:P1} · descartes al codificar {s.EncoderSkipRate:P1}." + (s.Note is null ? "" : $" {s.Note}");
            restore.IsEnabled = s.Active;
            benchButton.IsEnabled = vm.EngineReady && !vm.IsRecording && !vm.IsStreaming;
        }

        adaptive.IsCheckedChanged += (_, _) =>
        {
            vm.Optimizer.AdaptiveEnabled = adaptive.IsChecked == true;
            vm.Machine.Save(vm.Machine.Load() with { AdaptiveBitrate = vm.Optimizer.AdaptiveEnabled });
            vm.Optimizer.Log(OptimizationEventKind.Info, vm.Optimizer.AdaptiveEnabled ? "Bitrate adaptativo activado" : "Bitrate adaptativo desactivado", "Cambio manual.");
        };
        restore.Click += async (_, _) =>
        {
            await vm.Optimizer.RestoreTargetAsync(vm.Session.Destinations.Statuses);
            adaptive.IsChecked = false;
        };

        benchButton.Click += async (_, _) =>
        {
            if (hardware is null)
                return;
            var candidates = EncoderBenchmark.Candidates(hardware.Encoders).ToList();
            if (!await vm.Dialogs.ConfirmAsync("Benchmark",
                    $"Se grabará el programa actual {candidates.Count} veces durante unos 5 s (una por codificador) en una carpeta temporal que se borra al terminar. No uses el estudio mientras tanto.",
                    "Medir"))
                return;
            benchButton.IsEnabled = false;
            benchTable.Children.Clear();
            try
            {
                var bitrate = ProfileAdvisor.StreamBitrateFor(vm.Project.Canvas);
                results = await Task.Run(() => EncoderBenchmark.RunAsync(vm.Engine, candidates, bitrate, TimeSpan.FromSeconds(4),
                    Path.Combine(Path.GetTempPath(), "LiveStream", "benchmark"),
                    text => Dispatcher.UIThread.Post(() => benchStatus.Text = text), CancellationToken.None));
                foreach (var r in results)
                    benchTable.Children.Add(new TextBlock
                    {
                        FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
                        FontSize = 12,
                        Text = r.Ok
                            ? $"{(r.Healthy ? "✓" : "✗")} {r.EncoderName,-34} descartes {r.SkippedPercent,5:0.0}%  render {r.LaggedPercent,5:0.0}%  CPU {r.CpuPercent,5:0}%"
                            : $"✗ {r.EncoderName,-34} {r.Error}",
                    });
                benchStatus.Text = $"Medido con {vm.Project.Canvas.Width}x{vm.Project.Canvas.Height} a {vm.Project.Canvas.Fps} FPS y {bitrate} kb/s.";
                vm.Optimizer.Log(OptimizationEventKind.Info, "Benchmark", string.Join("; ", results.Select(r =>
                    r.Ok ? $"{r.EncoderName}: descartes {r.SkippedPercent:0.0}%, CPU {r.CpuPercent:0}%" : $"{r.EncoderName}: {r.Error}")));
                RenderRecommendations();
            }
            catch (Exception ex)
            {
                benchStatus.Text = ex.Message;
            }
            finally
            {
                RenderStatus();
            }
        };

        vm.Optimizer.Changed += OnChanged;
        void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RenderLog);
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RenderStatus());
        timer.Start();
        window.Closed += (_, _) =>
        {
            timer.Stop();
            vm.Optimizer.Changed -= OnChanged;
            _open = null;
        };

        var body = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 6,
                Children =
                {
                    Section("Equipo"), hardwareText,
                    Section("Benchmark de codificadores"),
                    new TextBlock { Text = "Codifica el programa real con cada codificador H.264 disponible y mide fotogramas descartados, retraso de render y CPU.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { benchButton, benchStatus } },
                    benchTable,
                    Section("Recomendaciones"),
                    new TextBlock { Text = "Nada se aplica sin tu autorización. Los cambios de codificador, resolución, FPS o GPU afectan a la próxima salida o requieren reiniciar.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                    recommendations,
                    Section("En directo"), adaptive,
                    new TextBlock { Text = "Si la red pierde fotogramas, baja el bitrate de la emisión sin cortarla y lo recupera poco a poco cuando la conexión se estabiliza (nunca por encima del configurado). Solo con codificadores que lo admiten.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                    status, restore,
                    Section("Registro de decisiones"), log,
                },
            },
        };
        var close = Primary("Cerrar");
        close.Click += (_, _) => window.Close();
        window.Content = Layout(body, close);
        RenderStatus();
        RenderLog();
        window.Show(owner);

        _ = Task.Run(async () =>
        {
            try
            {
                var info = await new HardwareProbe(vm.Engine).ProbeAsync(CancellationToken.None);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    hardware = info;
                    hardwareText.Text =
                        $"{info.CpuName} · {info.LogicalCores} hilos · {info.MemoryGb:0.#} GB de memoria\n" +
                        string.Join("\n", info.Adapters.Where(a => !a.IsSoftware).Select(a =>
                            $"GPU {a.Index}: {a.Name} ({a.Vendor}, {a.DedicatedMemoryBytes >> 20} MB){(a.Index == info.RenderAdapterIndex ? "  ← renderiza ahora" : "")}")) + "\n" +
                        "Codificadores: " + string.Join(", ", info.Encoders.Select(e =>
                            $"{e.Name}{(e.Hardware ? $" ({e.Vendor})" : " (CPU)")}{(e.DynamicBitrate ? "" : " sin bitrate dinámico")}"));
                    RenderRecommendations();
                    RenderStatus();
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => hardwareText.Text = $"No se pudo detectar el hardware: {ex.Message}");
            }
        });
    }
}
