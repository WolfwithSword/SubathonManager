using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Models;
using SubathonManager.Core.Events;
using SubathonManager.Data;

namespace SubathonManager.UI.Views
{
    public partial class EventListView
    {
        public ObservableCollection<SubathonEvent> EventItems { get; set; } = new();
        private int _maxItems = 20;

        public EventListView()
        {
            InitializeComponent();
            EventListPanel.ItemsSource = EventItems;
            LoadRecentEvents();

            SubathonEvents.SubathonEventProcessed += OnSubathonEventProcessed;
            SubathonEvents.SubathonEventsDeleted += OnSubathonEventsDeleted;
        }

        private void OnSubathonEventsDeleted()
        {
            Task.Run(() => LoadRecentEvents());
        }

        private async void OnSubathonEventProcessed(SubathonEvent subathonEvent)
        {
            if (subathonEvent.PointsValue < 1 && subathonEvent.SecondsValue < 1) return;
            
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
            using var db = new AppDbContext();
            SubathonData? subathon = await db.SubathonDatas.FirstOrDefaultAsync(s => s.IsActive);
            List<SubathonEvent> events = new();
            if (subathon != null)
            {
                events = await db.SubathonEvents.Where(ev => ev.SubathonId == subathon.Id 
                                                             && (ev.SecondsValue >= 1 || ev.PointsValue >= 1 ))
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
            // basically only meant to be run if something breaks, doesn't even show normally
            // TODO think about behaviour for showing it. 
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
                    
                    AppDbContext.DeleteSubathonEvent(ev);
                    SubathonEvents.RaiseSubathonEventCreated(newEv);
                });;
            }
        }

        private void DeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Wpf.Ui.Controls.Button btn && btn.DataContext is SubathonEvent ev)
            {
                Task.Run(() =>
                {
                    AppDbContext.DeleteSubathonEvent(ev);
                });
            }
        }
    }
}