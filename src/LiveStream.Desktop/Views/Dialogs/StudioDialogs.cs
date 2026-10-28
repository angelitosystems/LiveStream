using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Audio;
using LiveStream.Application.Scenes;
using LiveStream.Application.Sources;
using LiveStream.Application.Studio;
using LiveStream.Desktop.Components;
using LiveStream.Desktop.ViewModels;
using LiveStream.Domain.Entities;
using LiveStream.Domain.ValueObjects;
using LiveStream.Native.Engine;

namespace LiveStream.Desktop.Views.Dialogs;

/// <summary>Diálogos del estudio. Todas las modificaciones salen como comandos del <c>CommandExecutor</c>.</summary>
public static class StudioDialogs
{
    internal static Window NewWindow(string title, double width, double height)
    {
        var window = new Window
        {
            Title = title,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        FitToScreen(window, width, height, minW: 420, minH: 300);
        return window;
    }

    /// <summary>
    /// Dimensiona un diálogo dentro del área de trabajo real (monitor y escalado DPI): nunca más grande que la
    /// pantalla disponible, con mínimos que tampoco la invadan. Independiente de la resolución del equipo.
    /// </summary>
    internal static void FitToScreen(Window window, double width, double height, double minW = 420, double minH = 300)
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            {
                if (main.Screens.ScreenFromVisual(main) is not { } screen)
                    screen = main.Screens.Primary;
                if (screen is not null)
                {
                        var area = screen.WorkingArea;
                    var scale = screen.Scaling;
                    var maxW = Math.Max(320, area.Width / scale - 32);
                    var maxH = Math.Max(240, area.Height / scale - 32);
                    width = Math.Min(width, maxW);
                    height = Math.Min(height, maxH);
                    minW = Math.Min(minW, maxW);
                    minH = Math.Min(minH, maxH);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        window.Width = width;
        window.Height = height;
        window.MinWidth = minW;
        window.MinHeight = minH;
        // CenterOwner puede dejar una ventana fuera del área de trabajo (propietario en una pantalla
        // pequeña o con escalado elevado). Al abrirse, se reajusta la posición dentro del área.
        window.Opened += ClampOnOpened;
    }

    private static void ClampOnOpened(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.Opened -= ClampOnOpened;
            try
            {
                var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
                if (screen is null)
                    return;
                var area = screen.WorkingArea; // píxeles de dispositivo, como Window.Position.
                var scale = window.RenderScaling;
                var w = (int)Math.Ceiling(window.Width * scale);
                var h = (int)Math.Ceiling(window.Height * scale);
                var x = Math.Clamp(window.Position.X, area.X, Math.Max(area.X, area.Right - w));
                var y = Math.Clamp(window.Position.Y, area.Y, Math.Max(area.Y, area.Bottom - h));
                if (x != window.Position.X || y != window.Position.Y)
                    window.Position = new Avalonia.PixelPoint(x, y);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal static Grid Layout(Control body, params Button[] buttons)
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

    internal static Grid LayoutWithTip(Control body, Control? leftContent, params Button[] buttons)
    {
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(16, 10)
        };
        if (leftContent is not null)
        {
            leftContent.VerticalAlignment = VerticalAlignment.Center;
            footer.Children.Add(leftContent);
        }
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons)
        {
            b.MinWidth = 96;
            buttonPanel.Children.Add(b);
        }
        Grid.SetColumn(buttonPanel, 1);
        footer.Children.Add(buttonPanel);

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(body);
        var footerBorder = new Border { Child = footer, BorderBrush = new SolidColorBrush(Color.Parse("#FF2F343B")), BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(footerBorder, 1);
        grid.Children.Add(footerBorder);
        return grid;
    }

    internal static (Color bg, Color fg) CategoryColors(string category) => category switch
    {
        "Captura" => (Color.Parse("#FF1A3353"), Color.Parse("#FF93C5FD")),
        "Audio" => (Color.Parse("#FF14382C"), Color.Parse("#FF6EE7B7")),
        "Contenido visual" => (Color.Parse("#FF2F2148"), Color.Parse("#FFD8B4FE")),
        _ => (Color.Parse("#FF252B36"), Color.Parse("#FFCBD5E1")),
    };

    internal static Button Primary(string text)
    {
        var b = new Button { Content = text, IsDefault = true };
        b.Classes.Add("primary");
        return b;
    }

    // ---- Añadir fuente ---------------------------------------------------------------------------

    /// <summary>Organiza el catálogo por categorías con buscador instantáneo, diseño moderno de 2 columnas y opción de compartir una fuente existente.</summary>
    public static async Task ShowAddSourceAsync(MainViewModel vm, string? initialCategory = null)
    {
        if (vm.EditSceneId is not { } sceneId || vm.Dialogs.Owner is not { } owner)
            return;

        var window = new Window
        {
            Title = "Añadir fuente",
            CanResize = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        FitToScreen(window, 900, 600, minW: 780, minH: 480);
        var available = SourceCatalog.Entries.Where(e => SourceTypeMap.IsAvailable(e.Kind)).ToList();
        var categories = available.Select(e => e.Category).Distinct().ToList();

        var selectedCategory = (!string.IsNullOrEmpty(initialCategory) && categories.Contains(initialCategory)) ? initialCategory : "Todas";
        var current = available.FirstOrDefault(e => selectedCategory == "Todas" || e.Category == selectedCategory) ?? available[0];

        var nameLabel = new TextBlock { Text = "Nombre de la fuente", FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBox { Watermark = "Ej. Pantalla principal...", HorizontalAlignment = HorizontalAlignment.Stretch };
        var deviceLabel = new TextBlock { Text = "Dispositivo de captura", FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
        var devices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var createNew = new RadioButton { Content = "Crear nueva fuente independiente", IsChecked = true, GroupName = "add_source_mode" };
        var useExisting = new RadioButton { Content = "Reutilizar fuente existente", GroupName = "add_source_mode" };
        var existingLabel = new TextBlock { Text = "Fuente existente a compartir", FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
        var existing = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };

        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        var errorCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF28181A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF5C2328")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            IsVisible = false,
            Child = error,
        };

        var allDevices = vm.EngineReady ? await vm.Engine.ListDevicesAsync(CancellationToken.None) : [];
        List<CaptureDevice> currentDevices = [];
        List<Source> currentExisting = [];

        var ok = Primary("Añadir fuente");
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        Guid? createdSource = null;
        var suppressNameChanged = false;
        var nameTouched = false;
        name.TextChanged += (_, _) => { if (!suppressNameChanged) nameTouched = true; };

        // Cabecera de vista previa de la fuente seleccionada (panel derecho)
        var previewIconGlyph = new Glyph { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var previewTitle = new TextBlock { FontWeight = FontWeight.Bold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        var previewCategoryText = new TextBlock { FontSize = 10, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var previewCategoryBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = previewCategoryText,
        };
        var previewDesc = new TextBlock { Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var previewCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF181C23")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF282D36")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 14,
                Children =
                {
                    new Border
                    {
                        Width = 44, Height = 44, CornerRadius = new CornerRadius(8),
                        Background = new SolidColorBrush(Color.Parse("#FF222834")),
                        Child = previewIconGlyph,
                    },
                    WithColumn(new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 4,
                        Children =
                        {
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { previewTitle, previewCategoryBorder } },
                            previewDesc,
                        },
                    }, 1),
                },
            },
        };

        void UpdateModeVisibility()
        {
            var isCreating = createNew.IsChecked == true;
            var deviceKind = DeviceKindFor(current.Kind);
            nameLabel.IsVisible = name.IsVisible = isCreating;
            deviceLabel.IsVisible = devices.IsVisible = isCreating && deviceKind is not null;
            existingLabel.IsVisible = existing.IsVisible = !isCreating;
        }

        createNew.IsCheckedChanged += (_, _) => UpdateModeVisibility();
        useExisting.IsCheckedChanged += (_, _) => UpdateModeVisibility();

        // Control de tarjetas del catálogo en el panel izquierdo
        var cardBorders = new Dictionary<SourceCatalog.Entry, Border>();

        void UpdateSelectionHighlight()
        {
            foreach (var (entry, border) in cardBorders)
            {
                var isSel = entry == current;
                border.Background = new SolidColorBrush(Color.Parse(isSel ? "#FF1A273D" : "#FF181B21"));
                border.BorderBrush = new SolidColorBrush(Color.Parse(isSel ? "#FF3D8BFF" : "#FF262B34"));
                border.BorderThickness = new Thickness(isSel ? 1.5 : 1);
            }
        }

        void OnKindChanged()
        {
            suppressNameChanged = true;
            if (!nameTouched)
                name.Text = NextSourceName(vm, current.DefaultName);
            suppressNameChanged = false;

            previewIconGlyph.Data = SourceCatalog.Geometry(current.Kind);
            previewTitle.Text = current.Label;
            var (catBg, catFg) = CategoryColors(current.Category);
            previewCategoryBorder.Background = new SolidColorBrush(catBg);
            previewCategoryText.Foreground = new SolidColorBrush(catFg);
            previewCategoryText.Text = current.Category;
            previewDesc.Text = current.Description;

            var deviceKind = DeviceKindFor(current.Kind);
            currentDevices = deviceKind is { } k ? allDevices.Where(d => d.Kind == k).ToList() : [];
            devices.ItemsSource = currentDevices.Select(d => d.Name.TrimStart(':', ' ')).ToList();
            devices.SelectedIndex = currentDevices.Count > 0 ? 0 : -1;

            currentExisting = vm.Project.Sources.Where(s => s.Kind == current.Kind).ToList();
            existing.ItemsSource = currentExisting.Select(s => s.Name).ToList();
            existing.SelectedIndex = currentExisting.Count > 0 ? 0 : -1;
            useExisting.IsEnabled = currentExisting.Count > 0;
            useExisting.Content = currentExisting.Count > 0
                ? $"Reutilizar fuente existente ({currentExisting.Count} disponibles)"
                : "Reutilizar fuente existente (ninguna en este proyecto)";
            if (!useExisting.IsEnabled)
                createNew.IsChecked = true;

            UpdateModeVisibility();
            UpdateSelectionHighlight();

            var noDevice = deviceKind is not null && currentDevices.Count == 0;
            if (noDevice)
            {
                error.Text = "No se ha detectado ningún dispositivo conectado para esta fuente. Comprueba la conexión o recarga dispositivos en Ajustes.";
                errorCard.IsVisible = true;
                ok.IsEnabled = false;
            }
            else
            {
                error.Text = "";
                errorCard.IsVisible = false;
                ok.IsEnabled = true;
            }
        }

        async Task SubmitAsync()
        {
            if (!ok.IsEnabled)
                return;

            if (useExisting.IsChecked == true && existing.SelectedIndex >= 0 && existing.SelectedIndex < currentExisting.Count)
            {
                var source = currentExisting[existing.SelectedIndex];
                var result = await vm.Run(new AddExistingSourceCommand(sceneId, source.Id));
                if (result.Success) window.Close(); else { error.Text = result.Error; errorCard.IsVisible = true; }
                return;
            }
            var settings = new JsonObject();
            if (devices.SelectedIndex >= 0 && devices.SelectedIndex < currentDevices.Count)
                settings[DeviceSettingKey(current.Kind)!] = currentDevices[devices.SelectedIndex].Id;
            if (current.Kind == SourceKind.Text)
                settings["text"] = "Texto";
            if (current.Kind == SourceKind.ColorSource)
            {
                settings["width"] = vm.Project.Canvas.Width;
                settings["height"] = vm.Project.Canvas.Height;
            }
            var command = new AddSourceCommand(sceneId, string.IsNullOrWhiteSpace(name.Text) ? current.DefaultName : name.Text.Trim(), current.Kind, settings.ToJsonString());
            var added = await vm.Run(command);
            if (!added.Success)
            {
                error.Text = added.Error;
                errorCard.IsVisible = true;
                return;
            }
            createdSource = command.SourceId;
            vm.SelectFromCanvas(command.ItemId, false);
            window.Close();
        }

        ok.Click += async (_, _) => await SubmitAsync();

        // 1. Cabecera superior moderna con título y buscador
        var searchBox = new TextBox
        {
            Watermark = "Buscar tipo de fuente...",
            Width = 260,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var topHeader = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF15181E")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF282D36")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 12),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = "Añadir fuente a la escena", FontWeight = FontWeight.Bold, FontSize = 16, Foreground = new SolidColorBrush(Color.Parse("#FFE6E8EB")) },
                            new TextBlock { Text = "Selecciona el tipo de contenido que deseas incorporar a la escena activa.", Classes = { "muted" }, FontSize = 11.5 },
                        },
                    },
                    WithColumn(searchBox, 1),
                },
            },
        };

        // 2. Barra de filtros de categoría (chips horizontales)
        var chipsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var categoryPills = new List<Button>();
        var allCategoriesOption = new List<string> { "Todas" };
        allCategoriesOption.AddRange(categories);

        void UpdateFilter()
        {
            var query = (searchBox.Text ?? "").Trim();
            var visibleCount = 0;
            SourceCatalog.Entry? firstVisible = null;
            var currentIsVisible = false;

            foreach (var entry in available)
            {
                var matchesCategory = selectedCategory == "Todas" || entry.Category == selectedCategory;
                var matchesQuery = string.IsNullOrEmpty(query)
                    || entry.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Category.Contains(query, StringComparison.OrdinalIgnoreCase);

                var visible = matchesCategory && matchesQuery;
                if (cardBorders.TryGetValue(entry, out var card))
                {
                    card.IsVisible = visible;
                    if (visible)
                    {
                        visibleCount++;
                        firstVisible ??= entry;
                        if (entry == current)
                            currentIsVisible = true;
                    }
                }
            }

            if (!currentIsVisible && firstVisible is not null)
            {
                current = firstVisible;
                OnKindChanged();
            }

            foreach (var pill in categoryPills)
            {
                var catName = pill.Tag as string ?? "";
                var isSelected = catName == selectedCategory;
                pill.Background = new SolidColorBrush(Color.Parse(isSelected ? "#FF254876" : "#FF1E2126"));
                pill.BorderBrush = new SolidColorBrush(Color.Parse(isSelected ? "#FF3D8BFF" : "#FF2C323D"));
                pill.Foreground = new SolidColorBrush(Color.Parse(isSelected ? "#FFFFFFFF" : "#FF9AA1AB"));
                pill.FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal;
            }
        }

        foreach (var cat in allCategoriesOption)
        {
            var count = cat == "Todas" ? available.Count : available.Count(e => e.Category == cat);
            var pill = new Button
            {
                Content = $"{cat} ({count})",
                Tag = cat,
                Padding = new Thickness(12, 5),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                FontSize = 11.5,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            pill.Click += (_, _) =>
            {
                selectedCategory = cat;
                UpdateFilter();
            };
            categoryPills.Add(pill);
            chipsPanel.Children.Add(pill);
        }

        searchBox.TextChanged += (_, _) => UpdateFilter();

        var filterBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF131519")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF242830")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 7),
            Child = chipsPanel,
        };

        // 3. Panel izquierdo: Catálogo de fuentes
        var catalogCardsPanel = new StackPanel { Spacing = 4 };
        foreach (var entry in available)
        {
            var (catBg, catFg) = CategoryColors(entry.Category);
            var catTag = new Border
            {
                Background = new SolidColorBrush(catBg),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = entry.Category, FontSize = 9.5, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(catFg) },
            };

            var card = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FF181B21")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF262B34")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                    ColumnSpacing = 10,
                    Children =
                    {
                        new Border
                        {
                            Width = 38, Height = 38, CornerRadius = new CornerRadius(6),
                            Background = new SolidColorBrush(Color.Parse("#FF20252F")),
                            Child = new Glyph { Data = SourceCatalog.Geometry(entry.Kind), Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                        },
                        WithColumn(new StackPanel
                        {
                            Orientation = Orientation.Vertical,
                            Spacing = 2,
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock { Text = entry.Label, FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = new SolidColorBrush(Color.Parse("#FFE6E8EB")), TextTrimming = TextTrimming.CharacterEllipsis },
                                new TextBlock { Text = entry.Description, Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 2 },
                            },
                        }, 1),
                        WithColumn(catTag, 2),
                    },
                },
            };

            var localEntry = entry;
            card.PointerEntered += (_, _) =>
            {
                if (localEntry != current)
                    card.Background = new SolidColorBrush(Color.Parse("#FF20252E"));
            };
            card.PointerExited += (_, _) =>
            {
                if (localEntry != current)
                    card.Background = new SolidColorBrush(Color.Parse("#FF181B21"));
            };
            card.PointerPressed += (_, _) =>
            {
                current = localEntry;
                OnKindChanged();
            };
            card.DoubleTapped += async (_, _) =>
            {
                current = localEntry;
                OnKindChanged();
                await SubmitAsync();
            };

            cardBorders[entry] = card;
            catalogCardsPanel.Children.Add(card);
        }

        var catalogScroll = new ScrollViewer
        {
            Content = catalogCardsPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14, 10, 10, 10),
        };

        var leftHeader = new Border
        {
            Padding = new Thickness(14, 10, 14, 6),
            Child = new TextBlock { Text = "TIPOS DISPONIBLES", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#FF8B949E")) },
        };
        var leftPane = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { leftHeader, WithRow(catalogScroll, 1) },
        };

        // 4. Panel derecho: Formulario de configuración
        var modeContainer = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF181C23")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF282D36")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "MODO DE INCORPORACIÓN", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#FF8B949E")) },
                    createNew,
                    useExisting,
                },
            },
        };

        var formFieldsPanel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "PARÁMETROS INICIALES", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#FF8B949E")) },
                new StackPanel { Spacing = 4, Children = { nameLabel, name } },
                new StackPanel { Spacing = 4, Children = { deviceLabel, devices } },
                new StackPanel { Spacing = 4, Children = { existingLabel, existing } },
            },
        };

        var formCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF181C23")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF282D36")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = formFieldsPanel,
        };

        var tipCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF15202E")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF243854")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock
            {
                Text = "💡 Tras añadir la fuente se abrirá el panel de edición detallada para calibrar filtros, resolución y parámetros adicionales.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#FF93C5FD")),
                TextWrapping = TextWrapping.Wrap,
            },
        };

        var rightContentStack = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                previewCard,
                modeContainer,
                formCard,
                tipCard,
                errorCard,
            },
        };

        var rightScroll = new ScrollViewer
        {
            Content = rightContentStack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14, 10, 16, 12),
        };

        var rightHeader = new Border
        {
            Padding = new Thickness(14, 10, 14, 6),
            Child = new TextBlock { Text = "CONFIGURACIÓN", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#FF8B949E")) },
        };
        var rightPane = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { rightHeader, WithRow(rightScroll, 1) },
        };

        // Estructura principal de 2 columnas equilibradas
        var columnsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("390,1,*"),
            Children =
            {
                leftPane,
                WithColumn(new Border { Background = new SolidColorBrush(Color.Parse("#FF262B34")) }, 1),
                WithColumn(rightPane, 2),
            },
        };

        var overallLayout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Children =
            {
                topHeader,
                WithRow(filterBar, 1),
                WithRow(columnsGrid, 2),
            },
        };

        OnKindChanged();
        UpdateFilter();

        var tipFooter = new TextBlock
        {
            Text = "Doble clic en un tipo de fuente para añadirla directamente.",
            Classes = { "muted" },
            FontSize = 11,
        };

        window.Content = LayoutWithTip(overallLayout, tipFooter, cancel, ok);
        await window.ShowDialog(owner);

        if (createdSource is { } created)
            await ShowPropertiesAsync(vm, created, justCreated: true);
    }

    // ---- Crear escena con plantilla --------------------------------------------------------------

    /// <summary>Diálogo de nueva escena: nombre y plantilla preconfigurada (vacía, cámara, gameplay, entrevista…).</summary>
    public static async Task ShowCreateSceneAsync(MainViewModel vm)
    {
        if (vm.Dialogs.Owner is not { } owner)
            return;

        var window = NewWindow("Nueva escena", 620, 470);
        var name = new TextBox { Watermark = "Nombre de la escena" };
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var options = SceneTemplates.Options;
        ToggleButton? currentToggle = null;
        var current = options[0];

        ToggleButton Row(SceneTemplateOption option)
        {
            var toggle = new ToggleButton
            {
                Padding = new Thickness(10, 8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = option.Name, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = option.Hint, Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                    },
                },
            };
            AutomationProperties.SetName(toggle, option.Name);
            toggle.Click += (_, _) =>
            {
                if (currentToggle is not null && !ReferenceEquals(currentToggle, toggle))
                    currentToggle.IsChecked = false;
                currentToggle = toggle;
                current = option;
            };
            toggle.IsChecked = option == current;
            if (toggle.IsChecked == true)
                currentToggle = toggle;
            return toggle;
        }

        var selector = new StackPanel { Spacing = 6 };
        foreach (var option in options)
            selector.Children.Add(Row(option));

        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(16),
            RowSpacing = 12,
        };
        name.Text = vm.Project.NextSceneName("Escena");
        var top = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Nombre" }, name, error } };
        body.Children.Add(top);
        var scroll = new ScrollViewer { Content = selector };
        Grid.SetRow(scroll, 1);
        body.Children.Add(scroll);

        var ok = Primary("Crear");
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        ok.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                error.Text = "El nombre de la escena no puede estar vacío.";
                return;
            }
            var id = await vm.AddSceneWithTemplateAsync(name.Text.Trim(), current.Kind);
            if (id is null)
                error.Text = vm.StatusMessage;
            else
                window.Close();
        };
        window.Content = Layout(body, cancel, ok);
        await window.ShowDialog(owner);
    }

    private static string NextSourceName(MainViewModel vm, string baseName)    {
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

    /// <summary>Edición y ajuste de propiedades en tiempo real de una fuente con diseño moderno e informativo.</summary>
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
        var window = NewWindow($"Editar fuente — {source.Name}", 880, 780);
        var nameBox = new TextBox { Text = source.Name, Watermark = "Nombre de la fuente...", HorizontalAlignment = HorizontalAlignment.Stretch };
        var editor = new PropertyEditor();
        var status = new TextBlock { Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap };

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

        var kindEntry = SourceCatalog.Entries.FirstOrDefault(e => e.Kind == source.Kind);
        var kindLabel = kindEntry?.Label ?? SourceCatalog.Label(source.Kind);
        var kindCategory = kindEntry?.Category ?? SourceCatalog.Category(source.Kind);
        var kindDesc = kindEntry?.Description ?? "Fuente de contenido del estudio";
        var (catBg, catFg) = CategoryColors(kindCategory);

        var headerCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF161A22")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF282D36")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(20, 14),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 14,
                Children =
                {
                    new Border
                    {
                        Width = 44,
                        Height = 44,
                        CornerRadius = new CornerRadius(8),
                        Background = new SolidColorBrush(Color.Parse("#FF222834")),
                        Child = new Glyph
                        {
                            Data = SourceCatalog.Geometry(source.Kind),
                            Width = 24,
                            Height = 24,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                    WithColumn(new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 4,
                        Children =
                        {
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                Children =
                                {
                                    new TextBlock { Text = source.Name, FontWeight = FontWeight.Bold, FontSize = 16, VerticalAlignment = VerticalAlignment.Center },
                                    new Border
                                    {
                                        Background = new SolidColorBrush(catBg),
                                        CornerRadius = new CornerRadius(4),
                                        Padding = new Thickness(6, 2),
                                        VerticalAlignment = VerticalAlignment.Center,
                                        Child = new TextBlock { Text = kindCategory, FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(catFg) },
                                    },
                                    new Border
                                    {
                                        Background = new SolidColorBrush(Color.Parse("#FF1E2838")),
                                        CornerRadius = new CornerRadius(4),
                                        Padding = new Thickness(6, 2),
                                        VerticalAlignment = VerticalAlignment.Center,
                                        Child = new TextBlock { Text = kindLabel, FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#FF93C5FD")) },
                                    },
                                    new Border
                                    {
                                        Background = new SolidColorBrush(Color.Parse("#FF162E22")),
                                        CornerRadius = new CornerRadius(4),
                                        Padding = new Thickness(6, 2),
                                        VerticalAlignment = VerticalAlignment.Center,
                                        Child = new TextBlock { Text = "● Señal activa", FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#FF34D399")) },
                                    },
                                },
                            },
                            new TextBlock { Text = kindDesc, Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap },
                        },
                    }, 1),
                },
            },
        };

        // Componentes de vista previa en tiempo real
        LiveSourcePreview? preview = null;
        AudioMeterControl? meter = null;

        if (!source.IsAudioOnly)
        {
            preview = new LiveSourcePreview { Height = 250, Margin = new Thickness(0, 0, 0, 14) };
            preview.Attach(vm, sourceId, allowCompare: false);
        }

        if (source.SupportsAudio)
        {
            meter = new AudioMeterControl { Margin = new Thickness(0, 4, 0, 8) };
            meter.Attach(vm, sourceId);
        }

        var generalCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1A1D24")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF282D36")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "IDENTIFICACIÓN Y NOMBRE", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#FF8B949E")) },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("180,*"),
                        ColumnSpacing = 16,
                        Children =
                        {
                            new TextBlock { Text = "Nombre en la escena", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Medium },
                            WithColumn(nameBox, 1),
                        },
                    },
                },
            },
        };

        // Tarjeta de control de audio independiente y enrutamiento
        Border audioCard;
        if (!source.SupportsAudio)
        {
            audioCard = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FF1A1D24")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF282D36")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "AUDIO Y ENRUTAMIENTO", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#FF8B949E")) },
                        new TextBlock { Text = "🔇 Esta fuente solo procesa vídeo y no proporciona canales de audio.", Classes = { "muted" }, FontSize = 12 }
                    }
                }
            };
        }
        else
        {
            var audioCaptureCheck = new CheckBox
            {
                Content = "Capturar audio de esta fuente",
                IsChecked = source.AudioCaptureEnabled,
                FontWeight = FontWeight.Medium
            };
            ToolTip.SetTip(audioCaptureCheck, "Activa o desactiva la captura de audio de esta fuente. Desactívala si usas un micrófono externo para evitar capturar audio dos veces.");

            var audioModeNotice = new TextBlock
            {
                Text = source.AudioCaptureEnabled
                    ? "El audio de este dispositivo se envía a la mezcla general."
                    : "⚠️ Modo: Solo vídeo. El motor libobs no capturará el audio de esta fuente para evitar duplicados.",
                Classes = { "muted" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            };

            var volumeSlider = new Slider
            {
                Minimum = 0,
                Maximum = 1.5,
                Value = source.Volume,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var initialDb = 20.0 * Math.Log10(Math.Max(0.0001, source.Volume));
            var volumeLabel = new TextBlock
            {
                Text = $"{initialDb:0.0} dB",
                FontSize = 11,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 55,
                TextAlignment = TextAlignment.Right
            };
            volumeSlider.PropertyChanged += async (s, e) =>
            {
                if (e.Property == Slider.ValueProperty && volumeSlider.Value is double val)
                {
                    var db = 20.0 * Math.Log10(Math.Max(0.0001, val));
                    volumeLabel.Text = $"{db:0.0} dB";
                    await vm.Run(new SetVolumeCommand(sourceId, val));
                }
            };

            var muteCheck = new CheckBox
            {
                Content = "Silenciar audio (Mute)",
                IsChecked = source.Muted,
                VerticalAlignment = VerticalAlignment.Center
            };
            muteCheck.IsCheckedChanged += async (_, _) =>
            {
                await vm.Run(new SetMutedCommand(sourceId, muteCheck.IsChecked == true));
            };

            var monitoringCombo = new ComboBox
            {
                ItemsSource = new List<string>
                {
                    "Desactivada (Solo emisión en directo)",
                    "Solo monitorización (Escuchar en auriculares)",
                    "Monitorización y emisión (Ambas)"
                },
                SelectedIndex = source.Monitoring switch
                {
                    AudioMonitoring.MonitorOnly => 1,
                    AudioMonitoring.MonitorAndOutput => 2,
                    _ => 0
                },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            monitoringCombo.SelectionChanged += async (_, _) =>
            {
                var mon = monitoringCombo.SelectedIndex switch
                {
                    1 => AudioMonitoring.MonitorOnly,
                    2 => AudioMonitoring.MonitorAndOutput,
                    _ => AudioMonitoring.None
                };
                await vm.Run(new SetMonitoringCommand(sourceId, mon));
            };

            var syncOffsetBox = new NumericUpDown
            {
                Minimum = -5000,
                Maximum = 5000,
                Increment = 10,
                Value = source.SyncOffsetMs,
                FormatString = "0 ms",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            syncOffsetBox.ValueChanged += async (_, _) =>
            {
                if (syncOffsetBox.Value is { } ms)
                    await vm.Run(new SetSyncOffsetCommand(sourceId, (int)ms));
            };

            var audioControlsContainer = new StackPanel
            {
                Spacing = 10,
                IsVisible = source.AudioCaptureEnabled
            };

            if (meter is not null)
                audioControlsContainer.Children.Add(meter);

            var volRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("160,*,Auto"),
                ColumnSpacing = 12,
                Children =
                {
                    new TextBlock { Text = "Volumen de entrada", VerticalAlignment = VerticalAlignment.Center },
                    WithColumn(volumeSlider, 1),
                    WithColumn(volumeLabel, 2)
                }
            };
            audioControlsContainer.Children.Add(volRow);

            var muteRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("160,*"),
                ColumnSpacing = 12,
                Children =
                {
                    new TextBlock { Text = "Estado de silencio", VerticalAlignment = VerticalAlignment.Center },
                    WithColumn(muteCheck, 1)
                }
            };
            audioControlsContainer.Children.Add(muteRow);

            var monRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("160,*"),
                ColumnSpacing = 12,
                Children =
                {
                    new TextBlock { Text = "Monitorización de audio", VerticalAlignment = VerticalAlignment.Center },
                    WithColumn(monitoringCombo, 1)
                }
            };
            audioControlsContainer.Children.Add(monRow);

            var syncRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("160,*"),
                ColumnSpacing = 12,
                Children =
                {
                    new TextBlock { Text = "Sincronización (Offset)", VerticalAlignment = VerticalAlignment.Center },
                    WithColumn(syncOffsetBox, 1)
                }
            };
            audioControlsContainer.Children.Add(syncRow);

            audioCaptureCheck.IsCheckedChanged += async (_, _) =>
            {
                var enabled = audioCaptureCheck.IsChecked == true;
                audioControlsContainer.IsVisible = enabled;
                audioModeNotice.Text = enabled
                    ? "El audio de este dispositivo se envía a la mezcla general."
                    : "⚠️ Modo: Solo vídeo. El motor libobs no capturará el audio de esta fuente para evitar duplicados.";
                await vm.Run(new SetAudioCaptureEnabledCommand(sourceId, enabled));
            };

            var audioStack = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "CONTROL DE AUDIO Y ENRUTAMIENTO", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#FF8B949E")) },
                }
            };

            if (!source.IsAudioOnly)
            {
                audioStack.Children.Add(audioCaptureCheck);
                audioStack.Children.Add(audioModeNotice);
            }
            audioStack.Children.Add(audioControlsContainer);

            audioCard = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FF1A1D24")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF282D36")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 14),
                Child = audioStack
            };
        }

        var propertiesCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1A1D24")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF282D36")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "AJUSTES ESPECÍFICOS DEL DISPOSITIVO", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#FF8B949E")) },
                    editor,
                    status,
                },
            },
        };

        var tipNotice = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF15202E")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF243854")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Child = new TextBlock
            {
                Text = "⚡ Los cambios realizados se previsualizan automáticamente en el motor y se consolidan al hacer clic en Guardar cambios.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#FF93C5FD")),
                TextWrapping = TextWrapping.Wrap,
            },
        };

        var contentStack = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 14,
        };

        if (preview is not null)
            contentStack.Children.Add(preview);

        contentStack.Children.Add(generalCard);
        contentStack.Children.Add(audioCard);
        contentStack.Children.Add(propertiesCard);
        contentStack.Children.Add(tipNotice);

        var mainLayout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                headerCard,
                WithRow(new ScrollViewer { Content = contentStack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 1),
            },
        };

        var accepted = false;
        var ok = Primary("Guardar cambios");
        var cancel = new Button { Content = justCreated ? "Usar valores por defecto" : "Cancelar", IsCancel = true };
        ok.Click += (_, _) =>
        {
            accepted = true;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();

        var filtersBtn = new Button
        {
            Content = "Filtros de la fuente…",
            Margin = new Thickness(0, 0, 8, 0)
        };
        filtersBtn.Click += async (_, _) =>
        {
            await ProductionDialogs.ShowFiltersAsync(vm, sourceId);
        };

        var idLabel = new TextBlock
        {
            Text = $"ID: {source.Id.ToString()[..8]}...",
            Classes = { "muted" },
            FontSize = 11,
        };

        window.Content = LayoutWithTip(mainLayout, idLabel, filtersBtn, cancel, ok);
        await window.ShowDialog(owner);
        pending?.Cancel();

        meter?.Dispose();
        if (preview is not null)
            await preview.DisposeAsync();

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

    internal static Control WithColumn(Control c, int column)
    {
        Grid.SetColumn(c, column);
        return c;
    }

    internal static Control WithRow(Control c, int row)
    {
        Grid.SetRow(c, row);
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
            var updated = new LiveStream.Domain.ValueObjects.Transform(V(x), V(y), V(sx) / 100, V(sy) / 100, V(rot), (int)V(cl), (int)V(ct), (int)V(cr), (int)V(cb));
            var result = await vm.Run(new SetItemTransformCommand(itemId, updated));
            if (result.Success) window.Close(); else error.Text = result.Error;
        };
        window.Content = Layout(new StackPanel { Children = { grid, new Border { Child = error, Padding = new Thickness(16, 0) } } }, cancel, ok);
        await window.ShowDialog(owner);
    }

    // ---- Ajustes ---------------------------------------------------------------------------------

    // ---- Ajustes (Estilo OBS Studio Moderno con Categorías Laterales) -----------------------------

    public static async Task ShowSettingsAsync(MainViewModel vm, string? initialPage = null)
    {
        if (vm.Dialogs.Owner is not { } owner)
            return;

        var project = vm.Project;
        var machineSettings = vm.Machine.Load();

        var encoders = vm.EngineReady ? await vm.Engine.ListVideoEncodersAsync(CancellationToken.None) : [];
        var encoderIds = new List<string> { "auto" }.Concat(encoders.Select(e => e.Id)).ToList();
        var encoderNames = new List<string> { "Automático (el mejor disponible)" }.Concat(encoders.Select(e => $"{e.Name} — {e.Codec.ToUpperInvariant()}")).ToList();
        ComboBox EncoderBox(string current) => new() { ItemsSource = encoderNames, SelectedIndex = Math.Max(0, encoderIds.IndexOf(current)), HorizontalAlignment = HorizontalAlignment.Stretch };
        NumericUpDown Num(double value, double min, double max, double step, string format = "0") => new() { Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, FormatString = format, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };

        Border GroupBox(string title, Control content)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FF161A22")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF232832")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 12),
                Margin = new Thickness(0, 0, 0, 14),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#FF3D8BFF")), Margin = new Thickness(0, 0, 0, 4) },
                        content,
                    },
                },
            };
        }

        StackPanel Form(params (string Label, Control Control)[] rows)
        {
            var panel = new StackPanel { Spacing = 10 };
            foreach (var (label, control) in rows)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), ColumnSpacing = 12 };
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
                row.Children.Add(WithColumn(control, 1));
                panel.Children.Add(row);
            }
            return panel;
        }

        // ==================== 1. GENERAL ====================
        var langBox = new ComboBox { ItemsSource = new[] { "Español", "English", "Português", "Français", "Deutsch" }, SelectedItem = machineSettings.Language, HorizontalAlignment = HorizontalAlignment.Stretch };
        var themeBox = new ComboBox { ItemsSource = new[] { "Oscuro (Grafito / LiveStream Pro)", "Negro OLED", "Claro (Experimental)" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var updatesCheck = new CheckBox { Content = "Comprobar actualizaciones automáticamente al inicio", IsChecked = machineSettings.CheckUpdatesAtStartup };
        var channelBox = new ComboBox { ItemsSource = new[] { "Estable (Predeterminado)", "Beta", "Desarrollo / Nightly" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };

        var confirmStartStream = new CheckBox { Content = "Mostrar diálogo de confirmación cuando se inicia una transmisión", IsChecked = machineSettings.ConfirmStartStream };
        var confirmStopStream = new CheckBox { Content = "Mostrar diálogo de confirmación cuando se para una transmisión", IsChecked = machineSettings.ConfirmStopStream };
        var confirmStopRecord = new CheckBox { Content = "Mostrar diálogo de confirmación al detener la grabación", IsChecked = machineSettings.ConfirmStopRecord };
        var autoRecordOnStream = new CheckBox { Content = "Grabar automáticamente cuando se transmite", IsChecked = machineSettings.AutoRecordOnStream };
        var keepRecordingOnStreamStop = new CheckBox { Content = "Mantener la grabación cuando se detiene la transmisión", IsChecked = machineSettings.KeepRecordingOnStreamStop };

        var snapEnabled = new CheckBox { Content = "Habilitar guías magnéticas de alineación en el lienzo", IsChecked = machineSettings.SnapEnabled };
        var snapSens = Num(machineSettings.SnapSensitivity, 1, 50, 1, "0.0");
        var snapScreen = new CheckBox { Content = "Ajustar las fuentes al borde de la pantalla", IsChecked = machineSettings.SnapToScreenEdges };
        var snapOthers = new CheckBox { Content = "Ajustar las fuentes a otras fuentes", IsChecked = machineSettings.SnapToOtherSources };

        var generalView = new ScrollViewer
        {
            Padding = new Thickness(16),
            Content = new StackPanel
            {
                Children =
                {
                    GroupBox("Idioma y Tema", Form(("Idioma de interfaz", langBox), ("Tema visual", themeBox))),
                    GroupBox("Actualizaciones", new StackPanel { Spacing = 8, Children = { Form(("Canal de actualizaciones", channelBox)), updatesCheck } }),
                    GroupBox("Salida y Confirmaciones", new StackPanel { Spacing = 8, Children = { confirmStartStream, confirmStopStream, confirmStopRecord, autoRecordOnStream, keepRecordingOnStreamStop } }),
                    GroupBox("Ajuste de alineación de la fuente (Lienzo)", new StackPanel { Spacing = 8, Children = { snapEnabled, Form(("Sensibilidad de ajuste", snapSens)), snapScreen, snapOthers } }),
                },
            },
        };

        // ==================== 2. EMISIÓN / TRANSMISIÓN ====================
        var destinations = new Button { Content = $"Configurar destinos de emisión… ({project.Destinations.Count(d => d.Enabled)} activos)", HorizontalAlignment = HorizontalAlignment.Left };
        destinations.Click += async (_, _) => await DestinationsDialog.ShowAsync(vm);
        var streamEnc = EncoderBox(project.Stream.EffectiveEncoder.VideoEncoderId);
        var streamVideo = Num(project.Stream.EffectiveEncoder.VideoBitrateKbps, 200, 150_000, 500);
        var streamAudio = Num(project.Stream.EffectiveEncoder.AudioBitrateKbps, 32, 512, 32);
        var keyframe = Num(project.Stream.EffectiveEncoder.KeyframeIntervalSec, 0, 20, 1);
        var rateControl = new ComboBox { ItemsSource = new[] { "CBR (Bitrate Constante - Recomendado)", "VBR (Bitrate Variable)", "CQP" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var profile = new ComboBox { ItemsSource = new[] { "high", "main", "baseline" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };

        var streamView = new ScrollViewer
        {
            Padding = new Thickness(16),
            Content = new StackPanel
            {
                Children =
                {
                    GroupBox("Destinos y Servidores", new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            destinations,
                            new TextBlock { Text = "Soporta RTMP/RTMPS hacia YouTube, Twitch, Kick, Facebook y servidores personalizados con claves cifradas (DPAPI).", Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                        },
                    }),
                    GroupBox("Codificador de Emisión (Compartido)", Form(
                        ("Codificador de video", streamEnc),
                        ("Control de frecuencia", rateControl),
                        ("Bitrate de video (kb/s)", streamVideo),
                        ("Intervalo de fotogramas clave (s)", keyframe),
                        ("Bitrate de audio (kb/s)", streamAudio),
                        ("Perfil de codificación", profile))),
                },
            },
        };

        // ==================== 3. SALIDA / GRABACIÓN ====================
        var folder = new TextBox { Text = project.Recording.Folder, Watermark = Path.GetDirectoryName(vm.Session.NextRecordingPath(DateTime.Now)) };
        var browse = new Button { Content = "Examinar…" };
        browse.Click += async (_, _) => { if (await vm.Dialogs.PickFolderAsync("Carpeta de grabación") is { } f) folder.Text = f; };
        var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { folder, WithColumn(browse, 1) } };
        var container = new ComboBox { ItemsSource = RecordingPreferences.SupportedContainers, SelectedItem = project.Recording.Container, HorizontalAlignment = HorizontalAlignment.Stretch };
        var recEnc = EncoderBox(project.Recording.EffectiveEncoder.VideoEncoderId);
        var recVideo = Num(project.Recording.EffectiveEncoder.VideoBitrateKbps, 200, 150_000, 1000);
        var recAudioBitrate = Num(project.Recording.EffectiveEncoder.AudioBitrateKbps, 64, 512, 32);
        var recQualityPreset = new ComboBox { ItemsSource = new[] { "Alta calidad (tamaño de archivo medio)", "Calidad indistinguible (archivo grande)", "Sin pérdida (archivo muy grande)", "Misma que la emisión" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };

        var outputView = new ScrollViewer
        {
            Padding = new Thickness(16),
            Content = new StackPanel
            {
                Children =
                {
                    GroupBox("Ruta y Contenedor de Grabación", Form(
                        ("Ruta de grabación", folderRow),
                        ("Formato del contenedor", container),
                        ("Preajuste de calidad", recQualityPreset))),
                    GroupBox("Codificador de Grabación", Form(
                        ("Codificador de video", recEnc),
                        ("Bitrate de video (kb/s)", recVideo),
                        ("Bitrate de audio (kb/s)", recAudioBitrate))),
                },
            },
        };

        // ==================== 4. AUDIO ====================
        var allAudioDevices = vm.EngineReady ? await vm.Engine.ListDevicesAsync(CancellationToken.None) : [];
        var audioOutputDevices = allAudioDevices.Where(d => d.Kind == CaptureDeviceKind.AudioOutput).ToList();
        var micDevices = allAudioDevices.Where(d => d.Kind == CaptureDeviceKind.Microphone).ToList();

        var desktopOptions = new List<(string Name, string Id)> { ("Dispositivo predeterminado del sistema", "default") };
        foreach (var d in audioOutputDevices)
            desktopOptions.Add((d.Name, d.Id));
        desktopOptions.Add(("Deshabilitado (sin audio de escritorio)", "disabled"));

        var micOptions = new List<(string Name, string Id)> { ("Micrófono predeterminado del sistema", "default") };
        foreach (var d in micDevices)
            micOptions.Add((d.Name, d.Id));
        micOptions.Add(("Deshabilitado (sin micrófono)", "disabled"));

        var currentDesktop = project.Sources.FirstOrDefault(s => s.Kind == SourceKind.AudioOutput && s.GlobalAudioChannel > 0);
        var currentDesktopId = "default";
        if (currentDesktop == null || currentDesktop.GlobalAudioChannel == 0)
        {
            currentDesktopId = "disabled";
        }
        else
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(currentDesktop.SettingsJson);
                if (doc.RootElement.TryGetProperty("device_id", out var p))
                    currentDesktopId = p.GetString() ?? "default";
            }
            catch (Exception ex) { Infrastructure.Diagnostics.AppLog.Warn("audio", $"Ajustes de audio: {ex.Message}"); }
        }

        var currentMic = project.Sources.FirstOrDefault(s => s.Kind == SourceKind.AudioInput && s.GlobalAudioChannel > 0);
        var currentMicId = "default";
        if (currentMic == null || currentMic.GlobalAudioChannel == 0)
        {
            currentMicId = "disabled";
        }
        else
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(currentMic.SettingsJson);
                if (doc.RootElement.TryGetProperty("device_id", out var p))
                    currentMicId = p.GetString() ?? "default";
            }
            catch (Exception ex) { Infrastructure.Diagnostics.AppLog.Warn("audio", $"Ajustes de audio: {ex.Message}"); }
        }

        var desktopAudioBox = new ComboBox
        {
            ItemsSource = desktopOptions.Select(o => o.Name).ToList(),
            SelectedIndex = Math.Max(0, desktopOptions.FindIndex(o => o.Id == currentDesktopId)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        if (desktopAudioBox.SelectedIndex < 0) desktopAudioBox.SelectedIndex = 0;

        var micDeviceBox = new ComboBox
        {
            ItemsSource = micOptions.Select(o => o.Name).ToList(),
            SelectedIndex = Math.Max(0, micOptions.FindIndex(o => o.Id == currentMicId)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        if (micDeviceBox.SelectedIndex < 0) micDeviceBox.SelectedIndex = 0;

        var sampleRateBox = new ComboBox { ItemsSource = new[] { "48 kHz (Predeterminado de estudio)", "44.1 kHz" }, SelectedIndex = machineSettings.AudioSampleRate == 44100 ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var channelsBox = new ComboBox { ItemsSource = new[] { "Estéreo (2.0)", "Mono" }, SelectedIndex = machineSettings.AudioChannels == 1 ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var monitorDeviceBox = new ComboBox
        {
            ItemsSource = new[] { "Dispositivo predeterminado del sistema" }.Concat(audioOutputDevices.Select(d => d.Name)).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var audioView = new ScrollViewer
        {
            Padding = new Thickness(16),
            Content = new StackPanel
            {
                Children =
                {
                    GroupBox("Dispositivos de Audio Globales", Form(
                        ("Audio del escritorio", desktopAudioBox),
                        ("Dispositivo de micrófono", micDeviceBox),
                        ("Dispositivo de monitorización", monitorDeviceBox))),
                    GroupBox("Formato de Audio", Form(
                        ("Frecuencia de muestreo", sampleRateBox),
                        ("Canales", channelsBox))),
                },
            },
        };

        // ==================== 5. VIDEO ====================
        var width = Num(project.Canvas.Width, 2, 7680, 2);
        var height = Num(project.Canvas.Height, 2, 7680, 2);
        var fps = new ComboBox { ItemsSource = new[] { 24, 25, 30, 48, 50, 60 }, SelectedItem = project.Canvas.Fps, HorizontalAlignment = HorizontalAlignment.Stretch };
        var scaleFilter = new ComboBox { ItemsSource = new[] { "Bicúbico (escalado nítido, 16 muestras)", "Lanczos (escalado fino, 36 muestras)", "Bilineal (más rápido pero borroso)" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };

        var videoView = new ScrollViewer
        {
            Padding = new Thickness(16),
            Content = new StackPanel
            {
                Children =
                {
                    GroupBox("Resolución y Lienzo", Form(
                        ("Resolución de la base (lienzo)", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { width, new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center }, height } }),
                        ("Filtro de escalado", scaleFilter),
                        ("Valores comunes de FPS", fps))),
                    new TextBlock
                    {
                        Text = "Nota: Para emisiones en formato vertical (TikTok / Reels / Shorts), define 1080 × 1920. El motor libobs actualiza el lienzo al guardar o reiniciar.",
                        Classes = { "muted" },
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(4, 0),
                    },
                },
            },
        };

        // ==================== 6. ATAJOS DE TECLADO ====================
        var hotkeysView = new ScrollViewer
        {
            Padding = new Thickness(16),
            Content = new StackPanel
            {
                Children =
                {
                    GroupBox("Atajos de Transmisión y Grabación", Form(
                        ("Iniciar / Detener transmisión", new TextBox { Text = "Ctrl + F9", IsReadOnly = true }),
                        ("Iniciar / Detener grabación", new TextBox { Text = "Ctrl + F10", IsReadOnly = true }),
                        ("Transición a Programa (Corte)", new TextBox { Text = "Espacio", IsReadOnly = true }),
                        ("Modo Estudio (Alternar)", new TextBox { Text = "Ctrl + Shift + S", IsReadOnly = true }))),
                    GroupBox("Atajos de Edición y Escenas", Form(
                        ("Seleccionar siguiente escena", new TextBox { Text = "Alt + Abajo", IsReadOnly = true }),
                        ("Seleccionar escena anterior", new TextBox { Text = "Alt + Arriba", IsReadOnly = true }),
                        ("Bloquear fuente", new TextBox { Text = "Ctrl + L", IsReadOnly = true }),
                        ("Mostrar / Ocultar fuente", new TextBox { Text = "Ctrl + H", IsReadOnly = true }),
                        ("Duplicar elemento", new TextBox { Text = "Ctrl + D", IsReadOnly = true }))),
                },
            },
        };

        // ==================== 7. AVANZADO ====================
        var adapterIndex = new ComboBox { ItemsSource = new[] { "0: GPU Predeterminada (Direct3D 11)", "1: Adaptador secundario / Integrada" }, SelectedIndex = Math.Min(1, machineSettings.RenderAdapterIndex), HorizontalAlignment = HorizontalAlignment.Stretch };
        var colorFormat = new ComboBox { ItemsSource = new[] { "NV12 (Recomendado)", "I420", "RGB" }, SelectedItem = machineSettings.ColorFormat, HorizontalAlignment = HorizontalAlignment.Stretch };
        var colorSpace = new ComboBox { ItemsSource = new[] { "sRGB", "Rec. 709", "Rec. 2100 (HDR)" }, SelectedItem = machineSettings.ColorSpace, HorizontalAlignment = HorizontalAlignment.Stretch };
        var colorRange = new ComboBox { ItemsSource = new[] { "Parcial (Predeterminado)", "Completo" }, SelectedItem = machineSettings.ColorRange, HorizontalAlignment = HorizontalAlignment.Stretch };
        var processPriority = new ComboBox { ItemsSource = new[] { "Alta", "Mayor que normal (Recomendado)", "Normal", "Baja" }, SelectedIndex = machineSettings.ProcessPriority, HorizontalAlignment = HorizontalAlignment.Stretch };

        var adaptiveBitrate = new CheckBox { Content = "Habilitar gestión dinámica de bitrate si hay saturación de red", IsChecked = machineSettings.AdaptiveBitrate };
        var devMode = new CheckBox { Content = "Modo desarrollador de plugins (permite paquetes sin firma .lsplugin)", IsChecked = machineSettings.PluginDeveloperMode };

        var advancedView = new ScrollViewer
        {
            Padding = new Thickness(16),
            Content = new StackPanel
            {
                Children =
                {
                    GroupBox("Procesamiento y Rendimiento", Form(
                        ("Prioridad del proceso en Windows", processPriority),
                        ("Adaptador de video Direct3D", adapterIndex))),
                    GroupBox("Formato de Color de Video", Form(
                        ("Formato de color", colorFormat),
                        ("Espacio de color", colorSpace),
                        ("Gama de colores", colorRange))),
                    GroupBox("Red y Plugins", new StackPanel { Spacing = 10, Children = { adaptiveBitrate, devMode } }),
                },
            },
        };

        // ==================== NAVEGADOR LATERAL OBS ====================
        var pages = new (string Title, string IconKey, Control View)[]
        {
            ("General", "Icon.Gear", generalView),
            ("Emisión", "Icon.Broadcast", streamView),
            ("Salida", "Icon.Record", outputView),
            ("Audio", "Icon.Speaker", audioView),
            ("Video", "Icon.Display", videoView),
            ("Atajos", "Icon.Layers", hotkeysView),
            ("Avanzado", "Icon.Sliders", advancedView),
        };

        var contentHost = new ContentControl { Margin = new Thickness(4) };

        var defaultIndex = 0;
        if (!string.IsNullOrEmpty(initialPage))
        {
            var matchIdx = pages.Select((p, i) => (p, i)).FirstOrDefault(x => string.Equals(x.p.Title, initialPage, StringComparison.OrdinalIgnoreCase)).i;
            if (matchIdx >= 0 && matchIdx < pages.Length)
                defaultIndex = matchIdx;
        }

        var navList = new ListBox
        {
            Classes = { "nav" },
            Width = 190,
            Background = new SolidColorBrush(Color.Parse("#FF13151A")),
            ItemsSource = pages.Select(p => p.Title).ToList(),
            SelectedIndex = defaultIndex,
        };

        navList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((title, _) =>
        {
            var item = pages.FirstOrDefault(p => p.Title == title);
            var iconKey = item.IconKey ?? "Icon.Gear";
            var iconGeom = Avalonia.Application.Current!.TryGetResource(iconKey, Avalonia.Styling.ThemeVariant.Default, out var val) && val is StreamGeometry g ? g : null;

            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Glyph { Data = iconGeom, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = title, FontWeight = FontWeight.Medium, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                },
            };
        });

        navList.SelectionChanged += (_, _) =>
        {
            if (navList.SelectedIndex >= 0 && navList.SelectedIndex < pages.Length)
            {
                contentHost.Content = pages[navList.SelectedIndex].View;
            }
        };

        contentHost.Content = pages[defaultIndex].View;

        var navSidebar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF13151A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF232832")),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = navList,
        };

        var mainLayout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children =
            {
                navSidebar,
                WithColumn(contentHost, 1),
            },
        };

        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 4) };
        var window = NewWindow("Ajustes", 880, 620);
        var ok = Primary("Aceptar");
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        var apply = new Button { Content = "Aplicar" };

        cancel.Click += (_, _) => window.Close();
        var canvasChanged = false;

        async Task<bool> SaveChangesAsync()
        {
            int I(NumericUpDown n) => (int)(n.Value ?? 0);

            // 1. Guardar ajustes del proyecto (Stream, Recording, Canvas)
            var stream = project.Stream with
            {
                Encoder = new EncoderSettings(encoderIds[Math.Max(0, streamEnc.SelectedIndex)], I(streamVideo), I(streamAudio), I(keyframe)),
            };
            var recording = new RecordingPreferences(folder.Text?.Trim() ?? string.Empty, container.SelectedItem as string ?? "mkv",
                new EncoderSettings(encoderIds[Math.Max(0, recEnc.SelectedIndex)], I(recVideo), I(recAudioBitrate)));

            var result = await vm.Run(new SetOutputSettingsCommand(stream, recording));
            if (!result.Success)
            {
                error.Text = result.Error;
                return false;
            }

            var canvas = new CanvasSettings(I(width), I(height), fps.SelectedItem is int f ? f : 30);
            if (canvas != project.Canvas)
            {
                var canvasResult = await vm.Run(new SetCanvasCommand(canvas));
                if (!canvasResult.Success)
                {
                    error.Text = canvasResult.Error;
                    return false;
                }
                canvasChanged = true;
            }

            // 2. Guardar ajustes de máquina (%APPDATA%\LiveStream\machine.json)
            var updatedMachine = machineSettings with
            {
                Language = langBox.SelectedItem as string ?? "Español",
                ConfirmStartStream = confirmStartStream.IsChecked == true,
                ConfirmStopStream = confirmStopStream.IsChecked == true,
                ConfirmStopRecord = confirmStopRecord.IsChecked == true,
                AutoRecordOnStream = autoRecordOnStream.IsChecked == true,
                KeepRecordingOnStreamStop = keepRecordingOnStreamStop.IsChecked == true,
                SnapEnabled = snapEnabled.IsChecked == true,
                SnapSensitivity = (double)(snapSens.Value ?? 10),
                SnapToScreenEdges = snapScreen.IsChecked == true,
                SnapToOtherSources = snapOthers.IsChecked == true,
                CheckUpdatesAtStartup = updatesCheck.IsChecked == true,
                AudioSampleRate = sampleRateBox.SelectedIndex == 1 ? 44100 : 48000,
                AudioChannels = channelsBox.SelectedIndex == 1 ? 1 : 2,
                RenderAdapterIndex = adapterIndex.SelectedIndex,
                ColorFormat = colorFormat.SelectedItem as string ?? "NV12",
                ColorSpace = colorSpace.SelectedItem as string ?? "sRGB",
                ColorRange = colorRange.SelectedItem as string ?? "Parcial",
                ProcessPriority = processPriority.SelectedIndex,
                AdaptiveBitrate = adaptiveBitrate.IsChecked == true,
                PluginDeveloperMode = devMode.IsChecked == true,
            };

            // 3. Aplicar configuración de dispositivos globales de audio (Desktop Audio y Micrófono)
            var chosenDesktop = desktopOptions[Math.Max(0, desktopAudioBox.SelectedIndex)];
            var existingDesktop = vm.Project.Sources.FirstOrDefault(s => s.Kind == SourceKind.AudioOutput && s.GlobalAudioChannel > 0)
                ?? vm.Project.Sources.FirstOrDefault(s => s.Kind == SourceKind.AudioOutput && s.Name.Contains("escritorio", StringComparison.OrdinalIgnoreCase));

            if (chosenDesktop.Id == "disabled")
            {
                if (existingDesktop != null)
                {
                    existingDesktop.SetGlobalAudioChannel(0);
                    if (vm.EngineReady)
                    {
                        try { await vm.Engine.SetGlobalAudioChannelAsync(existingDesktop.Id, 0, CancellationToken.None); } catch (Exception ex) { Infrastructure.Diagnostics.AppLog.Warn("audio", $"Ajustes de audio: {ex.Message}"); }
                        try { await vm.Engine.RemoveSourceAsync(existingDesktop.Id, CancellationToken.None); } catch (Exception ex) { Infrastructure.Diagnostics.AppLog.Warn("audio", $"Ajustes de audio: {ex.Message}"); }
                    }
                    vm.Project.RemoveSource(existingDesktop.Id);
                }
            }
            else
            {
                var settingsJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["device_id"] = chosenDesktop.Id,
                    ["use_device_timing"] = true,
                });

                if (existingDesktop == null)
                {
                    var newDesktop = new Source(Guid.NewGuid(), "Audio del escritorio", SourceKind.AudioOutput, settingsJson);
                    newDesktop.SetGlobalAudioChannel(1);
                    vm.Project.AddSource(newDesktop);
                    if (vm.EngineReady)
                    {
                        try
                        {
                            await vm.Engine.CreateSourceAsync(new SourceDefinition(newDesktop.Id, newDesktop.Name, newDesktop.Kind, settingsJson), CancellationToken.None);
                            await vm.Engine.SetGlobalAudioChannelAsync(newDesktop.Id, 1, CancellationToken.None);
                        }
                        catch (Exception ex) { Infrastructure.Diagnostics.AppLog.Warn("audio", $"Ajustes de audio: {ex.Message}"); }
                    }
                }
                else
                {
                    existingDesktop.SettingsJson = settingsJson;
                    existingDesktop.SetGlobalAudioChannel(1);
                    if (vm.EngineReady)
                    {
                        try
                        {
                            await vm.Engine.UpdateSourceSettingsAsync(existingDesktop.Id, settingsJson, CancellationToken.None);
                            await vm.Engine.SetGlobalAudioChannelAsync(existingDesktop.Id, 1, CancellationToken.None);
                        }
                        catch (Exception ex) { Infrastructure.Diagnostics.AppLog.Warn("audio", $"Ajustes de audio: {ex.Message}"); }
                    }
                }
            }

            var chosenMic = micOptions[Math.Max(0, micDeviceBox.SelectedIndex)];
            var existingMic = vm.Project.Sources.FirstOrDefault(s => s.Kind == SourceKind.AudioInput && s.GlobalAudioChannel > 0)
                ?? vm.Project.Sources.FirstOrDefault(s => s.Kind == SourceKind.AudioInput && (s.Name.Contains("Micro", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Micrófono", StringComparison.OrdinalIgnoreCase)));

            if (chosenMic.Id == "disabled")
            {
                if (existingMic != null)
                {
                    existingMic.SetGlobalAudioChannel(0);
                    if (vm.EngineReady)
                    {
                        try { await vm.Engine.SetGlobalAudioChannelAsync(existingMic.Id, 0, CancellationToken.None); } catch (Exception ex) { Infrastructure.Diagnostics.AppLog.Warn("audio", $"Ajustes de audio: {ex.Message}"); }
                        try { await vm.Engine.RemoveSourceAsync(existingMic.Id, CancellationToken.None); } catch (Exception ex) { Infrastructure.Diagnostics.AppLog.Warn("audio", $"Ajustes de audio: {ex.Message}"); }
                    }
                    vm.Project.RemoveSource(existingMic.Id);
                }
            }
            else
            {
                var settingsJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["device_id"] = chosenMic.Id,
                    ["use_device_timing"] = false,
                });

                if (existingMic == null)
                {
                    var newMic = new Source(Guid.NewGuid(), "Micrófono", SourceKind.AudioInput, settingsJson);
                    newMic.SetGlobalAudioChannel(3);
                    vm.Project.AddSource(newMic);
                    if (vm.EngineReady)
                    {
                        try
                        {
                            await vm.Engine.CreateSourceAsync(new SourceDefinition(newMic.Id, newMic.Name, newMic.Kind, settingsJson), CancellationToken.None);
                            await vm.Engine.SetGlobalAudioChannelAsync(newMic.Id, 3, CancellationToken.None);
                        }
                        catch (Exception ex) { Infrastructure.Diagnostics.AppLog.Warn("audio", $"Ajustes de audio: {ex.Message}"); }
                    }
                }
                else
                {
                    existingMic.SettingsJson = settingsJson;
                    existingMic.SetGlobalAudioChannel(3);
                    if (vm.EngineReady)
                    {
                        try
                        {
                            await vm.Engine.UpdateSourceSettingsAsync(existingMic.Id, settingsJson, CancellationToken.None);
                            await vm.Engine.SetGlobalAudioChannelAsync(existingMic.Id, 3, CancellationToken.None);
                        }
                        catch (Exception ex) { Infrastructure.Diagnostics.AppLog.Warn("audio", $"Ajustes de audio: {ex.Message}"); }
                    }
                }
            }

            vm.RefreshMixer();
            await vm.Session.SaveAsync();

            vm.Machine.Save(updatedMachine);
            vm.StatusMessage = "Ajustes guardados correctamente.";
            return true;
        }

        apply.Click += async (_, _) =>
        {
            try
            {
                await SaveChangesAsync();
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
            }
        };

        ok.Click += async (_, _) =>
        {
            try
            {
                if (await SaveChangesAsync())
                    window.Close();
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
            }
        };

        var buttonBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(16, 10),
            Children = { ok, cancel, apply },
        };

        var rootDialogPanel = new DockPanel
        {
            Children =
            {
                WithDock(buttonBar, Dock.Bottom),
                WithDock(error, Dock.Bottom),
                mainLayout,
            },
        };

        window.Content = rootDialogPanel;
        await window.ShowDialog(owner);

        if (canvasChanged && await vm.Dialogs.ConfirmAsync("Reiniciar", "El nuevo lienzo se aplica al reiniciar LiveStream. ¿Reiniciar ahora?", "Reiniciar"))
            await vm.RelaunchWithProjectAsync(vm.Session.ProjectPath);
    }

    private static Control WithDock(Control c, Dock dock)
    {
        DockPanel.SetDock(c, dock);
        return c;
    }
}
