using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views;

public partial class EventListView : UserControl
{
    public ObservableCollection<SubathonEvent> EventItems { get; set; } = new();
    private readonly int _maxItems = 20;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IConfig _config;

    public EventListView()
    {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();
        EventListPanel.ItemsSource = EventItems;
        _config = AppServices.Provider.GetRequiredService<IConfig>();
        Task.Run(async () => await LoadRecentEvents());

        SubathonEvents.SubathonEventProcessed += OnSubathonEventProcessed;
        SubathonEvents.SubathonEventsDeleted += OnSubathonEventsDeleted;
        SettingsEvents.EventVisibilityChanged += OnEventVisibilityChanged;
    }

    private void OnSubathonEventsDeleted(List<SubathonEvent> events)
        => Task.Run(async () => await LoadRecentEvents());

    private void OnEventVisibilityChanged()
        => Task.Run(async () => await LoadRecentEvents());

    private async void OnSubathonEventProcessed(SubathonEvent subathonEvent, bool wasEffective)
    {
        bool showOverride = _config.GetBool("App", "ShowLockedEvents", false);

        if (!showOverride && subathonEvent.PointsValue < 1
            && subathonEvent.GetFinalSecondsValueRaw() <= 0
            && subathonEvent.EventType != SubathonEventType.Command
            && subathonEvent.EventType != SubathonEventType.DonationAdjustment
            && subathonEvent.EventType != SubathonEventType.TwitchHypeTrain) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var existing = EventItems.FirstOrDefault(x => x.Id == subathonEvent.Id);
            if (existing != null)
                EventItems.Remove(existing);

            EventItems.Insert(0, subathonEvent);
            while (EventItems.Count > _maxItems)
                EventItems.RemoveAt(EventItems.Count - 1);
        });
    }

    private async Task LoadRecentEvents()
    {
        bool showOverride = _config.GetBool("App", "ShowLockedEvents", false);
        await using var db = await _factory.CreateDbContextAsync();
        SubathonData? subathon = await db.SubathonDatas.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);
        List<SubathonEvent> events = new();
        if (subathon != null)
        {
            events = await db.SubathonEvents.Where(ev => ev.SubathonId == subathon.Id
                                                         && (showOverride || ev.SecondsValue > 0 || ev.PointsValue >= 1
                                                             || ev.Command != SubathonCommandType.None
                                                             || ev.EventType == SubathonEventType.TwitchHypeTrain
                                                             || ev.EventType == SubathonEventType.DonationAdjustment
                                                             || ev.EventType == SubathonEventType.Command))
                .OrderByDescending(e => e.EventTimestamp)
                .Take(_maxItems)
                .ToListAsync();
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EventItems.Clear();
            events.ForEach(ev => EventItems.Add(ev));
        });
    }

    private void ReprocessBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SubathonEvent ev })
        {
            Task.Run(() =>
            {
                SubathonEvent newEv = new SubathonEvent
                {
                    Id = ev.Id,
                    SubathonId = ev.SubathonId,
                    EventTimestamp = ev.EventTimestamp,
                    Source = ev.Source,
                    EventTypeMeta = ev.EventTypeMeta,
                    EventType = ev.EventType,
                    User = ev.User,
                    Value = ev.Value,
                    Amount = ev.Amount,
                    Currency = ev.Currency,
                    SecondsValue = ev.SecondsValue,
                    PointsValue = ev.PointsValue,
                };

                using var db = _factory.CreateDbContext();
                ServiceManager.EventsOrNull?.DeleteSubathonEvent(db, ev);
                SubathonEvents.RaiseSubathonEventCreated(newEv);
            });
        }
    }

    private void DeleteBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SubathonEvent ev }) return;
        if (ev.Command.IsControlTypeCommand()) return;
        Task.Run(() =>
        {
            using var db = _factory.CreateDbContext();
            ServiceManager.EventsOrNull?.DeleteSubathonEvent(db, ev);
        });
    }
}
