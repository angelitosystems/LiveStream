using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using StreamPilot.Application.Abstractions;
using StreamPilot.Desktop.ViewModels;
using StreamPilot.Desktop.Views;
using StreamPilot.Native.Engine;

namespace StreamPilot.Desktop;

public sealed class App : Avalonia.Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddSingleton<LibObsEngine>();
        services.AddSingleton<IStreamingEngine>(sp => sp.GetRequiredService<LibObsEngine>());
        services.AddSingleton<EngineSpikeViewModel>();
        _services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<EngineSpikeViewModel>(),
            };
            // Bloquea a propósito: libobs debe apagarse antes de que termine el proceso.
            // No hay interbloqueo porque el trabajo nativo corre en su propio hilo.
            desktop.Exit += (_, _) => _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
