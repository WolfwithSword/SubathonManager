using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Data.Widgets;
using SubathonManager.Tests.Utility;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.DataUnitTests;

[Collection("WorkingDirectory")]
public class WidgetCatalogTests
{
    private static IDbContextFactory<AppDbContext> MakeFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<AppDbContext>>();
    }

    private static string WritePreset(string folder, string manifestJson,
        IEnumerable<KeyValuePair<string, string>>? files = null)
        => TestPacks.WriteSmw(Path.Combine(WidgetPackPaths.PresetsRoot, folder), manifestJson, files);

    private static string WriteImport(string folder, string manifestJson,
        IEnumerable<KeyValuePair<string, string>>? files = null)
        => TestPacks.WriteSmw(Path.Combine(WidgetPackPaths.ImportsRoot, folder), manifestJson, files);

    #region Roots / paths

    [Fact]
    public void Roots_ArePresetsThenImports()
    {
        using var ws = new TempWorkspace("catalog");
        var roots = WidgetCatalog.Roots().ToList();

        Assert.Equal(2, roots.Count);
        Assert.Equal(WidgetPackPaths.PresetsRoot, roots[0].Path);
        Assert.Equal(WidgetCatalogSource.Preset, roots[0].Source);
        Assert.Equal(WidgetPackPaths.ImportsRoot, roots[1].Path);
        Assert.Equal(WidgetCatalogSource.Imported, roots[1].Source);
    }

    [Fact]
    public void PreviewCacheRoot_IsUnderCache()
    {
        using var ws = new TempWorkspace("catalog");
        Assert.Equal(Path.Combine(ws.Root, "cache", "widget-previews"), WidgetCatalog.PreviewCacheRoot);
    }

    [Fact]
    public void ToAbsolutePath_RootedPath_IsReturnedUnchanged()
    {
        using var ws = new TempWorkspace("catalog");
        string rooted = Path.Combine(ws.Root, "packs", "a.smw");

        Assert.Equal(rooted, WidgetCatalog.ToAbsolutePath(rooted));
    }

    [Fact]
    public void ToAbsolutePath_RelativePath_IsResolvedAgainstCwd()
    {
        using var ws = new TempWorkspace("catalog");

        Assert.Equal(Path.Combine(ws.Root, "presets", "a.smw"),
            WidgetCatalog.ToAbsolutePath(Path.Combine("presets", "a.smw")));
    }

    #endregion

    #region RefreshAsync

    [Fact]
    public async Task RefreshAsync_NoRoots_ReturnsEmpty()
    {
        using var ws = new TempWorkspace("catalog");
        var entries = await WidgetCatalog.RefreshAsync(MakeFactory(), TestContext.Current.CancellationToken);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task RefreshAsync_PresetAndImport_AreBothPickedUpWithTheRightSource()
    {
        using var ws = new TempWorkspace("catalog");
        WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson(name: "racers", packId: "wolf.widgets.racers"));
        WriteImport("packed/wolf.widgets.timer/1-0-0.smw",
            TestPacks.WidgetManifestJson(name: "Timer", packId: "wolf.widgets.timer"));

        var entries = await WidgetCatalog.RefreshAsync(MakeFactory(), TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Equal(WidgetCatalogSource.Preset, entries.Single(e => e.Name == "racers").Source);
        Assert.Equal(WidgetCatalogSource.Imported, entries.Single(e => e.Name == "Timer").Source);
    }

    [Fact]
    public async Task RefreshAsync_FillsEveryManifestField()
    {
        using var ws = new TempWorkspace("catalog");
        string file = WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson(
            name: "racers", author: "Wolf", group: "Alerts", version: "2.0.0",
            entry: "content/racers.html", packId: "wolf.alerts.racers",
            docsUrl: "https://docs", tags: ["a", "b"], scaleX: 2f, scaleY: 3f));

        var entry = (await WidgetCatalog.RefreshAsync(MakeFactory(), TestContext.Current.CancellationToken)).Single();

        Assert.Equal("wolf.alerts.racers", entry.PackId);
        Assert.Equal("racers", entry.Name);
        Assert.Equal("Wolf", entry.Author);
        Assert.Equal("Alerts", entry.Group);
        Assert.Equal("2.0.0", entry.Version);
        Assert.Equal("content/racers.html", entry.Entry);
        Assert.Equal("https://docs", entry.DocsUrl);
        Assert.Equal("a, b", entry.Tags);
        Assert.Equal(2f, entry.ScaleX);
        Assert.Equal(3f, entry.ScaleY);
        Assert.Equal(new FileInfo(file).Length, entry.FileSize);
        Assert.NotEqual(default(DateTime), entry.LastSeenUtc);
    }

    [Fact]
    public async Task RefreshAsync_StoresARelativePackPath_WhenUnderTheWorkingDirectory()
    {
        using var ws = new TempWorkspace("catalog");
        WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson());

        var entry = (await WidgetCatalog.RefreshAsync(MakeFactory(), TestContext.Current.CancellationToken)).Single();

        Assert.False(Path.IsPathRooted(entry.PackPath));
        Assert.Equal(Path.Combine("presets", "racers", "1-0-0.smw"), entry.PackPath);
    }

    [Fact]
    public async Task RefreshAsync_PackWithoutEntry_IsSkipped()
    {
        using var ws = new TempWorkspace("catalog");
        WritePreset("broken/1-0-0.smw", TestPacks.WidgetManifestJson(entry: ""));

        Assert.Empty(await WidgetCatalog.RefreshAsync(MakeFactory(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshAsync_UnreadablePack_IsSkipped()
    {
        using var ws = new TempWorkspace("catalog");
        Directory.CreateDirectory(Path.Combine(WidgetPackPaths.PresetsRoot, "broken"));
        await File.WriteAllTextAsync(Path.Combine(WidgetPackPaths.PresetsRoot, "broken", "1-0-0.smw"), 
            "not a zip", TestContext.Current.CancellationToken);

        Assert.Empty(await WidgetCatalog.RefreshAsync(MakeFactory(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshAsync_IsIdempotent_AndReusesTheStoredRow()
    {
        using var ws = new TempWorkspace("catalog");
        WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson(name: "racers"));
        var factory = MakeFactory();

        var first = await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken);
        var second = await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(first[0].Id, second[0].Id);

        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await db.WidgetCatalogEntries.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshAsync_ChangedFile_IsReRead()
    {
        using var ws = new TempWorkspace("catalog");
        string file = WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson(name: "Old Name"));
        var factory = MakeFactory();
        await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken);

        WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson(name: "New Name", version: "2.0.0"));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));

        var entries = await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken);

        Assert.Equal("New Name", entries.Single().Name);
        Assert.Equal("2.0.0", entries.Single().Version);
    }

    [Fact]
    public async Task RefreshAsync_DeletedPack_DropsTheRow()
    {
        using var ws = new TempWorkspace("catalog");
        string file = WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson());
        var factory = MakeFactory();
        Assert.Single(await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken));

        File.Delete(file);
        Assert.Empty(await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken));

        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await db.WidgetCatalogEntries.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshAsync_OrdersByAuthorGroupNameThenVersionDescending()
    {
        using var ws = new TempWorkspace("catalog");
        WritePreset("a/1-0-0.smw", TestPacks.WidgetManifestJson(
            author: "Zed", group: "Alerts", name: "Alpha", packId: "zed.alerts.alpha"));
        WritePreset("b/1-0-0.smw", TestPacks.WidgetManifestJson(
            author: "Jane", group: "Beta", name: "Widget", packId: "jane.beta.widget"));
        WritePreset("c/1-0-0.smw", TestPacks.WidgetManifestJson(
            author: "Jane", group: "Alpha", name: "Widget", packId: "jane.alpha.widget"));
        WritePreset("d/2-0-0.smw", TestPacks.WidgetManifestJson(
            author: "Jane", group: "Alpha", name: "Widget", version: "2.0.0", packId: "jane.alpha.widget"));

        var entries = await WidgetCatalog.RefreshAsync(MakeFactory(), TestContext.Current.CancellationToken);

        Assert.Equal(4, entries.Count);
        Assert.Equal("Jane", entries[0].Author);
        Assert.Equal("Alpha", entries[0].Group);
        Assert.Equal("2.0.0", entries[0].Version);
        Assert.Equal("1.0.0", entries[1].Version);
        Assert.Equal("Beta", entries[2].Group);
        Assert.Equal("Zed", entries[3].Author);
    }

    [Fact]
    public async Task RefreshAsync_ExtractsThePreviewImageIntoTheCache()
    {
        using var ws = new TempWorkspace("catalog");
        WritePreset("racers/1-0-0.smw",
            TestPacks.WidgetManifestJson(preview: "preview.png"),
            new Dictionary<string, string> { ["preview.png"] = "fake png bytes" });

        var entry = (await WidgetCatalog.RefreshAsync(MakeFactory(), TestContext.Current.CancellationToken)).Single();

        Assert.Equal("preview.png", entry.PreviewImage);
        Assert.True(File.Exists(entry.PreviewCachePath));
        Assert.StartsWith(WidgetCatalog.PreviewCacheRoot, entry.PreviewCachePath);
        Assert.Equal(".png", Path.GetExtension(entry.PreviewCachePath));
    }

    [Fact]
    public async Task RefreshAsync_PreviewImageMissingFromArchive_LeavesTheCachePathBlank()
    {
        using var ws = new TempWorkspace("catalog");
        WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson(preview: "preview.png"));

        var entry = (await WidgetCatalog.RefreshAsync(MakeFactory(), TestContext.Current.CancellationToken)).Single();

        Assert.Equal(string.Empty, entry.PreviewCachePath);
    }

    [Fact]
    public async Task RefreshAsync_UnchangedPack_ReExtractsAMissingPreview()
    {
        using var ws = new TempWorkspace("catalog");
        WritePreset("racers/1-0-0.smw",
            TestPacks.WidgetManifestJson(preview: "preview.png"),
            new Dictionary<string, string> { ["preview.png"] = "fake png bytes" });
        var factory = MakeFactory();

        var first = (await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken)).Single();
        File.Delete(first.PreviewCachePath);

        var second = (await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken)).Single();

        Assert.True(File.Exists(second.PreviewCachePath));
    }

    [Fact]
    public async Task RefreshAsync_Cancellation_Propagates()
    {
        using var ws = new TempWorkspace("catalog");
        WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WidgetCatalog.RefreshAsync(MakeFactory(), cts.Token));
    }

    #endregion

    #region RefreshEntryAsync

    [Fact]
    public async Task RefreshEntryAsync_MissingFileAndNoRow_ReturnsNull()
    {
        using var ws = new TempWorkspace("catalog");

        Assert.Null(await WidgetCatalog.RefreshEntryAsync(MakeFactory(),
            Path.Combine("presets", "nope.smw"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshEntryAsync_MissingFileWithRow_RemovesTheRow()
    {
        using var ws = new TempWorkspace("catalog");
        string file = WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson());
        var factory = MakeFactory();
        string packPath = (await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken))
            .Single().PackPath;

        File.Delete(file);
        var result = await WidgetCatalog.RefreshEntryAsync(factory, packPath, TestContext.Current.CancellationToken);

        Assert.Null(result);
        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await db.WidgetCatalogEntries.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshEntryAsync_NewPack_InsertsARow()
    {
        using var ws = new TempWorkspace("catalog");
        WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson(name: "racers"));
        var factory = MakeFactory();

        var entry = await WidgetCatalog.RefreshEntryAsync(factory,
            Path.Combine("presets", "racers", "1-0-0.smw"), TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.Equal("racers", entry!.Name);
        Assert.Equal(WidgetCatalogSource.Preset, entry.Source);

        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await db.WidgetCatalogEntries.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshEntryAsync_ImportedPack_IsMarkedImported()
    {
        using var ws = new TempWorkspace("catalog");
        WriteImport("packed/wolf.widgets.timer/1-0-0.smw", TestPacks.WidgetManifestJson(name: "Timer"));

        var entry = await WidgetCatalog.RefreshEntryAsync(MakeFactory(),
            Path.Combine("imports", "widgets", "packed", "wolf.widgets.timer", "1-0-0.smw"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.Equal(WidgetCatalogSource.Imported, entry!.Source);
    }

    [Fact]
    public async Task RefreshEntryAsync_UnreadableManifest_ReturnsNull()
    {
        using var ws = new TempWorkspace("catalog");
        Directory.CreateDirectory(Path.Combine(WidgetPackPaths.PresetsRoot, "broken"));
        await File.WriteAllTextAsync(Path.Combine(WidgetPackPaths.PresetsRoot, "broken", "1-0-0.smw"),
            "not a zip", TestContext.Current.CancellationToken);

        Assert.Null(await WidgetCatalog.RefreshEntryAsync(MakeFactory(),
            Path.Combine("presets", "broken", "1-0-0.smw"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshEntryAsync_ClearsThePackCacheDirectory()
    {
        using var ws = new TempWorkspace("catalog");
        string file = WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson());
        string cacheDir = WidgetPackPaths.CacheDirFor(file);
        Directory.CreateDirectory(cacheDir);
        await File.WriteAllTextAsync(Path.Combine(cacheDir, "stale.bin"), "old",
            TestContext.Current.CancellationToken);

        await WidgetCatalog.RefreshEntryAsync(MakeFactory(),
            Path.Combine("presets", "racers", "1-0-0.smw"), TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(cacheDir));
    }

    #endregion

    #region DeleteAsync

    [Fact]
    public async Task DeleteAsync_PresetEntry_IsRefused()
    {
        using var ws = new TempWorkspace("catalog");
        string file = WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson());
        var factory = MakeFactory();
        var entry = (await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken)).Single();

        Assert.False(await WidgetCatalog.DeleteAsync(factory, entry, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task DeleteAsync_ImportedEntry_RemovesTheFileAndItsCache()
    {
        using var ws = new TempWorkspace("catalog");
        string file = WriteImport("packed/wolf.widgets.timer/1-0-0.smw", TestPacks.WidgetManifestJson());
        var factory = MakeFactory();
        var entry = (await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken)).Single();

        string cacheDir = WidgetPackPaths.CacheDirFor(file);
        Directory.CreateDirectory(cacheDir);

        Assert.True(await WidgetCatalog.DeleteAsync(factory, entry, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(cacheDir));
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheNowEmptyPackFolder()
    {
        using var ws = new TempWorkspace("catalog");
        string file = WriteImport("packed/wolf.widgets.timer/1-0-0.smw", TestPacks.WidgetManifestJson());
        var factory = MakeFactory();
        var entry = (await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken)).Single();

        await WidgetCatalog.DeleteAsync(factory, entry, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
    }

    [Fact]
    public async Task DeleteAsync_KeepsAPackFolderThatStillHasVersions()
    {
        using var ws = new TempWorkspace("catalog");
        WriteImport("packed/wolf.widgets.timer/1-0-0.smw", TestPacks.WidgetManifestJson(version: "1.0.0"));
        string keep = WriteImport("packed/wolf.widgets.timer/2-0-0.smw",
            TestPacks.WidgetManifestJson(version: "2.0.0"));
        var factory = MakeFactory();
        var entries = await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken);
        var older = entries.Single(e => e.Version == "1.0.0");

        Assert.True(await WidgetCatalog.DeleteAsync(factory, older, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(keep));
        Assert.True(Directory.Exists(Path.GetDirectoryName(keep)));
    }

    [Fact]
    public async Task DeleteAsync_AlreadyMissingFile_StillSucceeds()
    {
        using var ws = new TempWorkspace("catalog");
        string file = WriteImport("packed/wolf.widgets.timer/1-0-0.smw", TestPacks.WidgetManifestJson());
        var factory = MakeFactory();
        var entry = (await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken)).Single();
        File.Delete(file);

        Assert.True(await WidgetCatalog.DeleteAsync(factory, entry, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheCachedPreview()
    {
        using var ws = new TempWorkspace("catalog");
        WriteImport("packed/wolf.widgets.timer/1-0-0.smw",
            TestPacks.WidgetManifestJson(preview: "preview.png"),
            new Dictionary<string, string> { ["preview.png"] = "fake png" });
        var factory = MakeFactory();
        var entry = (await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken)).Single();
        string preview = entry.PreviewCachePath;
        Assert.True(File.Exists(preview));

        await WidgetCatalog.DeleteAsync(factory, entry, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(preview));
    }

    [Fact]
    public async Task DeleteAsync_PreviewOutsideTheCacheRoot_IsLeftAlone()
    {
        using var ws = new TempWorkspace("catalog");
        string file = WriteImport("packed/wolf.widgets.timer/1-0-0.smw", TestPacks.WidgetManifestJson());
        var factory = MakeFactory();
        var entry = (await WidgetCatalog.RefreshAsync(factory, TestContext.Current.CancellationToken)).Single();

        string outside = ws.WriteFile("elsewhere/important.png", "keep me");
        entry.PreviewCachePath = outside;

        await WidgetCatalog.DeleteAsync(factory, entry, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(outside));
    }

    #endregion

    #region index

    [Fact]
    public void BuildIndex_ThenLookupByPathAndPackId()
    {
        using var ws = new TempWorkspace("catalog");
        var a = new WidgetCatalogEntry
        {
            PackPath = Path.Combine("presets", "a.smw"), PackId = "wolf.widgets.timer", Version = "1.0.0"
        };
        var b = new WidgetCatalogEntry
        {
            PackPath = Path.Combine("presets", "b.smw"), PackId = "wolf.widgets.timer", Version = "2.0.0"
        };
        var c = new WidgetCatalogEntry { PackPath = Path.Combine("presets", "c.smw"), PackId = "" };

        WidgetCatalog.BuildIndex([a, b, c]);

        Assert.Same(a, WidgetCatalog.EntryForPackFile(Path.Combine(ws.Root, "presets", "a.smw")));
        Assert.Equal(2, WidgetCatalog.EntriesForPackId("wolf.widgets.timer").Count);
        Assert.Empty(WidgetCatalog.EntriesForPackId(""));
        Assert.Empty(WidgetCatalog.EntriesForPackId("nope"));
    }

    [Fact]
    public void EntriesForPackId_IsCaseInsensitive_AndReturnsACopy()
    {
        using var ws = new TempWorkspace("catalog");
        var a = new WidgetCatalogEntry { PackPath = "a.smw", PackId = "Wolf.Widgets.Timer" };
        WidgetCatalog.BuildIndex([a]);

        var list = WidgetCatalog.EntriesForPackId("wolf.widgets.timer");
        Assert.Single(list);

        list.Clear();
        Assert.Single(WidgetCatalog.EntriesForPackId("wolf.widgets.timer"));
    }

    [Fact]
    public void EntryForPackFile_AbsoluteStoredPath_IsFoundToo()
    {
        using var ws = new TempWorkspace("catalog");
        string absolute = Path.Combine(Path.GetTempPath(), "outside", "a.smw");
        var a = new WidgetCatalogEntry { PackPath = absolute, PackId = "wolf.widgets.timer" };
        WidgetCatalog.BuildIndex([a]);

        Assert.Same(a, WidgetCatalog.EntryForPackFile(absolute));
    }

    [Fact]
    public void EntryForPackFile_Unknown_ReturnsNull()
    {
        using var ws = new TempWorkspace("catalog");
        WidgetCatalog.BuildIndex([]);

        Assert.Null(WidgetCatalog.EntryForPackFile(Path.Combine(ws.Root, "presets", "nope.smw")));
    }

    [Fact]
    public void BuildIndex_ReplacesThePreviousIndex()
    {
        using var ws = new TempWorkspace("catalog");
        WidgetCatalog.BuildIndex([new WidgetCatalogEntry { PackPath = "a.smw", PackId = "old.pack" }]);
        WidgetCatalog.BuildIndex([new WidgetCatalogEntry { PackPath = "b.smw", PackId = "new.pack" }]);

        Assert.Empty(WidgetCatalog.EntriesForPackId("old.pack"));
        Assert.Single(WidgetCatalog.EntriesForPackId("new.pack"));
    }

    [Fact]
    public async Task LoadIndexAsync_RebuildsFromTheDatabase()
    {
        using var ws = new TempWorkspace("catalog");
        WidgetCatalog.BuildIndex([]);

        var factory = MakeFactory();
        await using (var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            db.WidgetCatalogEntries.Add(new WidgetCatalogEntry
            {
                PackPath = Path.Combine("presets", "a.smw"), PackId = "wolf.widgets.timer"
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await WidgetCatalog.LoadIndexAsync(factory, TestContext.Current.CancellationToken);

        Assert.Single(WidgetCatalog.EntriesForPackId("wolf.widgets.timer"));
    }

    [Fact]
    public async Task RefreshAsync_PopulatesTheIndex()
    {
        using var ws = new TempWorkspace("catalog");
        WidgetCatalog.BuildIndex([]);
        WritePreset("racers/1-0-0.smw", TestPacks.WidgetManifestJson(packId: "wolf.widgets.racers"));

        await WidgetCatalog.RefreshAsync(MakeFactory(), TestContext.Current.CancellationToken);

        Assert.Single(WidgetCatalog.EntriesForPackId("wolf.widgets.racers"));
    }

    #endregion
}
