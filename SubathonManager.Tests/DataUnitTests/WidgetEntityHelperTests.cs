using Moq;
using IniParser.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Data;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;

namespace SubathonManager.Tests.DataUnitTests;

[Collection("ProviderOverrideTests")]
public class WidgetEntityHelperTests
{
    private static IConfig MockConfig(Dictionary<(string, string), string>? values = null)
    {
        var mock = new Mock<IConfig>();
        
        mock.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) =>
                values != null && values.TryGetValue((s, k), out var v) ? v : d);
            
        var kd0 = new KeyData("Port");
        kd0.Value = "14045";
        mock.Setup(c => c.GetSection("Server")).Returns(() =>
        {
            var kdc = new KeyDataCollection();
            kdc.AddKey(kd0);
            return kdc;
        });
        return mock.Object;
    }
    
    private static void SetupServices()
    { 
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(MockConfig());
        AppServices.Provider = services.BuildServiceProvider();
    }
    

    [Fact]
    public void LoadNewJsVariables_CreatesAndUpdatesVariables()
    {
        SetupServices();
        var widget = new Widget("TestWidget", "x.html");
        widget.JsVariables = new List<JsVariable>
        {
            new JsVariable
                { Name = "selectVar", Type = WidgetVariableType.StringSelect, Value = "b,c", WidgetId = Guid.NewGuid() }
        };

        var helper = new WidgetEntityHelper(null, null);

        var metadata = new Dictionary<string, string>
        {
            { "newVar.String", "val1" },
            { "selectVar.StringSelect", "a,b,c" },
            { "eventVar.EventTypeSelect", "InvalidEventType" },
            { "eventVar2.EventTypeSelect", $"{SubathonEventType.KoFiSub}" }
        };

        var (added, names, updated) = helper.LoadNewJsVariables(widget, metadata);

        Assert.Contains(added, v => v.Name == "newVar" && v.Value == "val1");

        Assert.Contains(updated, v => v.Name == "selectVar" && v.Value.Contains("b,c,a")); // order preserved

        Assert.Contains(added, v => v.Name == "eventVar" && v.Value == string.Empty);
        Assert.Contains(added, v => v.Name == "eventVar2" && v.Value == $"{SubathonEventType.KoFiSub}");
        AppServices.Provider = null!;
    }

    [Fact]
    public void ExtractWidgetMetadata_ReturnsCorrectDictionary()
    {
        SetupServices();
        string tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, @"
            <!-- WIDGET_META
            key1: value1
            key2:value2
            invalidLine
            key3: value3
            END_WIDGET_META -->
        ");

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
        await File.WriteAllTextAsync(tempFile, @"
            <!-- WIDGET_META
            keyA: valueA
            END_WIDGET_META -->
        ");

        var helper = new WidgetEntityHelper(null, null);
        var dict = await helper.ExtractWidgetMetadata(tempFile);

        Assert.Single(dict);
        Assert.Equal("valueA", dict["keyA"]);

        File.Delete(tempFile);
        AppServices.Provider = null!;
    }
}
