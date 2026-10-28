using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LiveStream.Extensions.Alerts;
using LiveStream.Desktop.Components;
using LiveStream.Desktop.ViewModels;
using static LiveStream.Desktop.Views.Dialogs.StudioDialogs;

namespace LiveStream.Desktop.Views.Dialogs;

/// <summary>
/// Explorador y selector interactivo del Banco de Sonidos Libres de Regalías.
/// Permite buscar, filtrar, reproducir vistas previas de audio en tiempo real
/// y seleccionar o importar efectos para las alertas del estudio.
/// </summary>
public static class SoundBankDialog
{
    public static async Task<string?> ShowPickerAsync(MainViewModel vm, Window? owner = null, string? currentPath = null)
    {
        var parent = owner ?? vm.Dialogs.Owner;
        if (parent is null) return null;

        var soundBank = vm.Feature(vm.Session.Extensions.Get<SoundBankService>(), "El banco de sonidos");
        var window = NewWindow("Banco de Efectos de Sonido Libres de Regalías", 720, 520);

        var searchBox = new TextBox
        {
            Watermark = "Buscar efecto de sonido (campana, moneda, nivel, victoria, retro...)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var categoryBox = new ComboBox
        {
            MinWidth = 200,
            ItemsSource = soundBank.Categories,
            SelectedIndex = 0,
        };

        var soundsListBox = new ListBox
        {
            Classes = { "studioList" },
            Margin = new Thickness(0, 8, 0, 8),
        };

        var playBtn = new Button
        {
            Content = "▶ Escuchar prueba",
            Classes = { "accent" },
            MinWidth = 130,
        };

        var stopBtn = new Button
        {
            Content = "■ Detener",
            MinWidth = 90,
        };

        var importBtn = new Button
        {
            Content = "Importar archivo local…",
            MinWidth = 150,
        };

        var selectBtn = Primary("Usar este sonido");
        selectBtn.IsEnabled = false;

        var cancelBtn = new Button { Content = "Cancelar", IsCancel = true };
        cancelBtn.Click += (_, _) =>
        {
            SoundPlayerService.Stop();
            window.Close();
        };

        string? selectedFilePath = currentPath;
        var currentItems = new List<SoundBankItem>();

        void RefreshList()
        {
            var selectedCat = categoryBox.SelectedItem?.ToString();
            currentItems = soundBank.List(searchBox.Text, selectedCat).ToList();

            var rowControls = new List<Control>();
            for (int i = 0; i < currentItems.Count; i++)
            {
                var item = currentItems[i];
                var isSelected = !string.IsNullOrEmpty(selectedFilePath) &&
                    (string.Equals(item.LocalFilePath, selectedFilePath, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(System.IO.Path.GetFileName(item.LocalFilePath), System.IO.Path.GetFileName(selectedFilePath), StringComparison.OrdinalIgnoreCase));

                var metaPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new Border
                        {
                            Background = new SolidColorBrush(Color.Parse("#FF1E293B")),
                            CornerRadius = new CornerRadius(3),
                            Padding = new Thickness(6, 2),
                            Child = new TextBlock { Text = item.License, FontSize = 9, Foreground = new SolidColorBrush(Color.Parse("#FF94A3B8")) }
                        },
                        new TextBlock { Text = $"{item.DurationSeconds:0.1}s", Classes = { "muted" }, FontSize = 10, VerticalAlignment = VerticalAlignment.Center }
                    }
                };
                DockPanel.SetDock(metaPanel, Dock.Right);

                var row = new Border
                {
                    Background = isSelected ? new SolidColorBrush(Color.Parse("#FF23354E")) : Brushes.Transparent,
                    BorderBrush = isSelected ? new SolidColorBrush(Color.Parse("#FF3B82F6")) : Brushes.Transparent,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6),
                    Margin = new Thickness(0, 1),
                    Child = new DockPanel
                    {
                        Children =
                        {
                            metaPanel,
                            new StackPanel
                            {
                                Spacing = 2,
                                Children =
                                {
                                    new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, FontSize = 12 },
                                    new TextBlock { Text = $"Categoría: {item.Category}", Classes = { "muted" }, FontSize = 10 }
                                }
                            }
                        }
                    }
                };

                rowControls.Add(row);
            }

            soundsListBox.ItemsSource = rowControls;

            // Auto-seleccionar coincidencia si existe
            if (!string.IsNullOrEmpty(selectedFilePath))
            {
                var matchIdx = currentItems.FindIndex(it =>
                    string.Equals(it.LocalFilePath, selectedFilePath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(it.LocalFilePath), System.IO.Path.GetFileName(selectedFilePath), StringComparison.OrdinalIgnoreCase));
                if (matchIdx >= 0)
                    soundsListBox.SelectedIndex = matchIdx;
            }
        }

        soundsListBox.SelectionChanged += (_, _) =>
        {
            var idx = soundsListBox.SelectedIndex;
            if (idx >= 0 && idx < currentItems.Count)
            {
                selectedFilePath = currentItems[idx].LocalFilePath;
                selectBtn.IsEnabled = true;
            }
            else
            {
                selectBtn.IsEnabled = false;
            }
        };

        searchBox.KeyUp += (_, _) => RefreshList();
        categoryBox.SelectionChanged += (_, _) => RefreshList();

        playBtn.Click += (_, _) =>
        {
            var idx = soundsListBox.SelectedIndex;
            if (idx >= 0 && idx < currentItems.Count)
            {
                SoundPlayerService.Play(currentItems[idx].LocalFilePath);
            }
        };

        stopBtn.Click += (_, _) => SoundPlayerService.Stop();

        importBtn.Click += async (_, _) =>
        {
            var file = await vm.Dialogs.PickFileAsync("Importar efecto de sonido", "Audios (*.wav *.mp3 *.ogg);;Todos (*.*)");
            if (!string.IsNullOrWhiteSpace(file))
            {
                try
                {
                    var imported = soundBank.ImportUserSound(file);
                    selectedFilePath = imported.LocalFilePath;
                    RefreshList();
                    SoundPlayerService.Play(imported.LocalFilePath);
                }
                catch (Exception ex)
                {
                    await vm.Dialogs.InfoAsync("Error de importación", ex.Message);
                }
            }
        };

        bool confirmed = false;
        selectBtn.Click += (_, _) =>
        {
            SoundPlayerService.Stop();
            confirmed = true;
            window.Close();
        };

        // Layout del Diálogo
        var filterBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 6),
            Children =
            {
                searchBox,
            }
        };
        Grid.SetColumn(categoryBox, 1);
        categoryBox.Margin = new Thickness(8, 0, 0, 0);
        filterBar.Children.Add(categoryBox);

        var playerBar = new DockPanel
        {
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { playBtn, stopBtn }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { importBtn }
                }
            }
        };

        var body = new DockPanel
        {
            Margin = new Thickness(16, 12),
            Children =
            {
                filterBar,
                playerBar,
                soundsListBox,
            }
        };
        DockPanel.SetDock(filterBar, Dock.Top);
        DockPanel.SetDock(playerBar, Dock.Bottom);

        window.Content = Layout(body, selectBtn, cancelBtn);

        RefreshList();
        await window.ShowDialog(parent);
        SoundPlayerService.Stop();

        return confirmed ? selectedFilePath : null;
    }
}
