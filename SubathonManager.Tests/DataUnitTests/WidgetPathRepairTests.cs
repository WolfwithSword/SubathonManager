using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Data.Widgets;
using SubathonManager.Tests.Utility;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.DataUnitTests;

[Collection("WorkingDirectory")]
public class WidgetPathRepairTests {
    private static IDbContextFactory<AppDbContext> MakeFactory() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<AppDbContext>>();
    }

    private static string MakePackedWidget(string entry = "content/widget.html") {
        string folder = Path.Combine(WidgetPackPaths.PackedRoot, "wolf.widgets.timer");
        Directory.CreateDirectory(folder);
        TestPacks.WriteZip(Path.Combine(folder, "1-0-0.smw"),
            [new KeyValuePair<string, string>(entry, "<html></html>")]);
        WidgetPackPaths.InvalidateResolveCache();
        return WidgetPackPaths.EntryPathIn(Path.Combine(folder, "1-0-0"), entry);
    }

    private static T WithPackFileSystem<T>(Func<T> body) {
        IWidgetFileSystem previous = WidgetFiles.Current;
        WidgetFiles.Current = new WidgetPackFileSystem();
        try {
            return body();
        }
        finally {
            WidgetFiles.Current = previous;
        }
    }

    private static string StalePath(string current) {
        string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), current);
        return Path.Combine(Path.GetTempPath(), "SubathonManager_win-x64_v2.0.1", relative);
    }

    [Fact]
    public void ToStored_PathInsideAppFolder_BecomesRelative() {
        using var ws = new TempWorkspace("pathstore");
        string absolute = ws.WriteFile("imports/widgets/unpacked/wolf/widget.html", "<html></html>");

        string stored = WidgetPathStore.ToStored(absolute);

        Assert.False(Path.IsPathRooted(stored));
        Assert.Equal(absolute, WidgetPathStore.ToAbsolute(stored));
    }

    [Fact]
    public void ToStored_PathOutsideAppFolder_StaysAbsolute() {
        using var ws = new TempWorkspace("pathstore");
        string outside = Path.Combine(Path.GetTempPath(), "elsewhere", "widget.html");

        string stored = WidgetPathStore.ToStored(outside);

        Assert.True(Path.IsPathRooted(stored));
        Assert.Equal(Path.GetFullPath(outside), stored);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void PathStore_BlankInput_IsPassedThrough(string? input, string expected) {
        Assert.Equal(expected, WidgetPathStore.ToStored(input));
        Assert.Equal(expected, WidgetPathStore.ToAbsolute(input));
    }

    [Fact]
    public void TryRebase_StaleInstallFolder_ReanchorsPackedWidget() {
        using var ws = new TempWorkspace("rebase");
        string current = MakePackedWidget();

        WithPackFileSystem(() => {
            Assert.True(WidgetPathRepair.TryRebase(StalePath(current), out string repaired));
            Assert.Equal(current, repaired);
            return true;
        });
    }

    [Fact]
    public void TryRebase_StalePresetPath_ReanchorsOnPresetsRoot() {
        using var ws = new TempWorkspace("rebase");
        const string entry = "content/widget.html";
        string packFolder = ws.Dir("presets", "harlequin");
        TestPacks.WriteZip(Path.Combine(packFolder, "Timer_1.0.0.smw"),
            [new KeyValuePair<string, string>(entry, "<html></html>")]);
        WidgetPackPaths.InvalidateResolveCache();

        string current = WidgetPackPaths.EntryPathIn(Path.Combine(packFolder, "Timer_1.0.0"), entry);

        WithPackFileSystem(() => {
            Assert.True(WidgetPathRepair.TryRebase(StalePath(current), out string repaired));
            Assert.Equal(current, repaired);
            return true;
        });
    }

    [Fact]
    public void TryRebase_NoAnchorSegment_IsLeftAlone() {
        using var ws = new TempWorkspace("rebase");
        MakePackedWidget();

        WithPackFileSystem(() => {
            string loose = Path.Combine(Path.GetTempPath(), "MyWidgets", "timer", "widget.html");
            Assert.False(WidgetPathRepair.TryRebase(loose, out string repaired));
            Assert.Equal(string.Empty, repaired);
            return true;
        });
    }

    [Fact]
    public void TryRebase_AnchorPresentButNoSuchWidget_IsLeftAlone() {
        using var ws = new TempWorkspace("rebase");
        MakePackedWidget();

        WithPackFileSystem(() => {
            string missing = Path.Combine(Path.GetTempPath(), "old", "imports", "widgets",
                "packed", "wolf.widgets.gone", "1-0-0", "content", "widget.html");
            Assert.False(WidgetPathRepair.TryRebase(missing, out _));
            return true;
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("widget.html")]
    public void TryRebase_DegenerateInput_IsLeftAlone(string? input) {
        using var ws = new TempWorkspace("rebase");
        Assert.False(WidgetPathRepair.TryRebase(input!, out _));
    }

    [Fact]
    public void Run_RepointsStaleWidget_AndLeavesHealthyOnesAlone() {
        using var ws = new TempWorkspace("repair");
        string current = MakePackedWidget();
        string healthy = ws.WriteFile("imports/widgets/unpacked/wolf/ok.html", "<html></html>");
        IDbContextFactory<AppDbContext> factory = MakeFactory();

        var route = new Route { Id = Guid.NewGuid(), Name = "Overlay" };
        var stale = new Widget("Stale", StalePath(current)) { RouteId = route.Id };
        var fine = new Widget("Fine", healthy) { RouteId = route.Id };

        using (AppDbContext db = factory.CreateDbContext()) {
            db.Routes.Add(route);
            db.Widgets.AddRange(stale, fine);
            db.SaveChanges();
        }

        int repaired = WithPackFileSystem(() => WidgetPathRepair.Run(factory));

        Assert.Equal(1, repaired);
        using (AppDbContext db = factory.CreateDbContext()) {
            Assert.Equal(current, db.Widgets.Single(w => w.Name == "Stale").HtmlPath);
            Assert.Equal(healthy, db.Widgets.Single(w => w.Name == "Fine").HtmlPath);
        }
    }

    [Fact]
    public void Run_UnrecoverableWidget_IsLeftUntouched() {
        using var ws = new TempWorkspace("repair");
        MakePackedWidget();
        IDbContextFactory<AppDbContext> factory = MakeFactory();

        string orphan = Path.Combine(Path.GetTempPath(), "MyWidgets", "timer", "widget.html");
        var route = new Route { Id = Guid.NewGuid(), Name = "Overlay" };

        using (AppDbContext db = factory.CreateDbContext()) {
            db.Routes.Add(route);
            db.Widgets.Add(new Widget("Orphan", orphan) { RouteId = route.Id });
            db.SaveChanges();
        }

        int repaired = WithPackFileSystem(() => WidgetPathRepair.Run(factory));

        Assert.Equal(0, repaired);
        using (AppDbContext db = factory.CreateDbContext()) {
            Assert.Equal(orphan, db.Widgets.Single().HtmlPath);
        }
    }

    [Fact]
    public void Normalize_CountsOnlyPathsInsideTheAppFolder() {
        using var ws = new TempWorkspace("normalize");
        string inside = ws.WriteFile("imports/widgets/unpacked/wolf/widget.html", "<html></html>");
        string outside = Path.Combine(Path.GetTempPath(), "elsewhere", "widget.html");
        IDbContextFactory<AppDbContext> factory = MakeFactory();

        var route = new Route { Id = Guid.NewGuid(), Name = "Overlay" };
        using (AppDbContext db = factory.CreateDbContext()) {
            db.Routes.Add(route);
            db.Widgets.AddRange(
                new Widget("Inside", inside) { RouteId = route.Id },
                new Widget("Outside", outside) { RouteId = route.Id });
            db.SaveChanges();
        }

        Assert.Equal(1, WidgetPathRepair.Normalize(factory));

        using (AppDbContext db = factory.CreateDbContext()) {
            Assert.Equal(inside, db.Widgets.Single(w => w.Name == "Inside").HtmlPath);
            Assert.Equal(Path.GetFullPath(outside), db.Widgets.Single(w => w.Name == "Outside").HtmlPath);
        }
    }
}
