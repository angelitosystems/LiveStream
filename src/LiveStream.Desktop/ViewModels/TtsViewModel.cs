using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveStream.Application.Tts;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Tts;

namespace LiveStream.Desktop.ViewModels;

public sealed partial class TtsViewModel : ObservableObject
{
    private readonly ChatTtsCoordinator _coordinator;
    private readonly TtsQueueService _queue;
    private readonly ITtsProvider _provider;

    public ObservableCollection<TtsVoice> AvailableVoices { get; } = [];
    public ObservableCollection<TtsUtterance> QueueItems { get; } = [];

    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _autoRead = true;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string? _currentText;
    [ObservableProperty] private string? _currentAuthor;
    [ObservableProperty] private string? _currentPlatform;
    [ObservableProperty] private TtsVoice? _selectedVoice;
    [ObservableProperty] private int _volume = 100;
    [ObservableProperty] private int _rate = 0;
    [ObservableProperty] private int _pitch = 0;
    [ObservableProperty] private int _maxLength = 160;
    [ObservableProperty] private ChatClickAction _clickAction = ChatClickAction.ReadAndShow;
    [ObservableProperty] private string _providerStatus = "Conectado";
    [ObservableProperty] private string _testPhrase = "Hola, esta es una prueba de voz para la transmisión en LiveStream.";

    // Reglas rápidas por plataforma
    [ObservableProperty] private bool _youtubeEnabled = true;
    [ObservableProperty] private bool _tiktokEnabled = true;
    [ObservableProperty] private bool _twitchEnabled = true;
    [ObservableProperty] private bool _facebookEnabled = true;
    [ObservableProperty] private bool _twitchSubsOnly;

    // Voces asignadas por plataforma
    [ObservableProperty] private TtsVoice? _selectedYouTubeVoice;
    [ObservableProperty] private TtsVoice? _selectedTikTokVoice;
    [ObservableProperty] private TtsVoice? _selectedTwitchVoice;
    [ObservableProperty] private TtsVoice? _selectedFacebookVoice;

    public TtsViewModel(ChatTtsCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _queue = coordinator.Queue;
        _provider = _queue.Settings.ProviderId == "external_api"
            ? new ExternalApiTtsProvider(new WindowsSapiTtsProvider())
            : new WindowsSapiTtsProvider();

        _enabled = _queue.Settings.Enabled;
        _autoRead = _coordinator.AutoReadEnabled;
        _volume = _queue.Settings.Volume;
        _rate = _queue.Settings.Rate;
        _pitch = _queue.Settings.Pitch;
        _maxLength = _queue.Settings.MaxLength;
        _clickAction = _queue.Settings.ClickAction;
        _providerStatus = _provider.StatusMessage ?? "Listo";

        _queue.CurrentUtteranceChanged += OnCurrentUtteranceChanged;
        _queue.QueueChanged += OnQueueChanged;
        _queue.StatusChanged += OnStatusChanged;

        _ = LoadVoicesAsync();
    }

    public async Task LoadVoicesAsync()
    {
        try
        {
            var voices = await _provider.GetVoicesAsync(CancellationToken.None);
            Dispatcher.UIThread.Post(() =>
            {
                AvailableVoices.Clear();
                foreach (var v in voices)
                    AvailableVoices.Add(v);

                SelectedVoice = AvailableVoices.FirstOrDefault(v => v.Id == _queue.Settings.DefaultVoiceId)
                                ?? AvailableVoices.FirstOrDefault(v => v.IsDefault)
                                ?? AvailableVoices.FirstOrDefault();

                SelectedYouTubeVoice = AvailableVoices.FirstOrDefault(v => v.Id == _coordinator.GetRuleFor(StreamPlatform.YouTube).CustomVoiceId);
                SelectedTwitchVoice = AvailableVoices.FirstOrDefault(v => v.Id == _coordinator.GetRuleFor(StreamPlatform.Twitch).CustomVoiceId);
                SelectedTikTokVoice = AvailableVoices.FirstOrDefault(v => v.Id == _coordinator.GetRuleFor(StreamPlatform.TikTok).CustomVoiceId);
                SelectedFacebookVoice = AvailableVoices.FirstOrDefault(v => v.Id == _coordinator.GetRuleFor(StreamPlatform.Facebook).CustomVoiceId);
            });
        }
        catch { }
    }

    partial void OnEnabledChanged(bool value)
    {
        _queue.Settings = _queue.Settings with { Enabled = value };
    }

    partial void OnAutoReadChanged(bool value)
    {
        _coordinator.AutoReadEnabled = value;
    }

    partial void OnVolumeChanged(int value)
    {
        _queue.Settings = _queue.Settings with { Volume = value };
    }

    partial void OnRateChanged(int value)
    {
        _queue.Settings = _queue.Settings with { Rate = value };
    }

    partial void OnPitchChanged(int value)
    {
        _queue.Settings = _queue.Settings with { Pitch = value };
    }

    partial void OnClickActionChanged(ChatClickAction value)
    {
        _queue.Settings = _queue.Settings with { ClickAction = value };
    }

    partial void OnMaxLengthChanged(int value)
    {
        _queue.Settings = _queue.Settings with { MaxLength = value };
    }

    partial void OnSelectedVoiceChanged(TtsVoice? value)
    {
        if (value is not null)
            _queue.Settings = _queue.Settings with { DefaultVoiceId = value.Id };
    }

    partial void OnSelectedYouTubeVoiceChanged(TtsVoice? value) =>
        _coordinator.SetPlatformRule(_coordinator.GetRuleFor(StreamPlatform.YouTube) with { CustomVoiceId = value?.Id });

    partial void OnSelectedTikTokVoiceChanged(TtsVoice? value) =>
        _coordinator.SetPlatformRule(_coordinator.GetRuleFor(StreamPlatform.TikTok) with { CustomVoiceId = value?.Id });

    partial void OnSelectedTwitchVoiceChanged(TtsVoice? value) =>
        _coordinator.SetPlatformRule(_coordinator.GetRuleFor(StreamPlatform.Twitch) with { CustomVoiceId = value?.Id });

    partial void OnSelectedFacebookVoiceChanged(TtsVoice? value) =>
        _coordinator.SetPlatformRule(_coordinator.GetRuleFor(StreamPlatform.Facebook) with { CustomVoiceId = value?.Id });

    partial void OnYoutubeEnabledChanged(bool value) =>
        _coordinator.SetPlatformRule(_coordinator.GetRuleFor(StreamPlatform.YouTube) with { Enabled = value });

    partial void OnTiktokEnabledChanged(bool value) =>
        _coordinator.SetPlatformRule(_coordinator.GetRuleFor(StreamPlatform.TikTok) with { Enabled = value });

    partial void OnTwitchEnabledChanged(bool value) =>
        _coordinator.SetPlatformRule(_coordinator.GetRuleFor(StreamPlatform.Twitch) with { Enabled = value });

    partial void OnFacebookEnabledChanged(bool value) =>
        _coordinator.SetPlatformRule(_coordinator.GetRuleFor(StreamPlatform.Facebook) with { Enabled = value });

    partial void OnTwitchSubsOnlyChanged(bool value) =>
        _coordinator.SetPlatformRule(_coordinator.GetRuleFor(StreamPlatform.Twitch) with { SubscribersOnly = value });

    [RelayCommand]
    public void ToggleMute()
    {
        IsMuted = !IsMuted;
        _queue.SetMuted(IsMuted);
    }

    [RelayCommand]
    public async Task TogglePause()
    {
        await _queue.TogglePauseAsync();
        IsPaused = _queue.IsPaused;
    }

    [RelayCommand]
    public void Skip()
    {
        _queue.SkipCurrent();
    }

    [RelayCommand]
    public void Repeat()
    {
        _queue.RepeatLast();
    }

    [RelayCommand]
    public void ClearQueue()
    {
        _queue.ClearQueue();
    }

    [RelayCommand]
    public void RemoveQueueItem(TtsUtterance? item)
    {
        if (item is not null)
            _queue.RemoveFromQueue(item.Id);
    }

    [RelayCommand]
    public void TestSpeak()
    {
        if (string.IsNullOrWhiteSpace(TestPhrase))
            return;

        _queue.Enqueue(new TtsUtterance(
            Id: $"test_{Guid.NewGuid():N}",
            Text: TestPhrase.Trim(),
            VoiceId: SelectedVoice?.Id,
            Rate: Rate,
            Volume: Volume,
            IsManual: true,
            Author: "Creador"));
    }

    private void OnCurrentUtteranceChanged(TtsUtterance? current)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (current is null)
            {
                CurrentText = null;
                CurrentAuthor = null;
                CurrentPlatform = null;
                IsPlaying = false;
            }
            else
            {
                CurrentText = current.Text;
                CurrentAuthor = current.Author ?? "Anónimo";
                CurrentPlatform = current.Platform?.ToString() ?? "Estudio";
                IsPlaying = true;
            }
        });
    }

    private void OnQueueChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            QueueItems.Clear();
            foreach (var item in _queue.PendingQueue)
                QueueItems.Add(item);
        });
    }

    private void OnStatusChanged(TtsPlaybackStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = status == TtsPlaybackStatus.Playing;
            IsPaused = status == TtsPlaybackStatus.Paused;
        });
    }
}
