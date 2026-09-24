using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.Controls;

namespace SubathonManager.UI.Views.Schedule;

public partial class ScheduleUpcomingView : UserControl {
    private const int Limit = 30;

    private readonly IDbContextFactory<AppDbContext> _factory;

    public ScheduleUpcomingView() {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();

        ScheduleEvents.ScheduleChanged += source => {
            if (ReferenceEquals(source, this)) return;
            Dispatcher.UIThread.Post(Refresh);
        };
        Loaded += (_, _) => Refresh();
    }

    public event Action<DateTime, Guid>? ItemRequested;

    private void Refresh() {
        DateTime today = DateTime.Today;
        List<ScheduleItem> items;
        using (AppDbContext db = _factory.CreateDbContext()) {
            items = db.ScheduleItems.AsNoTracking()
                .Where(i => i.Date >= today && !i.IsDone)
                .OrderBy(i => i.Date).ThenBy(i => i.SortOrder).ThenBy(i => i.CreatedAt)
                .Take(Limit)
                .ToList();
        }

        CardsStack.Children.Clear();
        foreach (ScheduleItem item in items)
            CardsStack.Children.Add(BuildCard(item, today));
        EmptyText.IsVisible = items.Count == 0;
    }

    private Border BuildCard(ScheduleItem item, DateTime today) {
        string when = item.Date == today ? "Today"
            : item.Date == today.AddDays(1) ? "Tomorrow"
            : item.Date.ToString("ddd, MMM d", CultureInfo.CurrentCulture);
        bool isEvent = item.Kind == ScheduleItemKind.Event;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var icon = new SymIcon {
            Glyph = isEvent ? "CalendarToday20" : "TaskListSquare20",
            Opacity = 0.8,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(icon, isEvent ? "Event" : "Task");

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock {
            Text = string.IsNullOrWhiteSpace(item.Title) ? "(untitled)" : item.Title,
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var meta = new TextBlock {
            Text = $"{when}  ·  {item.TimeLabel()}",
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0)
        };

        meta.Classes.Add("soft");
        text.Children.Add(meta);

        string firstLine = item.Description.Split('\n', 2)[0].Trim();
        if (firstLine.Length > 0) {
            var desc = new TextBlock {
                Text = firstLine,
                FontSize = 12,
                Opacity = 0.8,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            desc.Classes.Add("soft");
            text.Children.Add(desc);
        }

        var doneBtn = new Button {
            Content = new SymIcon { Glyph = "CheckmarkCircle20" },
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        doneBtn.Classes.Add("donebtn");
        ToolTip.SetTip(doneBtn, "Mark as done");
        doneBtn.Click += (_, _) => MarkDone(item.Id);

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(doneBtn, 2);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(doneBtn);

        var card = new Border { Child = grid };
        card.Classes.Add("upcard");
        ToolTip.SetTip(text, "Open in Schedule");
        card.Tapped += (_, e) => {
            if (IsWithinButton(e.Source)) return;
            ItemRequested?.Invoke(item.Date, item.Id);
        };
        return card;
    }

    private static bool IsWithinButton(object? source) {
        var v = source as Visual;
        while (v != null) {
            if (v is Button) return true;
            v = v.GetVisualParent();
        }

        return false;
    }

    private void MarkDone(Guid id) {
        using (AppDbContext db = _factory.CreateDbContext()) {
            db.ScheduleItems.Where(i => i.Id == id)
                .ExecuteUpdate(s => s.SetProperty(i => i.IsDone, true));
        }

        Refresh();
        ScheduleEvents.RaiseScheduleChanged(this);
    }
}