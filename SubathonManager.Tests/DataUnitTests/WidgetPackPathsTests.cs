using SubathonManager.Data.Widgets;
using SubathonManager.Tests.Utility;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.DataUnitTests;

public class WidgetPackPathsPureTests
{
    
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Timer", "timer")]
    [InlineData("My Timer", "my-timer")]
    [InlineData("  Wolf With Sword  ", "wolf-with-sword")]
    [InlineData("Hello!!!World", "hello-world")]
    [InlineData("a---b", "a-b")]
    [InlineData("--lead-and-trail--", "lead-and-trail")]
    [InlineData("1.0.0", "1-0-0")]
    [InlineData("!!!", "")]
    [InlineData("Über", "ber")]
    [InlineData("MiXeD_CaSe", "mixed-case")]
    public void Slug_Branches(string? input, string expected)
        => Assert.Equal(expected, WidgetPackPaths.Slug(input));

        
    [Theory]
    [InlineData(null, WidgetPackPaths.DefaultGroup)]
    [InlineData("", WidgetPackPaths.DefaultGroup)]
    [InlineData("    ", WidgetPackPaths.DefaultGroup)]
    [InlineData("  Alerts  ", "Alerts")]
    [InlineData("Alerts", "Alerts")]
    public void NormalizeGroup_Branches(string? input, string expected)
        => Assert.Equal(expected, WidgetPackPaths.NormalizeGroup(input));

        
    [Fact]
    public void MakePackId_AllThreeParts()
        => Assert.Equal("wolf.alerts.my-timer", WidgetPackPaths.MakePackId("Wolf", "Alerts", "My Timer"));

    [Fact]
    public void MakePackId_BlankAuthor_IsOmitted()
        => Assert.Equal("widgets.timer", WidgetPackPaths.MakePackId("", "", "Timer"));

    [Fact]
    public void MakePackId_BlankName_FallsBackToWidget()
        => Assert.Equal("wolf.alerts.widget", WidgetPackPaths.MakePackId("Wolf", "Alerts", "   "));

    [Fact]
    public void MakePackId_UnsluggableAuthor_IsOmitted()
        => Assert.Equal("widgets.timer", WidgetPackPaths.MakePackId("!!!", null!, "Timer"));

    [Fact]
    public void MakePackId_NullGroup_UsesDefaultGroup()
        => Assert.Equal("wolf.widgets.timer", WidgetPackPaths.MakePackId("Wolf", null!, "Timer"));

        
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("1-0-0", "1.0.0")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("10-2", "10.2")]
    [InlineData("beta-1", "beta-1")]
    [InlineData("1-beta", "1-beta")]
    [InlineData("1-2-beta-3", "1.2-beta-3")]
    public void DisplayVersion_Branches(string? input, string expected)
        => Assert.Equal(expected, WidgetPackPaths.DisplayVersion(input));

        
    [Theory]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("1.0.0", true)]
    [InlineData("1-0-0", true)]
    [InlineData("1_0_0", true)]
    [InlineData("2026.1", true)]
    [InlineData("v1.0.0", false)]
    [InlineData("1.0.", false)]
    [InlineData("1..0", false)]
    [InlineData("latest", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void IsVersionName_Branches(string? input, bool expected)
        => Assert.Equal(expected, WidgetPackPaths.IsVersionName(input));

        
    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("2.0.0", "10.0.0", -1)]
    [InlineData("1-2-3", "1-2-3", 0)]
    [InlineData("1-2-3", "1-1-9", 1)]
    [InlineData("1.2", "1.2.0", -1)]
    [InlineData("1.2.0", "1.2", 1)]
    public void CompareVersions_Branches(string left, string right, int expectedSign)
        => Assert.Equal(expectedSign, Math.Sign(WidgetPackPaths.CompareVersions(left, right)));

    [Fact]
    public void CompareVersions_SameVersionDifferentSeparators_IsNotEqual()
    {
        Assert.True(WidgetPackPaths.CompareVersions("1-2-3", "1.2.3") < 0);
        Assert.True(WidgetPackPaths.CompareVersions("1.2.3", "1-2-3") > 0);
    }

    [Fact]
    public void CompareVersions_NoDigits_FallsBackToStringCompare()
    {
        Assert.Equal(0, WidgetPackPaths.CompareVersions("latest", "latest"));
        Assert.True(WidgetPackPaths.CompareVersions("beta", "alpha") > 0);
    }

    [Fact]
    public void CompareVersions_LongVersionBeatsShort_WhenExtraSegmentNonZero()
        => Assert.True(WidgetPackPaths.CompareVersions("1.2.1", "1.2") > 0);

    [Fact]
    public void CompareVersions_UsableAsAComparer()
    {
        var sorted = new[] { "1.10.0", "1.2.0", "2.0.0", "1.9.9" }
            .OrderBy(v => v, Comparer<string>.Create(WidgetPackPaths.CompareVersions))
            .ToList();

        Assert.Equal(new[] {"1.2.0", "1.9.9", "1.10.0", "2.0.0"}, sorted);
    }

        
    [Fact]
    public void EntryPathIn_ConvertsForwardSlashesToPlatformSeparators()
    {
        string root = Path.Combine("C:", "mount");
        Assert.Equal(Path.Combine(root, "content", "widget.html"),
            WidgetPackPaths.EntryPathIn(root, "content/widget.html"));
    }

    [Fact]
    public void EntryPathIn_FlatEntry()
        => Assert.Equal(Path.Combine("root", "widget.html"),
            WidgetPackPaths.EntryPathIn("root", "widget.html"));

        
    [Fact]
    public void PackExtension_IsSmw() => Assert.Equal(".smw", WidgetPackPaths.PackExtension);

    [Fact]
    public void DefaultGroup_IsWidgets() => Assert.Equal("widgets", WidgetPackPaths.DefaultGroup);

}


[Collection("WorkingDirectory")]
public class WidgetPackPathsWorkspaceTests
{
    
    [Fact]
    public void Roots_AreRelativeToCurrentDirectory()
    {
        using var ws = new TempWorkspace("packpaths");

        Assert.Equal(Path.Combine(ws.Root, "imports", "widgets"), WidgetPackPaths.ImportsRoot);
        Assert.Equal(Path.Combine(ws.Root, "imports", "widgets", "packed"), WidgetPackPaths.PackedRoot);
        Assert.Equal(Path.Combine(ws.Root, "imports", "widgets", "unpacked"), WidgetPackPaths.UnpackedRoot);
        Assert.Equal(Path.Combine(ws.Root, "cache", "widgets"), WidgetPackPaths.CacheRoot);
        Assert.Equal(Path.Combine(ws.Root, "presets"), WidgetPackPaths.PresetsRoot);
    }

    [Fact]
    public void PackFolder_MountRoot_PackFile_And_EntryPath_Compose()
    {
        using var ws = new TempWorkspace("packpaths");

        string folder = WidgetPackPaths.PackFolder("wolf.widgets.timer");
        Assert.Equal(Path.Combine(WidgetPackPaths.PackedRoot, "wolf.widgets.timer"), folder);
        Assert.Equal(Path.Combine(folder, "1-0-0"), WidgetPackPaths.MountRoot("wolf.widgets.timer", "1-0-0"));
        Assert.Equal(Path.Combine(folder, "1-0-0.smw"), WidgetPackPaths.PackFile("wolf.widgets.timer", "1-0-0"));
        Assert.Equal(Path.Combine(folder, "1-0-0", "content", "widget.html"),
            WidgetPackPaths.EntryPath("wolf.widgets.timer", "1-0-0", "content/widget.html"));
    }

        
    [Fact]
    public void VersionsIn_MissingFolder_ReturnsEmpty()
    {
        using var ws = new TempWorkspace("packpaths");
        Assert.Empty(WidgetPackPaths.VersionsIn(Path.Combine(ws.Root, "nope")));
    }

    [Fact]
    public void VersionsIn_ReturnsPackFileNamesWithoutExtension()
    {
        using var ws = new TempWorkspace("packpaths");
        string folder = ws.Dir("packs");
        File.WriteAllText(Path.Combine(folder, "1-0-0.smw"), "");
        File.WriteAllText(Path.Combine(folder, "1-1-0.smw"), "");
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "");

        var versions = WidgetPackPaths.VersionsIn(folder);

        Assert.Equal(2, versions.Count);
        Assert.Contains("1-0-0", versions);
        Assert.Contains("1-1-0", versions);
    }

    [Fact]
    public void VersionsIn_IsCached_UntilInvalidated()
    {
        using var ws = new TempWorkspace("packpaths");
        string folder = ws.Dir("packs");
        File.WriteAllText(Path.Combine(folder, "1-0-0.smw"), "");

        Assert.Single(WidgetPackPaths.VersionsIn(folder));

        File.WriteAllText(Path.Combine(folder, "2-0-0.smw"), "");
        Assert.Single(WidgetPackPaths.VersionsIn(folder));

        WidgetPackPaths.InvalidateVersionCache();
        Assert.Equal(2, WidgetPackPaths.VersionsIn(folder).Count);
    }

    [Fact]
    public void InstalledVersions_ReadsThePackedFolderForThePackId()
    {
        using var ws = new TempWorkspace("packpaths");
        string folder = WidgetPackPaths.PackFolder("wolf.widgets.timer");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "1-0-0.smw"), "");

        Assert.Equal(new[] {"1-0-0"}, WidgetPackPaths.InstalledVersions("wolf.widgets.timer"));
    }

    [Fact]
    public void InvalidateVersionCache_WithPackId_OnlyClearsThatPack()
    {
        using var ws = new TempWorkspace("packpaths");
        string mine = WidgetPackPaths.PackFolder("wolf.widgets.timer");
        string other = WidgetPackPaths.PackFolder("wolf.widgets.alerts");
        Directory.CreateDirectory(mine);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(mine, "1-0-0.smw"), "");
        File.WriteAllText(Path.Combine(other, "1-0-0.smw"), "");

        Assert.Single(WidgetPackPaths.InstalledVersions("wolf.widgets.timer"));
        Assert.Single(WidgetPackPaths.InstalledVersions("wolf.widgets.alerts"));

        File.WriteAllText(Path.Combine(mine, "2-0-0.smw"), "");
        File.WriteAllText(Path.Combine(other, "2-0-0.smw"), "");

        WidgetPackPaths.InvalidateVersionCache("wolf.widgets.timer");

        Assert.Equal(2, WidgetPackPaths.InstalledVersions("wolf.widgets.timer").Count);
        Assert.Single(WidgetPackPaths.InstalledVersions("wolf.widgets.alerts"));
    }

        
    [Fact]
    public void IsInPresets_TrueOnlyBelowPresetsRoot()
    {
        using var ws = new TempWorkspace("packpaths");

        Assert.True(WidgetPackPaths.IsInPresets(Path.Combine(WidgetPackPaths.PresetsRoot, "honse", "a.smw")));
        Assert.False(WidgetPackPaths.IsInPresets(WidgetPackPaths.PresetsRoot));
        Assert.False(WidgetPackPaths.IsInPresets(Path.Combine(ws.Root, "elsewhere", "a.smw")));
    }

    [Fact]
    public void IsInGlobalStore_TruePastPackedOrPresets()
    {
        using var ws = new TempWorkspace("packpaths");

        Assert.True(WidgetPackPaths.IsInGlobalStore(Path.Combine(WidgetPackPaths.PackedRoot, "p", "1-0-0.smw")));
        Assert.True(WidgetPackPaths.IsInGlobalStore(Path.Combine(WidgetPackPaths.PresetsRoot, "p", "1-0-0.smw")));
        Assert.False(WidgetPackPaths.IsInGlobalStore(Path.Combine(ws.Root, "downloads", "1-0-0.smw")));
        Assert.False(WidgetPackPaths.IsInGlobalStore(WidgetPackPaths.UnpackedRoot));
    }

    [Fact]
    public void IsInPresets_MalformedPath_ReturnsFalse()
    {
        using var ws = new TempWorkspace("packpaths");
        Assert.False(WidgetPackPaths.IsInPresets("\0bad"));
        Assert.False(WidgetPackPaths.IsInGlobalStore("\0bad"));
    }

        
    [Fact]
    public void UnpackRoot_SlugsEverySegment()
    {
        using var ws = new TempWorkspace("packpaths");

        Assert.Equal(Path.Combine(WidgetPackPaths.UnpackedRoot, "wolf", "alerts", "my-timer", "1.0.0"),
            WidgetPackPaths.UnpackRoot("Wolf", "Alerts", "My Timer", "1.0.0"));
    }

    [Fact]
    public void UnpackRoot_BlankSegments_UseFallbacks()
    {
        using var ws = new TempWorkspace("packpaths");

        Assert.Equal(Path.Combine(WidgetPackPaths.UnpackedRoot, "unknown", "widgets", "widget", "1.0.0"),
            WidgetPackPaths.UnpackRoot("", "", "", ""));
    }

    [Fact]
    public void UnpackRoot_UnsluggableSegments_UseFallbacks()
    {
        using var ws = new TempWorkspace("packpaths");

        Assert.Equal(Path.Combine(WidgetPackPaths.UnpackedRoot, "unknown", "widgets", "widget", "1.0.0"),
            WidgetPackPaths.UnpackRoot("!!!", "###", "$$$", "  "));
    }

    [Fact]
    public void UnpackRoot_VersionKeepsDotsButSanitisesInvalidChars()
    {
        using var ws = new TempWorkspace("packpaths");
        string expected = Path.Combine(WidgetPackPaths.UnpackedRoot, "wolf", "alerts", "timer", "1.0_0");

        Assert.Equal(expected, WidgetPackPaths.UnpackRoot("Wolf", "Alerts", "Timer", "1.0:0"));
        Assert.Equal(expected, WidgetPackPaths.UnpackRoot("Wolf", "Alerts", "Timer", "1.0/0"));
    }

    [Fact]
    public void UnpackRoot_VersionWithTrailingDot_IsStripped()
    {
        using var ws = new TempWorkspace("packpaths");
        Assert.Equal(Path.Combine(WidgetPackPaths.UnpackedRoot, "wolf", "alerts", "timer", "1.0"),
            WidgetPackPaths.UnpackRoot("Wolf", "Alerts", "Timer", "1.0."));
    }

    [Fact]
    public void UnpackRoot_VersionThatSanitisesAway_FallsBackTo100()
    {
        using var ws = new TempWorkspace("packpaths");
        Assert.Equal(Path.Combine(WidgetPackPaths.UnpackedRoot, "wolf", "alerts", "timer", "1.0.0"),
            WidgetPackPaths.UnpackRoot("Wolf", "Alerts", "Timer", "..."));
        Assert.Equal(Path.Combine(WidgetPackPaths.UnpackedRoot, "wolf", "alerts", "timer", "___"),
            WidgetPackPaths.UnpackRoot("Wolf", "Alerts", "Timer", "///"));
    }

    [Fact]
    public void UnpackRootFor_GlobalStorePack_UsesTheSharedUnpackedTree()
    {
        using var ws = new TempWorkspace("packpaths");
        string packFolder = Path.Combine(WidgetPackPaths.PackedRoot, "wolf.widgets.timer");
        var location = new WidgetPackPaths.PackLocation(
            Path.Combine(packFolder, "1-0-0.smw"),
            packFolder,
            Path.Combine(packFolder, "1-0-0"),
            "wolf.widgets.timer",
            "1-0-0");

        Assert.Equal(WidgetPackPaths.UnpackRoot("Wolf", "Alerts", "Timer", "1-0-0"),
            WidgetPackPaths.UnpackRootFor(location, "Wolf", "Alerts", "Timer"));
    }

    [Fact]
    public void UnpackRootFor_LooseFolderPack_UnpacksBesideThePack()
    {
        using var ws = new TempWorkspace("packpaths");
        string container = Path.Combine(ws.Root, "downloads");
        string packFolder = Path.Combine(container, "wolf.widgets.timer");
        var location = new WidgetPackPaths.PackLocation(
            Path.Combine(packFolder, "1-0-0.smw"),
            packFolder,
            Path.Combine(packFolder, "1-0-0"),
            "wolf.widgets.timer",
            "1-0-0");

        Assert.Equal(Path.Combine(container, "unpacked", "wolf.widgets.timer", "1-0-0"),
            WidgetPackPaths.UnpackRootFor(location, "Wolf", "Alerts", "Timer"));
    }

        
    [Fact]
    public void CacheDirFor_IsUnderCacheRoot_AndNamedFromTheFile()
    {
        using var ws = new TempWorkspace("packpaths");
        string dir = WidgetPackPaths.CacheDirFor(Path.Combine(ws.Root, "packs", "My Timer.smw"));

        Assert.StartsWith(WidgetPackPaths.CacheRoot + Path.DirectorySeparatorChar, dir);
        Assert.StartsWith("my-timer-", Path.GetFileName(dir));
        Assert.Equal("my-timer-".Length + 8, Path.GetFileName(dir).Length);
    }

    [Fact]
    public void CacheDirFor_IsStable_AndCaseInsensitive()
    {
        using var ws = new TempWorkspace("packpaths");
        string a = WidgetPackPaths.CacheDirFor(Path.Combine(ws.Root, "packs", "timer.smw"));
        string b = WidgetPackPaths.CacheDirFor(Path.Combine(ws.Root, "PACKS", "TIMER.smw"));

        Assert.Equal(a.ToLowerInvariant(), b.ToLowerInvariant());
    }

    [Fact]
    public void CacheDirFor_DifferentFolders_SameName_GetDifferentDirs()
    {
        using var ws = new TempWorkspace("packpaths");
        string a = WidgetPackPaths.CacheDirFor(Path.Combine(ws.Root, "one", "timer.smw"));
        string b = WidgetPackPaths.CacheDirFor(Path.Combine(ws.Root, "two", "timer.smw"));

        Assert.NotEqual(a, b);
    }

            private static (string packFile, string mountRoot) MakeMountedPack(TempWorkspace ws,
        string packId = "wolf.widgets.timer", string version = "1-0-0")
    {
        string folder = Path.Combine(WidgetPackPaths.PackedRoot, packId);
        Directory.CreateDirectory(folder);
        string packFile = Path.Combine(folder, version + WidgetPackPaths.PackExtension);
        File.WriteAllText(packFile, "not really a zip, Resolve only checks existence");
        return (packFile, Path.Combine(folder, version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankPath_ReturnsNull(string? path)
    {
        using var ws = new TempWorkspace("packpaths");
        Assert.Null(WidgetPackPaths.Resolve(path!));
    }

    [Fact]
    public void Resolve_MalformedPath_ReturnsNull()
    {
        using var ws = new TempWorkspace("packpaths");
        Assert.Null(WidgetPackPaths.Resolve("\0nope"));
    }

    [Fact]
    public void Resolve_UnpackedPathWithNoSiblingSmw_ReturnsNull()
    {
        using var ws = new TempWorkspace("packpaths");
        string html = ws.WriteFile("loose/widget/widget.html", "<html></html>");

        Assert.Null(WidgetPackPaths.Resolve(html));
    }

    [Fact]
    public void Resolve_EntryDirectlyUnderMount_FindsThePack()
    {
        using var ws = new TempWorkspace("packpaths");
        var (packFile, mountRoot) = MakeMountedPack(ws);

        var location = WidgetPackPaths.Resolve(Path.Combine(mountRoot, "widget.html"));

        Assert.NotNull(location);
        Assert.Equal(packFile, location!.PackFileStr);
        Assert.Equal(mountRoot, location.MountRootStr);
        Assert.Equal("wolf.widgets.timer", location.PackIdStr);
        Assert.Equal("1-0-0", location.VersionStr);
        Assert.Equal(Path.GetDirectoryName(mountRoot), location.PackFolderStr);
    }

    [Fact]
    public void Resolve_NestedEntry_WalksUpToTheMount()
    {
        using var ws = new TempWorkspace("packpaths");
        var (packFile, mountRoot) = MakeMountedPack(ws);

        var location = WidgetPackPaths.Resolve(Path.Combine(mountRoot, "content", "sub", "widget.html"));

        Assert.NotNull(location);
        Assert.Equal(packFile, location!.PackFileStr);
        Assert.Equal(mountRoot, location.MountRootStr);
    }

    [Fact]
    public void Resolve_IsCached_ByDirectory()
    {
        using var ws = new TempWorkspace("packpaths");
        string folder = Path.Combine(WidgetPackPaths.PackedRoot, "wolf.widgets.timer");
        Directory.CreateDirectory(folder);
        string mountRoot = Path.Combine(folder, "1-0-0");

        Assert.Null(WidgetPackPaths.Resolve(Path.Combine(mountRoot, "widget.html")));

        File.WriteAllText(Path.Combine(folder, "1-0-0.smw"), "");
        Assert.Null(WidgetPackPaths.Resolve(Path.Combine(mountRoot, "widget.html")));

        WidgetPackPaths.InvalidateResolveCache();
        Assert.NotNull(WidgetPackPaths.Resolve(Path.Combine(mountRoot, "widget.html")));
    }

    [Fact]
    public void TryResolve_Packed_ReturnsForwardSlashEntry()
    {
        using var ws = new TempWorkspace("packpaths");
        var (packFile, mountRoot) = MakeMountedPack(ws);

        bool ok = WidgetPackPaths.TryResolve(Path.Combine(mountRoot, "content", "widget.html"),
            out var resolvedPack, out var entry, out var packId, out var version);

        Assert.True(ok);
        Assert.Equal(packFile, resolvedPack);
        Assert.Equal("content/widget.html", entry);
        Assert.Equal("wolf.widgets.timer", packId);
        Assert.Equal("1-0-0", version);
    }

    [Fact]
    public void TryResolve_Unpacked_ReturnsFalseAndBlankOutParams()
    {
        using var ws = new TempWorkspace("packpaths");
        string html = ws.WriteFile("loose/widget/widget.html", "<html></html>");

        bool ok = WidgetPackPaths.TryResolve(html, out var packFile, out var entry,
            out var packId, out var version);

        Assert.False(ok);
        Assert.Equal(string.Empty, packFile);
        Assert.Equal(string.Empty, entry);
        Assert.Equal(string.Empty, packId);
        Assert.Equal(string.Empty, version);
    }
}
