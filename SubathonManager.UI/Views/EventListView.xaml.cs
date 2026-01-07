using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Data;

namespace SubathonManager.UI.Views
{
    public partial class EventListView
    {
        public ObservableCollection<SubathonEvent> EventItems { get; set; } = new();
        private int _maxItems = 20;
        private readonly IDbContextFactory<AppDbContext> _factory;

        public EventListView()
        {
            _factory = AppServices.Provider!.GetRequiredService<IDbContextFactory<AppDbContext>>();
            InitializeComponent();
            EventListPanel.ItemsSource = EventItems;
            LoadRecentEvents();

            SubathonEvents.SubathonEventProcessed += OnSubathonEventProcessed;
            SubathonEvents.SubathonEventsDeleted += OnSubathonEventsDeleted;
        }

        private void OnSubathonEventsDeleted(List<SubathonEvent> events)
        {
            Task.Run(LoadRecentEvents);
        }

        private async void OnSubathonEventProcessed(SubathonEvent subathonEvent, bool wasEffective)
        {
            if (subathonEvent.PointsValue < 1 
                && subathonEvent.GetFinalSecondsValueRaw() <= 0
                && subathonEvent.EventType != SubathonEventType.Command
                && subathonEvent.EventType != SubathonEventType.DonationAdjustment
                && subathonEvent.EventType != SubathonEventType.TwitchHypeTrain) return;
            
            await Dispatcher.InvokeAsync(() =>
            {
                var existing = EventItems.FirstOrDefault(x => x.Id == subathonEvent.Id);
                if (existing != null)
                    EventItems.Remove(existing);

                EventItems.Insert(0, subathonEvent);
                while (EventItems.Count > _maxItems)
                    EventItems.RemoveAt(EventItems.Count - 1);
            });
        }

        private async void LoadRecentEvents()
        {
            await using var db = await _factory.CreateDbContextAsync();
            SubathonData? subathon = await db.SubathonDatas.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);
            List<SubathonEvent> events = new();
            if (subathon != null)
            {
                events = await db.SubathonEvents.Where(ev => ev.SubathonId == subathon.Id 
                                                             && (ev.SecondsValue > 0 || ev.PointsValue >= 1 
                                                                 || ev.Command != SubathonCommandType.None
                                                                 || ev.EventType == SubathonEventType.TwitchHypeTrain
                                                                 || ev.EventType == SubathonEventType.DonationAdjustment
                                                                 || ev.EventType == SubathonEventType.Command))
                    .OrderByDescending(e => e.EventTimestamp)
                    .Take(_maxItems)
                    .ToListAsync();
            }

            await Dispatcher.InvokeAsync(() =>
            {
                EventItems.Clear();
                events.ForEach(ev => EventItems.Add(ev));
            });
        }

        private void ReprocessBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Wpf.Ui.Controls.Button btn && btn.DataContext is SubathonEvent ev)
            {
                Task.Run(() =>
                {
                    // lazy, delete and remake
                    SubathonEvent newEv = new SubathonEvent
                    {
                        Id = ev.Id,
                        SubathonId = ev.SubathonId,
                        EventTimestamp = ev.EventTimestamp,
                        Source = ev.Source,
                        EventType = ev.EventType,
                        User = ev.User,
                        Value = ev.Value,
                        Amount = ev.Amount,
                        Currency = ev.Currency,
                        SecondsValue = ev.SecondsValue,
                        PointsValue = ev.PointsValue,
                    };
                    
                    using var db = _factory.CreateDbContext();
                    App.AppEventService?.DeleteSubathonEvent(db, ev);
                    SubathonEvents.RaiseSubathonEventCreated(newEv);
                });;
            }
        }

        private void DeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Wpf.Ui.Controls.Button btn && btn.DataContext is SubathonEvent ev)
            {
                if (ev.Command.IsControlTypeCommand()) return;
                Task.Run(() =>
                {
                    using var db = _factory.CreateDbContext();
                    App.AppEventService?.DeleteSubathonEvent(db, ev);
                });
            }
        }
    }
}