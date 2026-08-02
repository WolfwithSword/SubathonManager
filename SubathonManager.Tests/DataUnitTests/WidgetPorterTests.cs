using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Data.Widgets;
using SubathonManager.Tests.Utility;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.DataUnitTests;

public class WidgetPorterPureTests
{
    
    [Theory]
    [InlineData("logo.png", "preview.png")]
    [InlineData("logo.JPG", "preview.jpg")]
    [InlineData("logo.jpeg", "preview.jpeg")]
    [InlineData("logo.webp", "preview.webp")]
    [InlineData("logo.gif", "preview.gif")]
    [InlineData("logo.bmp", "preview.png")]
    [InlineData("logo", "preview.png")]
    public void PreviewEntryName_Branches(string source, string expected)
        => Assert.Equal(expected, WidgetPorter.PreviewEntryName(source));

    [Fact]
    public void PreviewExtensions_AreTheDocumentedSet()
        => Assert.Equal(new[] {".png", ".jpg", ".jpeg", ".webp", ".gif"}, WidgetPorter.PreviewExtensions);

        
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,, ,")]
    public void ParseTags_BlankInput_ReturnsEmpty(string? raw)
        => Assert.Empty(WidgetPorter.ParseTags(raw));

    [Fact]
    public void ParseTags_SplitsTrimsAndDropsEmpties()
        => Assert.Equal(new[] {"timer", "sub", "alerts"}, WidgetPorter.ParseTags("  timer , sub ,, alerts  "));

    [Fact]
    public void ParseTags_DedupesCaseInsensitively_KeepingFirstSpelling()
        => Assert.Equal(new[] {"Timer", "sub"}, WidgetPorter.ParseTags("Timer, timer, TIMER, sub"));

        
    [Fact]
    public void BuildFileName_JoinsAllFourParts()
        => Assert.Equal("Wolf_Alerts_Sub-Timer_1.0.0.smw",
            WidgetPorter.BuildFileName("Wolf", "Alerts", "Sub Timer", "1.0.0"));

    [Fact]
    public void BuildFileName_BlankGroup_BecomesTheDefaultGroup()
        => Assert.Equal("Wolf_widgets_Timer_1.0.0.smw",
            WidgetPorter.BuildFileName("Wolf", "", "Timer", "1.0.0"));

    [Fact]
    public void BuildFileName_BlankAuthor_IsSkipped()
        => Assert.Equal("widgets_Timer_1.0.0.smw",
            WidgetPorter.BuildFileName("  ", null!, "Timer", "1.0.0"));

    [Fact]
    public void BuildFileName_ReplacesInvalidFileNameChars()
        => Assert.Equal("Wolf_widgets_A_B_1.0.0.smw",
            WidgetPorter.BuildFileName("Wolf", "widgets", "A/B", "1.0.0"));

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
        string name = WidgetPorter.BuildFileName("Wolf", "widgets", $"A{bad}B", "1.0.0");

        Assert.DoesNotContain(bad, name);
        Assert.Equal("Wolf_widgets_A_B_1.0.0.smw", name);
    }

    [Fact]
    public void BuildFileName_ReservedDeviceName_IsEscaped()
        => Assert.Equal("Wolf_widgets__CON_1.0.0.smw",
            WidgetPorter.BuildFileName("Wolf", "widgets", "CON", "1.0.0"));

    [Fact]
    public void BuildFileName_TrailingDotsAndSpaces_AreStripped()
        => Assert.Equal("Wolf_widgets_Timer_1.0.0.smw",
            WidgetPorter.BuildFileName("Wolf", "widgets", "Timer.", "1.0.0"));

    [Fact]
    public void BuildFileName_AlwaysEndsInSmw()
        => Assert.EndsWith(".smw", WidgetPorter.BuildFileName("a", "b", "c", "d"));

        
    private static WidgetPorter.ExportPlan PlanWith(params WidgetPorter.SmwEntry[] entries)
    {
        var plan = new WidgetPorter.ExportPlan();
        plan.Entries.AddRange(entries);
        return plan;
    }

    [Fact]
    public void IsSelected_LockedOrDefaultSelected_IsTrue()
    {
        var plan = PlanWith(
            new WidgetPorter.SmwEntry { ZipEntry = "locked", Locked = true, DefaultSelected = false },
            new WidgetPorter.SmwEntry { ZipEntry = "picked", DefaultSelected = true },
            new WidgetPorter.SmwEntry { ZipEntry = "skipped", DefaultSelected = false });

        Assert.True(plan.IsSelected("locked"));
        Assert.True(plan.IsSelected("picked"));
        Assert.False(plan.IsSelected("skipped"));
        Assert.False(plan.IsSelected("unknown"));
    }

    [Fact]
    public void IsSelected_IsCaseInsensitive()
    {
        var plan = PlanWith(new WidgetPorter.SmwEntry { ZipEntry = "content/A.png", DefaultSelected = true });
        Assert.True(plan.IsSelected("CONTENT/a.png"));
    }

    [Fact]
    public void ResolveVarValue_MandatoryRewriteWins()
    {
        var plan = new WidgetPorter.ExportPlan
        {
            VariableRewrites =
            {
                ["logo"] = "./_external/logo.png"
            }
        };

        Assert.Equal("./_external/logo.png",
            plan.ResolveVarValue(new JsVariable { Name = "logo", Value = "C:/abs/logo.png" }));
    }

    [Fact]
    public void ResolveVarValue_OptionalRewrite_OnlyAppliesWhenTheEntryIsSelected()
    {
        var plan = PlanWith(new WidgetPorter.SmwEntry { ZipEntry = "content/_external/resources/a.png" });
        plan.OptionalRewrites["logo"] = ("content/_external/resources/a.png", "./_external/resources/a.png");
        var jsVar = new JsVariable { Name = "logo", Value = "/resources/a.png" };

        Assert.Equal("/resources/a.png", plan.ResolveVarValue(jsVar));

        plan.Entries[0].DefaultSelected = true;
        Assert.Equal("./_external/resources/a.png", plan.ResolveVarValue(jsVar));
    }

    [Fact]
    public void ResolveVarValue_NoRewrite_ReturnsTheVariableValue()
    {
        var plan = new WidgetPorter.ExportPlan();
        Assert.Equal("hello", plan.ResolveVarValue(new JsVariable { Name = "greeting", Value = "hello" }));
    }

    [Fact]
    public void ResolveVarValue_NullValue_BecomesEmptyString()
    {
        var plan = new WidgetPorter.ExportPlan();
        Assert.Equal(string.Empty, plan.ResolveVarValue(new JsVariable { Name = "x", Value = null! }));
    }

        
    [Fact]
    public void Constants()
    {
        Assert.Equal("widget.json", WidgetPorter.ManifestFileName);
        Assert.Equal("content", WidgetPorter.ContentFolder);
        Assert.Equal("_external", WidgetPorter.ExternalFolder);
        Assert.Equal("_shared", WidgetPorter.SharedFolder);
        Assert.Equal("1", WidgetPorter.FormatVersion);
    }

}

[Collection("WorkingDirectory")]
public class WidgetPorterWorkspaceTests
{
    private const string PlainHtml = "<html><head></head><body>hi</body></html>";

    private static Widget MakeLooseWidget(TempWorkspace ws, string html = PlainHtml, string folder = "widgets/timer")
    {
        string htmlPath = ws.WriteFile($"{folder}/widget.html", html);
        return new Widget("Timer", htmlPath) { Width = 400, Height = 300 };
    }

    private static string Manifest(ZipArchive zip)
    {
        using var reader = new StreamReader(zip.GetEntry(WidgetPorter.ManifestFileName)!.Open());
        return reader.ReadToEnd();
    }

    private static string EntryText(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    
    [Fact]
    public void ExportsDirectory_IsUnderTheWorkingDirectory()
    {
        using var ws = new TempWorkspace("porter");
        Assert.Equal(Path.Combine(ws.Root, "exports", "widgets"), WidgetPorter.ExportsDirectory);
    }

        
    [Fact]
    public void ReadExistingMeta_NoMetaFile_ReturnsDefaults()
    {
        using var ws = new TempWorkspace("porter");
        var meta = WidgetPorter.ReadExistingMeta(MakeLooseWidget(ws));

        Assert.Equal(string.Empty, meta.Author);
        Assert.Empty(meta.Vars);
    }

    [Fact]
    public void ReadExistingMeta_MalformedJson_ReturnsDefaults()
    {
        using var ws = new TempWorkspace("porter");
        var widget = MakeLooseWidget(ws);
        File.WriteAllText(widget.HtmlPath + ".json", "{{{ not json");

        Assert.Equal(string.Empty, WidgetPorter.ReadExistingMeta(widget).Author);
    }

    [Fact]
    public void ReadExistingMeta_ValidJson_IsDeserialised()
    {
        using var ws = new TempWorkspace("porter");
        var widget = MakeLooseWidget(ws);
        File.WriteAllText(widget.HtmlPath + ".json",
            """{ "author": "Wolf", "url": "https://docs", "width": 640, "height": 480 }""");

        var meta = WidgetPorter.ReadExistingMeta(widget);

        Assert.Equal("Wolf", meta.Author);
        Assert.Equal("https://docs", meta.Url);
        Assert.Equal(640, meta.Width);
        Assert.Equal(480, meta.Height);
    }

        
    [Theory]
    [InlineData(WidgetType.Image)]
    [InlineData(WidgetType.Video)]
    public void BuildPlan_AssetWidget_ProducesAnEmptyPlan(WidgetType type)
    {
        using var ws = new TempWorkspace("porter");
        var widget = MakeLooseWidget(ws);
        widget.Type = type;

        var plan = WidgetPorter.BuildPlan(widget);

        Assert.Empty(plan.Entries);
        Assert.Equal(string.Empty, plan.EntryZipPath);
    }

    [Fact]
    public void BuildPlan_MissingHtml_ProducesAnEmptyPlan()
    {
        using var ws = new TempWorkspace("porter");
        var widget = new Widget("Timer", Path.Combine(ws.Root, "widgets", "gone", "widget.html"));

        Assert.Empty(WidgetPorter.BuildPlan(widget).Entries);
    }

        
    [Fact]
    public void BuildPlan_AlwaysEmitsManifestEntryAndMeta()
    {
        using var ws = new TempWorkspace("porter");
        var plan = WidgetPorter.BuildPlan(MakeLooseWidget(ws));

        Assert.Equal("content/widget.html", plan.EntryZipPath);
        var manifest = plan.Entries.Single(e => e.Kind == WidgetPorter.SmwEntryKind.Manifest);
        Assert.Equal(WidgetPorter.ManifestFileName, manifest.ZipEntry);
        Assert.True(manifest.Locked);
        Assert.NotNull(manifest.Generator);

        var entry = plan.Entries.Single(e => e.Kind == WidgetPorter.SmwEntryKind.Entry);
        Assert.Equal("content/widget.html", entry.ZipEntry);
        Assert.True(entry.Locked);
        Assert.Null(entry.Generator);

        var meta = plan.Entries.Single(e => e.Kind == WidgetPorter.SmwEntryKind.Meta);
        Assert.Equal("content/widget.html.json", meta.ZipEntry);
        Assert.True(meta.DefaultSelected);
        Assert.Null(meta.AbsSource);
    }

    [Fact]
    public void BuildPlan_ExistingMetaFile_IsUsedAsTheSource()
    {
        using var ws = new TempWorkspace("porter");
        var widget = MakeLooseWidget(ws);
        File.WriteAllText(widget.HtmlPath + ".json", "{}");

        var meta = WidgetPorter.BuildPlan(widget).Entries
            .Single(e => e.ZipEntry == "content/widget.html.json");

        Assert.Equal(widget.HtmlPath + ".json", meta.AbsSource);
    }

    [Fact]
    public void BuildPlan_LinkedCssInsideTheWidgetFolder_KeepsItsRelativeLayout()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("widgets/timer/css/style.css", ":root { --accent: red; }");
        var widget = MakeLooseWidget(ws,
            """<html><head><link rel="stylesheet" href="css/style.css"/></head></html>""");

        var css = WidgetPorter.BuildPlan(widget).Entries
            .Single(e => e.Kind == WidgetPorter.SmwEntryKind.Css);

        Assert.Equal("content/css/style.css", css.ZipEntry);
        Assert.True(css.DefaultSelected);
    }

    [Fact]
    public void BuildPlan_LinkedCssOutsideTheWidgetFolder_GoesToTheSharedFolder()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("shared/common.css", "body{}");
        var widget = MakeLooseWidget(ws,
            """<html><head><link rel="stylesheet" href="../../shared/common.css"/></head></html>""");

        var plan = WidgetPorter.BuildPlan(widget);
        var css = plan.Entries.Single(e => e.Kind == WidgetPorter.SmwEntryKind.Css);

        Assert.Equal("content/_shared/common.css", css.ZipEntry);

        var htmlEntry = plan.Entries.Single(e => e.Kind == WidgetPorter.SmwEntryKind.Entry);
        Assert.NotNull(htmlEntry.Generator);
        string rewritten = Encoding.UTF8.GetString(htmlEntry.Generator!(new WidgetPorter.SmwExportOptions()));
        Assert.Contains("./_shared/common.css", rewritten);
        Assert.DoesNotContain("../../shared/common.css", rewritten);
    }

    [Fact]
    public void BuildPlan_CssMetaEntry_OnlyWhenThereIsSomethingToSay()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("widgets/timer/style.css", ":root { --accent: red; }");
        var widget = MakeLooseWidget(ws,
            """<html><head><link rel="stylesheet" href="style.css"/></head></html>""");

        Assert.DoesNotContain(WidgetPorter.BuildPlan(widget).Entries,
            e => e.ZipEntry == "content/style.css.json");

        widget.CssVariables.Add(new CssVariable
        {
            Name = "accent", Value = "blue", Type = WidgetCssVariableType.Color, Description = "Accent"
        });

        Assert.Contains(WidgetPorter.BuildPlan(widget).Entries,
            e => e.ZipEntry == "content/style.css.json");
    }

    [Fact]
    public void BuildPlan_CssGenerator_AppliesTheDatabaseValue()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("widgets/timer/style.css", ":root { --accent: red; }");
        var widget = MakeLooseWidget(ws,
            """<html><head><link rel="stylesheet" href="style.css"/></head></html>""");
        widget.CssVariables.Add(new CssVariable
        {
            Name = "accent", Value = "blue", Type = WidgetCssVariableType.Color
        });

        var css = WidgetPorter.BuildPlan(widget).Entries
            .Single(e => e.Kind == WidgetPorter.SmwEntryKind.Css);

        Assert.NotNull(css.Generator);
        string rewritten = Encoding.UTF8.GetString(css.Generator!(new WidgetPorter.SmwExportOptions()));
        Assert.Contains("--accent: blue;", rewritten);
        Assert.DoesNotContain("red", rewritten);
    }

    [Fact]
    public void BuildPlan_LoneCssVariableClaimedOnce_AcrossSeveralStylesheets()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("widgets/timer/a.css", ":root { --accent: red; }");
        ws.WriteFile("widgets/timer/b.css", ":root { --accent: green; }");
        var widget = MakeLooseWidget(ws, """
            <html><head>
              <link rel="stylesheet" href="a.css"/>
              <link rel="stylesheet" href="b.css"/>
            </head></html>
            """);
        widget.CssVariables.Add(new CssVariable { Name = "accent", Value = "blue" });

        var plan = WidgetPorter.BuildPlan(widget);
        var cssEntries = plan.Entries.Where(e => e.Kind == WidgetPorter.SmwEntryKind.Css).ToList();

        Assert.Equal(2, cssEntries.Count);
        Assert.NotNull(cssEntries[0].Generator);
        Assert.Null(cssEntries[1].Generator);
    }

    [Fact]
    public void BuildPlan_MissingLinkedCss_IsIgnored()
    {
        using var ws = new TempWorkspace("porter");
        var widget = MakeLooseWidget(ws,
            """<html><head><link rel="stylesheet" href="gone.css"/></head></html>""");

        Assert.DoesNotContain(WidgetPorter.BuildPlan(widget).Entries,
            e => e.Kind == WidgetPorter.SmwEntryKind.Css);
    }

    [Fact]
    public void BuildPlan_OtherFilesInTheFolder_BecomeUnselectedAssets()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("widgets/timer/img/bg.png", "png");
        ws.WriteFile("widgets/timer/script.js", "console.log(1)");
        var widget = MakeLooseWidget(ws);

        var assets = WidgetPorter.BuildPlan(widget).Entries
            .Where(e => e.Kind == WidgetPorter.SmwEntryKind.Asset).ToList();

        Assert.Equal(2, assets.Count);
        Assert.All(assets, a => Assert.False(a.DefaultSelected));
        Assert.Contains(assets, a => a.ZipEntry == "content/img/bg.png");
        Assert.Contains(assets, a => a.ZipEntry == "content/script.js");
    }

    [Fact]
    public void BuildPlan_TheEntryHtmlIsNotAlsoListedAsAnAsset()
    {
        using var ws = new TempWorkspace("porter");
        var widget = MakeLooseWidget(ws);

        Assert.DoesNotContain(WidgetPorter.BuildPlan(widget).Entries,
            e => e.Kind == WidgetPorter.SmwEntryKind.Asset && e.ZipEntry == "content/widget.html");
    }

        
    [Fact]
    public void BuildPlan_AbsoluteFileVariable_BecomesAnExternalEntryAndRewrite()
    {
        using var ws = new TempWorkspace("porter");
        string sound = ws.WriteFile("sounds/alert.mp3", "id3");
        var widget = MakeLooseWidget(ws);
        widget.JsVariables.Add(new JsVariable
        {
            Name = "alertSound", Value = sound, Type = WidgetVariableType.SoundFile
        });

        var plan = WidgetPorter.BuildPlan(widget);
        var external = plan.Entries.Single(e => e.Kind == WidgetPorter.SmwEntryKind.External);

        Assert.Equal("content/_external/alert.mp3", external.ZipEntry);
        Assert.Equal(sound, external.AbsSource);
        Assert.True(external.DefaultSelected);
        Assert.Equal("./_external/alert.mp3", plan.VariableRewrites["alertSound"]);
    }

    [Fact]
    public void BuildPlan_AbsoluteFolderVariable_BundlesTheWholeFolder()
    {
        using var ws = new TempWorkspace("porter");
        string folder = ws.Dir("media");
        File.WriteAllText(Path.Combine(folder, "a.mp4"), "a");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "b.mp4"), "b");

        var widget = MakeLooseWidget(ws);
        widget.JsVariables.Add(new JsVariable
        {
            Name = "videoFolder", Value = folder, Type = WidgetVariableType.FolderPath
        });

        var plan = WidgetPorter.BuildPlan(widget);
        var external = plan.Entries.Where(e => e.Kind == WidgetPorter.SmwEntryKind.External).ToList();

        Assert.Equal(2, external.Count);
        Assert.Contains(external, e => e.ZipEntry == "content/_external/videoFolder/a.mp4");
        Assert.Contains(external, e => e.ZipEntry == "content/_external/videoFolder/sub/b.mp4");
        Assert.All(external, e => Assert.True(e.DefaultSelected));
        Assert.Equal("./_external/videoFolder", plan.VariableRewrites["videoFolder"]);
    }

    [Fact]
    public void BuildPlan_FolderVariableWithInvalidCharsInItsName_IsSanitisedInTheZipPath()
    {
        using var ws = new TempWorkspace("porter");
        string folder = ws.Dir("media");
        File.WriteAllText(Path.Combine(folder, "a.mp4"), "a");

        var widget = MakeLooseWidget(ws);
        widget.JsVariables.Add(new JsVariable
        {
            Name = "clips:main", Value = folder, Type = WidgetVariableType.FolderPath
        });

        var plan = WidgetPorter.BuildPlan(widget);
        var external = plan.Entries.Single(e => e.Kind == WidgetPorter.SmwEntryKind.External);
        Assert.Equal("content/_external/clips_main/a.mp4", external.ZipEntry);
        Assert.Equal("./_external/clips_main", plan.VariableRewrites["clips:main"]);
    }

    [Fact]
    public void BuildPlan_RelativeFileVariable_SelectsTheMatchingAssetInstead()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("widgets/timer/img/bg.png", "png");
        ws.WriteFile("widgets/timer/img/unused.png", "png");
        var widget = MakeLooseWidget(ws);
        widget.JsVariables.Add(new JsVariable
        {
            Name = "bg", Value = "./img/bg.png", Type = WidgetVariableType.ImageFile
        });

        var plan = WidgetPorter.BuildPlan(widget);

        Assert.True(plan.Entries.Single(e => e.ZipEntry == "content/img/bg.png").DefaultSelected);
        Assert.False(plan.Entries.Single(e => e.ZipEntry == "content/img/unused.png").DefaultSelected);
        Assert.Empty(plan.VariableRewrites);
    }

    [Fact]
    public void BuildPlan_RelativeFolderVariable_SelectsEverythingBeneathIt()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("widgets/timer/clips/a.mp4", "a");
        ws.WriteFile("widgets/timer/clips/b.mp4", "b");
        ws.WriteFile("widgets/timer/other.png", "x");
        var widget = MakeLooseWidget(ws);
        widget.JsVariables.Add(new JsVariable
        {
            Name = "clips", Value = "./clips", Type = WidgetVariableType.FolderPath
        });

        var plan = WidgetPorter.BuildPlan(widget);

        Assert.True(plan.Entries.Single(e => e.ZipEntry == "content/clips/a.mp4").DefaultSelected);
        Assert.True(plan.Entries.Single(e => e.ZipEntry == "content/clips/b.mp4").DefaultSelected);
        Assert.False(plan.Entries.Single(e => e.ZipEntry == "content/other.png").DefaultSelected);
    }

    [Fact]
    public void BuildPlan_NonFileVariables_AreIgnored()
    {
        using var ws = new TempWorkspace("porter");
        var widget = MakeLooseWidget(ws);
        widget.JsVariables.Add(new JsVariable { Name = "label", Value = "hello", Type = WidgetVariableType.String });
        widget.JsVariables.Add(new JsVariable { Name = "count", Value = "3", Type = WidgetVariableType.Int });

        var plan = WidgetPorter.BuildPlan(widget);

        Assert.DoesNotContain(plan.Entries, e => e.Kind == WidgetPorter.SmwEntryKind.External);
        Assert.Empty(plan.VariableRewrites);
    }

    [Fact]
    public void BuildPlan_BlankOrMissingFileVariables_AreIgnored()
    {
        using var ws = new TempWorkspace("porter");
        var widget = MakeLooseWidget(ws);
        widget.JsVariables.Add(new JsVariable { Name = "a", Value = "", Type = WidgetVariableType.ImageFile });
        widget.JsVariables.Add(new JsVariable
        {
            Name = "b", Value = Path.Combine(ws.Root, "nope.png"), Type = WidgetVariableType.ImageFile
        });
        widget.JsVariables.Add(new JsVariable
        {
            Name = "c", Value = "not/rooted/relative.png", Type = WidgetVariableType.ImageFile
        });

        var plan = WidgetPorter.BuildPlan(widget);

        Assert.DoesNotContain(plan.Entries, e => e.Kind == WidgetPorter.SmwEntryKind.External);
        Assert.Empty(plan.VariableRewrites);
    }

        
    [Fact]
    public void BuildPlan_ResourceFiles_BecomeOptionalExternalEntries()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("resources/images/logo.png", "png");
        ws.WriteFile("resources/audio/ding.mp3", "mp3");
        var widget = MakeLooseWidget(ws);

        var plan = WidgetPorter.BuildPlan(widget);
        var external = plan.Entries.Where(e => e.Kind == WidgetPorter.SmwEntryKind.External).ToList();

        Assert.Equal(2, external.Count);
        Assert.All(external, e => Assert.False(e.DefaultSelected));
        Assert.All(external, e => Assert.False(e.InUse));
        Assert.Contains(external, e => e.ZipEntry == "content/_external/resources/images/logo.png");
        Assert.Contains(external, e => e.ZipEntry == "content/_external/resources/audio/ding.mp3");
    }

    [Fact]
    public void BuildPlan_ResourceUsedByAVariable_IsMarkedInUseWithAnOptionalRewrite()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("resources/images/logo.png", "png");
        var widget = MakeLooseWidget(ws);
        widget.JsVariables.Add(new JsVariable
        {
            Name = "logo", Value = "/resources/images/logo.png", Type = WidgetVariableType.ImageFile
        });

        var plan = WidgetPorter.BuildPlan(widget);
        var external = plan.Entries.Single(e => e.Kind == WidgetPorter.SmwEntryKind.External);

        Assert.True(external.InUse);
        Assert.Equal("Used by variable \"logo\"", external.UsageHint);
        Assert.Equal(("content/_external/resources/images/logo.png",
            "./_external/resources/images/logo.png"), plan.OptionalRewrites["logo"]);
    }

    [Fact]
    public void BuildPlan_ResourceReferencedFromMarkup_IsMarkedInUse()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("resources/images/logo.png", "png");
        var widget = MakeLooseWidget(ws, """<html><body><img src="/resources/images/logo.png"></body></html>""");

        var external = WidgetPorter.BuildPlan(widget).Entries
            .Single(e => e.Kind == WidgetPorter.SmwEntryKind.External);

        Assert.True(external.InUse);
        Assert.Equal("Referenced directly by this widget's html/css", external.UsageHint);
    }

    [Fact]
    public void BuildPlan_ResourceReferencedFromCss_IsMarkedInUse()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("resources/images/bg.png", "png");
        ws.WriteFile("widgets/timer/style.css", "body { background: url(/resources/images/bg.png); }");
        var widget = MakeLooseWidget(ws,
            """<html><head><link rel="stylesheet" href="style.css"/></head></html>""");

        var external = WidgetPorter.BuildPlan(widget).Entries
            .Single(e => e.Kind == WidgetPorter.SmwEntryKind.External);

        Assert.True(external.InUse);
    }

    [Fact]
    public void BuildPlan_HtmlResourceUrls_AreRewritten_OnlyWhenTheResourceIsSelected()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("resources/images/logo.png", "png");
        var widget = MakeLooseWidget(ws, """<html><body><img src="/resources/images/logo.png"></body></html>""");

        var plan = WidgetPorter.BuildPlan(widget);
        var htmlEntry = plan.Entries.Single(e => e.Kind == WidgetPorter.SmwEntryKind.Entry);
        Assert.NotNull(htmlEntry.Generator);

        Assert.Contains("/resources/images/logo.png",
            Encoding.UTF8.GetString(htmlEntry.Generator!(new WidgetPorter.SmwExportOptions())));
        plan.Entries.Single(e => e.ZipEntry == "content/_external/resources/images/logo.png")
            .DefaultSelected = true;

        string rewritten = Encoding.UTF8.GetString(htmlEntry.Generator(new WidgetPorter.SmwExportOptions()));
        Assert.Contains("./_external/resources/images/logo.png", rewritten);
    }

    [Fact]
    public void BuildPlan_NestedCssResourceUrls_GetTheRightNumberOfDotDots()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("resources/images/bg.png", "png");
        ws.WriteFile("widgets/timer/css/style.css", "body { background: url(/resources/images/bg.png); }");
        var widget = MakeLooseWidget(ws,
            """<html><head><link rel="stylesheet" href="css/style.css"/></head></html>""");

        var plan = WidgetPorter.BuildPlan(widget);
        plan.Entries.Single(e => e.ZipEntry == "content/_external/resources/images/bg.png")
            .DefaultSelected = true;

        var css = plan.Entries.Single(e => e.Kind == WidgetPorter.SmwEntryKind.Css);
        string rewritten = Encoding.UTF8.GetString(css.Generator!(new WidgetPorter.SmwExportOptions()));
        Assert.Contains("../_external/resources/images/bg.png", rewritten);
    }

        
    [Fact]
    public async Task ExportWidgetAsync_WritesManifestEntryAndMeta()
    {
        using var ws = new TempWorkspace("porter");
        var widget = MakeLooseWidget(ws);
        var plan = WidgetPorter.BuildPlan(widget);
        string output = Path.Combine(ws.Root, "exports", "timer.smw");

        await WidgetPorter.ExportWidgetAsync(plan, new WidgetPorter.SmwExportOptions
        {
            Name = "Sub Timer", Author = "Wolf", Group = "Alerts", Version = "1.2.0",
            Tags = ["timer"]
        }, output);

        Assert.True(File.Exists(output));
        await using var zip = await ZipFile.OpenReadAsync(output, TestContext.Current.CancellationToken);

        Assert.NotNull(zip.GetEntry(WidgetPorter.ManifestFileName));
        Assert.Equal(PlainHtml, EntryText(zip, "content/widget.html"));
        Assert.NotNull(zip.GetEntry("content/widget.html.json"));
    }

    [Fact]
    public async Task ExportWidgetAsync_ManifestCarriesTheExportOptions()
    {
        using var ws = new TempWorkspace("porter");
        var widget = MakeLooseWidget(ws);
        widget.DocsUrl = "https://docs";
        var plan = WidgetPorter.BuildPlan(widget);
        string output = Path.Combine(ws.Root, "exports", "timer.smw");

        await WidgetPorter.ExportWidgetAsync(plan, new WidgetPorter.SmwExportOptions
        {
            Name = "Sub Timer", Author = "Wolf", Group = "Alerts", Version = "1.2.0", Tags = ["timer", "sub"]
        }, output);

        await using var zip = await ZipFile.OpenReadAsync(output, TestContext.Current.CancellationToken);
        var root = JsonDocument.Parse(Manifest(zip)).RootElement;
        var w = root.GetProperty("widget");

        Assert.Equal("1", root.GetProperty("version").GetString());
        Assert.Equal("wolf.alerts.sub-timer", w.GetProperty("pack_id").GetString());
        Assert.Equal("Sub Timer", w.GetProperty("name").GetString());
        Assert.Equal("Wolf", w.GetProperty("author").GetString());
        Assert.Equal("Alerts", w.GetProperty("group").GetString());
        Assert.Equal("1.2.0", w.GetProperty("widget_version").GetString());
        Assert.Equal("content/widget.html", w.GetProperty("entry").GetString());
        Assert.Equal("https://docs", w.GetProperty("docsUrl").GetString());
        Assert.Equal(new[] {"timer", "sub"}, w.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!));
        Assert.Equal(400, w.GetProperty("size").GetProperty("width").GetInt32());
        Assert.Equal(300, w.GetProperty("size").GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task ExportWidgetAsync_BlankName_FallsBackToTheWidgetName()
    {
        using var ws = new TempWorkspace("porter");
        var plan = WidgetPorter.BuildPlan(MakeLooseWidget(ws));
        string output = Path.Combine(ws.Root, "exports", "timer.smw");

        await WidgetPorter.ExportWidgetAsync(plan, new WidgetPorter.SmwExportOptions { Author = "Wolf" }, output);

        await using var zip = await ZipFile.OpenReadAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal("Timer",
            JsonDocument.Parse(Manifest(zip)).RootElement.GetProperty("widget").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ExportWidgetAsync_BlankAppVersion_IsFilledFromAppServices()
    {
        using var ws = new TempWorkspace("porter");
        var plan = WidgetPorter.BuildPlan(MakeLooseWidget(ws));
        string output = Path.Combine(ws.Root, "exports", "timer.smw");

        await WidgetPorter.ExportWidgetAsync(plan, new WidgetPorter.SmwExportOptions(), output);

        await using var zip = await ZipFile.OpenReadAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal(AppServices.AppVersion,
            JsonDocument.Parse(Manifest(zip)).RootElement.GetProperty("app_version").GetString());
    }

    [Fact]
    public async Task ExportWidgetAsync_ExplicitAppVersion_IsKept()
    {
        using var ws = new TempWorkspace("porter");
        var plan = WidgetPorter.BuildPlan(MakeLooseWidget(ws));
        string output = Path.Combine(ws.Root, "exports", "timer.smw");

        await WidgetPorter.ExportWidgetAsync(plan,
            new WidgetPorter.SmwExportOptions { AppVersion = "0.0.1-test" }, output);

        await using var zip = await ZipFile.OpenReadAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal("0.0.1-test",
            JsonDocument.Parse(Manifest(zip)).RootElement.GetProperty("app_version").GetString());
    }

    [Fact]
    public async Task ExportWidgetAsync_UnselectedAssets_AreSkipped()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("widgets/timer/img/bg.png", "png");
        var plan = WidgetPorter.BuildPlan(MakeLooseWidget(ws));
        string output = Path.Combine(ws.Root, "exports", "timer.smw");

        await WidgetPorter.ExportWidgetAsync(plan, new WidgetPorter.SmwExportOptions(), output);

        await using var zip = await ZipFile.OpenReadAsync(output, TestContext.Current.CancellationToken);
        Assert.Null(zip.GetEntry("content/img/bg.png"));
    }

    [Fact]
    public async Task ExportWidgetAsync_SelectedAssets_AreIncluded()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("widgets/timer/img/bg.png", "png-bytes");
        var plan = WidgetPorter.BuildPlan(MakeLooseWidget(ws));
        plan.Entries.Single(e => e.ZipEntry == "content/img/bg.png").DefaultSelected = true;
        string output = Path.Combine(ws.Root, "exports", "timer.smw");

        await WidgetPorter.ExportWidgetAsync(plan, new WidgetPorter.SmwExportOptions(), output);

        await using var zip = await ZipFile.OpenReadAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal("png-bytes", EntryText(zip, "content/img/bg.png"));
    }

    [Fact]
    public async Task ExportWidgetAsync_CreatesTheOutputFolder()
    {
        using var ws = new TempWorkspace("porter");
        var plan = WidgetPorter.BuildPlan(MakeLooseWidget(ws));
        string output = Path.Combine(ws.Root, "deep", "nested", "out", "timer.smw");

        await WidgetPorter.ExportWidgetAsync(plan, new WidgetPorter.SmwExportOptions(), output);

        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task ExportWidgetAsync_PreviewImage_IsAddedAndNamedInTheManifest()
    {
        using var ws = new TempWorkspace("porter");
        string preview = ws.WriteFile("art/thumb.jpg", "jpeg-bytes");
        var plan = WidgetPorter.BuildPlan(MakeLooseWidget(ws));
        string output = Path.Combine(ws.Root, "exports", "timer.smw");

        await WidgetPorter.ExportWidgetAsync(plan,
            new WidgetPorter.SmwExportOptions { PreviewImagePath = preview }, output);

        await using var zip = await ZipFile.OpenReadAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal("jpeg-bytes", EntryText(zip, "preview.jpg"));
        Assert.Equal("preview.jpg",
            JsonDocument.Parse(Manifest(zip)).RootElement
                .GetProperty("widget").GetProperty("preview_image").GetString());
    }

    [Fact]
    public async Task ExportWidgetAsync_MissingPreviewImage_IsSkipped()
    {
        using var ws = new TempWorkspace("porter");
        var plan = WidgetPorter.BuildPlan(MakeLooseWidget(ws));
        string output = Path.Combine(ws.Root, "exports", "timer.smw");

        await WidgetPorter.ExportWidgetAsync(plan,
            new WidgetPorter.SmwExportOptions { PreviewImagePath = Path.Combine(ws.Root, "gone.png") }, output);

        await using var zip = await ZipFile.OpenReadAsync(output, TestContext.Current.CancellationToken);
        Assert.Null(zip.GetEntry("preview.png"));
    }

    [Fact]
    public async Task ExportWidgetAsync_DuplicateZipEntries_AreWrittenOnce()
    {
        using var ws = new TempWorkspace("porter");
        var plan = WidgetPorter.BuildPlan(MakeLooseWidget(ws));
        plan.Entries.Add(new WidgetPorter.SmwEntry
        {
            ZipEntry = "content/widget.html",
            Kind = WidgetPorter.SmwEntryKind.Asset,
            DefaultSelected = true,
            Generator = _ => "duplicate"u8.ToArray()
        });
        string output = Path.Combine(ws.Root, "exports", "timer.smw");

        await WidgetPorter.ExportWidgetAsync(plan, new WidgetPorter.SmwExportOptions(), output);

        await using var zip = await ZipFile.OpenReadAsync(output, TestContext.Current.CancellationToken);
        Assert.Single(zip.Entries, e => e.FullName == "content/widget.html");
        Assert.Equal(PlainHtml, EntryText(zip, "content/widget.html"));
    }

    [Fact]
    public async Task ExportWidgetAsync_MetaJson_CarriesResolvedVariableValues()
    {
        using var ws = new TempWorkspace("porter");
        string sound = ws.WriteFile("sounds/alert.mp3", "id3");
        var widget = MakeLooseWidget(ws);
        widget.JsVariables.Add(new JsVariable
        {
            Name = "alertSound", Value = sound, Type = WidgetVariableType.SoundFile
        });
        widget.JsVariables.Add(new JsVariable
        {
            Name = "enabled", Value = "true", Type = WidgetVariableType.Boolean
        });
        widget.JsVariables.Add(new JsVariable { Name = "count", Value = "7", Type = WidgetVariableType.Int });
        widget.JsVariables.Add(new JsVariable
        {
            Name = "mode", Value = "a, b, c", Type = WidgetVariableType.StringSelect
        });

        var plan = WidgetPorter.BuildPlan(widget);
        string output = Path.Combine(ws.Root, "exports", "timer.smw");
        await WidgetPorter.ExportWidgetAsync(plan, new WidgetPorter.SmwExportOptions { Author = "Wolf" }, output);

        await using var zip = await ZipFile.OpenReadAsync(output, TestContext.Current.CancellationToken);
        var vars = JsonDocument.Parse(EntryText(zip, "content/widget.html.json"))
            .RootElement.GetProperty("Vars");

        Assert.Equal("./_external/alert.mp3", vars.GetProperty("alertSound").GetProperty("Value").GetString());
        Assert.True(vars.GetProperty("enabled").GetProperty("Value").GetBoolean());
        Assert.Equal(7, vars.GetProperty("count").GetProperty("Value").GetInt32());
        Assert.Equal("a", vars.GetProperty("mode").GetProperty("Value").GetString());
        Assert.Equal(["a", "b", "c"],
            vars.GetProperty("mode").GetProperty("Options").EnumerateArray().Select(o => o.GetString()!));
    }

    [Fact]
    public async Task ExportedPack_CanBeInstalledAndReadBack()
    {
        using var ws = new TempWorkspace("porter");
        ws.WriteFile("widgets/timer/style.css", ":root { --accent: red; }");
        var widget = MakeLooseWidget(ws,
            """<html><head><link rel="stylesheet" href="style.css"/></head><body>hi</body></html>""");

        var plan = WidgetPorter.BuildPlan(widget);
        string output = Path.Combine(ws.Root, "exports", "timer.smw");
        await WidgetPorter.ExportWidgetAsync(plan, new WidgetPorter.SmwExportOptions
        {
            Name = "Sub Timer", Author = "Wolf", Group = "Alerts", Version = "1.0.0"
        }, output);

        var installed = WidgetPackInstaller.Install(output);

        Assert.NotNull(installed);
        Assert.Equal("wolf.alerts.sub-timer", installed!.Manifest.PackId);

        var fs = new WidgetPackFileSystem();
        Assert.True(fs.Exists(installed.HtmlPath));
        Assert.Contains("<body>hi</body>", fs.ReadAllText(installed.HtmlPath)!);
        Assert.Contains("--accent",
            fs.ReadAllText(Path.Combine(Path.GetDirectoryName(installed.HtmlPath)!, "style.css"))!);
    }

            [Fact]
    public void ExtractExistingPreview_LooseWidget_ReturnsNull()
    {
        using var ws = new TempWorkspace("porter");
        Assert.Null(WidgetPorter.ExtractExistingPreview(MakeLooseWidget(ws)));
    }

    [Fact]
    public void ExtractExistingPreview_PackWithoutAPreview_ReturnsNull()
    {
        using var ws = new TempWorkspace("porter");
        string source = TestPacks.WriteSmw(ws.Path_("downloads", "timer.smw"),
            TestPacks.WidgetManifestJson(packId: "wolf.widgets.timer", version: "1.0.0"),
            new Dictionary<string, string> { ["content/widget.html"] = PlainHtml });
        var installed = WidgetPackInstaller.Install(source)!;

        Assert.Null(WidgetPorter.ExtractExistingPreview(new Widget("Timer", installed.HtmlPath)));
    }

    [Fact]
    public void ExtractExistingPreview_PackWithAPreview_WritesItToATempFile()
    {
        using var ws = new TempWorkspace("porter");
        string source = TestPacks.WriteSmw(ws.Path_("downloads", "timer.smw"),
            TestPacks.WidgetManifestJson(packId: "wolf.widgets.timer", version: "1.0.0", preview: "preview.png"),
            new Dictionary<string, string>
            {
                ["content/widget.html"] = PlainHtml,
                ["preview.png"] = "png-bytes"
            });
        var installed = WidgetPackInstaller.Install(source)!;

        var previous = WidgetFiles.Current;
        WidgetFiles.Current = new WidgetPackFileSystem();
        try
        {
            string? temp = WidgetPorter.ExtractExistingPreview(new Widget("Timer", installed.HtmlPath));

            Assert.NotNull(temp);
            Assert.Equal(".png", Path.GetExtension(temp));
            Assert.Equal("png-bytes", File.ReadAllText(temp!));
            try { File.Delete(temp!); } catch { /**/ }
        }
        finally
        {
            WidgetFiles.Current = previous;
        }
    }
}
