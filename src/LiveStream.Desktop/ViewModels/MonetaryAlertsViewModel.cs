using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveStream.Extensions.Alerts;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Entities;

namespace LiveStream.Desktop.ViewModels;

public sealed partial class MonetaryAlertsViewModel : ObservableObject
{
    private readonly MonetaryAlertService _service;
    private readonly ChannelPointsService _points;

    public ObservableCollection<MonetaryAlertRule> Rules { get; } = [];
    public ObservableCollection<VirtualPointsEntry> PointsHistory { get; } = [];

    [ObservableProperty] private MonetaryAlertRule? _selectedRule;

    // Campos de edición de regla
    [ObservableProperty] private string _ruleName = "Nueva regla monetaria";
    [ObservableProperty] private StreamPlatform _rulePlatform = StreamPlatform.YouTube;
    [ObservableProperty] private AlertKind _ruleKind = AlertKind.SuperChat;
    [ObservableProperty] private decimal _ruleMinAmount = 5.0m;
    [ObservableProperty] private string _ruleCurrency = "USD";
    [ObservableProperty] private string _ruleSpeechTemplate = "¡Muchas gracias {username} por tu apoyo de {amount} {currency}!";
    [ObservableProperty] private bool _ruleEnabled = true;

    // Puntos de canal
    [ObservableProperty] private bool _pointsEnabled = true;
    [ObservableProperty] private string _pointName = "Puntos de Canal";
    [ObservableProperty] private decimal _pointsPerCurrency = 100m;
    [ObservableProperty] private int _pointsPerSub = 500;
    [ObservableProperty] private int _pointsPerGift = 50;

    public MonetaryAlertsViewModel(MonetaryAlertService service, ChannelPointsService points)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _points = points ?? throw new ArgumentNullException(nameof(points));

        _pointsEnabled = _points.Settings.Enabled;
        _pointName = _points.Settings.PointName;
        _pointsPerCurrency = _points.Settings.PointsPerCurrencyUnit;
        _pointsPerSub = _points.Settings.PointsPerSubscription;
        _pointsPerGift = _points.Settings.PointsPerGiftUnit;

        _points.PointsAwarded += OnPointsAwarded;

        RefreshRules();
        RefreshHistory();
    }

    public void RefreshRules()
    {
        Rules.Clear();
        foreach (var r in _service.Rules)
            Rules.Add(r);

        SelectedRule = Rules.FirstOrDefault();
    }

    public void RefreshHistory()
    {
        PointsHistory.Clear();
        foreach (var entry in _points.History)
            PointsHistory.Add(entry);
    }

    partial void OnSelectedRuleChanged(MonetaryAlertRule? value)
    {
        if (value is null) return;
        RuleName = value.Name;
        RulePlatform = value.Platform;
        RuleKind = value.Kind;
        RuleMinAmount = value.MinAmount;
        RuleCurrency = value.Currency;
        RuleSpeechTemplate = value.SpeechTemplate;
        RuleEnabled = value.Enabled;
    }

    partial void OnPointsEnabledChanged(bool value)
    {
        _points.Settings = _points.Settings with { Enabled = value };
    }

    partial void OnPointNameChanged(string value)
    {
        _points.Settings = _points.Settings with { PointName = value };
    }

    partial void OnPointsPerCurrencyChanged(decimal value)
    {
        _points.Settings = _points.Settings with { PointsPerCurrencyUnit = value };
    }

    partial void OnPointsPerSubChanged(int value)
    {
        _points.Settings = _points.Settings with { PointsPerSubscription = value };
    }

    partial void OnPointsPerGiftChanged(int value)
    {
        _points.Settings = _points.Settings with { PointsPerGiftUnit = value };
    }

    [RelayCommand]
    public void SaveCurrentRule()
    {
        var id = SelectedRule?.Id ?? Guid.NewGuid().ToString("N");
        var updated = new MonetaryAlertRule(
            Id: id,
            Name: RuleName.Trim(),
            Platform: RulePlatform,
            Kind: RuleKind,
            MinAmount: RuleMinAmount,
            Currency: RuleCurrency.Trim().ToUpperInvariant(),
            SpeechTemplate: RuleSpeechTemplate.Trim(),
            Enabled: RuleEnabled);

        _service.AddRule(updated);
        RefreshRules();
        SelectedRule = Rules.FirstOrDefault(r => r.Id == id);
    }

    [RelayCommand]
    public void DeleteCurrentRule()
    {
        if (SelectedRule is not { } r) return;
        _service.RemoveRule(r.Id);
        RefreshRules();
    }

    [RelayCommand]
    public void TestCurrentAlert()
    {
        if (SelectedRule is not { } r) return;
        _service.TriggerTestAlert(r);
    }

    private void OnPointsAwarded(VirtualPointsEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PointsHistory.Insert(0, entry);
            if (PointsHistory.Count > 100)
                PointsHistory.RemoveAt(PointsHistory.Count - 1);
        });
    }
}
