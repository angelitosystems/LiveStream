using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace LiveStream.Desktop.Components;

/// <summary>
/// Ventana flotante independiente para desacoplar paneles del estudio (multimonitor).
/// Al cerrarse o pulsar «Reacoplar», devuelve el panel a su posición en el StudioDockHost.
/// </summary>
public sealed class FloatingDockWindow : Window
{
    private readonly StudioDockHost _dockHost;
    private readonly string _panelId;
    private readonly Control _panelContent;
    private bool _isReattaching;

    public FloatingDockWindow(StudioDockHost dockHost, string panelId, string title, string iconKey, Control panelContent)
    {
        _dockHost = dockHost;
        _panelId = panelId;
        _panelContent = panelContent;

        Title = $"LiveStream — {title}";
        Width = 460;
        Height = 420;
        MinWidth = 280;
        MinHeight = 200;
        Background = new SolidColorBrush(Color.Parse("#FF13161C"));

        var header = new Border
        {
            Height = 34,
            Background = new SolidColorBrush(Color.Parse("#FF1C2028")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF282D37")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 0),
        };

        var headerLayout = new DockPanel();

        var reattachBtn = new Button
        {
            Classes = { "icon" },
            Width = 26,
            Height = 26,
            Content = new Glyph { Data = TryGeometry("Icon.Fit"), Width = 13, Height = 13 },
        };
        ToolTip.SetTip(reattachBtn, "Reacoplar este panel en la ventana principal");
        reattachBtn.Click += (_, _) => Reattach();
        DockPanel.SetDock(reattachBtn, Dock.Right);
        headerLayout.Children.Add(reattachBtn);

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Glyph { Data = TryGeometry(iconKey), Width = 14, Height = 14, Foreground = new SolidColorBrush(Color.Parse("#FF3B82F6")) },
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        headerLayout.Children.Add(titleStack);
        header.Child = headerLayout;

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // Limpiar parentesco si lo tenía
        if (panelContent.Parent is Panel p)
            p.Children.Remove(panelContent);

        root.Children.Add(panelContent);
        Content = root;

        Closing += (_, _) =>
        {
            if (!_isReattaching)
                Reattach();
        };
    }

    public void Reattach()
    {
        if (_isReattaching)
            return;

        _isReattaching = true;

        if (Content is DockPanel dockPanel)
            dockPanel.Children.Remove(_panelContent);

        _dockHost.ReattachPanel(_panelId);
        Close();
    }

    private static StreamGeometry? TryGeometry(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, ThemeVariant.Default, out var value) == true && value is StreamGeometry g ? g : null;
}
