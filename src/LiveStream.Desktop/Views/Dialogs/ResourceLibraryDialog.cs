using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LiveStream.Desktop.ViewModels;
using LiveStream.Infrastructure.Persistence;
using static LiveStream.Desktop.Views.Dialogs.StudioDialogs;

namespace LiveStream.Desktop.Views.Dialogs;

/// <summary>
/// Biblioteca de recursos: imágenes y videos disponibles para los gráficos (diseño libre, logos). Importar
/// copia el archivo a %APPDATA%\LiveStream\library, así el original se puede borrar o mover sin romper los
/// gráficos. Sirve para gestionar la biblioteca o para elegir un recurso al editar un elemento.
/// </summary>
public static class ResourceLibraryDialog
{
    public static Task ShowBrowserAsync(MainViewModel vm) => ShowAsync(vm, null, pick: false);

    /// <summary>Devuelve la ruta del recurso elegido, o null si se cancela.</summary>
    public static Task<string?> ShowPickerAsync(MainViewModel vm, Window owner) => ShowAsync(vm, owner, pick: true);

    private static async Task<string?> ShowAsync(MainViewModel vm, Window? owner, bool pick)
    {
        var parent = owner ?? vm.Dialogs.Owner;
        if (parent is null)
            return null;
        var library = vm.Library;

        var window = NewWindow(pick ? "Biblioteca de recursos" : "Biblioteca de recursos de gráficos", 860, 560);

        var suppressCategory = false;
        string? categoryFilter = null;

        var category = new ComboBox { MinWidth = 240, VerticalAlignment = VerticalAlignment.Center };
        var list = new ListBox { Classes = { "studioList" } };
        var import = new Button { Content = "Importar…" };
        var openFolder = new Button { Content = "Abrir carpeta" };
        var remove = new Button { Content = "Eliminar" };
        var use = Primary("Usar recurso");
        var cancel = new Button { Content = pick ? "Cancelar" : "Cerrar", IsCancel = true };
        if (!pick)
            use.IsVisible = false;
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };

        var rows = new List<ResourceItem>();
        static string FormatRow(ResourceItem item) =>
            $"{item.Name}  ·  {item.Category}  ·  {(item.Kind == ResourceKind.Video ? "video" : "imagen")}  ·  {Math.Max(1, item.Size / 1024)} kB";

        ResourceItem? Selected() =>
            list.SelectedItem is not { } row ? null : rows.FirstOrDefault(i => FormatRow(i) == row.ToString());

        async Task RefreshAsync()
        {
            suppressCategory = true;
            try
            {
                var entries = new List<string> { "(todas las categorías)" };
                entries.AddRange(library.Categories());
                category.ItemsSource = entries;
                category.SelectedIndex = Math.Max(0, entries.IndexOf(categoryFilter ?? entries[0]));
            }
            finally
            {
                suppressCategory = false;
            }
            rows = library.List(categoryFilter).ToList();
            list.ItemsSource = rows.Select(FormatRow).ToList();
            list.SelectedIndex = rows.Count > 0 ? 0 : -1;
            status.Text = pick && rows.Count == 0
                ? "La biblioteca está vacía: pulsa «Importar…» o «Examinar…» al añadir un recurso."
                : $"{rows.Count} recurso(s).";
        }

        category.SelectionChanged += (_, _) =>
        {
            if (suppressCategory)
                return;
            categoryFilter = category.SelectedIndex >= 1 && category.SelectedItem is { } value ? value.ToString() : null;
            _ = RefreshAsync();
        };
        import.Click += async (_, _) =>
        {
            var path = await vm.Dialogs.PickFileAsync("Importar a la biblioteca",
                ResourceLibrary.ImageFilter + ";;" + ResourceLibrary.VideoFilter + ";;Todos (*.*)");
            if (path is null)
                return;
            try
            {
                var item = library.Import(path, categoryFilter ?? ResourceLibrary.DefaultCategory);
                status.Text = $"Importado a «{item.Category}»: {item.Name}";
                error.Text = "";
                await RefreshAsync();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                    or NotSupportedException)
            {
                error.Text = ex.Message;
            }
        };
        remove.Click += async (_, _) =>
        {
            if (Selected() is not { } item)
            {
                error.Text = "Selecciona un recurso para eliminarlo.";
                return;
            }
            if (!await vm.Dialogs.ConfirmAsync("Eliminar recurso",
                    $"«{item.Name}» se borra de la biblioteca (los gráficos que lo usen dejarán de ver el archivo). ¿Continuar?",
                    "Eliminar", destructive: true))
                return;
            try
            {
                library.Delete(item);
                error.Text = "";
                await RefreshAsync();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                error.Text = ex.Message;
            }
        };
        openFolder.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = library.Root,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                error.Text = $"No se pudo abrir la carpeta: {ex.Message}";
            }
        };

        void Use()
        {
            if (Selected()?.Path is { Length: > 0 } path)
            {
                window.Tag = path;
                window.Close();
            }
        }
        use.Click += (_, _) => Use();
        list.DoubleTapped += (_, _) => { if (pick) Use(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { cancel, use },
        };

        var top = new DockPanel();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { import, openFolder, remove },
        };
        DockPanel.SetDock(actions, Dock.Right);
        top.Children.Add(actions);
        top.Children.Add(category);

        var body = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        body.Children.Add(top);
        body.Children.Add(list);
        body.Children.Add(error);
        body.Children.Add(status);
        body.Children.Add(buttons);
        window.Content = body;

        await RefreshAsync();
        await window.ShowDialog(parent);
        return window.Tag as string;
    }
}
