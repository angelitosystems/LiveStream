using Avalonia.Controls;
using StreamPilot.Desktop.ViewModels;

namespace StreamPilot.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Preview.NativeHandleCreated += async (hwnd, w, h) => await Vm.OnPreviewCreatedAsync(hwnd, w, h);
        Preview.NativeHandleResized += async (w, h) => await Vm.OnPreviewResizedAsync(w, h);
        // Síncrono a propósito: libobs debe soltar el HWND antes de que la ventana hija se destruya.
        Preview.NativeHandleDestroying += () =>
        {
            try
            {
                Vm.OnPreviewDestroyingAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // El motor ya se apagó; sp_engine_destroy también libera la vista previa.
            }
        };
    }

    private EngineSpikeViewModel Vm => (EngineSpikeViewModel)DataContext!;
}
