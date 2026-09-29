using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;

namespace SubathonManager.Tests.CoreUnitTests;

public class ScheduleCsvTests {
    private static readonly DateTime Day = new(2026, 9, 23);

    [Fact]
    public void Write_UsesDbColumnNamesInOrder() {
        string csv = ScheduleCsv.Write([]);
        Assert.Equal("Date,Kind,StartMinute,EndMinute,Title,Description,IsDone", csv.TrimEnd());
    }

    [Fact]
    public void Write_ThenParse_RoundTripsInOrder() {
        var items = new List<ScheduleItem> {
            new() {
                Date = Day, Kind = ScheduleItemKind.Event, StartMinute = 22 * 60, EndMinute = 2 * 60,
                Title = "Late stream", Description = "line one\nline \"two\", with comma", IsDone = true
            },
            new() { Date = Day, Kind = ScheduleItemKind.Task, Title = "Prep overlay" },
            new() { Date = Day.AddDays(1), Kind = ScheduleItemKind.Event, StartMinute = 9 * 60, Title = "Breakfast" }
        };

        ScheduleCsv.ParseResult parsed = ScheduleCsv.Parse(ScheduleCsv.Write(items));

        Assert.Empty(parsed.Errors);
        Assert.Equal(3, parsed.Items.Count);
        for (var i = 0; i < items.Count; i++) {
            Assert.Equal(items[i].Date, parsed.Items[i].Date);
            Assert.Equal(items[i].Kind, parsed.Items[i].Kind);
            Assert.Equal(items[i].StartMinute, parsed.Items[i].StartMinute);
            Assert.Equal(items[i].EndMinute, parsed.Items[i].EndMinute);
            Assert.Equal(items[i].Title, parsed.Items[i].Title);
            Assert.Equal(items[i].Description, parsed.Items[i].Description);
            Assert.Equal(items[i].IsDone, parsed.Items[i].IsDone);
        }
    }

    [Fact]
    public void Parse_ColumnsMatchByNameAndOptionalOnesDefault() {
        const string csv = "title,DATE\nJust a title,2026-09-23\n";
        ScheduleCsv.ParseResult parsed = ScheduleCsv.Parse(csv);

        Assert.Empty(parsed.Errors);
        ScheduleItem item = Assert.Single(parsed.Items);
        Assert.Equal("Just a title", item.Title);
        Assert.Equal(Day, item.Date);
        Assert.Equal(ScheduleItemKind.Event, item.Kind);
        Assert.Null(item.StartMinute);
        Assert.False(item.IsDone);
    }

    [Fact]
    public void Parse_MissingRequiredColumn_ReportsError() {
        ScheduleCsv.ParseResult parsed = ScheduleCsv.Parse("Date,Kind\n2026-09-23,Event\n");
        Assert.Empty(parsed.Items);
        Assert.Contains(parsed.Errors, e => e.Contains("Title"));
    }

    [Fact]
    public void Parse_BadRows_AreSkippedWithLineNumbers() {
        const string csv = "Date,Kind,StartMinute,EndMinute,Title,Description,IsDone\n" +
                           "not-a-date,Event,,,A,,false\n" +
                           "2026-09-23,Party,,,B,,false\n" +
                           "2026-09-23,Event,25:00,,C,,false\n" +
                           "2026-09-23,Event,,,,,false\n" +
                           "2026-09-23,Task,,,Good,,true\n";

        ScheduleCsv.ParseResult parsed = ScheduleCsv.Parse(csv);

        ScheduleItem item = Assert.Single(parsed.Items);
        Assert.Equal("Good", item.Title);
        Assert.True(item.IsDone);
        Assert.Equal(4, parsed.Errors.Count);
        Assert.StartsWith("Line 2:", parsed.Errors[0]);
        Assert.StartsWith("Line 5:", parsed.Errors[3]);
    }

    [Fact]
    public void Parse_EndWithoutStart_IsDropped() {
        ScheduleCsv.ParseResult parsed = ScheduleCsv.Parse("Date,Title,EndMinute\n2026-09-23,X,10:00\n");
        Assert.Null(Assert.Single(parsed.Items).EndMinute);
    }

    [Fact]
    public void Parse_IgnoresBomAndBlankLines() {
        ScheduleCsv.ParseResult parsed = ScheduleCsv.Parse((char)0xFEFF + "Date,Title\r\n\r\n2026-09-23,X\r\n\r\n");
        Assert.Empty(parsed.Errors);
        Assert.Single(parsed.Items);
    }

    [Fact]
    public void PlanImport_ExactMatchWithSameDescription_IsIgnored() {
        var existing = new ScheduleItem { Date = Day, Title = "Stream", StartMinute = 600, Description = "d" };
        var incoming = new ScheduleItem { Date = Day, Title = "Stream", StartMinute = 600, Description = "d" };

        ScheduleCsv.ImportPlan plan = ScheduleCsv.PlanImport([existing], [incoming]);

        Assert.Empty(plan.ToAdd);
        Assert.Empty(plan.ToUpdate);
        Assert.Equal(1, plan.Unchanged);
    }

    [Fact]
    public void PlanImport_ExactMatchWithNewDescription_UpdatesOnlyDescription() {
        var existing = new ScheduleItem {
            Date = Day, Title = "Stream", StartMinute = 600, Description = "old", IsDone = true,
            Kind = ScheduleItemKind.Event
        };
        var incoming = new ScheduleItem {
            Date = Day, Title = "Stream", StartMinute = 600, Description = "new", IsDone = false,
            Kind = ScheduleItemKind.Task
        };

        ScheduleCsv.ImportPlan plan = ScheduleCsv.PlanImport([existing], [incoming]);

        Assert.Same(existing, Assert.Single(plan.ToUpdate));
        Assert.Empty(plan.ToAdd);
        Assert.Equal("new", existing.Description);
        Assert.True(existing.IsDone);
        Assert.Equal(ScheduleItemKind.Event, existing.Kind);
    }

    [Theory]
    [InlineData("Stream ", 600, null)]
    [InlineData("stream", 600, null)]
    [InlineData("Stream", 601, null)]
    [InlineData("Stream", 600, 700)]
    [InlineData("Stream", null, null)]
    public void PlanImport_AnyDifferenceInSlot_Adds(string title, int? start, int? end) {
        var existing = new ScheduleItem { Date = Day, Title = "Stream", StartMinute = 600 };
        var incoming = new ScheduleItem { Date = Day, Title = title, StartMinute = start, EndMinute = end };

        ScheduleCsv.ImportPlan plan = ScheduleCsv.PlanImport([existing], [incoming]);

        Assert.Single(plan.ToAdd);
        Assert.Empty(plan.ToUpdate);
    }

    [Fact]
    public void PlanImport_OtherDay_Adds() {
        var existing = new ScheduleItem { Date = Day, Title = "Stream" };
        var incoming = new ScheduleItem { Date = Day.AddDays(1), Title = "Stream" };
        Assert.Single(ScheduleCsv.PlanImport([existing], [incoming]).ToAdd);
    }

    [Fact]
    public void PlanImport_AppendsAfterExistingInReadOrder() {
        var existing = new ScheduleItem { Date = Day, Title = "First", SortOrder = 4 };
        var a = new ScheduleItem { Date = Day, Title = "A" };
        var b = new ScheduleItem { Date = Day.AddDays(1), Title = "B" };
        var c = new ScheduleItem { Date = Day, Title = "C" };

        ScheduleCsv.ImportPlan plan = ScheduleCsv.PlanImport([existing], [a, b, c]);

        Assert.Equal(new[] { a, b, c }, plan.ToAdd);
        Assert.Equal(5, a.SortOrder);
        Assert.Equal(6, c.SortOrder);
        Assert.Equal(0, b.SortOrder);
    }

    [Fact]
    public void PlanImport_DuplicateRowsInSameFile_AddOnce() {
        var a = new ScheduleItem { Date = Day, Title = "A", Description = "x" };
        var again = new ScheduleItem { Date = Day, Title = "A", Description = "x" };

        ScheduleCsv.ImportPlan plan = ScheduleCsv.PlanImport([], [a, again]);

        Assert.Single(plan.ToAdd);
        Assert.Equal(1, plan.Unchanged);
    }
}
