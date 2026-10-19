using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using LiveStream.Install;

namespace LiveStream.Setup;

/// <summary>
/// Asistente de instalación: bienvenida → opciones → instalación → listo (con la invitación a auspiciar).
/// La lógica vive en <see cref="SetupEngine"/>; esta ventana solo recoge las opciones y muestra el avance.
/// </summary>
internal sealed partial class SetupWindow : Window
{
    private enum Page { Welcome, Options, Install, Done, Error }

    private static readonly string[] StepNames = ["Bienvenida", "Opciones", "Instalación", "Listo"];

    private readonly SetupArguments _arguments;
    private Page _page;
    private bool _installing;
    private string _installedDirectory = "";

    public SetupWindow() : this(SetupArguments.Parse([]))
    {
    }

    public SetupWindow(SetupArguments arguments)
    {
        _arguments = arguments;
        AvaloniaXamlLoader.Load(this);

        var version = SetupEngine.Version;
        Title = arguments.IsUpdate ? $"Actualizar LiveStream {version}" : $"Instalar LiveStream {version}";
        Control<TextBlock>("VersionChip").Text = $"Versión {version}";
        Control<TextBlock>("WelcomeTitle").Text = arguments.IsUpdate ? $"Actualiza a LiveStream {version}" : "Bienvenido a LiveStream";
        Control<TextBlock>("InstallTitle").Text = arguments.IsUpdate ? "Actualizando LiveStream" : "Instalando LiveStream";
        Control<TextBox>("DirectoryBox").Text = arguments.Directory;
        Control<CheckBox>("StartMenuCheck").IsChecked = !arguments.NoShortcuts;
        Control<CheckBox>("DesktopCheck").IsChecked = !arguments.NoShortcuts && File.Exists(InstallLayout.DesktopShortcutPath);
        Control<CheckBox>("LaunchCheck").IsChecked = true;
        Control<TextBlock>("SpaceText").Text = SpaceDescription();

        Closing += (_, e) => e.Cancel = _installing; // no se interrumpe a mitad del intercambio de carpetas
        Show(Page.Welcome);
    }

    private T Control<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private static string SpaceDescription()
    {
        try
        {
            var bytes = SetupEngine.InstalledSize();
            return bytes > 0 ? $"Espacio necesario: {bytes / 1024d / 1024d:0} MB" : "";
        }
        catch (Exception ex)
        {
            SetupEngine.Log($"No se pudo leer el tamaño del paquete: {ex.Message}");
            return "";
        }
    }

    private void Show(Page page)
    {
        _page = page;
        Control<StackPanel>("WelcomePage").IsVisible = page == Page.Welcome;
        Control<StackPanel>("OptionsPage").IsVisible = page == Page.Options;
        Control<DockPanel>("InstallPage").IsVisible = page == Page.Install;
        Control<StackPanel>("DonePage").IsVisible = page == Page.Done;
        Control<StackPanel>("ErrorPage").IsVisible = page == Page.Error;

        var back = Control<Button>("BackButton");
        var next = Control<Button>("NextButton");
        var cancel = Control<Button>("CancelButton");
        back.IsVisible = page is Page.Options or Page.Error;
        next.IsEnabled = page != Page.Install;
        cancel.IsVisible = page is Page.Welcome or Page.Options or Page.Error;
        Control<Button>("CloseButton").IsEnabled = page != Page.Install;
        next.Content = page switch
        {
            Page.Welcome => "Siguiente",
            Page.Options => _arguments.IsUpdate ? "Actualizar" : "Instalar",
            Page.Install => "Instalando…",
            Page.Done => "Finalizar",
            _ => "Reintentar",
        };
        RenderSteps(page switch { Page.Welcome => 0, Page.Options => 1, Page.Install => 2, Page.Done => 3, _ => 2 });
    }

    private void RenderSteps(int current)
    {
        var host = Control<StackPanel>("Steps");
        host.Children.Clear();
        for (var i = 0; i < StepNames.Length; i++)
        {
            var done = i < current || (_page == Page.Done && i == current);
            var dot = new Border { Classes = { "stepDot" } };
            dot.Classes.Set("active", i == current && !done);
            dot.Classes.Set("done", done);
            dot.Child = done
                ? new TextBlock { Text = "", FontFamily = (Avalonia.Media.FontFamily)this.FindResource("St.Icons")!, FontSize = 10 }
                : new TextBlock { Text = (i + 1).ToString() };
            var label = new TextBlock { Text = StepNames[i], Classes = { "stepLabel" } };
            label.Classes.Set("active", i == current);
            host.Children.Add(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { dot, label } });
        }
    }

    private async void OnNext(object? sender, RoutedEventArgs e)
    {
        switch (_page)
        {
            case Page.Welcome:
                Show(Page.Options);
                break;
            case Page.Options:
            case Page.Error:
                await InstallAsync();
                break;
            case Page.Done:
                Program.ExitCode = 0;
                if (Control<CheckBox>("LaunchCheck").IsChecked == true)
                {
                    try
                    {
                        SetupEngine.Launch(_installedDirectory);
                    }
                    catch (Exception ex)
                    {
                        SetupEngine.Log($"No se pudo abrir LiveStream: {ex.Message}");
                    }
                }
                Close();
                break;
        }
    }

    private async Task InstallAsync()
    {
        var directoryText = Control<TextBox>("DirectoryBox").Text?.Trim() ?? "";
        string directory;
        try
        {
            directory = Path.GetFullPath(string.IsNullOrEmpty(directoryText) ? InstallLayout.DefaultDirectory : directoryText).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Fail("La carpeta indicada no es válida.", 1);
            return;
        }

        var options = new InstallOptions(
            directory,
            Control<CheckBox>("StartMenuCheck").IsChecked == true,
            Control<CheckBox>("DesktopCheck").IsChecked == true,
            _arguments.WaitPid);

        Show(Page.Install);
        _installing = true;
        var bar = Control<ProgressBar>("Progress");
        var percent = Control<TextBlock>("PercentText");
        var status = Control<TextBlock>("StatusText");
        var progress = new Progress<InstallProgress>(p =>
        {
            bar.Value = p.Fraction;
            percent.Text = $"{p.Fraction * 100:0} %";
            status.Text = p.Status;
        });

        try
        {
            await Task.Run(() => SetupEngine.Install(options, progress));
            _installedDirectory = directory;
            _installing = false;
            Control<TextBlock>("DoneText").Text = options.StartMenuShortcut
                ? "Lo encontrarás en el menú Inicio. Tus proyectos y ajustes están a salvo."
                : $"Instalado en {directory}.";
            Show(Page.Done);
            if (_arguments.Launch)
                Control<CheckBox>("LaunchCheck").IsChecked = true;
        }
        catch (SetupBlockedException ex)
        {
            Fail(ex.Message, ex.ExitCode);
        }
        catch (Exception ex)
        {
            SetupEngine.Log($"Error: {ex}");
            Fail(ex.Message, 1);
        }
    }

    private void Fail(string message, int exitCode)
    {
        _installing = false;
        SetupEngine.Log(message);
        Program.ExitCode = exitCode;
        Control<SelectableTextBlock>("ErrorText").Text = message;
        Show(Page.Error);
    }

    private void OnBack(object? sender, RoutedEventArgs e) => Show(_page == Page.Options ? Page.Welcome : Page.Options);

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_installing)
            return;
        if (_page == Page.Done)
            Program.ExitCode = 0;
        Close();
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnDragWindow(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
            BeginMoveDrag(e);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Carpeta donde instalar LiveStream",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            // Se instala en una subcarpeta propia: el desinstalador borra la carpeta entera.
            Control<TextBox>("DirectoryBox").Text =
                string.Equals(Path.GetFileName(path.TrimEnd('\\')), InstallLayout.AppName, StringComparison.OrdinalIgnoreCase)
                    ? path.TrimEnd('\\')
                    : Path.Combine(path, InstallLayout.AppName);
        }
    }

    private void OnSponsor(object? sender, RoutedEventArgs e) => Open(Sponsorship.SponsorUrl);

    private void OnBrands(object? sender, RoutedEventArgs e) => Open(Sponsorship.BrandsUrl);

    private void OnProject(object? sender, RoutedEventArgs e) => Open(Sponsorship.ProjectUrl);

    private void OnOpenLog(object? sender, RoutedEventArgs e)
    {
        if (File.Exists(SetupEngine.LogPath))
            Open(SetupEngine.LogPath);
    }

    private static void Open(string target)
    {
        try
        {
            SetupEngine.OpenUrl(target);
        }
        catch (Exception ex)
        {
            SetupEngine.Log($"No se pudo abrir {target}: {ex.Message}");
        }
    }
}
