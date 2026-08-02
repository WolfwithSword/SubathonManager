using SubathonManager.Data.Overlays;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.DataUnitTests;

/// <summary>Naming helpers that never look at the filesystem, so this class runs in parallel.</summary>
public class OverlayPackPathsPureTests
{
    
    [Theory]
    [InlineData("Main Overlay", "1.2.0", "Main Overlay v1.2.0")]
    [InlineData("Main Overlay", "1-2-0", "Main Overlay v1.2.0")]
    [InlineData("  Padded  ", "1.0.0", "Padded v1.0.0")]
    [InlineData("Main Overlay", "", "Main Overlay")]
    [InlineData("Main Overlay", "   ", "Main Overlay")]
    [InlineData("Main Overlay", null, "Main Overlay")]
    [InlineData("", "1.0.0", "Imported Overlay v1.0.0")]
    [InlineData("   ", "1.0.0", "Imported Overlay v1.0.0")]
    [InlineData(null, "1.0.0", "Imported Overlay v1.0.0")]
    public void RouteName_Branches(string? name, string? version, string expected)
        => Assert.Equal(expected, OverlayPackPaths.RouteName(name!, version!));
        
    [Theory]
    [InlineData("Main Overlay v1.2.0", "Main Overlay")]
    [InlineData("Main Overlay v1", "Main Overlay")]
    [InlineData("Main Overlay V2.0.0-beta", "Main Overlay")]
    [InlineData("  Main Overlay v1.0.0  ", "Main Overlay")]
    [InlineData("Main Overlay", "Main Overlay")]
    [InlineData("Overlay vNext", "Overlay vNext")]
    [InlineData("v1.0.0", "v1.0.0")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void BaseRouteName_Branches(string? routeName, string expected)
        => Assert.Equal(expected, OverlayPackPaths.BaseRouteName(routeName));

    [Fact]
    public void BaseRouteName_UndoesRouteName()
    {
        string routeName = OverlayPackPaths.RouteName("Stream Layout", "2-1-0");
        Assert.Equal("Stream Layout v2.1.0", routeName);
        Assert.Equal("Stream Layout", OverlayPackPaths.BaseRouteName(routeName));
    }

    [Fact]
    public void BaseRouteName_OnlyStripsTheTrailingVersion()
        => Assert.Equal("Overlay v1.0.0 Copy", OverlayPackPaths.BaseRouteName("Overlay v1.0.0 Copy"));

        
    [Fact]
    public void BuildFileName_JoinsSanitisedPartsWithUnderscores()
        => Assert.Equal("Wolf_Main-Overlay_1.0.0.smo",
            OverlayPackPaths.BuildFileName("Wolf", "Main Overlay", "1.0.0"));

    [Fact]
    public void BuildFileName_SkipsBlankParts()
        => Assert.Equal("Main-Overlay_1.0.0.smo",
            OverlayPackPaths.BuildFileName("", "Main Overlay", "1.0.0"));

    [Fact]
    public void BuildFileName_AllBlank_FallsBackToOverlay()
        => Assert.Equal("overlay.smo", OverlayPackPaths.BuildFileName("", "  ", null!));

    [Fact]
    public void BuildFileName_ReplacesInvalidFileNameChars()
    {
        string name = OverlayPackPaths.BuildFileName("Wolf", "A/B:C", "1.0.0");

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.Equal("Wolf_A_B_C_1.0.0.smo", name);
    }

    [Theory]
    [InlineData('/')]
    [InlineData('\\')]
    [InlineData(':')]
    [InlineData('*')]
    [InlineData('?')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('|')]
    [InlineData('"')]
    public void BuildFileName_RejectsEveryInvalidChar_OnEveryPlatform(char bad)
    {
        string name = OverlayPackPaths.BuildFileName("Wolf", $"Main{bad}Overlay", "1.0.0");

        Assert.DoesNotContain(bad, name);
        Assert.Equal("Wolf_Main_Overlay_1.0.0.smo", name);
    }

    [Fact]
    public void BuildFileName_ReservedDeviceName_IsEscaped()
        => Assert.Equal("_CON.smo", OverlayPackPaths.BuildFileName("", "CON", null!));

    [Fact]
    public void BuildFileName_TrailingDotsAndSpaces_AreStripped()
        => Assert.Equal("Wolf_Main_1.0.0.smo", OverlayPackPaths.BuildFileName("Wolf ", "Main.", "1.0.0"));

    [Fact]
    public void BuildFileName_AlwaysEndsWithTheOverlayExtension()
        => Assert.EndsWith(OverlayPackPaths.OverlayExtension,
            OverlayPackPaths.BuildFileName("a", "b", "c"));

        
    [Fact]
    public void Constants()
    {
        Assert.Equal(".smo", OverlayPackPaths.OverlayExtension);
        Assert.Equal("unpack", OverlayPackPaths.UnpackFolderName);
    }

    }

[Collection("WorkingDirectory")]
public class OverlayPackPathsWorkspaceTests
{
    [Fact]
    public void ImportsRoot_IsRelativeToCurrentDirectory()
    {
        using var ws = new TempWorkspace("overlaypaths");
        Assert.Equal(Path.Combine(ws.Root, "imports", "overlays"), OverlayPackPaths.ImportsRoot);
    }

    [Fact]
    public void OverlayRoot_SlugsAuthorAndName()
    {
        using var ws = new TempWorkspace("overlaypaths");

        Assert.Equal(Path.Combine(OverlayPackPaths.ImportsRoot, "wolf-with-sword", "main-overlay"),
            OverlayPackPaths.OverlayRoot("Wolf With Sword", "Main Overlay"));
    }

    [Fact]
    public void OverlayRoot_BlankSegments_UseFallbacks()
    {
        using var ws = new TempWorkspace("overlaypaths");

        Assert.Equal(Path.Combine(OverlayPackPaths.ImportsRoot, "unknown", "overlay"),
            OverlayPackPaths.OverlayRoot("", ""));
    }

    [Fact]
    public void OverlayRoot_UnsluggableSegments_UseFallbacks()
    {
        using var ws = new TempWorkspace("overlaypaths");

        Assert.Equal(Path.Combine(OverlayPackPaths.ImportsRoot, "unknown", "overlay"),
            OverlayPackPaths.OverlayRoot("!!!", "###"));
    }

    [Fact]
    public void ArchiveFile_IsVersionDotSmoUnderOverlayRoot()
    {
        using var ws = new TempWorkspace("overlaypaths");

        Assert.Equal(Path.Combine(OverlayPackPaths.OverlayRoot("Wolf", "Main"), "1.2.0.smo"),
            OverlayPackPaths.ArchiveFile("Wolf", "Main", "1.2.0"));
    }

    [Fact]
    public void ArchiveFile_BlankVersion_FallsBackTo100()
    {
        using var ws = new TempWorkspace("overlaypaths");

        Assert.Equal(Path.Combine(OverlayPackPaths.OverlayRoot("Wolf", "Main"), "1.0.0.smo"),
            OverlayPackPaths.ArchiveFile("Wolf", "Main", "  "));
    }

    [Fact]
    public void ArchiveFile_SanitisesInvalidVersionChars()
    {
        using var ws = new TempWorkspace("overlaypaths");

        Assert.Equal(Path.Combine(OverlayPackPaths.OverlayRoot("Wolf", "Main"), "1.0_0.smo"),
            OverlayPackPaths.ArchiveFile("Wolf", "Main", "1.0:0"));
        Assert.Equal(Path.Combine(OverlayPackPaths.OverlayRoot("Wolf", "Main"), "1.0_0.smo"),
            OverlayPackPaths.ArchiveFile("Wolf", "Main", "1.0/0"));
    }

    [Fact]
    public void UnpackDir_IsTheUnpackFolderUnderOverlayRoot()
    {
        using var ws = new TempWorkspace("overlaypaths");

        Assert.Equal(Path.Combine(OverlayPackPaths.OverlayRoot("Wolf", "Main"), "unpack"),
            OverlayPackPaths.UnpackDir("Wolf", "Main"));
    }

    [Fact]
    public void ImportedVersions_MissingFolder_ReturnsEmpty()
    {
        using var ws = new TempWorkspace("overlaypaths");
        Assert.Empty(OverlayPackPaths.ImportedVersions("Wolf", "Main"));
    }

    [Fact]
    public void ImportedVersions_ListsSmoFileNamesOnly()
    {
        using var ws = new TempWorkspace("overlaypaths");
        string root = OverlayPackPaths.OverlayRoot("Wolf", "Main");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "1.0.0.smo"), "");
        File.WriteAllText(Path.Combine(root, "1.1.0.smo"), "");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "");
        Directory.CreateDirectory(Path.Combine(root, "unpack"));

        var versions = OverlayPackPaths.ImportedVersions("Wolf", "Main");

        Assert.Equal(2, versions.Count);
        Assert.Contains("1.0.0", versions);
        Assert.Contains("1.1.0", versions);
    }

    [Fact]
    public void ImportedVersions_IsNotCached_SeesNewFilesImmediately()
    {
        using var ws = new TempWorkspace("overlaypaths");
        string root = OverlayPackPaths.OverlayRoot("Wolf", "Main");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "1.0.0.smo"), "");
        Assert.Single(OverlayPackPaths.ImportedVersions("Wolf", "Main"));

        File.WriteAllText(Path.Combine(root, "2.0.0.smo"), "");
        Assert.Equal(2, OverlayPackPaths.ImportedVersions("Wolf", "Main").Count);
    }
}
