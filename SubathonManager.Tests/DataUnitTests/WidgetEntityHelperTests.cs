using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Data;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Tests.Utility;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.DataUnitTests;

[Collection("ProviderOverrideTests")]
public class WidgetEntityHelperTests
{
    private static IConfig MakeMockConfig(Dictionary<(string, string), string>? values = null)
    {  
        values ??= new Dictionary<(string, string), string>();
        if (values.ContainsKey(("Server", "Port"))) 
            values[("Server", "Port")] = "14045";
        return MockConfig.MakeMockConfig(values);
    }
    
    private static IDbContextFactory<AppDbContext> SetupServices()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(MakeMockConfig());
        AppServices.Provider = services.BuildServiceProvider();
        return AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    }
    
    private static string CreateTempHtmlWithCss(string cssContent, out string cssPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        cssPath = Path.Combine(dir, "style.css");
        File.WriteAllText(cssPath, cssContent);

        var htmlPath = Path.Combine(dir, "widget.html");
        File.WriteAllText(htmlPath, $"<html><head><link rel=\"stylesheet\" href=\"style.css\"/></head></html>");

        return htmlPath;
    }
    
    private static string CreateTempHtmlWithCssAndMeta(string cssContent, string metaJson, out string cssPath)
    {
        var htmlPath = CreateTempHtmlWithCss(cssContent, out cssPath);
        File.WriteAllText(cssPath + ".json", metaJson);
        return htmlPath;
    }
    
    private static JsonElement ToJsonElement<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonDocument.Parse(json).RootElement;
    }

    private static async Task<Widget> SeedWidgetAsync(IDbContextFactory<AppDbContext> factory)
    {
        var widget = new Widget("W", "w.html")
        {
            X = 0f, Y = 0f, Z = 0,
            ScaleX = 1f, ScaleY = 1f
        };
        await using var db = await factory.CreateDbContextAsync();
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        return widget;
    }

    [Fact]
    public void LoadNewJsVariables_CreatesAndUpdatesVariables()
    {
        SetupServices();
        var widget = new Widget("TestWidget", "x.html");
        widget.JsVariables =
        [
            new JsVariable
                { Name = "selectVar", Type = WidgetVariableType.StringSelect, Value = "b,c", WidgetId = Guid.NewGuid() }
        ];

        var helper = new WidgetEntityHelper(null, null);

        var metadata = new Dictionary<string, string>
        {
            { "newVar.String", "val1" },
            { "selectVar.StringSelect", "a,b,c" },
            { "eventVar.EventTypeSelect", "InvalidEventType" },
            { "eventVar2.EventTypeSelect", $"{SubathonEventType.KoFiSub}" }
        };

        var (added, names, updated) = helper.LoadNewJsVariables(widget, metadata);

        // new vars
        Assert.Contains(added, v => v is { Name: "newVar", Value: "val1" });

        Assert.Contains(updated, v => v.Name == "selectVar" && v.Value.Contains("b,c,a")); // order preserved, a appended

        Assert.Contains(added, v => v is { Name: "eventVar", Value: "" });
        Assert.Contains(added, v => v.Name == "eventVar2" && v.Value == $"{SubathonEventType.KoFiSub}");
        AppServices.Provider = null!;
    }

    [Fact]
    public void ExtractWidgetMetadata_ReturnsCorrectDictionary()
    {
        SetupServices();
        string tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, """
                                    
                                                <!-- WIDGET_META
                                                key1: value1
                                                key2:value2
                                                invalidLine
                                                key3: value3
                                                END_WIDGET_META -->
                                                
                                    """);

        var helper = new WidgetEntityHelper(null, null);
        var dict = helper.ExtractWidgetMetadataSync(tempFile);

        Assert.Equal(3, dict.Count);
        Assert.Equal("value1", dict["key1"]);
        Assert.Equal("value2", dict["key2"]);
        Assert.Equal("value3", dict["key3"]);

        File.Delete(tempFile);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task ExtractWidgetMetadataAsync_ReturnsCorrectDictionary()
    {
        SetupServices();
        string tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, """

                                                           <!-- WIDGET_META
                                                           keyA: valueA
                                                           END_WIDGET_META -->
                                                       
                                               """, TestContext.Current.CancellationToken);

        var helper = new WidgetEntityHelper(null, null);
        var dict = await helper.ExtractWidgetMetadata(tempFile);

        Assert.Single(dict);
        Assert.Equal("valueA", dict["keyA"]);

        File.Delete(tempFile);
        AppServices.Provider = null!;
    }
    
    [Fact]
    public void SyncCssVariables_AddsNewVariables()
    {
        var factory = SetupServices();
        var htmlPath = CreateTempHtmlWithCss("--test-color: #fff;", out _);

        var widget = new Widget("W", htmlPath);
        using var db = factory.CreateDbContext();
        db.Widgets.Add(widget);
        db.SaveChanges();

        var helper = new WidgetEntityHelper(factory, null);
        helper.SyncCssVariables(widget);

        using var db2 = factory.CreateDbContext();
        var vars = db2.CssVariables.Where(v => v.WidgetId == widget.Id).ToList();
        Assert.Single(vars);
        Assert.Equal("test-color", vars[0].Name);
        Assert.Equal("#fff", vars[0].Value);

        AppServices.Provider = null!;
    }

    [Fact]
    public void SyncCssVariables_RemovesStaleVariables()
    {
        var factory = SetupServices();
        var htmlPath = CreateTempHtmlWithCss("--keep: red;", out _);

        var widget = new Widget("W", htmlPath);
        using var db = factory.CreateDbContext();
        db.Widgets.Add(widget);
        
        // pre-seed
        db.CssVariables.Add(new CssVariable { Name = "stale-var", Value = "blue", WidgetId = widget.Id });
        db.SaveChanges();

        var helper = new WidgetEntityHelper(factory, null);
        helper.SyncCssVariables(widget);

        using var db2 = factory.CreateDbContext();
        var vars = db2.CssVariables.Where(v => v.WidgetId == widget.Id).ToList();
        Assert.DoesNotContain(vars, v => v.Name == "stale-var");
        Assert.Contains(vars, v => v.Name == "keep");

        AppServices.Provider = null!;
    }
    
    [Fact]
    public void SyncCssVariables_UpdatesTypeAndDescription_WhenChanged()
    {
        var factory = SetupServices();
        var metaJson = """
                       {
                           "my-var": { "type": "Color", "description": "Primary color" }
                       }
                       """;
        var htmlPath = CreateTempHtmlWithCssAndMeta("--my-var: #000;", metaJson, out _);

        var widget = new Widget("W", htmlPath);
        using var db = factory.CreateDbContext();
        db.Widgets.Add(widget);
        
        // existing with diff type/desc, to update
        db.CssVariables.Add(new CssVariable
        {
            Name = "my-var",
            Value = "#000",
            WidgetId = widget.Id,
            Type = WidgetCssVariableType.Default,
            Description = string.Empty
        });
        db.SaveChanges();

        var helper = new WidgetEntityHelper(factory, null);
        helper.SyncCssVariables(widget);

        using var db2 = factory.CreateDbContext();
        var updated = db2.CssVariables.FirstOrDefault(v => v.WidgetId == widget.Id && v.Name == "my-var");
        Assert.NotNull(updated);
        Assert.Equal(WidgetCssVariableType.Color, updated.Type);
        Assert.Equal("Primary color", updated.Description);

        AppServices.Provider = null!;
    }

    [Fact]
    public void SyncCssVariables_DeduplicatesVariables()
    {
        var factory = SetupServices();
        var htmlPath = CreateTempHtmlWithCss("--dup: 1px;", out _);

        var widget = new Widget("W", htmlPath);
        using var db = factory.CreateDbContext();
        db.Widgets.Add(widget);
        
        // sim dupe
        db.CssVariables.Add(new CssVariable { Name = "dup", Value = "1px", WidgetId = widget.Id });
        db.CssVariables.Add(new CssVariable { Name = "dup", Value = "1px", WidgetId = widget.Id });
        db.SaveChanges();

        var helper = new WidgetEntityHelper(factory, null);
        helper.SyncCssVariables(widget);

        using var db2 = factory.CreateDbContext();
        var vars = db2.CssVariables.Where(v => v.WidgetId == widget.Id && v.Name == "dup").ToList();
        Assert.Single(vars);

        AppServices.Provider = null!;
    }

    [Fact]
    public void SyncCssVariables_NoHtmlFile_DoesNotThrow()
    {
        var factory = SetupServices();
        var widget = new Widget("W", "/nonexistent/path_to/widget.html");
        using var db = factory.CreateDbContext();
        db.Widgets.Add(widget);
        db.SaveChanges();

        var helper = new WidgetEntityHelper(factory, null);
        var ex = Record.Exception(() => helper.SyncCssVariables(widget));
        Assert.Null(ex);

        AppServices.Provider = null!;
    }
    
    private static string CreateTempHtmlWithMeta(string metaBlock)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, $"<!-- WIDGET_META\n{metaBlock}\nEND_WIDGET_META -->");
        return path;
    }

    [Fact]
    public void SyncJsVariables_AddsNewVariables()
    {
        var factory = SetupServices();
        var htmlPath = CreateTempHtmlWithMeta("myVar.String:hello");

        var widget = new Widget("W", htmlPath);
        using var db = factory.CreateDbContext();
        db.Widgets.Add(widget);
        db.SaveChanges();

        var helper = new WidgetEntityHelper(factory, null);
        helper.SyncJsVariables(widget);

        using var db2 = factory.CreateDbContext();
        var vars = db2.JsVariables.Where(v => v.WidgetId == widget.Id).ToList();
        Assert.Single(vars);
        Assert.Equal("myVar", vars[0].Name);
        Assert.Equal("hello", vars[0].Value);

        File.Delete(htmlPath);
        AppServices.Provider = null!;
    }

    [Fact]
    public void SyncJsVariables_RemovesStaleVariables()
    {
        var factory = SetupServices();
        var htmlPath = CreateTempHtmlWithMeta("keepVar.String:yes");

        var widget = new Widget("W", htmlPath);
        using var db = factory.CreateDbContext();
        db.Widgets.Add(widget);
        db.JsVariables.Add(new JsVariable { Name = "oldVar", Value = "old", WidgetId = widget.Id });
        db.SaveChanges();

        var helper = new WidgetEntityHelper(factory, null);
        helper.SyncJsVariables(widget);

        using var db2 = factory.CreateDbContext();
        var vars = db2.JsVariables.Where(v => v.WidgetId == widget.Id).ToList();
        Assert.DoesNotContain(vars, v => v.Name == "oldVar");
        Assert.Contains(vars, v => v.Name == "keepVar");

        File.Delete(htmlPath);
        AppServices.Provider = null!;
    }

    [Fact]
    public void SyncJsVariables_UpdatesDocsUrl_WhenValidUrlInMeta()
    {
        var factory = SetupServices();
        var htmlPath = CreateTempHtmlWithMeta("Url: https://example.com/docs\nmyVar.String: val");

        var widget = new Widget("W", htmlPath) { DocsUrl = string.Empty };
        using var db = factory.CreateDbContext();
        db.Widgets.Add(widget);
        db.SaveChanges();

        var helper = new WidgetEntityHelper(factory, null);
        helper.SyncJsVariables(widget);

        Assert.Equal("https://example.com/docs", widget.DocsUrl);

        File.Delete(htmlPath);
        AppServices.Provider = null!;
    }

    [Fact]
    public void SyncJsVariables_DoesNotUpdateDocsUrl_WhenInvalid()
    {
        var factory = SetupServices();
        var htmlPath = CreateTempHtmlWithMeta("Url: not-a-url\nmyVar.String:val");

        var widget = new Widget("W", htmlPath) { DocsUrl = "https://example.com" };
        using var db = factory.CreateDbContext();
        db.Widgets.Add(widget);
        db.SaveChanges();

        var helper = new WidgetEntityHelper(factory, null);
        helper.SyncJsVariables(widget);

        Assert.Equal("https://example.com", widget.DocsUrl);

        File.Delete(htmlPath);
        AppServices.Provider = null!;
    }

    [Fact]
    public void SyncJsVariables_UpdatesStringSelectValues_PreservingOrder()
    {
        var factory = SetupServices();
        var htmlPath = CreateTempHtmlWithMeta("selectVar.StringSelect:a,b,c");

        var widget = new Widget("W", htmlPath);
        var existingVar = new JsVariable
        {
            Name = "selectVar",
            Type = WidgetVariableType.StringSelect,
            Value = "c,b",
            WidgetId = widget.Id
        };
        widget.JsVariables.Add(existingVar);

        using var db = factory.CreateDbContext();
        db.Widgets.Add(widget);
        db.JsVariables.Add(existingVar);
        db.SaveChanges();

        var helper = new WidgetEntityHelper(factory, null);
        helper.SyncJsVariables(widget);

        using var db2 = factory.CreateDbContext();
        var updated = db2.JsVariables.FirstOrDefault(v => v.WidgetId == widget.Id && v.Name == "selectVar");
        Assert.NotNull(updated);
        
        // a shifted to end when added, order preserved for selected value first
        Assert.Equal("c,b,a", updated.Value);

        File.Delete(htmlPath);
        AppServices.Provider = null!;
    }

    [Fact]
    public void SyncJsVariables_DeduplicatesVariables()
    {
        var factory = SetupServices();
        var htmlPath = CreateTempHtmlWithMeta("dupVar.String:x");

        var widget = new Widget("W", htmlPath);
        using var db = factory.CreateDbContext();
        db.Widgets.Add(widget);
        db.JsVariables.Add(new JsVariable { Name = "dupVar", Value = "x", WidgetId = widget.Id });
        db.JsVariables.Add(new JsVariable { Name = "dupVar", Value = "x", WidgetId = widget.Id });
        db.SaveChanges();

        var helper = new WidgetEntityHelper(factory, null);
        helper.SyncJsVariables(widget);

        using var db2 = factory.CreateDbContext();
        var vars = db2.JsVariables.Where(v => v.WidgetId == widget.Id && v.Name == "dupVar").ToList();
        Assert.Single(vars);

        File.Delete(htmlPath);
        AppServices.Provider = null!;
    }

    [Fact]
    public void SyncJsVariables_NoneValue_StoredAsEmpty()
    {
        var factory = SetupServices();
        var htmlPath = CreateTempHtmlWithMeta("myVar.String:NONE");

        var widget = new Widget("W", htmlPath);
        using var db = factory.CreateDbContext();
        db.Widgets.Add(widget);
        db.SaveChanges();

        var helper = new WidgetEntityHelper(factory, null);
        helper.SyncJsVariables(widget);

        using var db2 = factory.CreateDbContext();
        var v = db2.JsVariables.FirstOrDefault(x => x.WidgetId == widget.Id && x.Name == "myVar");
        Assert.NotNull(v);
        Assert.Equal(string.Empty, v.Value);

        File.Delete(htmlPath);
        AppServices.Provider = null!;
    }
    
    
    [Fact]
    public async Task UpdateWidgetPosition_ReturnsFalse_WhenDataIsEmpty()
    {
        var factory = SetupServices();
        var widget = await SeedWidgetAsync(factory);
        var helper = new WidgetEntityHelper(factory, null);

        var result = await helper.UpdateWidgetPosition(widget.Id.ToString(), new Dictionary<string, JsonElement>());

        Assert.False(result);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task UpdateWidgetPosition_ReturnsFalse_WhenGuidIsInvalid()
    {
        var factory = SetupServices();
        var helper = new WidgetEntityHelper(factory, null);
        var data = new Dictionary<string, JsonElement> { { "x", ToJsonElement(10f) } };

        var result = await helper.UpdateWidgetPosition("not-a-guid", data);

        Assert.False(result);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task UpdateWidgetPosition_ReturnsFalse_WhenWidgetNotFound()
    {
        var factory = SetupServices();
        var helper = new WidgetEntityHelper(factory, null);
        var data = new Dictionary<string, JsonElement> { { "x", ToJsonElement(10f) } };

        var result = await helper.UpdateWidgetPosition(Guid.NewGuid().ToString(), data);

        Assert.False(result);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task UpdateWidgetPosition_UpdatesX_WhenProvided()
    {
        var factory = SetupServices();
        var widget = await SeedWidgetAsync(factory);
        var helper = new WidgetEntityHelper(factory, null);
        var data = new Dictionary<string, JsonElement> { { "x", ToJsonElement(99f) } };

        var result = await helper.UpdateWidgetPosition(widget.Id.ToString(), data);

        Assert.True(result);
        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var updated = await db.Widgets.FindAsync([widget.Id], TestContext.Current.CancellationToken);
        Assert.Equal(99f, updated!.X);
        Assert.Equal(0f, updated.Y);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task UpdateWidgetPosition_UpdatesY_WhenProvided()
    {
        var factory = SetupServices();
        var widget = await SeedWidgetAsync(factory);
        var helper = new WidgetEntityHelper(factory, null);
        var data = new Dictionary<string, JsonElement> { { "y", ToJsonElement(42f) } };

        var result = await helper.UpdateWidgetPosition(widget.Id.ToString(), data);

        Assert.True(result);
        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var updated = await db.Widgets.FindAsync([widget.Id], TestContext.Current.CancellationToken);
        Assert.Equal(0f, updated!.X);
        Assert.Equal(42f, updated.Y);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task UpdateWidgetPosition_UpdatesZ_WhenProvided()
    {
        var factory = SetupServices();
        var widget = await SeedWidgetAsync(factory);
        var helper = new WidgetEntityHelper(factory, null);
        var data = new Dictionary<string, JsonElement> { { "z", ToJsonElement(5) } };

        var result = await helper.UpdateWidgetPosition(widget.Id.ToString(), data);

        Assert.True(result);
        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var updated = await db.Widgets.FindAsync(new object?[] { widget.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(5, updated!.Z);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task UpdateWidgetPosition_UpdatesAllFields_WhenAllProvided()
    {
        var factory = SetupServices();
        var widget = await SeedWidgetAsync(factory);
        var helper = new WidgetEntityHelper(factory, null);
        var data = new Dictionary<string, JsonElement>
        {
            { "x", ToJsonElement(10f) },
            { "y", ToJsonElement(20f) },
            { "z", ToJsonElement(3) }
        };

        var result = await helper.UpdateWidgetPosition(widget.Id.ToString(), data);

        Assert.True(result);
        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var updated = await db.Widgets.FindAsync([widget.Id], TestContext.Current.CancellationToken);
        Assert.Equal(10f, updated!.X);
        Assert.Equal(20f, updated.Y);
        Assert.Equal(3, updated.Z);
        AppServices.Provider = null!;
    }
    [Fact]
    public async Task UpdateWidgetScale_IgnoresUnrecognisedKeys()
    {
        var factory = SetupServices();
        var widget = await SeedWidgetAsync(factory);
        var helper = new WidgetEntityHelper(factory, null);
        var data = new Dictionary<string, JsonElement>
        {
            { "scaleX", ToJsonElement(2f) },
            { "unknown", ToJsonElement("surprise") }
        };

        var result = await helper.UpdateWidgetScale(widget.Id.ToString(), data);

        Assert.True(result);
        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var updated = await db.Widgets.FindAsync(new object?[] { widget.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(2f, updated!.ScaleX);
        AppServices.Provider = null!;
    }
    
    [Fact]
    public async Task UpdateWidgetScale_ReturnsFalse_WhenDataIsEmpty()
    {
        var factory = SetupServices();
        var widget = await SeedWidgetAsync(factory);
        var helper = new WidgetEntityHelper(factory, null);

        var result = await helper.UpdateWidgetScale(widget.Id.ToString(), new Dictionary<string, JsonElement>());

        Assert.False(result);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task UpdateWidgetScale_ReturnsFalse_WhenGuidIsInvalid()
    {
        var factory = SetupServices();
        var helper = new WidgetEntityHelper(factory, null);
        var data = new Dictionary<string, JsonElement> { { "scaleX", ToJsonElement(2.0f) } };

        var result = await helper.UpdateWidgetScale("not-a-guid", data);

        Assert.False(result);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task UpdateWidgetScale_ReturnsFalse_WhenWidgetNotFound()
    {
        var factory = SetupServices();
        var helper = new WidgetEntityHelper(factory, null);
        var data = new Dictionary<string, JsonElement> { { "scaleX", ToJsonElement(2.0f) } };

        var result = await helper.UpdateWidgetScale(Guid.NewGuid().ToString(), data);

        Assert.False(result);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task UpdateWidgetScale_UpdatesScaleX_WhenProvided()
    {
        var factory = SetupServices();
        var widget = await SeedWidgetAsync(factory);
        var helper = new WidgetEntityHelper(factory, null);
        var data = new Dictionary<string, JsonElement> { { "scaleX", ToJsonElement(3.5f) } };

        var result = await helper.UpdateWidgetScale(widget.Id.ToString(), data);

        Assert.True(result);
        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var updated = await db.Widgets.FindAsync(new object?[] { widget.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(3.5f, updated!.ScaleX);
        Assert.Equal(1f, updated.ScaleY);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task UpdateWidgetScale_UpdatesScaleY_WhenProvided()
    {
        var factory = SetupServices();
        var widget = await SeedWidgetAsync(factory);
        var helper = new WidgetEntityHelper(factory, null);
        var data = new Dictionary<string, JsonElement> { { "scaleY", ToJsonElement(2.0f) } };

        var result = await helper.UpdateWidgetScale(widget.Id.ToString(), data);

        Assert.True(result);
        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var updated = await db.Widgets.FindAsync([widget.Id], TestContext.Current.CancellationToken);
        Assert.Equal(1f, updated!.ScaleX);
        Assert.Equal(2.0f, updated.ScaleY);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task UpdateWidgetScale_UpdatesXAndY_WhenProvided()
    {
        var factory = SetupServices();
        var widget = await SeedWidgetAsync(factory);
        var helper = new WidgetEntityHelper(factory, null);
        var data = new Dictionary<string, JsonElement>
        {
            { "x", ToJsonElement(100f) },
            { "y", ToJsonElement(200f) }
        };

        var result = await helper.UpdateWidgetScale(widget.Id.ToString(), data);

        Assert.True(result);
        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var updated = await db.Widgets.FindAsync([widget.Id], TestContext.Current.CancellationToken);
        Assert.Equal(100f, updated!.X);
        Assert.Equal(200f, updated.Y);
        AppServices.Provider = null!;
    }

    [Fact]
    public async Task UpdateWidgetScale_UpdatesAllFields_WhenAllProvided()
    {
        var factory = SetupServices();
        var widget = await SeedWidgetAsync(factory);
        var helper = new WidgetEntityHelper(factory, null);
        var data = new Dictionary<string, JsonElement>
        {
            { "scaleX", ToJsonElement(1.5f) },
            { "scaleY", ToJsonElement(2.5f) },
            { "x", ToJsonElement(50f) },
            { "y", ToJsonElement(75f) }
        };

        var result = await helper.UpdateWidgetScale(widget.Id.ToString(), data);

        Assert.True(result);
        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var updated = await db.Widgets.FindAsync([widget.Id], TestContext.Current.CancellationToken);
        Assert.Equal(1.5f, updated!.ScaleX);
        Assert.Equal(2.5f, updated.ScaleY);
        Assert.Equal(50f, updated.X);
        Assert.Equal(75f, updated.Y);
        AppServices.Provider = null!;
    }
}
