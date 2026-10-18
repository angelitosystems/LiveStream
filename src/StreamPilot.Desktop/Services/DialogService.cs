using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace StreamPilot.Desktop.Services;

/// <summary>Diálogos modales sencillos y selectores de archivos. Todos usan las clases del tema central.</summary>
public sealed class DialogService
{
    public Window? Owner => (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Aceptar", bool destructive = false)
    {
        var result = false;
        await ShowAsync(title, message, null, [
            ("Cancelar", false, () => { }),
            (confirmText, true, () => result = true),
        ], destructive);
        return result;
    }

    public Task InfoAsync(string title, string message) =>
        ShowAsync(title, message, null, [("Aceptar", true, () => { })], false);

    public async Task<string?> PromptAsync(string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, Width = 340 };
        string? result = null;
        await ShowAsync(title, label, box, [
            ("Cancelar", false, () => { }),
            ("Aceptar", true, () => result = box.Text?.Trim()),
        ], false, () => { box.Focus(); box.SelectAll(); });
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner is null)
            return null;
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickFileAsync(string title, string? filter = null)
    {
        if (Owner is null)
            return null;
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            FileTypeFilter = ParseObsFilter(filter),
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string extension)
    {
        if (Owner is null)
            return null;
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Convierte el filtro de libobs ("Imágenes (*.png *.jpg);;Todos (*.*)") al de Avalonia.</summary>
    private static List<FilePickerFileType>? ParseObsFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return null;
        var types = new List<FilePickerFileType>();
        foreach (var part in filter.Split(";;", StringSplitOptions.RemoveEmptyEntries))
        {
            var open = part.IndexOf('(');
            var close = part.LastIndexOf(')');
            if (open < 0 || close <= open)
                continue;
            var patterns = part[(open + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            types.Add(new FilePickerFileType(part[..open].Trim()) { Patterns = patterns });
        }
        return types.Count == 0 ? null : types;
    }

    private async Task ShowAsync(string title, string message, Control? extra, (string Text, bool IsDefault, Action OnClick)[] buttons,
        bool destructive, Action? onOpened = null)
    {
        if (Owner is null)
            return;

        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 380,
            MaxWidth = 560,
        };

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (text, isDefault, onClick) in buttons)
        {
            var button = new Button { Content = text, MinWidth = 96, IsDefault = isDefault, IsCancel = !isDefault };
            if (isDefault)
                button.Classes.Add(destructive ? "live" : "primary");
            button.Click += (_, _) =>
            {
                onClick();
                window.Close();
            };
            buttonRow.Children.Add(button);
        }

        var content = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (extra is not null)
            content.Children.Add(extra);
        content.Children.Add(buttonRow);
        window.Content = content;
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                window.Close();
        };
        if (onOpened is not null)
            window.Opened += (_, _) => onOpened();

        await window.ShowDialog(Owner);
    }
}
