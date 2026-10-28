using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LiveStream.Application.Alerts;
using LiveStream.Desktop.Components;
using LiveStream.Desktop.ViewModels;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Entities;
using static LiveStream.Desktop.Views.Dialogs.StudioDialogs;

namespace LiveStream.Desktop.Views.Dialogs;

/// <summary>
/// Diseñador visual de plantillas de alertas y efectos multimedia.
/// Permite editar textos con tokens dinámicos, seleccionar sonidos del banco libre,
/// ajustar colores y probar al instante en el estudio con visualización en tiempo real.
/// </summary>
public static class AlertDesignerDialog
{
    public static async Task ShowAsync(MainViewModel vm, Window? owner = null)
    {
        var parent = owner ?? vm.Dialogs.Owner;
        if (parent is null) return;

        var session = vm.Session;
        var window = NewWindow("Editor Visual de Plantillas de Alerta y Sonidos", 880, 640);

        // 1. Selector de Tipo de Alerta
        var kindSelector = new ComboBox
        {
            MinWidth = 260,
            ItemsSource = new List<string>
            {
                "Nuevo Seguidor (Follower)",
                "Nueva Suscripción (Subscription)",
                "Aportación / Donación",
                "Super Chat",
                "Raid de Canal",
                "Bienvenida a Primer Espectador",
            },
            SelectedIndex = 0,
        };

        var ruleNameBox = new TextBox { Watermark = "Nombre de la alerta" };
        var enabledCheck = new CheckBox { Content = "Regla de alerta activada", IsChecked = true, Margin = new Thickness(0, 4) };

        var templateBox = new TextBox
        {
            Watermark = "Ej: ¡Gracias por acompañarnos, {username}! ({tier})",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 60,
        };

        // Visualización y Estilos
        var durationInput = new NumericUpDown
        {
            Minimum = 2,
            Maximum = 30,
            Value = 5,
            Increment = 1,
            FormatString = "0 s",
            MinWidth = 120,
        };

        var textColorBox = new TextBox { Text = "#FFFFFF", MinWidth = 100 };
        var accentColorBox = new TextBox { Text = "#F59E0B", MinWidth = 100 };
        var bgColorBox = new TextBox { Text = "#1E293B", MinWidth = 100 };

        // 2. Panel de Previsualización en Vivo dentro del Diálogo
        var previewTitle = new TextBlock
        {
            Text = "¡Nuevo seguidor!",
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#FFF59E0B")),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var previewMessage = new TextBlock
        {
            Text = "¡Gracias por seguirnos, EspectadorVIP!",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

        var previewCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1E293B")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FFF59E0B")),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 16),
            Margin = new Thickness(10),
            MinHeight = 90,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth = 360,
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { previewTitle, previewMessage }
            }
        };

        var previewContainer = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF0F1115")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF282D37")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            MinHeight = 180,
            Child = previewCard,
        };

        void UpdatePreview()
        {
            try
            {
                previewTitle.Text = string.IsNullOrWhiteSpace(ruleNameBox.Text) ? "Alerta" : ruleNameBox.Text;

                var sampleEvent = new AlertEvent(
                    Id: Guid.NewGuid().ToString("N"),
                    Platform: StreamPlatform.Twitch,
                    Kind: AlertKind.Follower,
                    Username: "AlexGamer_TV",
                    Amount: 15.00m,
                    Currency: "USD",
                    Tier: "Tier 1",
                    Months: 6,
                    Viewers: 85,
                    Message: "¡Gran directo amigo!",
                    Timestamp: DateTime.UtcNow
                );

                previewMessage.Text = AlertProcessor.FormatTemplate(templateBox.Text ?? "", sampleEvent);

                if (Color.TryParse(accentColorBox.Text?.Trim(), out var accentCol))
                {
                    previewTitle.Foreground = new SolidColorBrush(accentCol);
                    previewCard.BorderBrush = new SolidColorBrush(accentCol);
                }

                if (Color.TryParse(textColorBox.Text?.Trim(), out var textCol))
                {
                    previewMessage.Foreground = new SolidColorBrush(textCol);
                }

                if (Color.TryParse(bgColorBox.Text?.Trim(), out var bgCol))
                {
                    previewCard.Background = new SolidColorBrush(bgCol);
                }
            }
            catch { }
        }

        // Token helper buttons
        var tokenStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 4, 0, 8),
        };

        void AddTokenButton(string token, string tip)
        {
            var btn = new Button
            {
                Content = token,
                Classes = { "icon" },
                Height = 22,
                Padding = new Thickness(6, 1),
                FontSize = 10,
            };
            ToolTip.SetTip(btn, tip);
            btn.Click += (_, _) =>
            {
                var cur = templateBox.Text ?? "";
                templateBox.Text = cur + " " + token;
                UpdatePreview();
            };
            tokenStack.Children.Add(btn);
        }

        AddTokenButton("{username}", "Inserta el nombre del espectador");
        AddTokenButton("{amount}", "Inserta la cantidad donada");
        AddTokenButton("{currency}", "Inserta la moneda (USD, EUR, etc.)");
        AddTokenButton("{tier}", "Inserta el nivel de suscripción");
        AddTokenButton("{months}", "Inserta los meses suscrito");
        AddTokenButton("{viewers}", "Inserta la cantidad de espectadores del raid");
        AddTokenButton("{message}", "Inserta el comentario o mensaje del usuario");

        // Sonido
        var soundText = new TextBlock
        {
            Text = "(Sin sonido asignado)",
            Classes = { "muted" },
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var soundPlayBtn = new Button { Content = "▶ Probar", Width = 75 };
        var soundBrowseBtn = new Button { Content = "Explorar Banco Libre…", Classes = { "accent" }, Width = 155 };
        var soundClearBtn = new Button { Content = "✕ Quitar", Width = 75 };

        string? currentSoundPath = null;

        templateBox.KeyUp += (_, _) => UpdatePreview();
        ruleNameBox.KeyUp += (_, _) => UpdatePreview();
        accentColorBox.KeyUp += (_, _) => UpdatePreview();
        textColorBox.KeyUp += (_, _) => UpdatePreview();
        bgColorBox.KeyUp += (_, _) => UpdatePreview();

        soundPlayBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(currentSoundPath))
                SoundPlayerService.Play(currentSoundPath);
        };

        soundClearBtn.Click += (_, _) =>
        {
            currentSoundPath = null;
            soundText.Text = "(Sin sonido asignado)";
        };

        soundBrowseBtn.Click += async (_, _) =>
        {
            var picked = await SoundBankDialog.ShowPickerAsync(vm, window, currentSoundPath);
            if (!string.IsNullOrWhiteSpace(picked))
            {
                currentSoundPath = picked;
                soundText.Text = System.IO.Path.GetFileName(picked);
                SoundPlayerService.Play(currentSoundPath);
            }
        };

        // Carga de la regla actual según la selección
        AlertRule? loadedRule = null;

        void LoadRuleForSelectedKind()
        {
            var kind = kindSelector.SelectedIndex switch
            {
                0 => AlertKind.Follower,
                1 => AlertKind.Subscription,
                2 => AlertKind.Donation,
                3 => AlertKind.SuperChat,
                4 => AlertKind.Raid,
                5 => AlertKind.Greeting,
                _ => AlertKind.Follower
            };

            loadedRule = session.Project.AlertRules.FirstOrDefault(r => r.Kind == kind)
                         ?? AlertRule.CreateDefault(kind);

            ruleNameBox.Text = loadedRule.Name;
            enabledCheck.IsChecked = loadedRule.Enabled;
            templateBox.Text = loadedRule.Template;
            durationInput.Value = loadedRule.DurationSec;
            textColorBox.Text = loadedRule.TextColor;
            accentColorBox.Text = loadedRule.AccentColor;
            bgColorBox.Text = loadedRule.BackgroundColor;
            currentSoundPath = loadedRule.SoundPath;
            soundText.Text = string.IsNullOrWhiteSpace(currentSoundPath)
                ? "(Por defecto según evento)"
                : System.IO.Path.GetFileName(currentSoundPath);

            UpdatePreview();
        }

        kindSelector.SelectionChanged += (_, _) => LoadRuleForSelectedKind();
        LoadRuleForSelectedKind();

        // Botón de prueba instantánea en el estudio
        var testInStudioBtn = new Button
        {
            Content = "⚡ Probar en Estudio",
            Classes = { "accent" },
            MinWidth = 140,
        };
        ToolTip.SetTip(testInStudioBtn, "Lanza esta alerta en tiempo real a través de la cola secuencial con su sonido y gráfico");

        testInStudioBtn.Click += (_, _) =>
        {
            var kind = kindSelector.SelectedIndex switch
            {
                0 => AlertKind.Follower,
                1 => AlertKind.Subscription,
                2 => AlertKind.Donation,
                3 => AlertKind.SuperChat,
                4 => AlertKind.Raid,
                5 => AlertKind.Greeting,
                _ => AlertKind.Follower
            };

            var ruleToTest = new AlertRule(
                Id: loadedRule?.Id ?? Guid.NewGuid(),
                Name: ruleNameBox.Text ?? "Alerta",
                Kind: kind,
                Template: templateBox.Text ?? "",
                Enabled: enabledCheck.IsChecked == true,
                DurationSec: (int)(durationInput.Value ?? 5),
                SoundPath: currentSoundPath,
                TextColor: textColorBox.Text ?? "#FFFFFF",
                AccentColor: accentColorBox.Text ?? "#F59E0B",
                BackgroundColor: bgColorBox.Text ?? "#1E293B"
            );

            vm.Alerts.TestPlatform = StreamPlatform.Twitch;
            session.Alerts.TriggerTestAlert(kind, StreamPlatform.Twitch, [ruleToTest]);
        };

        // Guardar regla
        var saveBtn = Primary("Guardar plantilla");
        saveBtn.Click += async (_, _) =>
        {
            var kind = kindSelector.SelectedIndex switch
            {
                0 => AlertKind.Follower,
                1 => AlertKind.Subscription,
                2 => AlertKind.Donation,
                3 => AlertKind.SuperChat,
                4 => AlertKind.Raid,
                5 => AlertKind.Greeting,
                _ => AlertKind.Follower
            };

            var updated = new AlertRule(
                Id: loadedRule?.Id ?? Guid.NewGuid(),
                Name: ruleNameBox.Text ?? "Alerta",
                Kind: kind,
                Template: templateBox.Text ?? "",
                Enabled: enabledCheck.IsChecked == true,
                DurationSec: (int)(durationInput.Value ?? 5),
                SoundPath: currentSoundPath,
                TextColor: textColorBox.Text ?? "#FFFFFF",
                AccentColor: accentColorBox.Text ?? "#F59E0B",
                BackgroundColor: bgColorBox.Text ?? "#1E293B"
            );

            session.Project.AddAlertRule(updated);
            await session.SaveAsync();
            window.Close();
        };

        var cancelBtn = new Button { Content = "Cerrar", IsCancel = true };
        cancelBtn.Click += (_, _) => window.Close();

        // Armado visual del layout en 2 columnas: Ajustes a la izquierda, Preview a la derecha
        var leftForm = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 0, 16, 0),
            Children =
            {
                new TextBlock { Text = "Tipo de Evento:", FontWeight = FontWeight.SemiBold, FontSize = 11, Classes = { "muted" } },
                kindSelector,
                enabledCheck,

                new TextBlock { Text = "Nombre de la Alerta:", FontWeight = FontWeight.SemiBold, FontSize = 11, Classes = { "muted" } },
                ruleNameBox,

                new TextBlock { Text = "Plantilla de Texto Dinámico:", FontWeight = FontWeight.SemiBold, FontSize = 11, Classes = { "muted" } },
                templateBox,
                tokenStack,

                new TextBlock { Text = "Efecto de Sonido:", FontWeight = FontWeight.SemiBold, FontSize = 11, Classes = { "muted" } },
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#FF161A22")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#FF282D37")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(8),
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            soundText,
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 6,
                                Children = { soundBrowseBtn, soundPlayBtn, soundClearBtn }
                            }
                        }
                    }
                },

                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                    Children =
                    {
                        new StackPanel { Margin = new Thickness(0,0,4,0), Children = { new TextBlock { Text = "Texto (Hex):", FontSize = 10, Classes = { "muted" } }, textColorBox } },
                        new StackPanel { Margin = new Thickness(4,0,4,0), Children = { new TextBlock { Text = "Acento (Hex):", FontSize = 10, Classes = { "muted" } }, accentColorBox } },
                        new StackPanel { Margin = new Thickness(4,0,0,0), Children = { new TextBlock { Text = "Fondo (Hex):", FontSize = 10, Classes = { "muted" } }, bgColorBox } },
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Duración en pantalla:", VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Classes = { "muted" } },
                        durationInput
                    }
                }
            }
        };

        var colorsGrid = leftForm.Children[leftForm.Children.Count - 2] as Grid;
        if (colorsGrid is not null && colorsGrid.Children.Count >= 3)
        {
            Grid.SetColumn(colorsGrid.Children[0], 0);
            Grid.SetColumn(colorsGrid.Children[1], 1);
            Grid.SetColumn(colorsGrid.Children[2], 2);
        }

        var rightCol = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Vista Previa en Tiempo Real:", FontWeight = FontWeight.Bold, FontSize = 12 },
                previewContainer,
                new TextBlock
                {
                    Text = "Esta vista simula el renderizado de la alerta tal como se proyecta en el canal de Programa (PGM) y Previsualización (PVW).",
                    Classes = { "muted" },
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                },
                new Border { Height = 10 },
                testInStudioBtn,
            }
        };

        var mainGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.2*,*"),
            Margin = new Thickness(16, 12),
            Children =
            {
                new ScrollViewer { Content = leftForm },
            }
        };
        Grid.SetColumn(rightCol, 1);
        mainGrid.Children.Add(rightCol);

        window.Content = Layout(mainGrid, saveBtn, cancelBtn);

        await window.ShowDialog(parent);
    }
}
