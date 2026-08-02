using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Data.Widgets;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.DataUnitTests;

public class WidgetDisplayHelperLabelTests
{
    [Theory]
    [InlineData(WidgetDisplayKind.Image, "Image")]
    [InlineData(WidgetDisplayKind.Video, "Video")]
    [InlineData(WidgetDisplayKind.UnpackedWidget, "Unpacked Widget")]
    [InlineData(WidgetDisplayKind.Widget, "Widget")]
    public void GetLabel_Branches(WidgetDisplayKind kind, string expected)
        => Assert.Equal(expected, kind.GetLabel());

    [Fact]
    public void GetLabel_UnknownValue_FallsBackToWidget()
        => Assert.Equal("Widget", ((WidgetDisplayKind)999).GetLabel());

    [Fact]
    public void GetLabel_CoversEveryDeclaredKind()
    {
        foreach (WidgetDisplayKind kind in Enum.GetValues<WidgetDisplayKind>())
            Assert.False(string.IsNullOrWhiteSpace(kind.GetLabel()), kind.ToString());
    }
}

[Collection("WorkingDirectory")]
public class WidgetDisplayHelperKindTests
{
    [Theory]
    [InlineData(WidgetType.Image, WidgetDisplayKind.Image)]
    [InlineData(WidgetType.Video, WidgetDisplayKind.Video)]
    public void GetDisplayKind_AssetTypes_NeverProbeTheFilesystem(WidgetType type, WidgetDisplayKind expected)
    {
        using var ws = new TempWorkspace("displaykind");
        var widget = new Widget("W", Path.Combine(ws.Root, "does", "not", "exist.png")) { Type = type };

        Assert.Equal(expected, widget.GetDisplayKind());
    }

    [Fact]
    public void GetDisplayKind_HtmlInsideAPack_IsWidget()
    {
        using var ws = new TempWorkspace("displaykind");
        string folder = Path.Combine(WidgetPackPaths.PackedRoot, "wolf.widgets.timer");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "1-0-0.smw"), "");
        WidgetPackPaths.InvalidateResolveCache();

        var widget = new Widget("W", Path.Combine(folder, "1-0-0", "content", "widget.html"));

        Assert.Equal(WidgetDisplayKind.Widget, widget.GetDisplayKind());
    }

    [Fact]
    public void GetDisplayKind_LooseHtml_IsUnpackedWidget()
    {
        using var ws = new TempWorkspace("displaykind");
        var widget = new Widget("W", ws.WriteFile("loose/widget.html", "<html></html>"));

        Assert.Equal(WidgetDisplayKind.UnpackedWidget, widget.GetDisplayKind());
    }

    [Fact]
    public void GetDisplayKind_ImageInsideAPack_StillReportsImage()
    {
        using var ws = new TempWorkspace("displaykind");
        string folder = Path.Combine(WidgetPackPaths.PackedRoot, "wolf.widgets.timer");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "1-0-0.smw"), "");
        WidgetPackPaths.InvalidateResolveCache();

        var widget = new Widget("W", Path.Combine(folder, "1-0-0", "content", "logo.png"))
        {
            Type = WidgetType.Image
        };

        Assert.Equal(WidgetDisplayKind.Image, widget.GetDisplayKind());
    }

    [Fact]
    public void GetDisplayKind_LabelsRoundTrip()
    {
        using var ws = new TempWorkspace("displaykind");
        var widget = new Widget("W", ws.WriteFile("loose/widget.html", "<html></html>"));

        Assert.Equal("Unpacked Widget", widget.GetDisplayKind().GetLabel());
    }
}
