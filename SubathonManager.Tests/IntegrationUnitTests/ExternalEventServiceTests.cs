using System.Text.Json;
using SubathonManager.Core.Events;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Integration;
using System.Reflection;
using Moq;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("IntegrationEventTests")]
public class ExternalEventServiceTests
{
    public ExternalEventServiceTests()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    [Fact]
    public void ProcessExternalCommand_ShouldReturnFalse_WhenCommandMissing()
    {
        var data = new Dictionary<string, JsonElement>();

        bool result = ExternalEventService.ProcessExternalCommand(data);

        Assert.False(result);
    }
    
    [Fact]
    public void ProcessExternalCommand_ShouldRaiseEvent_WhenValidCommand()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        SubathonEvent? ev = null;
        Action<SubathonEvent> handler = e => ev = e;
        SubathonEvents.SubathonEventCreated += handler;

        var json = @"{
            ""command"": ""Pause"",
            ""user"": ""Tester"",
            ""message"": """"
        }";

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        bool result = ExternalEventService.ProcessExternalCommand(data);

        Assert.True(result);
        Assert.NotNull(ev);
        Assert.Equal("Tester", ev!.User);
        Assert.Equal("Pause", ev.Value);
        Assert.Equal(SubathonEventSource.External, ev.Source);
        Assert.Equal(SubathonCommandType.Pause, ev.Command);
        Assert.Equal(SubathonEventType.Command, ev.EventType);
        SubathonEvents.SubathonEventCreated -= handler;
    }
    
    [Fact]
    public void ProcessExternalCommand_ShouldRaiseEvent_WhenValidCommandWithParam()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        SubathonEvent? ev = null;
        Action<SubathonEvent> handler = e => ev = e;
        SubathonEvents.SubathonEventCreated += handler;

        var json = @"{
            ""command"": ""AddPoints"",
            ""user"": ""Tester"",
            ""message"": ""5""
        }";

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        bool result = ExternalEventService.ProcessExternalCommand(data);

        Assert.True(result);
        Assert.NotNull(ev);
        Assert.Equal("Tester", ev!.User);
        Assert.Equal("AddPoints 5", ev.Value);
        Assert.Equal(SubathonEventSource.External, ev.Source);
        Assert.Equal(SubathonCommandType.AddPoints, ev.Command);
        Assert.Equal(SubathonEventType.Command, ev.EventType);
        SubathonEvents.SubathonEventCreated -= handler;
    }

    [Fact]
    public void ProcessExternalCommand_ShouldRaiseEvent_WhenValidCommandWithParam2()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        SubathonEvent? ev = null;
        Action<SubathonEvent> handler = e => ev = e;
        SubathonEvents.SubathonEventCreated += handler;

        var json = @"{
            ""command"": ""SubtractTime"",
            ""user"": ""Tester"",
            ""message"": ""5h 2m5s""
        }";

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        bool result = ExternalEventService.ProcessExternalCommand(data);

        Assert.True(result);
        Assert.NotNull(ev);
        Assert.Equal("Tester", ev!.User);
        Assert.Equal("SubtractTime 5h 2m5s", ev.Value);
        Assert.Equal(SubathonEventSource.External, ev.Source);
        Assert.Equal(SubathonCommandType.SubtractTime, ev.Command);
        Assert.Equal(SubathonEventType.Command, ev.EventType);
        SubathonEvents.SubathonEventCreated -= handler;
    }
        
    [Fact]
    public void ProcessExternalCommand_ShouldRaiseEvent_WhenValidCommandWithParam3()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        SubathonEvent? ev = null;
        Action<SubathonEvent> handler = e => ev = e;
        SubathonEvents.SubathonEventCreated += handler;

        var json = @"{
            ""command"": ""SetMultiplier"",
            ""user"": ""Tester"",
            ""message"": ""2.3xpt 1h""
        }";

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        bool result = ExternalEventService.ProcessExternalCommand(data);

        Assert.True(result);
        Assert.NotNull(ev);
        Assert.Equal("Tester", ev!.User);
        Assert.Equal("2.3|3600s|True|True", ev.Value);
        Assert.Equal(SubathonEventSource.External, ev.Source);
        Assert.Equal(SubathonCommandType.SetMultiplier, ev.Command);
        Assert.Equal(SubathonEventType.Command, ev.EventType);
        SubathonEvents.SubathonEventCreated -= handler;
    }
    
    [Fact]
    public void ProcessExternalCommand_ShouldNotRaiseEvent_InvalidCommand()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        SubathonEvent? ev = null;
        Action<SubathonEvent> handler = e => ev = e;
        SubathonEvents.SubathonEventCreated += handler;

        var json = @"{
            ""command"": ""START"",
            ""user"": ""Tester"",
            ""message"": ""Hello""
        }";

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        bool result = ExternalEventService.ProcessExternalCommand(data);

        Assert.False(result);
        Assert.Null(ev);
        SubathonEvents.SubathonEventCreated -= handler;
    }
    
         
    [Fact]
    public void ProcessExternalCommand_ShouldRaiseEvent_WhenInvalidCommandWithParam2()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        SubathonEvent? ev = null;
        Action<SubathonEvent> handler = e => ev = e;
        SubathonEvents.SubathonEventCreated += handler;

        var json = @"{
            ""command"": ""SetMultiplier"",
            ""user"": ""Tester"",
            ""message"": ""2.3x 1h""
        }";

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        bool result = ExternalEventService.ProcessExternalCommand(data);

        Assert.True(result);
        Assert.NotNull(ev);
        Assert.Equal("Tester", ev!.User);
        Assert.Equal("SetMultiplier Failed", ev.Value);
        Assert.Equal(SubathonEventSource.External, ev.Source);
        Assert.Equal(SubathonCommandType.StopMultiplier, ev.Command);
        Assert.Equal(SubathonEventType.Command, ev.EventType);
        SubathonEvents.SubathonEventCreated -= handler;
    }

    [Fact]
    public void ProcessExternalSub_ShouldRaiseEvent_WithDefaults()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        SubathonEvent? ev = null;
        Action<SubathonEvent> handler = e => ev = e;
        SubathonEvents.SubathonEventCreated += handler;

        var json = @"{
            ""type"": ""ExternalSub"",
            ""user"": """",
            ""value"": ""subt1"",
            ""amount"": 3,
            ""seconds"": 120,
            ""points"": 10,
            ""id"": ""b3e1f7e2-1234-4a5b-9e8f-123456789abc""
        }";

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        bool result = ExternalEventService.ProcessExternalSub(data);

        Assert.True(result);
        Assert.NotNull(ev);
        Assert.Equal("EXTERNAL", ev!.User);
        Assert.Equal("subt1", ev.Value);
        Assert.Equal(3, ev.Amount);
        Assert.Equal(120, ev.SecondsValue);
        Assert.Equal(10, ev.PointsValue);
        Assert.Equal(SubathonEventSource.External, ev.Source);
        Assert.Equal(SubathonEventType.ExternalSub, ev.EventType);
        Assert.Equal(Guid.Parse("b3e1f7e2-1234-4a5b-9e8f-123456789abc"), ev.Id);

        SubathonEvents.SubathonEventCreated -= handler;
    }
    
    
    [Fact]
    public void ProcessKoFiSub_ShouldRaiseEvent_WithDefaults()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        SubathonEvent? ev = null;
        Action<SubathonEvent> handler = e => ev = e;
        SubathonEvents.SubathonEventCreated += handler;

        var json = @"{
            ""type"": ""KoFiSub"",
            ""user"": ""Jo Bob"",
            ""value"": ""Silver"",
            ""amount"": 1,
            ""id"": ""b3e1f7e2-1234-4a5b-9e8f-123456789abc""
        }";

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        bool result = ExternalEventService.ProcessExternalSub(data);

        Assert.True(result);
        Assert.NotNull(ev);
        Assert.Equal("Jo Bob", ev!.User);
        Assert.Equal("Silver", ev.Value);
        Assert.Equal(1, ev.Amount);
        Assert.Equal(SubathonEventSource.KoFi, ev.Source);
        Assert.Equal(SubathonEventType.KoFiSub, ev.EventType);
        Assert.Equal(Guid.Parse("b3e1f7e2-1234-4a5b-9e8f-123456789abc"), ev.Id);

        SubathonEvents.SubathonEventCreated -= handler;
    }

    [Fact]
    public void ProcessExternalDonation_ShouldRaiseEvent_WithValidData()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        SubathonEvent? ev = null;
        Action<SubathonEvent> handler = e => ev = e;
        SubathonEvents.SubathonEventCreated += handler;

        var json = @"{
            ""type"": ""ExternalDonation"",
            ""user"": ""Donor"",
            ""currency"": ""AUD"",
            ""amount"": ""12.77"",
            ""id"": ""c1e2d3f4-5678-4abc-9def-987654321abc""
        }";

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        bool result = ExternalEventService.ProcessExternalDonation(data);

        Assert.True(result);
        Assert.NotNull(ev);
        Assert.Equal("Donor", ev!.User);
        Assert.Equal("12.77", ev.Value);
        Assert.Equal("AUD", ev.Currency);
        Assert.Equal(SubathonEventSource.External, ev.Source);
        Assert.Equal(SubathonEventType.ExternalDonation, ev.EventType);
        Assert.Equal(Guid.Parse("c1e2d3f4-5678-4abc-9def-987654321abc"), ev.Id);

        SubathonEvents.SubathonEventCreated -= handler;
    }
    
    [Fact]
    public void ProcessKoFiDonation_ShouldRaiseEvent_WithValidData()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        SubathonEvent? ev = null;
        Action<SubathonEvent> handler = e => ev = e;
        SubathonEvents.SubathonEventCreated += handler;

        var json = @"{
            ""type"": ""KoFiDonation"",
            ""user"": ""Donor"",
            ""currency"": ""TWD"",
            ""amount"": ""778"",
            ""id"": ""c1e2d3f4-5678-4abc-9def-987654321abc""
        }";

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        bool result = ExternalEventService.ProcessExternalDonation(data);

        Assert.True(result);
        Assert.NotNull(ev);
        Assert.Equal("Donor", ev!.User);
        Assert.Equal("778", ev.Value);
        Assert.Equal("TWD", ev.Currency);
        Assert.Equal(SubathonEventSource.KoFi, ev.Source);
        Assert.Equal(SubathonEventType.KoFiDonation, ev.EventType);
        Assert.Equal(Guid.Parse("c1e2d3f4-5678-4abc-9def-987654321abc"), ev.Id);

        SubathonEvents.SubathonEventCreated -= handler;
    }

    [Fact]
    public void ProcessExternalDonation_ShouldReturnFalse_WhenAmountInvalid()
    {
        var json = @"{
            ""type"": ""ExternalDonation"",
            ""user"": ""Donor"",
            ""currency"": ""USD"",
            ""amount"": ""notanumber""
        }";

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        bool result = ExternalEventService.ProcessExternalDonation(data);

        Assert.False(result);
    }
}
