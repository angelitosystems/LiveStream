using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveStream.Extensions.Alerts;
using LiveStream.Application.Studio;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Entities;

namespace LiveStream.Desktop.ViewModels;

public sealed partial class AlertsViewModel : ObservableObject
{
    private readonly StudioSession _session;
    private readonly AlertQueue _queue;

    public ObservableCollection<AlertEventRowViewModel> RecentAlerts { get; } = [];

    [ObservableProperty] private bool _hasActiveAlert;
    [ObservableProperty] private string _activeAlertTitle = string.Empty;
    [ObservableProperty] private string _activeAlertText = string.Empty;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isSoundMuted;
    [ObservableProperty] private StreamPlatform _testPlatform = StreamPlatform.Twitch;

    public event Action? RequestOpenDesigner;
    public event Action? RequestOpenSoundBank;

    [RelayCommand]
    public void OpenDesigner() => RequestOpenDesigner?.Invoke();

    [RelayCommand]
    public void OpenSoundBank() => RequestOpenSoundBank?.Invoke();

    [RelayCommand]
    public void SelectPlatform(StreamPlatform platform)
    {
        TestPlatform = platform;
    }

    public AlertsViewModel(StudioSession session, AlertQueue queue)
    {
        _session = session;
        _queue = queue;

        _queue.AlertStarted += (_, state) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                HasActiveAlert = true;
                ActiveAlertTitle = state.Rule.Name;
                ActiveAlertText = state.FormattedText;
                PendingCount = _queue.PendingCount;
                RecentAlerts.Insert(0, new AlertEventRowViewModel(state.Event, state.FormattedText));
                if (RecentAlerts.Count > 100)
                    RecentAlerts.RemoveAt(RecentAlerts.Count - 1);
            });
        };

        _queue.AlertCompleted += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                HasActiveAlert = _queue.ActiveAlert is not null;
                if (!HasActiveAlert)
                {
                    ActiveAlertTitle = string.Empty;
                    ActiveAlertText = string.Empty;
                }
                PendingCount = _queue.PendingCount;
            });
        };

        _queue.QueueChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                PendingCount = _queue.PendingCount;
                IsPaused = _queue.IsPaused;
            });
        };

        _isSoundMuted = _queue.IsSoundMuted;
    }

    [RelayCommand]
    public void ToggleSoundMute()
    {
        IsSoundMuted = !IsSoundMuted;
        _queue.IsSoundMuted = IsSoundMuted;
    }

    [RelayCommand]
    public void TogglePauseQueue()
    {
        _queue.IsPaused = !_queue.IsPaused;
        IsPaused = _queue.IsPaused;
    }

    [RelayCommand]
    public void SkipActiveAlert() => _queue.SkipActive();

    [RelayCommand]
    public void ClearQueue() => _queue.Clear();

    [RelayCommand]
    public void TriggerTestAlert(AlertKind kind)
    {
        _queue.TriggerTestAlert(kind, TestPlatform, _session.Project.AlertRules);
    }
}
