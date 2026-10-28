using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using LiveStream.Infrastructure.Diagnostics;
using LiveStream.Infrastructure.Persistence;

namespace LiveStream.Desktop.Components;

/// <summary>
/// Contenedor avanzado de paneles del estudio (Docking System modular y adaptable).
/// Permite:
/// 1. Reorganizar pestañas haciendo clic y arrastrando directamente entre ellas (reordenamiento dentro del slot o entre slots).
/// 2. Fusión automática en pestañas (Tabs) mediante Drag & Drop al centro de cualquier panel.
/// 3. Detección espacial de zonas de soltado (Drop Zones) con marcador visual translúcido.
/// 4. Desacople y desacoplamiento de paneles como ventanas flotantes independientes (multimonitor).
/// 5. Maximización segura de cualquier panel (sin errores de visual parent) y restauración a columnas.
/// </summary>
public sealed class StudioDockHost : Grid
{
    public const string DefaultModernLayout = "scenes,sources|mixer,graphics|transitions,controls|chat,alerts";
    public const string ProductionPreset = "scenes,sources,graphics|mixer,transitions|controls,chat,alerts";
    public const string CompactPreset = "scenes,sources,graphics|mixer,transitions,controls,chat,alerts";
    public const string ExpandedPreset = "scenes|sources|graphics|mixer|transitions|controls|chat|alerts";

    public sealed record PanelDefinition(
        string Id,
        string Title,
        string IconKey,
        Control PanelContent,
        Control? HeaderHost = null);

    private readonly List<PanelDefinition> _registered = [];
    private readonly List<List<string>> _slots = [];
    private readonly Dictionary<string, int> _activeTabBySlot = [];
    private readonly Dictionary<string, FloatingDockWindow> _floatingWindows = [];
    private readonly List<Control> _slotControls = [];
    private readonly MachineSettingsStore _settingsStore;

    // Estado del sistema Drag & Drop
    private string? _draggedPanelId;
    private int? _dragSourceSlotIndex;
    private int? _dragSourceTabIndex;
    private Point _dragStartPoint;
    private bool _isDragging;
    private int? _targetSlotIndex;
    private int? _targetTabIndex;
    private DropTargetKind _dropTargetKind = DropTargetKind.None;

    // Overlay visual para feedback espacial al arrastrar
    private readonly Border _dropOverlay;
    private readonly TextBlock _dropOverlayText;
    private readonly Glyph _dropOverlayIcon;

    private int? _maximizedSlotIndex;

    private enum DropTargetKind
    {
        None,
        TabReorder,
        TabsCenter,
        SplitLeft,
        SplitRight,
        FloatDetach,
    }

    public event Action? LayoutChanged;

    public StudioDockHost()
    {
        _settingsStore = new MachineSettingsStore();

        // Configurar overlay de soltado
        _dropOverlayIcon = new Glyph
        {
            Data = TryGeometry("Icon.Layers"),
            Width = 16,
            Height = 16,
            Foreground = new SolidColorBrush(Color.Parse("#FF60A5FA")),
            Margin = new Thickness(0, 0, 8, 0),
        };

        _dropOverlayText = new TextBlock
        {
            Text = "Soltar para unir en pestañas",
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var overlayBanner = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F01A202C")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF60A5FA")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _dropOverlayIcon, _dropOverlayText }
            }
        };

        _dropOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(90, 37, 99, 235)),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF60A5FA")),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false,
            IsVisible = false,
            Child = overlayBanner,
        };

        PointerMoved += OnHostPointerMoved;
        PointerReleased += OnHostPointerReleased;
        PointerCaptureLost += (_, _) => EndDrag();
    }

    public static void DetachFromParent(Control? control)
    {
        if (control is null) return;
        if (control.Parent is Panel p)
            p.Children.Remove(control);
        else if (control.Parent is ContentControl cc)
            cc.Content = null;
        else if (control.Parent is Decorator dec)
            dec.Child = null;
        else if (control.Parent is ContentPresenter cp)
            cp.Content = null;

        if (Avalonia.VisualTree.VisualExtensions.GetVisualParent(control) is { } vp)
        {
            if (vp is Panel vpPanel)
                vpPanel.Children.Remove(control);
            else if (vp is ContentPresenter vpCp)
                vpCp.Content = null;
            else if (vp is Decorator vpDec)
                vpDec.Child = null;
            else if (vp is ContentControl vpCc)
                vpCc.Content = null;
        }
    }

    private void DetachAllPanels()
    {
        foreach (var def in _registered)
        {
            DetachFromParent(def.PanelContent);
        }
    }

    public void RegisterPanel(PanelDefinition def)
    {
        _registered.RemoveAll(p => p.Id == def.Id);
        _registered.Add(def);

        if (def.HeaderHost is not null)
        {
            AdaptiveHeader.SetExtraMenuProvider(def.HeaderHost, flyout => PopulatePanelMenu(def.Id, flyout));
            AttachHeaderDragGrip(def);
        }
    }

    public void InitializeLayout(string? defaultLayout = null)
    {
        var settings = _settingsStore.Load();
        var layoutString = settings.DockLayout;

        if (string.IsNullOrWhiteSpace(layoutString))
            layoutString = defaultLayout ?? DefaultModernLayout;

        ApplyLayoutString(layoutString);
    }

    public void ResetToDefault()
    {
        _maximizedSlotIndex = null;
        ApplyLayoutString(DefaultModernLayout);
        SaveLayout();
    }

    public void ApplyPreset(string layoutString)
    {
        _maximizedSlotIndex = null;
        ApplyLayoutString(layoutString);
        SaveLayout();
    }

    public void JoinPanelsAsTabs(string sourceId, string targetId)
    {
        if (sourceId == targetId)
            return;

        foreach (var slot in _slots)
            slot.Remove(sourceId);

        _slots.RemoveAll(s => s.Count == 0);

        var targetSlot = _slots.FirstOrDefault(s => s.Contains(targetId));
        if (targetSlot is not null)
        {
            if (!targetSlot.Contains(sourceId))
                targetSlot.Add(sourceId);
        }
        else
        {
            _slots.Add([targetId, sourceId]);
        }

        RebuildGrid();
        SaveLayout();
    }

    public void SeparatePanel(string panelId)
    {
        var currentSlot = _slots.FirstOrDefault(s => s.Contains(panelId));
        if (currentSlot is null || currentSlot.Count <= 1)
            return;

        currentSlot.Remove(panelId);
        var slotIndex = _slots.IndexOf(currentSlot);
        _slots.Insert(slotIndex + 1, [panelId]);

        RebuildGrid();
        SaveLayout();
    }

    public void InsertPanelBeside(string sourceId, string targetId, bool before)
    {
        if (sourceId == targetId)
            return;

        foreach (var slot in _slots)
            slot.Remove(sourceId);

        _slots.RemoveAll(s => s.Count == 0);

        var targetSlot = _slots.FirstOrDefault(s => s.Contains(targetId));
        var targetIndex = targetSlot is not null ? _slots.IndexOf(targetSlot) : _slots.Count;

        var insertIndex = before ? Math.Max(0, targetIndex) : Math.Min(_slots.Count, targetIndex + 1);
        _slots.Insert(insertIndex, [sourceId]);

        RebuildGrid();
        SaveLayout();
    }

    public void MoveTab(string panelId, int targetSlotIndex, int? targetTabIndex = null)
    {
        if (targetSlotIndex < 0 || targetSlotIndex >= _slots.Count)
            return;

        var sourceSlot = _slots.FirstOrDefault(s => s.Contains(panelId));
        if (sourceSlot is null)
            return;

        var targetSlot = _slots[targetSlotIndex];
        sourceSlot.Remove(panelId);

        var insertIdx = targetTabIndex.HasValue
            ? Math.Clamp(targetTabIndex.Value, 0, targetSlot.Count)
            : targetSlot.Count;
        targetSlot.Insert(insertIdx, panelId);

        _slots.RemoveAll(s => s.Count == 0);

        var newSlotIndex = _slots.IndexOf(targetSlot);
        if (newSlotIndex >= 0)
        {
            var newTabIndex = targetSlot.IndexOf(panelId);
            _activeTabBySlot[$"slot_{newSlotIndex}"] = Math.Max(0, newTabIndex);
        }

        RebuildGrid();
        SaveLayout();
    }

    public void MoveSlot(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _slots.Count || toIndex < 0 || toIndex >= _slots.Count || fromIndex == toIndex)
            return;

        var slot = _slots[fromIndex];
        _slots.RemoveAt(fromIndex);
        _slots.Insert(toIndex, slot);

        RebuildGrid();
        SaveLayout();
    }

    public void FloatPanel(string panelId)
    {
        var def = _registered.FirstOrDefault(r => r.Id == panelId);
        if (def is null)
            return;

        if (_floatingWindows.TryGetValue(panelId, out var existing))
        {
            existing.Activate();
            return;
        }

        foreach (var slot in _slots)
            slot.Remove(panelId);

        _slots.RemoveAll(s => s.Count == 0);
        RebuildGrid();
        SaveLayout();

        DetachFromParent(def.PanelContent);
        var win = new FloatingDockWindow(this, panelId, def.Title, def.IconKey, def.PanelContent);
        _floatingWindows[panelId] = win;
        win.Closed += (_, _) => _floatingWindows.Remove(panelId);
        win.Show();
    }

    public void ReattachPanel(string panelId)
    {
        _floatingWindows.Remove(panelId);

        var def = _registered.FirstOrDefault(r => r.Id == panelId);
        if (def is not null)
            DetachFromParent(def.PanelContent);

        if (_slots.Count > 0)
        {
            _slots[^1].Add(panelId);
        }
        else
        {
            _slots.Add([panelId]);
        }

        RebuildGrid();
        SaveLayout();
    }

    public void ToggleMaximizeSlot(int slotIndex)
    {
        try
        {
            if (_maximizedSlotIndex == slotIndex)
                _maximizedSlotIndex = null;
            else
                _maximizedSlotIndex = slotIndex;

            RebuildGrid();
        }
        catch (Exception ex)
        {
            AppLog.Error("dock", "Error al maximizar panel", ex);
        }
    }

    private void ApplyLayoutString(string layoutString)
    {
        _slots.Clear();
        var parts = layoutString.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var assigned = new HashSet<string>();

        foreach (var part in parts)
        {
            var panelIds = part.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(id => _registered.Any(r => r.Id == id))
                .ToList();

            if (panelIds.Count > 0)
            {
                _slots.Add(panelIds);
                foreach (var id in panelIds)
                    assigned.Add(id);
            }
        }

        var unassigned = _registered.Where(r => !assigned.Contains(r.Id)).Select(r => r.Id).ToList();
        if (unassigned.Count > 0)
        {
            if (_slots.Count > 0)
            {
                foreach (var id in unassigned)
                    _slots[^1].Add(id);
            }
            else
            {
                _slots.Add(unassigned);
            }
        }

        RebuildGrid();
    }

    private void SaveLayout()
    {
        var layoutString = string.Join('|', _slots.Select(s => string.Join(',', s)));
        var settings = _settingsStore.Load();
        if (settings.DockLayout != layoutString)
        {
            _settingsStore.Save(settings with { DockLayout = layoutString });
        }
        LayoutChanged?.Invoke();
    }

    private void RebuildGrid()
    {
        // 1. Limpiar cuidadosamente parentesco previo de todos los paneles registrados
        foreach (var r in _registered)
            DetachFromParent(r.PanelContent);

        Children.Clear();
        ColumnDefinitions.Clear();
        _slotControls.Clear();

        if (_slots.Count == 0)
            return;

        // Modo maximizado: solo un slot visible ocupando todo el ancho
        if (_maximizedSlotIndex.HasValue && _maximizedSlotIndex.Value >= 0 && _maximizedSlotIndex.Value < _slots.Count)
        {
            ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            var slotControl = BuildSlotControl(_maximizedSlotIndex.Value, _slots[_maximizedSlotIndex.Value]);
            SetColumn(slotControl, 0);
            Children.Add(slotControl);
            _slotControls.Add(slotControl);
            AttachDropOverlayToGrid(0, 1);
            return;
        }

        // Modo regular de múltiples columnas con separadores Splitter
        var colDefs = new ColumnDefinitions();
        for (var i = 0; i < _slots.Count; i++)
        {
            if (i > 0)
                colDefs.Add(new ColumnDefinition(6, GridUnitType.Pixel));

            var count = _slots[i].Count;
            var weight = Math.Max(1.0, Math.Min(1.8, 1.0 + (count - 1) * 0.35));
            colDefs.Add(new ColumnDefinition(weight, GridUnitType.Star));
        }
        ColumnDefinitions = colDefs;

        var colIndex = 0;
        for (var i = 0; i < _slots.Count; i++)
        {
            if (i > 0)
            {
                var splitter = new GridSplitter
                {
                    ResizeDirection = GridResizeDirection.Columns,
                    Background = new SolidColorBrush(Color.Parse("#FF1C2028")),
                };
                SetColumn(splitter, colIndex);
                Children.Add(splitter);
                colIndex++;
            }

            var slotPanels = _slots[i];
            var slotControl = BuildSlotControl(i, slotPanels);
            SetColumn(slotControl, colIndex);
            Children.Add(slotControl);
            _slotControls.Add(slotControl);
            colIndex++;
        }

        AttachDropOverlayToGrid(0, Math.Max(1, colIndex));
    }

    private void AttachDropOverlayToGrid(int startCol, int colSpan)
    {
        DetachFromParent(_dropOverlay);
        SetColumn(_dropOverlay, startCol);
        SetColumnSpan(_dropOverlay, colSpan);
        Children.Add(_dropOverlay);
    }

    private Control BuildSlotControl(int slotIndex, List<string> panelIds)
    {
        var isMaximized = _maximizedSlotIndex == slotIndex;

        if (panelIds.Count == 1)
        {
            var def = _registered.FirstOrDefault(r => r.Id == panelIds[0]);
            if (def is not null)
            {
                DetachFromParent(def.PanelContent);
                return WrapSinglePanel(slotIndex, def, isMaximized);
            }
            return new Panel();
        }

        // Múltiples paneles: contenedor con barra de pestañas moderna y arrastrable
        var slotKey = $"slot_{slotIndex}";
        if (!_activeTabBySlot.TryGetValue(slotKey, out var activeIndex) || activeIndex >= panelIds.Count)
        {
            activeIndex = 0;
            _activeTabBySlot[slotKey] = 0;
        }

        var tabContainer = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };

        var tabStripDock = new DockPanel
        {
            Margin = new Thickness(4, 0, 4, 4),
        };

        var slotTools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(slotTools, Dock.Right);

        var maxBtn = new Button
        {
            Classes = { "icon" },
            Width = 24,
            Height = 24,
            Content = new Glyph { Data = TryGeometry(isMaximized ? "Icon.Minimize" : "Icon.Maximize"), Width = 11, Height = 11 },
        };
        ToolTip.SetTip(maxBtn, isMaximized ? "Restaurar todas las columnas" : "Maximizar este grupo de paneles");
        maxBtn.Click += (_, _) => ToggleMaximizeSlot(slotIndex);
        slotTools.Children.Add(maxBtn);

        var menuBtn = new Button
        {
            Classes = { "icon" },
            Width = 24,
            Height = 24,
            Content = new Glyph { Data = TryGeometry("Icon.Ellipsis"), Width = 11, Height = 11 },
        };
        ToolTip.SetTip(menuBtn, "Opciones de distribución");
        menuBtn.Click += (_, _) =>
        {
            var flyout = new MenuFlyout();
            var activePanelId = panelIds[Math.Clamp(activeIndex, 0, panelIds.Count - 1)];
            PopulatePanelMenu(activePanelId, flyout);
            flyout.ShowAt(menuBtn);
        };
        slotTools.Children.Add(menuBtn);

        tabStripDock.Children.Add(slotTools);

        var tabButtonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tabStripDock.Children.Add(tabButtonsPanel);
        tabContainer.Tag = tabButtonsPanel;

        var contentHolder = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        SetRow(contentHolder, 1);

        void SelectTab(int index)
        {
            _activeTabBySlot[slotKey] = index;
            var selectedId = panelIds[index];
            var def = _registered.FirstOrDefault(r => r.Id == selectedId);
            if (def is not null)
            {
                DetachFromParent(def.PanelContent);
                contentHolder.Content = def.PanelContent;
                if (def.HeaderHost is not null)
                    AdaptiveHeader.ForceRecompute(def.HeaderHost);
            }

            for (var b = 0; b < tabButtonsPanel.Children.Count; b++)
            {
                if (tabButtonsPanel.Children[b] is Button tabBtn)
                {
                    var isSelected = (b == index);
                    tabBtn.Classes.Set("activeTab", isSelected);
                    tabBtn.Background = isSelected
                        ? new SolidColorBrush(Color.Parse("#FF23354E"))
                        : new SolidColorBrush(Color.Parse("#FF161A22"));
                    tabBtn.BorderBrush = isSelected
                        ? new SolidColorBrush(Color.Parse("#FF3B82F6"))
                        : new SolidColorBrush(Color.Parse("#FF282D37"));
                }
            }
        }

        for (var t = 0; t < panelIds.Count; t++)
        {
            var idx = t;
            var pId = panelIds[t];
            var def = _registered.FirstOrDefault(r => r.Id == pId);
            if (def is null)
                continue;

            var tabBtn = new Button
            {
                Padding = new Thickness(8, 4),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF282D37")),
                Background = new SolidColorBrush(Color.Parse("#FF161A22")),
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new Glyph { Data = TryGeometry("Icon.Grip"), Width = 9, Height = 9, Opacity = 0.4 },
                        new Glyph { Data = TryGeometry(def.IconKey), Width = 13, Height = 13 },
                        new TextBlock { Text = def.Title, FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };

            // 1. Clic selecciona la pestaña
            tabBtn.Click += (_, _) => SelectTab(idx);

            // 2. Arrastre interactivo para reorganizar pestañas o mover entre slots
            tabBtn.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(tabBtn).Properties.IsLeftButtonPressed)
                {
                    SelectTab(idx);
                    _draggedPanelId = pId;
                    _dragSourceSlotIndex = slotIndex;
                    _dragSourceTabIndex = idx;
                    _dragStartPoint = e.GetPosition(this);
                    _isDragging = false;
                }
            };

            tabBtn.PointerMoved += (s, e) =>
            {
                if (_draggedPanelId is null)
                    return;

                var pt = e.GetPosition(this);
                var delta = pt - _dragStartPoint;

                if (!_isDragging && (Math.Abs(delta.X) > 6 || Math.Abs(delta.Y) > 6))
                {
                    _isDragging = true;
                    _dropOverlay.IsVisible = true;
                    e.Pointer.Capture(this);
                }

                if (!_isDragging)
                    return;

                UpdateDropTarget(pt);
            };

            tabBtn.PointerReleased += (s, e) =>
            {
                if (_isDragging && _draggedPanelId is not null)
                {
                    CompleteDrag();
                }
                if (e.Pointer.Captured == this)
                    e.Pointer.Capture(null);
                EndDrag();
            };

            tabButtonsPanel.Children.Add(tabBtn);
        }

        tabContainer.Children.Add(tabStripDock);
        tabContainer.Children.Add(contentHolder);

        SelectTab(activeIndex);
        return tabContainer;
    }

    private Control WrapSinglePanel(int slotIndex, PanelDefinition def, bool isMaximized)
    {
        var container = new DockPanel();

        var topBar = new DockPanel
        {
            Height = 22,
            Margin = new Thickness(2, 0, 2, 2),
        };

        var rightBtns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(rightBtns, Dock.Right);

        var maxBtn = new Button
        {
            Classes = { "icon" },
            Width = 20,
            Height = 20,
            Content = new Glyph { Data = TryGeometry(isMaximized ? "Icon.Minimize" : "Icon.Maximize"), Width = 10, Height = 10 },
        };
        ToolTip.SetTip(maxBtn, isMaximized ? "Restaurar columnas" : "Maximizar este panel");
        maxBtn.Click += (_, _) => ToggleMaximizeSlot(slotIndex);
        rightBtns.Children.Add(maxBtn);

        var floatBtn = new Button
        {
            Classes = { "icon" },
            Width = 20,
            Height = 20,
            Content = new Glyph { Data = TryGeometry("Icon.Fit"), Width = 10, Height = 10 },
        };
        ToolTip.SetTip(floatBtn, "Desacoplar como ventana flotante");
        floatBtn.Click += (_, _) => FloatPanel(def.Id);
        rightBtns.Children.Add(floatBtn);

        topBar.Children.Add(rightBtns);

        var gripPill = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1A1E26")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF282D37")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    new Glyph { Data = TryGeometry("Icon.Grip"), Width = 8, Height = 8, Opacity = 0.5 },
                    new Glyph { Data = TryGeometry(def.IconKey), Width = 11, Height = 11 },
                    new TextBlock { Text = def.Title, FontSize = 10, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } }
                }
            }
        };
        ToolTip.SetTip(gripPill, "Arrastra para mover o unir en pestañas con otro panel");

        gripPill.PointerPressed += (s, e) =>
        {
            if (e.GetCurrentPoint(gripPill).Properties.IsLeftButtonPressed)
            {
                _draggedPanelId = def.Id;
                _dragSourceSlotIndex = slotIndex;
                _dragSourceTabIndex = 0;
                _dragStartPoint = e.GetPosition(this);
                _isDragging = false;
                e.Pointer.Capture(gripPill);
            }
        };

        gripPill.PointerMoved += (s, e) =>
        {
            if (_draggedPanelId is null)
                return;

            var pt = e.GetPosition(this);
            var delta = pt - _dragStartPoint;

            if (!_isDragging && (Math.Abs(delta.X) > 4 || Math.Abs(delta.Y) > 4))
            {
                _isDragging = true;
                _dropOverlay.IsVisible = true;
            }

            if (!_isDragging)
                return;

            UpdateDropTarget(pt);
        };

        gripPill.PointerReleased += (s, e) =>
        {
            if (_isDragging && _draggedPanelId is not null)
            {
                CompleteDrag();
            }
            e.Pointer.Capture(null);
            EndDrag();
        };

        topBar.Children.Add(gripPill);

        DockPanel.SetDock(topBar, Dock.Top);
        container.Children.Add(topBar);

        DetachFromParent(def.PanelContent);
        container.Children.Add(def.PanelContent);

        return container;
    }

    private void AttachHeaderDragGrip(PanelDefinition def)
    {
        if (def.HeaderHost is Grid headerGrid)
        {
            var grip = new Border
            {
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Padding = new Thickness(4),
                Child = new Glyph { Data = TryGeometry("Icon.Grip"), Width = 10, Height = 10, Opacity = 0.5 },
            };
            ToolTip.SetTip(grip, "Arrastra para mover, unir en pestañas o soltar fuera para desacoplar");

            void StartDrag(PointerPressedEventArgs e, Control sourceControl)
            {
                if (e.GetCurrentPoint(sourceControl).Properties.IsLeftButtonPressed)
                {
                    var currentSlot = _slots.FirstOrDefault(sl => sl.Contains(def.Id));
                    var slotIdx = currentSlot is not null ? _slots.IndexOf(currentSlot) : 0;
                    var tabIdx = currentSlot is not null ? currentSlot.IndexOf(def.Id) : 0;

                    _draggedPanelId = def.Id;
                    _dragSourceSlotIndex = slotIdx;
                    _dragSourceTabIndex = tabIdx;
                    _dragStartPoint = e.GetPosition(this);
                    _isDragging = false;
                    e.Pointer.Capture(sourceControl);
                }
            }

            void OnDragMove(PointerEventArgs e)
            {
                if (_draggedPanelId is null) return;
                var pt = e.GetPosition(this);
                var delta = pt - _dragStartPoint;
                if (!_isDragging && (Math.Abs(delta.X) > 4 || Math.Abs(delta.Y) > 4))
                {
                    _isDragging = true;
                    _dropOverlay.IsVisible = true;
                }
                if (!_isDragging) return;
                UpdateDropTarget(pt);
            }

            void OnDragRelease(PointerReleasedEventArgs e, Control sourceControl)
            {
                if (_isDragging && _draggedPanelId is not null)
                    CompleteDrag();
                e.Pointer.Capture(null);
                EndDrag();
            }

            grip.PointerPressed += (s, e) => StartDrag(e, grip);
            grip.PointerMoved += (s, e) => OnDragMove(e);
            grip.PointerReleased += (s, e) => OnDragRelease(e, grip);

            if (headerGrid.Children.FirstOrDefault(c => GetColumn(c) == 0) is StackPanel titleStack)
            {
                titleStack.Children.Insert(0, grip);
                titleStack.Cursor = new Cursor(StandardCursorType.Hand);
                ToolTip.SetTip(titleStack, "Arrastra desde la barra de título para reorganizar o desacoplar");
                titleStack.PointerPressed += (s, e) => StartDrag(e, titleStack);
                titleStack.PointerMoved += (s, e) => OnDragMove(e);
                titleStack.PointerReleased += (s, e) => OnDragRelease(e, titleStack);
            }
        }
    }

    #region Drag & Drop Processing

    private void OnHostPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedPanelId is null || !_isDragging)
            return;

        UpdateDropTarget(e.GetPosition(this));
    }

    private void UpdateDropTarget(Point point)
    {
        // Si el puntero se desplaza considerablemente fuera del área del dock host (ej. hacia la vista previa o fuera)
        var draggedDef = _registered.FirstOrDefault(r => r.Id == _draggedPanelId);
        var draggedTitle = draggedDef?.Title ?? "Panel";

        if (point.Y < -15 || point.Y > Bounds.Height + 15 || point.X < -15 || point.X > Bounds.Width + 15)
        {
            _targetSlotIndex = null;
            _dropTargetKind = DropTargetKind.FloatDetach;
            _dropOverlayIcon.Data = TryGeometry("Icon.Fit");
            _dropOverlayText.Text = $"Soltar fuera para desacoplar «{draggedTitle}» como ventana flotante";
            _dropOverlay.Margin = new Thickness(8);
            return;
        }

        var colDefs = ColumnDefinitions;
        if (colDefs.Count == 0 || Bounds.Width <= 0)
            return;

        var totalWidth = Bounds.Width;
        var accumulatedX = 0.0;
        var slotIndex = 0;
        var foundSlot = -1;
        var slotStart = 0.0;
        var slotWidth = 0.0;

        for (var c = 0; c < colDefs.Count; c++)
        {
            var colWidth = colDefs[c].ActualWidth;
            if (colWidth <= 0)
                colWidth = totalWidth / Math.Max(1, _slots.Count);

            if (c % 2 == 0) // Columna de slot
            {
                if (point.X >= accumulatedX && point.X <= accumulatedX + colWidth)
                {
                    foundSlot = slotIndex;
                    slotStart = accumulatedX;
                    slotWidth = colWidth;
                    break;
                }
                slotIndex++;
            }
            accumulatedX += colWidth;
        }

        if (foundSlot >= 0 && foundSlot < _slots.Count)
        {
            _targetSlotIndex = foundSlot;
            var localXInSlot = point.X - slotStart;
            var ratio = Math.Clamp(localXInSlot / Math.Max(1, slotWidth), 0.0, 1.0);

            var targetPanels = _slots[foundSlot];
            var targetTitle = targetPanels.Count > 0 ? _registered.FirstOrDefault(r => r.Id == targetPanels[0])?.Title ?? "panel" : "panel";

            var isSameSlot = _dragSourceSlotIndex.HasValue && _dragSourceSlotIndex.Value == foundSlot;
            var isOverTabBar = point.Y <= 45;

            // Si es el mismo slot o estamos sobre el área de pestañas de un slot con pestañas:
            if ((isSameSlot || isOverTabBar) && targetPanels.Count > 1 && foundSlot < _slotControls.Count && _slotControls[foundSlot] is Grid tg && tg.Tag is StackPanel tabBtns && tabBtns.Children.Count > 0)
            {
                _dropTargetKind = DropTargetKind.TabReorder;
                var hoverTab = tabBtns.Children.Count - 1;
                for (var b = 0; b < tabBtns.Children.Count; b++)
                {
                    var btn = tabBtns.Children[b];
                    var btnMid = btn.Bounds.Left + (btn.Bounds.Width / 2.0) + 4;
                    if (localXInSlot <= btnMid)
                    {
                        hoverTab = b;
                        break;
                    }
                }
                _targetTabIndex = hoverTab;
                var hoverDef = _registered.FirstOrDefault(r => r.Id == targetPanels[Math.Clamp(hoverTab, 0, targetPanels.Count - 1)]);

                _dropOverlayIcon.Data = TryGeometry("Icon.Grip");
                _dropOverlayText.Text = $"Colocar «{draggedTitle}» en posición {hoverTab + 1}" + (hoverDef is not null ? $" (junto a {hoverDef.Title})" : "");

                var targetBtn = tabBtns.Children[Math.Clamp(hoverTab, 0, tabBtns.Children.Count - 1)];
                var targetX = slotStart + targetBtn.Bounds.Left + 4;
                var targetW = Math.Max(70, targetBtn.Bounds.Width);
                _dropOverlay.Margin = new Thickness(targetX, 2, Math.Max(0, totalWidth - (targetX + targetW)), Math.Max(0, Bounds.Height - 34));
            }
            else if (ratio is >= 0.15 and <= 0.85)
            {
                // Zona central: Fusión a pestañas
                _dropTargetKind = DropTargetKind.TabsCenter;
                _targetTabIndex = null;
                _dropOverlayIcon.Data = TryGeometry("Icon.Layers");
                _dropOverlayText.Text = $"Unir «{draggedTitle}» como pestaña con «{targetTitle}»";
                _dropOverlay.Margin = new Thickness(slotStart + 4, 4, Math.Max(0, totalWidth - (slotStart + slotWidth) + 4), 4);
            }
            else if (ratio < 0.15)
            {
                // Zona izquierda: Dividir / Mover antes
                _dropTargetKind = DropTargetKind.SplitLeft;
                _targetTabIndex = null;
                _dropOverlayIcon.Data = TryGeometry("Icon.Up");
                _dropOverlayText.Text = $"Insertar columna a la izquierda de «{targetTitle}»";
                _dropOverlay.Margin = new Thickness(slotStart + 4, 4, Math.Max(0, totalWidth - (slotStart + (slotWidth * 0.5)) + 4), 4);
            }
            else
            {
                // Zona derecha: Dividir / Mover después
                _dropTargetKind = DropTargetKind.SplitRight;
                _targetTabIndex = null;
                _dropOverlayIcon.Data = TryGeometry("Icon.Down");
                _dropOverlayText.Text = $"Insertar columna a la derecha de «{targetTitle}»";
                _dropOverlay.Margin = new Thickness(slotStart + (slotWidth * 0.5) + 4, 4, Math.Max(0, totalWidth - (slotStart + slotWidth) + 4), 4);
            }
        }
        else
        {
            _targetSlotIndex = null;
            _dropTargetKind = DropTargetKind.None;
            _dropOverlayText.Text = "Arrastra hacia un panel para acoplar";
            _dropOverlay.Margin = new Thickness(4);
        }
    }

    private void OnHostPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging && _draggedPanelId is not null)
        {
            CompleteDrag();
        }

        EndDrag();
    }

    private void CompleteDrag()
    {
        if (_draggedPanelId is null)
            return;

        if (_dropTargetKind == DropTargetKind.FloatDetach)
        {
            FloatPanel(_draggedPanelId);
            return;
        }

        if (!_targetSlotIndex.HasValue)
            return;

        try
        {
            switch (_dropTargetKind)
            {
                case DropTargetKind.TabReorder when _targetSlotIndex.HasValue:
                    MoveTab(_draggedPanelId, _targetSlotIndex.Value, _targetTabIndex);
                    break;

                case DropTargetKind.TabsCenter when _targetSlotIndex.HasValue:
                    MoveTab(_draggedPanelId, _targetSlotIndex.Value, null);
                    break;

                case DropTargetKind.SplitLeft when _targetSlotIndex.HasValue:
                    if (_targetSlotIndex.Value >= 0 && _targetSlotIndex.Value < _slots.Count)
                    {
                        var targetPanels = _slots[_targetSlotIndex.Value];
                        if (targetPanels.Count > 0)
                            InsertPanelBeside(_draggedPanelId, targetPanels[0], before: true);
                    }
                    break;

                case DropTargetKind.SplitRight when _targetSlotIndex.HasValue:
                    if (_targetSlotIndex.Value >= 0 && _targetSlotIndex.Value < _slots.Count)
                    {
                        var targetPanels = _slots[_targetSlotIndex.Value];
                        if (targetPanels.Count > 0)
                            InsertPanelBeside(_draggedPanelId, targetPanels[0], before: false);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("dock", "Error al completar arrastre de pestaña", ex);
        }
    }

    private void EndDrag()
    {
        _isDragging = false;
        _draggedPanelId = null;
        _dragSourceSlotIndex = null;
        _dragSourceTabIndex = null;
        _targetSlotIndex = null;
        _targetTabIndex = null;
        _dropTargetKind = DropTargetKind.None;
        _dropOverlay.IsVisible = false;
    }

    #endregion

    private void PopulatePanelMenu(string panelId, MenuFlyout flyout)
    {
        var thisDef = _registered.FirstOrDefault(r => r.Id == panelId);
        if (thisDef is null)
            return;

        var currentSlot = _slots.FirstOrDefault(s => s.Contains(panelId));
        var slotIndex = currentSlot is not null ? _slots.IndexOf(currentSlot) : -1;

        // 1. Unir en pestañas con...
        var joinSubmenu = new MenuItem
        {
            Header = "Unir en pestañas con…",
            Icon = new Glyph { Data = TryGeometry("Icon.Layers"), Width = 14, Height = 14 },
        };
        foreach (var other in _registered.Where(r => r.Id != panelId))
        {
            var otherId = other.Id;
            var item = new MenuItem
            {
                Header = other.Title,
                Icon = new Glyph { Data = TryGeometry(other.IconKey), Width = 14, Height = 14 },
            };
            item.Click += (_, _) => JoinPanelsAsTabs(panelId, otherId);
            joinSubmenu.Items.Add(item);
        }
        flyout.Items.Add(joinSubmenu);

        // 2. Separar panel si está agrupado
        if (currentSlot is not null && currentSlot.Count > 1)
        {
            var separateItem = new MenuItem
            {
                Header = "Separar en columna independiente",
                Icon = new Glyph { Data = TryGeometry("Icon.Fit"), Width = 14, Height = 14 },
            };
            separateItem.Click += (_, _) => SeparatePanel(panelId);
            flyout.Items.Add(separateItem);
        }

        // 3. Desacoplar como ventana flotante
        var floatItem = new MenuItem
        {
            Header = "Desacoplar ventana flotante",
            Icon = new Glyph { Data = TryGeometry("Icon.Fit"), Width = 14, Height = 14 },
        };
        floatItem.Click += (_, _) => FloatPanel(panelId);
        flyout.Items.Add(floatItem);

        // 4. Mover a la izquierda / derecha
        if (slotIndex > 0)
        {
            var moveLeft = new MenuItem
            {
                Header = "Mover panel hacia la izquierda",
                Icon = new Glyph { Data = TryGeometry("Icon.Up"), Width = 14, Height = 14 },
            };
            moveLeft.Click += (_, _) => MoveSlot(slotIndex, slotIndex - 1);
            flyout.Items.Add(moveLeft);
        }
        if (slotIndex >= 0 && slotIndex < _slots.Count - 1)
        {
            var moveRight = new MenuItem
            {
                Header = "Mover panel hacia la derecha",
                Icon = new Glyph { Data = TryGeometry("Icon.Down"), Width = 14, Height = 14 },
            };
            moveRight.Click += (_, _) => MoveSlot(slotIndex, slotIndex + 1);
            flyout.Items.Add(moveRight);
        }

        flyout.Items.Add(new Separator());

        // 5. Diseños predefinidos (Presets)
        var presetsSubmenu = new MenuItem
        {
            Header = "Distribución de paneles",
            Icon = new Glyph { Data = TryGeometry("Icon.Grid"), Width = 14, Height = 14 },
        };

        var pDefault = new MenuItem { Header = "4 Bloques con pestañas (Predeterminado: [Escenas+Fuentes], [Mezclador+Gráficos], [Transic.+Controles], [Chat+Alertas])" };
        pDefault.Click += (_, _) => ResetToDefault();
        presetsSubmenu.Items.Add(pDefault);

        var pProd = new MenuItem { Header = "3 Grupos de producción ([Escenas+Fuentes+Gráficos], [Mezclador+Transic.], [Controles+Chat+Alertas])" };
        pProd.Click += (_, _) => ApplyPreset(ProductionPreset);
        presetsSubmenu.Items.Add(pProd);

        var pCompact = new MenuItem { Header = "2 Grupos compactos ([Escenas+Fuentes+Gráficos], [Mezclador+Transic.+Controles+Chat+Alertas])" };
        pCompact.Click += (_, _) => ApplyPreset(CompactPreset);
        presetsSubmenu.Items.Add(pCompact);

        var pExpanded = new MenuItem { Header = "8 Columnas independientes (Sin pestañas)" };
        pExpanded.Click += (_, _) => ApplyPreset(ExpandedPreset);
        presetsSubmenu.Items.Add(pExpanded);

        flyout.Items.Add(presetsSubmenu);

        var resetItem = new MenuItem
        {
            Header = "Restaurar diseño predeterminado",
            Icon = new Glyph { Data = TryGeometry("Icon.Reset"), Width = 14, Height = 14 },
        };
        resetItem.Click += (_, _) => ResetToDefault();
        flyout.Items.Add(resetItem);
    }

    private static StreamGeometry? TryGeometry(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, ThemeVariant.Default, out var value) == true && value is StreamGeometry g ? g : null;
}
