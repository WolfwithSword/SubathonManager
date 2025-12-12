using Moq;
using System.Reflection;

namespace SubathonManager.Tests.CoreUnitTests;

public class UtilsTests
{

    public UtilsTests()
    {
    }

    [Fact]
    public void ParseDurationString()
    {
        Assert.Equal(TimeSpan.Zero, Utils.ParseDurationString(string.Empty));
        Assert.Equal(TimeSpan.Zero, Utils.ParseDurationString(" "));
        Assert.Equal(TimeSpan.FromHours(1), Utils.ParseDurationString("1:00:00"));
        Assert.Equal(TimeSpan.FromHours(1), Utils.ParseDurationString("1h"));
        
        Assert.Equal(TimeSpan.FromSeconds(3 + (7*60) + (3*60*60)), Utils.ParseDurationString("3h 7m3s"));
        Assert.Equal(TimeSpan.FromSeconds(3 + (7*60) + (3*60*60)), Utils.ParseDurationString("3:07:03"));
        Assert.Equal(TimeSpan.FromSeconds((5*60) + (1*60*60) + (9*24*60*60)),
            Utils.ParseDurationString("9.1:05:00"));
        Assert.Equal(TimeSpan.FromSeconds((5*60) + (1*60*60) + (9*24*60*60)),
            Utils.ParseDurationString("9d5m1h"));
        
        Assert.Equal(TimeSpan.FromSeconds(10), Utils.ParseDurationString("10"));
        
        Assert.Equal(TimeSpan.Zero, Utils.ParseDurationString("10q"));
        Assert.Equal(TimeSpan.Zero, Utils.ParseDurationString("abcd1234"));
        Assert.Equal(TimeSpan.FromHours(1), Utils.ParseDurationString("1h 9f"));
    }

    [Fact]
    public void GenerateHashFromString()
    {
        Assert.Equal(Guid.Parse("bdc43e17-41dc-fb50-8c1c-54a72b1ec93e"),
            Utils.CreateGuidFromUniqueString("subathonmanager"));
    }

    [Fact]
    public void ParseCurrency()
    {
        Assert.Equal("USD", Utils.TryParseCurrency("USD"));
        Assert.Equal("CAD", Utils.TryParseCurrency("CAD"));
        Assert.Equal("CAD", Utils.TryParseCurrency("CA$"));
        Assert.Equal("KRW", Utils.TryParseCurrency("₩"));
        Assert.Equal("AUD", Utils.TryParseCurrency("A$"));
        Assert.Equal("PKR", Utils.TryParseCurrency("PK₨"));
        Assert.Equal("TWD", Utils.TryParseCurrency("NT$"));
        Assert.Equal("VND", Utils.TryParseCurrency("₫"));
        
        Assert.Equal("AAA", Utils.TryParseCurrency("AAA"));
        
        Assert.Equal(string.Empty, Utils.TryParseCurrency("123"));
        Assert.Equal(string.Empty, Utils.TryParseCurrency(string.Empty));
    }

    [Fact]
    public void EscapeCsvData()
    {
        Assert.Equal("Test", Utils.EscapeCsv("Test"));
        Assert.Equal("\"Test1,Test2\"", Utils.EscapeCsv("Test1,Test2"));
        Assert.Equal("\"\"\"Test1\"\"\"", Utils.EscapeCsv("\"Test1\""));
        Assert.Equal("\"Test\r\nTest2\"", Utils.EscapeCsv("Test\r\nTest2"));
    }
    
}