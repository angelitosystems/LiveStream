using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LiveStream.Desktop.ViewModels;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Integrations;
using static LiveStream.Desktop.Views.Dialogs.StudioDialogs;

namespace LiveStream.Desktop.Views.Dialogs;

/// <summary>
/// Centro de configuración de integraciones por plataforma (YouTube, Twitch, TikTok, Facebook, Instagram).
/// Permite gestionar conexiones oficiales mediante OAuth y WebSockets, validar permisos y comprobar el estado de las APIs.
/// </summary>
public static class IntegrationsDialog
{
    private static Window? _open;

    public static async Task ShowAsync(MainViewModel vm)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }

        if (vm.Dialogs.Owner is not { } owner)
            return;

        var window = NewWindow("Integraciones por plataforma", 840, 680);
        _open = window;

        var vmIntegrations = vm.Integrations;
        vmIntegrations.ReloadPlatforms();

        var platformsList = new StackPanel { Spacing = 4, Width = 220 };
        var detailContainer = new Border
        {
            Padding = new Thickness(20),
            Background = new SolidColorBrush(Color.Parse("#FF16191E")),
            CornerRadius = new CornerRadius(8),
        };

        void RenderPlatformDetail(PlatformIntegrationRowViewModel row)
        {
            vmIntegrations.SelectedPlatform = row;
            var p = row.Platform;
            var config = row.Config;

            var statusTextBlock = new TextBlock
            {
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
                Text = vmIntegrations.StatusMessage ?? "",
            };

            var platformName = row.Name;
            var title = new TextBlock
            {
                Text = platformName,
                FontSize = 20,
                FontWeight = FontWeight.Bold,
            };

            var badge = new Border
            {
                Background = new SolidColorBrush(Color.Parse(row.StatusColor)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = row.StatusText,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White,
                },
            };

            var header = new DockPanel();
            DockPanel.SetDock(badge, Dock.Right);
            header.Children.Add(badge);
            header.Children.Add(title);

            var docText = p switch
            {
                StreamPlatform.YouTube =>
                    "• Requiere YouTube Data API v3.\n" +
                    "• Para chat público puedes usar tu API Key de Google Cloud Console.\n" +
                    "• Para Super Chats y miembros se requiere OAuth 2.0 (scope: https://www.googleapis.com/auth/youtube.readonly).\n" +
                    "• ID de Emisión: Copia el ID del video o el Live Chat ID desde YouTube Studio.",
                StreamPlatform.Twitch =>
                    "• Conexión oficial a través de WebSockets IRC (wss://irc-ws.chat.twitch.tv:443).\n" +
                    "• Para chat público anónimo solo necesitas el nombre del canal.\n" +
                    "• Para suscripciones, raids y donaciones de Bits introduce un OAuth Token (oauth:xxxxx) con scopes 'chat:read' y 'channel:read:subscriptions'.",
                StreamPlatform.TikTok =>
                    "• TikTok Open Platform LIVE API.\n" +
                    "• Requiere cuenta con permisos de desarrollador de TikTok LIVE aprobados por TikTok for Developers.\n" +
                    "• Ingresa tu Room ID de emisión activa y tu User Access Token autorizado.",
                StreamPlatform.Facebook =>
                    "• Facebook Graph API (live_videos/{id}/comments).\n" +
                    "• Requiere User o Page Access Token con permisos 'pages_read_engagement' o 'live_videos'.\n" +
                    "• Ingresa el Live Video ID de la transmisión de Facebook.",
                StreamPlatform.Instagram =>
                    "• Instagram Graph API para creadores y profesionales.\n" +
                    "• Requiere cuenta profesional de Instagram y sesión de Instagram Live Producer (instagram.com/live/producer).\n" +
                    "• Ingresa el Live Media ID y un User Access Token con permiso 'instagram_manage_comments'.",
                _ => "Configuración de conexión para la plataforma."
            };

            var docBlock = new TextBlock
            {
                Text = docText,
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 15),
                FontSize = 12,
            };

            var accountBox = new TextBox
            {
                Text = vmIntegrations.AccountName,
                Watermark = p == StreamPlatform.Twitch ? "Canal de Twitch (ej. midudev)" : "Nombre de usuario o canal",
            };
            accountBox.TextChanged += (_, _) => vmIntegrations.AccountName = accountBox.Text ?? "";

            var idBox = new TextBox
            {
                Text = vmIntegrations.AccountId,
                Watermark = p switch
                {
                    StreamPlatform.YouTube => "ID de video en directo o Live Chat ID",
                    StreamPlatform.TikTok => "Room ID de TikTok LIVE",
                    StreamPlatform.Facebook => "Live Video ID",
                    StreamPlatform.Instagram => "Live Media ID",
                    _ => "ID de emisión (opcional)"
                },
            };
            idBox.TextChanged += (_, _) => vmIntegrations.AccountId = idBox.Text ?? "";

            var tokenBox = new TextBox
            {
                PasswordChar = '•',
                Text = vmIntegrations.SecretToken,
                Watermark = "Token OAuth / API Key (se guarda cifrado con DPAPI)",
            };
            tokenBox.TextChanged += (_, _) => vmIntegrations.SecretToken = tokenBox.Text ?? "";

            var chatCheck = new CheckBox { Content = "Habilitar Chat en pantalla", IsChecked = vmIntegrations.EnableChat };
            chatCheck.IsCheckedChanged += (_, _) => vmIntegrations.EnableChat = chatCheck.IsChecked == true;

            var alertsCheck = new CheckBox { Content = "Habilitar Alertas de eventos (seguidores, subs, donaciones)", IsChecked = vmIntegrations.EnableAlerts };
            alertsCheck.IsCheckedChanged += (_, _) => vmIntegrations.EnableAlerts = alertsCheck.IsChecked == true;

            var reconnectCheck = new CheckBox { Content = "Reconectar automáticamente ante desconexiones", IsChecked = vmIntegrations.AutoReconnect };
            reconnectCheck.IsCheckedChanged += (_, _) => vmIntegrations.AutoReconnect = reconnectCheck.IsChecked == true;

            var connectBtn = Primary(row.Status == IntegrationStatus.Connected ? "Actualizar conexión" : "Conectar");
            connectBtn.Click += async (_, _) =>
            {
                connectBtn.IsEnabled = false;
                await vmIntegrations.SaveAndConnectAsync();
                connectBtn.IsEnabled = true;
                statusTextBlock.Text = vmIntegrations.StatusMessage;
                RenderPlatformDetail(row);
                RenderPlatformsList();
            };

            var disconnectBtn = new Button
            {
                Content = "Desconectar",
                IsEnabled = row.Status != IntegrationStatus.Disconnected,
            };
            disconnectBtn.Click += async (_, _) =>
            {
                await vmIntegrations.DisconnectAsync();
                statusTextBlock.Text = vmIntegrations.StatusMessage;
                RenderPlatformDetail(row);
                RenderPlatformsList();
            };

            var testBtn = new Button
            {
                Content = "Enviar evento de prueba",
            };
            ToolTip.SetTip(testBtn, "Simula un mensaje de chat y una alerta para validar en pantalla");
            testBtn.Click += (_, _) =>
            {
                vm.Session.Integrations.SimulateMessage(new ChatMessage(
                    Id: Guid.NewGuid().ToString("N"),
                    Platform: p,
                    AuthorName: "UsuarioTest",
                    AuthorId: "12345",
                    Message: $"¡Probando integración con {platformName}!",
                    Timestamp: DateTime.UtcNow));

                vm.Session.Integrations.SimulateAlert(new AlertEvent(
                    Id: Guid.NewGuid().ToString("N"),
                    Platform: p,
                    Kind: AlertKind.Follower,
                    Username: "NuevoSeguidorTest",
                    Timestamp: DateTime.UtcNow));

                statusTextBlock.Text = $"Se inyectó un mensaje y alerta de prueba para {platformName}.";
            };

            var buttonsBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 15, 0, 0),
                Children = { connectBtn, disconnectBtn, testBtn }
            };

            var contentStack = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    header,
                    docBlock,
                    new TextBlock { Text = "Canal / Usuario:", FontWeight = FontWeight.SemiBold, FontSize = 12 },
                    accountBox,
                    new TextBlock { Text = "ID de Emisión / Sala:", FontWeight = FontWeight.SemiBold, FontSize = 12 },
                    idBox,
                    new TextBlock { Text = "Clave de API o Token de Acceso:", FontWeight = FontWeight.SemiBold, FontSize = 12 },
                    tokenBox,
                    new StackPanel { Margin = new Thickness(0, 8, 0, 0), Spacing = 4, Children = { chatCheck, alertsCheck, reconnectCheck } },
                    buttonsBar,
                    statusTextBlock
                }
            };

            detailContainer.Child = new ScrollViewer { Content = contentStack };
        }

        void RenderPlatformsList()
        {
            platformsList.Children.Clear();
            foreach (var p in vmIntegrations.Platforms)
            {
                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(12, 10),
                    Margin = new Thickness(0, 2),
                };

                var pGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                pGrid.Children.Add(new TextBlock
                {
                    Text = p.Name,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var statusDot = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.Parse(p.StatusColor)),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(statusDot, 1);
                pGrid.Children.Add(statusDot);

                btn.Content = pGrid;
                btn.Click += (_, _) => RenderPlatformDetail(p);
                platformsList.Children.Add(btn);
            }
        }

        RenderPlatformsList();
        if (vmIntegrations.Platforms.Count > 0)
            RenderPlatformDetail(vmIntegrations.Platforms[0]);

        var mainLayout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(16),
        };

        var leftBar = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 0, 16, 0),
            Children =
            {
                new TextBlock { Text = "Plataformas", FontWeight = FontWeight.Bold, FontSize = 14 },
                new ScrollViewer { Content = platformsList }
            }
        };

        Grid.SetColumn(leftBar, 0);
        Grid.SetColumn(detailContainer, 1);
        mainLayout.Children.Add(leftBar);
        mainLayout.Children.Add(detailContainer);

        var close = Primary("Cerrar");
        close.Click += (_, _) => window.Close();
        window.Content = Layout(mainLayout, close);

        window.Closed += (_, _) => _open = null;
        window.Show(owner);
    }
}
