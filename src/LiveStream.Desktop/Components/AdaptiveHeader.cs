using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace LiveStream.Desktop.Components;

/// <summary>
/// Cabecera de panel adaptable: las acciones de cabecera (botones de icono) se agrupan en un menú «⋯»
/// cuando la columna del panel es estrecha y no caben junto al título, en lugar de desbordarse y solaparse
/// con él. La primera acción (la principal) nunca se agrupa; cada acción del menú ejecuta su comando o la
/// acción del botón original. Uso: <c>c:AdaptiveHeader.Enabled="True"</c> en el panel raíz de la cabecera.
/// </summary>
public static class AdaptiveHeader
{
    private const double OverflowWidth = 28;
    private const double EdgeMargin = 4;

    private sealed class State
    {
        public required Panel Actions;
        public required List<Control> Buttons;
        public required List<Visual> Titles;
        public required Button Overflow;
        public required MenuFlyout Flyout;
        public required double ButtonSpacing;
        public int Visible = int.MaxValue;
        public bool Busy;
        public Action<MenuFlyout>? ExtraMenuProvider;
    }

    private static readonly ConditionalWeakTable<Control, State> States = new();
    private static readonly ConditionalWeakTable<Control, object?> Wired = new();
    private static readonly ConditionalWeakTable<Control, Action<MenuFlyout>> ExtraProviders = new();

    public static void SetEnabled(Control element, bool value)
    {
        if (!value)
            return;
        if (!Wired.TryGetValue(element, out _))
        {
            Wired.Add(element, null);
            element.AttachedToVisualTree += OnAttached;
        }
    }

    /// <summary>
    /// Permite asociar opciones adicionales al menú «⋯» (como unir paneles en pestañas o desacoplar).
    /// </summary>
    public static void SetExtraMenuProvider(Control element, Action<MenuFlyout> provider)
    {
        ExtraProviders.AddOrUpdate(element, provider);
        if (States.TryGetValue(element, out var state))
        {
            state.ExtraMenuProvider = provider;
            Recompute(element);
        }
    }

    public static void ForceRecompute(Control host)
    {
        Recompute(host);
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control host)
            Wire(host);
    }

    private static void Wire(Control host)
    {
        if (States.TryGetValue(host, out _))
            return;

        // Panel de acciones: panel que contiene botones de acciones de la cabecera.
        var actions = host.GetVisualDescendants().OfType<Panel>()
            .FirstOrDefault(p => p.GetVisualDescendants().OfType<Control>().Any(c => c is Button or ToggleButton));
        if (actions is null)
            return;

        var buttons = actions.GetVisualDescendants().OfType<Control>().Where(c => c is Button or ToggleButton).ToList();
        if (buttons.Count == 0)
            return;

        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => RebuildItems(host);

        var overflow = new Button
        {
            Classes = { "icon" },
            Content = new Glyph { Data = TryGeometry("Icon.Ellipsis"), Width = 14, Height = 14 },
            IsVisible = false,
        };
        ToolTip.SetTip(overflow, "Más opciones");
        AutomationProperties.SetName(overflow, "Más opciones");
        overflow.Click += (_, _) =>
        {
            RebuildItems(host);
            flyout.ShowAt(overflow);
        };
        actions.Children.Add(overflow);

        ExtraProviders.TryGetValue(host, out var extraProvider);

        var state = new State
        {
            Actions = actions,
            Buttons = buttons,
            Titles = host.GetVisualChildren().OfType<Visual>().Where(c => c != actions).ToList(),
            Overflow = overflow,
            Flyout = flyout,
            ButtonSpacing = actions is StackPanel stack ? stack.Spacing : 2,
            ExtraMenuProvider = extraProvider,
        };
        States.Add(host, state);

        host.PropertyChanged += (_, e) =>
        {
            if (Equals(e.Property, Layoutable.BoundsProperty))
                Recompute(host);
        };

        Dispatcher.UIThread.Post(() => Recompute(host), DispatcherPriority.Loaded);
    }

    private static void Recompute(Control host)
    {
        if (!States.TryGetValue(host, out var state) || state.Busy)
            return;

        state.Busy = true;
        try
        {
            var usable = UsableWidth(host);
            if (usable <= 0)
                return;

            var widths = new double[state.Buttons.Count];
            var buttonsWidth = 0d;
            for (var i = 0; i < state.Buttons.Count; i++)
            {
                var b = state.Buttons[i];
                b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                widths[i] = Math.Max(26, b.DesiredSize.Width);
                buttonsWidth += widths[i];
            }

            var titleWidth = 0d;
            foreach (var title in state.Titles)
            {
                if (title is Layoutable layoutable)
                {
                    layoutable.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    titleWidth = Math.Max(titleWidth, layoutable.DesiredSize.Width);
                }
            }

            var spacing = state.ButtonSpacing;
            var fullSpacing = spacing * (state.Buttons.Count + 1);
            var hasExtra = state.ExtraMenuProvider is not null;

            int keep;
            bool showOverflow;

            if (titleWidth + buttonsWidth + fullSpacing <= usable)
            {
                // Todos caben: si hay opciones extra mostramos siempre el menú de tres puntos «...»
                keep = state.Buttons.Count;
                showOverflow = hasExtra;
            }
            else
            {
                // No caben todos: agrupamos de derecha a izquierda
                keep = 1;
                showOverflow = true;
                for (var k = state.Buttons.Count - 1; k >= 1; k--)
                {
                    var actionsWidth = widths.Take(k).Sum() + OverflowWidth + spacing * (k + 1);
                    if (titleWidth + actionsWidth <= usable)
                    {
                        keep = k;
                        break;
                    }
                }
            }

            state.Visible = keep;
            for (var i = 0; i < state.Buttons.Count; i++)
            {
                state.Buttons[i].IsVisible = (i < keep);
            }
            state.Overflow.IsVisible = showOverflow;
        }
        finally
        {
            state.Busy = false;
        }
    }

    private static void RebuildItems(Control host)
    {
        if (!States.TryGetValue(host, out var state))
            return;

        state.Flyout.Items.Clear();

        // 1. Acciones agrupadas que no cupieron en la barra
        var addedActions = 0;
        for (var i = state.Visible; i < state.Buttons.Count; i++)
        {
            state.Flyout.Items.Add(ItemFor(state.Buttons[i]));
            addedActions++;
        }

        // 2. Acciones del proveedor adicional (pestañas, ubicación, dock)
        if (state.ExtraMenuProvider is not null)
        {
            if (addedActions > 0)
                state.Flyout.Items.Add(new Separator());

            state.ExtraMenuProvider(state.Flyout);
        }
    }

    /// <summary>Reproduce la acción del control original desde el menú: comando, subflyout o evento Click propio.</summary>
    private static MenuItem ItemFor(Control source)
    {
        var name = AutomationProperties.GetName(source);
        if (string.IsNullOrWhiteSpace(name) && ToolTip.GetTip(source) is { } tip)
            name = tip.ToString();
        if (string.IsNullOrWhiteSpace(name) && source is ContentControl { Content: TextBlock label })
            name = label.Text;

        var item = new MenuItem
        {
            Header = name ?? "Acción",
            IsEnabled = source.IsEnabled,
        };

        if (source is ContentControl cc && cc.Content is Glyph g)
        {
            item.Icon = new Glyph { Data = g.Data, Width = 14, Height = 14 };
        }
        else if (source is Panel p && p.Children.OfType<Glyph>().FirstOrDefault() is { } pg)
        {
            item.Icon = new Glyph { Data = pg.Data, Width = 14, Height = 14 };
        }

        if (source is Button { Command: { } command } b)
        {
            item.Command = command;
            item.CommandParameter = b.CommandParameter;
        }
        else if (source is ToggleButton { Command: { } toggleCommand } tb)
        {
            item.Command = toggleCommand;
            item.CommandParameter = tb.CommandParameter;
        }
        else if (source is Button btn && btn.Flyout is { } btnFlyout)
        {
            item.Click += (_, _) =>
            {
                Dispatcher.UIThread.Post(() => btnFlyout.ShowAt(btn));
            };
        }
        else if (FlyoutBase.GetAttachedFlyout(source) is { } attachedFlyout)
        {
            item.Click += (_, _) =>
            {
                Dispatcher.UIThread.Post(() => FlyoutBase.ShowAttachedFlyout(source));
            };
        }
        else
        {
            item.Click += (_, _) => InvokeOriginal(source);
        }
        return item;
    }

    private static void InvokeOriginal(Control source)
    {
        switch (source)
        {
            case ToggleButton toggle:
                toggle.IsChecked = toggle.IsChecked != true;
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, toggle));
                break;
            case Button button:
                if (button.Command is not null && button.Command.CanExecute(button.CommandParameter))
                {
                    button.Command.Execute(button.CommandParameter);
                }
                else
                {
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                }
                break;
            default:
                source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, source));
                break;
        }
    }

    private static double UsableWidth(Control host)
    {
        if (host.Parent is Border { Padding: { } padding } border && border.Bounds.Width > 0)
            return Math.Max(80, border.Bounds.Width - padding.Left - padding.Right - EdgeMargin);
        if (host.Bounds.Width > 0)
            return Math.Max(80, host.Bounds.Width - EdgeMargin);
        return 160;
    }

    private static StreamGeometry? TryGeometry(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, ThemeVariant.Default, out var value) == true && value is StreamGeometry g ? g : null;
}
