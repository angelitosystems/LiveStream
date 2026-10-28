using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveStream.Extensions.Chat;
using LiveStream.Extensions.Tts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Tts;

namespace LiveStream.Desktop.ViewModels;

public sealed partial class ChatViewModel : ObservableObject
{
    private readonly UnifiedChatService _chat;
    private readonly ChatOverlayService _overlay;
    private readonly ChatTtsCoordinator? _coordinator;

    public ObservableCollection<ChatMessageRowViewModel> Messages { get; } = [];

    [ObservableProperty] private ChatMessageRowViewModel? _selectedMessage;
    [ObservableProperty] private bool _isAutoScrollPaused;
    [ObservableProperty] private string _userFilterText = string.Empty;
    [ObservableProperty] private string _searchFilterText = string.Empty;
    [ObservableProperty] private string _wordBlacklistText = string.Empty;
    [ObservableProperty] private bool _showOnlyModerated;
    [ObservableProperty] private bool _showOnlyHighlights;
    [ObservableProperty] private bool _showOnlyDonations;
    [ObservableProperty] private StreamPlatform? _selectedPlatform;
    [ObservableProperty] private ChatClickAction _clickAction = ChatClickAction.ReadAndShow;

    // Campos del simulador de prueba
    [ObservableProperty] private string _simulatedAuthor = "UsuarioPrueba";
    [ObservableProperty] private string _simulatedMessage = "¡Hola desde el chat de prueba!";
    [ObservableProperty] private StreamPlatform _simulatedPlatform = StreamPlatform.YouTube;
    [ObservableProperty] private bool _simulatedIsDonation;
    [ObservableProperty] private decimal _simulatedDonationAmount = 10m;

    public ChatViewModel(UnifiedChatService chat, ChatOverlayService overlay, ChatTtsCoordinator? coordinator = null)
    {
        _chat = chat;
        _overlay = overlay;
        _coordinator = coordinator;

        _chat.MessageAdded += (_, msg) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_chat.Filter.Matches(msg))
                    Messages.Add(new ChatMessageRowViewModel(msg));
            });
        };

        _chat.ChatReset += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => Messages.Clear());
        };

        _chat.ModerationToggled += (_, msg) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var existing = Messages.FirstOrDefault(m => m.Id == msg.Id);
                if (existing is not null)
                    existing.Message = msg;
            });
        };

        RefreshFilteredMessages();
    }

    public ChatOverlayStyle OverlayStyle => _overlay.Style;

    partial void OnUserFilterTextChanged(string value) => ApplyFilter();
    partial void OnSearchFilterTextChanged(string value) => ApplyFilter();
    partial void OnShowOnlyModeratedChanged(bool value) => ApplyFilter();
    partial void OnShowOnlyHighlightsChanged(bool value) => ApplyFilter();
    partial void OnShowOnlyDonationsChanged(bool value) => ApplyFilter();
    partial void OnSelectedPlatformChanged(StreamPlatform? value) => ApplyFilter();
    partial void OnWordBlacklistTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var blacklist = WordBlacklistText.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _chat.Filter = new ChatFilter(
            Platform: SelectedPlatform,
            UserQuery: string.IsNullOrWhiteSpace(UserFilterText) ? null : UserFilterText,
            SearchQuery: string.IsNullOrWhiteSpace(SearchFilterText) ? null : SearchFilterText,
            WordBlacklist: blacklist.Length > 0 ? blacklist : null,
            ShowOnlyModerated: ShowOnlyModerated,
            ShowOnlyHighlights: ShowOnlyHighlights,
            ShowOnlyDonations: ShowOnlyDonations);

        RefreshFilteredMessages();
    }

    public void RefreshFilteredMessages()
    {
        Messages.Clear();
        foreach (var msg in _chat.FilteredMessages)
            Messages.Add(new ChatMessageRowViewModel(msg));
    }

    [RelayCommand]
    public void ToggleAutoScroll()
    {
        IsAutoScrollPaused = !IsAutoScrollPaused;
        _chat.IsAutoScrollPaused = IsAutoScrollPaused;
    }

    [RelayCommand]
    public void ClearChat()
    {
        _chat.Clear();
        _overlay.ClearOverlay();
    }

    [RelayCommand]
    public void ToggleModeration(ChatMessageRowViewModel? row)
    {
        if (row is null) return;
        _chat.ToggleModerated(row.Id);
    }

    partial void OnClickActionChanged(ChatClickAction value)
    {
        if (_coordinator is not null)
            _coordinator.Queue.Settings = _coordinator.Queue.Settings with { ClickAction = value };
    }

    [RelayCommand]
    public void ClickMessage(ChatMessageRowViewModel? row)
    {
        if (row is null) return;

        switch (ClickAction)
        {
            case ChatClickAction.ReadOnly:
                _coordinator?.SpeakMessageManually(row.Message);
                break;

            case ChatClickAction.ShowOnly:
                _chat.ToggleModerated(row.Id);
                break;

            case ChatClickAction.ReadAndShow:
                _chat.ToggleModerated(row.Id);
                _coordinator?.SpeakMessageManually(row.Message);
                break;

            case ChatClickAction.ShowAfterRead:
                if (_coordinator is not null)
                {
                    void OnCompleted(TtsUtterance u)
                    {
                        if (u.Id == row.Id)
                        {
                            _coordinator.Queue.UtteranceCompleted -= OnCompleted;
                            Dispatcher.UIThread.Post(() => _chat.ToggleModerated(row.Id));
                        }
                    }
                    _coordinator.Queue.UtteranceCompleted += OnCompleted;
                    _coordinator.SpeakMessageManually(row.Message);
                }
                else
                {
                    _chat.ToggleModerated(row.Id);
                }
                break;

            case ChatClickAction.ReadAfterShow:
                _chat.ToggleModerated(row.Id);
                Task.Delay(1500).ContinueWith(_ =>
                {
                    Dispatcher.UIThread.Post(() => _coordinator?.SpeakMessageManually(row.Message));
                });
                break;
        }
    }

    [RelayCommand]
    public void SetClickAction(ChatClickAction action)
    {
        ClickAction = action;
    }

    [RelayCommand]
    public void SpeakMessage(ChatMessageRowViewModel? row)
    {
        if (row is null || _coordinator is null) return;
        _coordinator.SpeakMessageManually(row.Message);
    }

    [RelayCommand]
    public void BlockUserTts(ChatMessageRowViewModel? row)
    {
        if (row is null || _coordinator is null || string.IsNullOrWhiteSpace(row.Message.AuthorId)) return;
        _coordinator.BlockUser(row.Message.AuthorId);
    }

    [RelayCommand]
    public void SendSimulatedMessage()
    {
        if (string.IsNullOrWhiteSpace(SimulatedAuthor) || string.IsNullOrWhiteSpace(SimulatedMessage))
            return;

        var msg = new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            Platform: SimulatedPlatform,
            AuthorName: SimulatedAuthor.Trim(),
            AuthorId: SimulatedAuthor.Trim().ToLowerInvariant(),
            Message: SimulatedMessage.Trim(),
            Timestamp: DateTime.UtcNow,
            IsDonation: SimulatedIsDonation,
            DonationAmount: SimulatedIsDonation ? SimulatedDonationAmount : null,
            Currency: SimulatedIsDonation ? "USD" : null,
            IsHighlighted: SimulatedIsDonation);

        _chat.AddMessage(msg);
        SimulatedMessage = string.Empty;
    }
}
