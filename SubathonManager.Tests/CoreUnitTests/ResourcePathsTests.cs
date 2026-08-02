using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.CoreUnitTests;

public class ResourcePathsPureTests
{
    
    [Theory]
    [InlineData("/resources/images/logo.png", true)]
    [InlineData("/RESOURCES/images/logo.png", true)]
    [InlineData("\\resources\\images\\logo.png", true)]
    [InlineData("resources/images/logo.png", false)]
    [InlineData("/res/images/logo.png", false)]
    [InlineData("./resources/logo.png", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsResourceUrl_Branches(string? value, bool expected)
        => Assert.Equal(expected, ResourcePaths.IsResourceUrl(value));

        
    [Theory]
    [InlineData("/resources/images/logo.png", "images/logo.png")]
    [InlineData("\\resources\\images\\logo.png", "images/logo.png")]
    [InlineData("/resources/images/logo.png?v=2", "images/logo.png")]
    [InlineData("/resources/images/logo.png#frag", "images/logo.png")]
    [InlineData("/resources/images/logo.png?v=2#frag", "images/logo.png")]
    [InlineData("/resources//images/logo.png/", "images/logo.png")]
    public void RelativeFromUrl_StripsPrefixQueryAndFragment(string url, string expected)
        => Assert.Equal(expected, ResourcePaths.RelativeFromUrl(url));

    [Theory]
    [InlineData("/resources/")]
    [InlineData("/resources/?x=1")]
    [InlineData("/notresources/logo.png")]
    [InlineData(null)]
    public void RelativeFromUrl_ReturnsNull_WhenNoUsableRelative(string? url)
        => Assert.Null(ResourcePaths.RelativeFromUrl(url));

        
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FindReferences_EmptyInput_YieldsNothing(string? text)
        => Assert.Empty(ResourcePaths.FindReferences(text));

    [Fact]
    public void FindReferences_NoMatch_YieldsNothing()
        => Assert.Empty(ResourcePaths.FindReferences("<p>nothing to see here</p>"));

    [Theory]
    [InlineData("<img src=\"/resources/images/logo.png\">")]
    [InlineData("<img src='/resources/images/logo.png'>")]
    [InlineData("background: url(/resources/images/logo.png);")]
    [InlineData("const a = `/resources/images/logo.png`;")]
    [InlineData("let x = /resources/images/logo.png ")]
    [InlineData("load(a,/resources/images/logo.png)")]
    [InlineData("x;/resources/images/logo.png")]
    [InlineData("[/resources/images/logo.png ")]
    public void FindReferences_AcceptedDelimiters(string text)
        => Assert.Equal(["images/logo.png"], ResourcePaths.FindReferences(text));

    [Fact]
    public void FindReferences_RejectsMidWordMatch()
    {
        Assert.Empty(ResourcePaths.FindReferences("https://cdn.example.com/resources/images/logo.png"));
    }

    [Fact]
    public void FindReferences_StripsQueryAndFragment()
    {
        Assert.Equal(new[] {"audio/ding.mp3"},
            ResourcePaths.FindReferences("<audio src=\"/resources/audio/ding.mp3?v=3#t=1\">"));
    }

    [Fact]
    public void FindReferences_UnescapesPercentEncoding()
    {
        Assert.Equal(new[] {"images/my logo.png"},
            ResourcePaths.FindReferences("<img src=\"/resources/images/my%20logo.png\">"));
    }

    [Fact]
    public void FindReferences_ReturnsEveryMatch_IncludingDuplicates()
    {
        var found = ResourcePaths.FindReferences(
            "<img src=\"/resources/a.png\"><img src=\"/resources/b.png\"><img src=\"/resources/a.png\">")
            .ToList();

        Assert.Equal(new[] {"a.png", "b.png", "a.png"}, found);
    }

    [Fact]
    public void FindReferences_BareTrailingSlashYieldsNothing()
    {
        Assert.Empty(ResourcePaths.FindReferences("<img src=\"/resources/\">"));
    }

        
    [Theory]
    [InlineData("")]
    public void RewriteReferences_EmptyText_ReturnedAsIs(string text)
        => Assert.Equal(text, ResourcePaths.RewriteReferences(text, _ => "./"));

    [Fact]
    public void RewriteReferences_NullPrefix_LeavesMatchUntouched()
    {
        const string html = "<img src=\"/resources/images/logo.png\">";
        Assert.Equal(html, ResourcePaths.RewriteReferences(html, _ => null));
    }

    [Fact]
    public void RewriteReferences_PrefixReplacesTheUrlPrefixOnly()
    {
        const string html = "<img src=\"/resources/images/logo.png\">";
        var result = ResourcePaths.RewriteReferences(html, _ => "../_external/resources/");

        Assert.Equal("<img src=\"../_external/resources/images/logo.png\">", result);
    }

    [Fact]
    public void RewriteReferences_PrefixCallbackReceivesNormalisedRelative()
    {
        var seen = new List<string>();
        ResourcePaths.RewriteReferences("<img src=\"/resources/images/my%20logo.png?v=1\">", rel =>
        {
            seen.Add(rel);
            return null;
        });

        Assert.Equal(new[] {"images/my logo.png"}, seen);
    }

    [Fact]
    public void RewriteReferences_KeepsOriginalCapturedSuffix_IncludingQuery()
    {
        var result = ResourcePaths.RewriteReferences(
            "<img src=\"/resources/images/logo.png?v=1\">", _ => "./assets/");
        Assert.Equal("<img src=\"./assets/images/logo.png?v=1\">", result);
    }

    [Fact]
    public void RewriteReferences_CanRewriteSelectively()
    {
        const string html = "<img src=\"/resources/a.png\"><img src=\"/resources/b.png\">";
        var result = ResourcePaths.RewriteReferences(html, rel => rel == "a.png" ? "./x/" : null);

        Assert.Equal("<img src=\"./x/a.png\"><img src=\"/resources/b.png\">", result);
    }

    [Fact]
    public void RewriteReferences_UnmatchedText_Unchanged()
    {
        const string css = "body { color: red; }";
        Assert.Equal(css, ResourcePaths.RewriteReferences(css, _ => "./x/"));
    }

        
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToResourceUrl_BlankInput_ReturnsNull(string? value)
        => Assert.Null(ResourcePaths.ToResourceUrl(value));

    [Fact]
    public void ToResourceUrl_AlreadyResourceUrl_NormalisesSlashesOnly()
        => Assert.Equal("/resources/images/logo.png",
            ResourcePaths.ToResourceUrl(@"\resources\images\logo.png"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("images/logo.png")]
    [InlineData("C:\\somewhere\\logo.png")]
    public void ToLocalPath_NonResourceUrl_ReturnsNull(string? value)
        => Assert.Null(ResourcePaths.ToLocalPath(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ResolveRequestPath_BlankInput_ReturnsNull(string? value)
        => Assert.Null(ResourcePaths.ResolveRequestPath(value));

    [Theory]
    [InlineData("/resources/")]
    [InlineData("/resources/?v=1")]
    [InlineData("/")]
    public void ResolveRequestPath_EmptyRelative_ReturnsNull(string value)
        => Assert.Null(ResourcePaths.ResolveRequestPath(value));

        
    [Fact]
    public void DefaultFolders_AreTheDocumentedThree()
        => Assert.Equal(new[] {"images", "images/logos", "audio"}, ResourcePaths.DefaultFolders);

    [Fact]
    public void UrlPrefix_MatchesBundleFolder()
        => Assert.Equal($"/{ResourcePaths.BundleFolder}/", ResourcePaths.UrlPrefix);

}

[Collection("WorkingDirectory")]
public class ResourcePathsWorkspaceTests
{
    [Fact]
    public void Root_IsResourcesUnderCurrentDirectory()
    {
        using var ws = new TempWorkspace("respaths");
        Assert.Equal(Path.GetFullPath(Path.Combine(ws.Root, "resources")), ResourcePaths.Root);
    }

    [Fact]
    public void EnsureCreated_CreatesRootAndDefaultFolders()
    {
        using var ws = new TempWorkspace("respaths");
        ResourcePaths.EnsureCreated();

        Assert.True(Directory.Exists(ResourcePaths.Root));
        foreach (var folder in ResourcePaths.DefaultFolders)
            Assert.True(Directory.Exists(Path.Combine(ResourcePaths.Root,
                folder.Replace('/', Path.DirectorySeparatorChar))), folder);
    }

    [Fact]
    public void EnsureCreated_IsIdempotent()
    {
        using var ws = new TempWorkspace("respaths");
        ResourcePaths.EnsureCreated();
        File.WriteAllText(Path.Combine(ResourcePaths.Root, "images", "keep.png"), "x");

        ResourcePaths.EnsureCreated();

        Assert.True(File.Exists(Path.Combine(ResourcePaths.Root, "images", "keep.png")));
    }

    [Fact]
    public void EnumerateRelative_MissingRoot_ReturnsEmpty()
    {
        using var ws = new TempWorkspace("respaths");
        Assert.Empty(ResourcePaths.EnumerateRelative());
    }

    [Fact]
    public void EnumerateRelative_ReturnsForwardSlashRelativePaths_SortedIgnoringCase()
    {
        using var ws = new TempWorkspace("respaths");
        ws.WriteFile("resources/images/zeta.png", "z");
        ws.WriteFile("resources/images/Alpha.png", "a");
        ws.WriteFile("resources/audio/ding.mp3", "d");

        var found = ResourcePaths.EnumerateRelative();

        Assert.Equal(new[] {"audio/ding.mp3", "images/Alpha.png", "images/zeta.png"}, found);
    }

    [Fact]
    public void EnumerateRelative_SkipsDotPrefixedFilesAndFolders()
    {
        using var ws = new TempWorkspace("respaths");
        ws.WriteFile("resources/.hidden", "h");
        ws.WriteFile("resources/.cache/thumb.png", "t");
        ws.WriteFile("resources/images/.dsstore", "d");
        ws.WriteFile("resources/images/visible.png", "v");

        Assert.Equal(new[] {"images/visible.png"}, ResourcePaths.EnumerateRelative());
    }

    [Fact]
    public void ToResourceUrl_PathInsideRoot_ReturnsUrl()
    {
        using var ws = new TempWorkspace("respaths");
        string local = ws.WriteFile("resources/images/logo.png", "x");

        Assert.Equal("/resources/images/logo.png", ResourcePaths.ToResourceUrl(local));
    }

    [Fact]
    public void ToResourceUrl_PathOutsideRoot_ReturnsNull()
    {
        using var ws = new TempWorkspace("respaths");
        string outside = ws.WriteFile("elsewhere/logo.png", "x");

        Assert.Null(ResourcePaths.ToResourceUrl(outside));
    }

    [Fact]
    public void ToResourceUrl_RootItself_ReturnsNull()
    {
        using var ws = new TempWorkspace("respaths");
        ResourcePaths.EnsureCreated();
        Assert.Null(ResourcePaths.ToResourceUrl(ResourcePaths.Root));
    }

    [Fact]
    public void ToResourceUrl_RelativePath_ResolvedAgainstCwd()
    {
        using var ws = new TempWorkspace("respaths");
        ws.WriteFile("resources/audio/ding.mp3", "x");

        Assert.Equal("/resources/audio/ding.mp3", 
            ResourcePaths.ToResourceUrl("./resources/audio/ding.mp3"));
    }

    [Fact]
    public void ToLocalPath_ResourceUrl_ReturnsAbsolutePathUnderRoot()
    {
        using var ws = new TempWorkspace("respaths");
        var local = ResourcePaths.ToLocalPath("/resources/images/logo.png");

        Assert.NotNull(local);
        Assert.Equal(Path.Combine(ResourcePaths.Root, "images", "logo.png"), local);
    }

    [Fact]
    public void ToLocalPath_DoesNotRequireTheFileToExist()
    {
        using var ws = new TempWorkspace("respaths");
        Assert.NotNull(ResourcePaths.ToLocalPath("/resources/never/created.png"));
    }

    [Fact]
    public void ToLocalPath_TraversalOutsideRoot_ReturnsNull()
    {
        using var ws = new TempWorkspace("respaths");
        Assert.Null(ResourcePaths.ToLocalPath("/resources/../../escaped.png"));
    }

    [Fact]
    public void ToLocalPath_TrailingSlashesTrimmed()
    {
        using var ws = new TempWorkspace("respaths");
        Assert.Equal(Path.Combine(ResourcePaths.Root, "images", "logo.png"),
            ResourcePaths.ToLocalPath("/resources//images/logo.png/"));
    }

    [Fact]
    public void ToResourceUrl_RoundTripsWithToLocalPath()
    {
        using var ws = new TempWorkspace("respaths");
        string local = ws.WriteFile("resources/images/logos/brand.png", "x");

        var url = ResourcePaths.ToResourceUrl(local);
        Assert.Equal("/resources/images/logos/brand.png", url);
        Assert.Equal(local, ResourcePaths.ToLocalPath(url));
    }

    [Fact]
    public void ResolveRequestPath_ExistingFile_ReturnsFullPath()
    {
        using var ws = new TempWorkspace("respaths");
        string local = ws.WriteFile("resources/images/logo.png", "x");

        Assert.Equal(local, ResourcePaths.ResolveRequestPath("/resources/images/logo.png"));
    }

    [Fact]
    public void ResolveRequestPath_WithoutUrlPrefix_StillResolves()
    {
        using var ws = new TempWorkspace("respaths");
        string local = ws.WriteFile("resources/images/logo.png", "x");

        Assert.Equal(local, ResourcePaths.ResolveRequestPath("images/logo.png"));
    }

    [Fact]
    public void ResolveRequestPath_StripsQueryString()
    {
        using var ws = new TempWorkspace("respaths");
        string local = ws.WriteFile("resources/images/logo.png", "x");

        Assert.Equal(local, ResourcePaths.ResolveRequestPath("/resources/images/logo.png?v=42"));
    }

    [Fact]
    public void ResolveRequestPath_UnescapesPercentEncoding()
    {
        using var ws = new TempWorkspace("respaths");
        string local = ws.WriteFile("resources/images/my logo.png", "x");

        Assert.Equal(local, ResourcePaths.ResolveRequestPath("/resources/images/my%20logo.png"));
    }

    [Fact]
    public void ResolveRequestPath_MissingFile_ReturnsNull()
    {
        using var ws = new TempWorkspace("respaths");
        ResourcePaths.EnsureCreated();

        Assert.Null(ResourcePaths.ResolveRequestPath("/resources/images/nope.png"));
    }

    [Fact]
    public void ResolveRequestPath_DirectoryTraversal_ReturnsNull()
    {
        using var ws = new TempWorkspace("respaths");
        ws.WriteFile("secret.txt", "hunter2");

        Assert.Null(ResourcePaths.ResolveRequestPath("/resources/../secret.txt"));
    }

    [Fact]
    public void ResolveRequestPath_DirectoryNotAFile_ReturnsNull()
    {
        using var ws = new TempWorkspace("respaths");
        ResourcePaths.EnsureCreated();

        Assert.Null(ResourcePaths.ResolveRequestPath("/resources/images"));
    }
}
