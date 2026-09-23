using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Integration;
using SubathonManager.Tests.Utility;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("GlobalState")]
public class MixItUpServiceTests {
    private static readonly Guid CommandId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public MixItUpServiceTests() {
        foreach (string field in new[] { "ConnectionUpdated", "ExternalSourceSeen" })
            typeof(IntegrationEvents).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, null);
        foreach (string field in new[] { "SubathonDataUpdate", "PromptRunUpdate", "SubathonEventProcessed" })
            typeof(SubathonEvents).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, null);
    }

    private static string Id(string name) {
        return $"{MixItUpService.IdentifierPrefix}{name}";
    }

    private static MixItUpService MakeService(RecordingHandler? handler = null,
        Dictionary<(string, string), string>? config = null, ITimerService? timerService = null) {
        handler ??= new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        return new MixItUpService(new Mock<ILogger<MixItUpService>>().Object,
            MockConfig.MakeMockConfig(config), factory.Object, timerService ?? new Mock<ITimerService>().Object);
    }

    private static Dictionary<(string, string), string> EnabledConfig(MixItUpTrigger trigger) {
        return new Dictionary<(string, string), string> {
            [(MixItUpService.ConfigSection, "Enabled")] = "True",
            [(MixItUpService.ConfigSection, MixItUpService.CommandConfigKey(trigger))] = CommandId.ToString()
        };
    }

    private static List<IntegrationConnection> CaptureConnections(Action trigger) {
        var captured = new List<IntegrationConnection>();

        void Handler(IntegrationConnection c) {
            if (c.Source == SubathonEventSource.MixItUp) captured.Add(c);
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try {
            trigger();
        }
        finally {
            IntegrationEvents.ConnectionUpdated -= Handler;
        }

        return captured;
    }

    [Fact]
    public void BuildCommandJson_ProducesImportableActionGroup() {
        JsonObject root = JsonNode.Parse(MixItUpCommandExporter.BuildCommandJson(SubathonCommandType.AddTime, 15000))!
            .AsObject();

        Assert.Equal("$type", root.First().Key);
        Assert.Equal("MixItUp.Base.Model.Commands.ActionGroupCommandModel, MixItUp.Base",
            root["$type"]!.GetValue<string>());
        Assert.Equal("Subathon - Add Time", root["Name"]!.GetValue<string>());
        Assert.Equal(4, root["Type"]!.GetValue<int>());

        JsonObject action = root["Actions"]!.AsArray().Single()!.AsObject();
        Assert.Equal("$type", action.First().Key);
        Assert.Equal("MixItUp.Base.Model.Actions.WebRequestActionModel, MixItUp.Base",
            action["$type"]!.GetValue<string>());
        Assert.Equal("http://localhost:15000/api/data/control", action["Url"]!.GetValue<string>());
        Assert.Equal(1, action["HttpMethod"]!.GetValue<int>());
        Assert.Equal(11, action["Type"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(SubathonCommandType.AddTime, "$allargs")]
    [InlineData(SubathonCommandType.Pause, "")]
    public void BuildRequestBody_ParameterOnlyWhenRequired(SubathonCommandType command, string expectedMessage) {
        var body = JsonSerializer.Deserialize<Dictionary<string, string>>(
            MixItUpCommandExporter.BuildRequestBody(command))!;

        Assert.Equal("Command", body["type"]);
        Assert.Equal(command.ToString(), body["command"]);
        Assert.Equal(expectedMessage, body["message"]);
        Assert.Equal("$username", body["user"]);
        Assert.Equal("MixItUp", body["source"]);
    }

    [Fact]
    public void WriteAll_WritesEveryCommandAndRemovesStaleFiles() {
        string folder = Path.Combine(Path.GetTempPath(), "SubathonManagerTests", $"miu-{Guid.NewGuid():N}");
        try {
            Directory.CreateDirectory(folder);
            string stale = Path.Combine(folder, $"Old{MixItUpCommandExporter.FileExtension}");
            File.WriteAllText(stale, "{}");

            IReadOnlyList<string> written = MixItUpCommandExporter.WriteAll(folder, 14040);

            Assert.False(File.Exists(stale));
            Assert.Equal(MixItUpCommandExporter.ExportableCommands.Count(), written.Count);
            Assert.DoesNotContain(written, p => p.Contains(nameof(SubathonCommandType.None)));
            Assert.All(written, p => Assert.True(File.Exists(p)));
        }
        finally {
            try {
                Directory.Delete(folder, true);
            }
            catch {
                /**/
            }
        }
    }

    [Fact]
    public void NotifySourceSeen_RaisesForExternalSourceOnly() {
        var seen = new List<SubathonEventSource>();
        IntegrationEvents.ExternalSourceSeen += seen.Add;
        try {
            foreach (string src in new[] { "MixItUp", "Twitch", "NotASource" }) {
                var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    $$"""{ "source": "{{src}}" }""")!;
                ExternalEventService.NotifySourceSeen(data);
            }

            ExternalEventService.NotifySourceSeen(new Dictionary<string, JsonElement>());
        }
        finally {
            IntegrationEvents.ExternalSourceSeen -= seen.Add;
        }

        Assert.Equal(new[] { SubathonEventSource.MixItUp }, seen);
    }

    [Fact]
    public void MarkSeen_GoesGreen_ThenGreyWhenSeenTimerFires() {
        var timer = new Mock<ITimerService>();
        Action? expire = null;
        timer.Setup(t => t.Register("mixitup-seen", MixItUpService.SeenWindow, It.IsAny<Action>()))
            .Callback((string _, TimeSpan _, Action cb) => expire = cb)
            .Returns(Mock.Of<IDisposable>());
        MixItUpService service = MakeService(timerService: timer.Object);

        List<IntegrationConnection> seen = CaptureConnections(() => service.MarkSeen(DateTime.Now));
        Assert.Single(seen);
        Assert.True(seen[0].Status);
        Assert.True(seen[0].Configured);
        Assert.NotNull(expire);

        List<IntegrationConnection> expired = CaptureConnections(expire!);
        Assert.Single(expired);
        Assert.False(expired[0].Status);
        Assert.False(expired[0].Configured);
        Assert.False(service.Connected);
        timer.Verify(t => t.Unregister("mixitup-seen"));
    }

    [Fact]
    public void MarkSeen_AgainReRegistersWindowWithoutRebroadcasting() {
        var timer = new Mock<ITimerService>();
        MixItUpService service = MakeService(timerService: timer.Object);
        service.MarkSeen(DateTime.Now);

        List<IntegrationConnection> again = CaptureConnections(() => service.MarkSeen(DateTime.Now));

        Assert.Empty(again);
        timer.Verify(t => t.Register("mixitup-seen", MixItUpService.SeenWindow, It.IsAny<Action>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task StartAsync_RegistersProbe_StopUnregistersAll() {
        var timer = new Mock<ITimerService>();
        MixItUpService service = MakeService(timerService: timer.Object);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
        timer.Verify(t => t.Register("mixitup-probe", MixItUpService.ProbeInterval, It.IsAny<Action>()));
        timer.Verify(t => t.Unregister("mixitup-probe"));
        timer.Verify(t => t.Unregister("mixitup-seen"));
    }

    [Fact]
    public async Task ProbeAsync_Success_MarksSeenWithVersion() {
        var handler = new RecordingHandler(HttpStatusCode.OK, "\"1.2.3.4\"");
        MixItUpService service = MakeService(handler);

        bool ok = await service.ProbeAsync(TestContext.Current.CancellationToken);
        Assert.True(ok);
        Assert.True(service.Connected);
        Assert.Equal("1.2.3.4", service.Version);
        Assert.EndsWith("/api/v2/status/version", handler.Requests.Single().Uri);
        service.Dispose();
    }

    [Fact]
    public async Task ProbeAsync_Unreachable_FailsSilently() {
        MixItUpService service = MakeService(new RecordingHandler(HttpStatusCode.OK, throwConnect: true));

        List<IntegrationConnection> updates = [];
        bool ok = true;
        Exception? ex = await Record.ExceptionAsync(async () => {
            IntegrationEvents.ConnectionUpdated += updates.Add;
            try {
                ok = await service.ProbeAsync(TestContext.Current.CancellationToken);
            }
            finally {
                IntegrationEvents.ConnectionUpdated -= updates.Add;
            }
        });

        Assert.Null(ex);
        Assert.False(ok);
        Assert.False(service.Connected);
        Assert.Empty(updates);
    }

    [Theory]
    [InlineData("\"1.0.0\"", "1.0.0")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("", null)]
    public void ParseVersion_HandlesJsonAndRaw(string body, string? expected) {
        Assert.Equal(expected, MixItUpService.ParseVersion(body));
    }

    [Fact]
    public void Fire_Disabled_DoesNothing() {
        Dictionary<(string, string), string> config = EnabledConfig(MixItUpTrigger.GoalCompleted);
        config[(MixItUpService.ConfigSection, "Enabled")] = "False";
        MixItUpService service = MakeService(config: config);

        Assert.False(service.Fire(MixItUpTrigger.GoalCompleted, new Dictionary<string, string>()));
    }

    [Fact]
    public void Fire_InvalidCommandId_DoesNothing() {
        Dictionary<(string, string), string> config = EnabledConfig(MixItUpTrigger.GoalCompleted);
        config[(MixItUpService.ConfigSection, MixItUpService.CommandConfigKey(MixItUpTrigger.GoalCompleted))] =
            "not-a-guid";
        MixItUpService service = MakeService(config: config);

        Assert.False(service.Fire(MixItUpTrigger.GoalCompleted, new Dictionary<string, string>()));
    }

    [Fact]
    public async Task Fire_PostsCommandWithSpecialIdentifiers() {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        MixItUpService service = MakeService(handler, EnabledConfig(MixItUpTrigger.GoalCompleted));

        bool fired = service.Fire(MixItUpTrigger.GoalCompleted,
            new Dictionary<string, string> { [Id("goaltext")] = "Hat on" });
        RecordedRequest request = await handler.WaitForRequestAsync(TestContext.Current.CancellationToken);

        Assert.True(fired);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith($"/api/v2/commands/{CommandId}", request.Uri);

        JsonObject body = JsonNode.Parse(request.Body)!.AsObject();
        JsonObject identifiers = body["SpecialIdentifiers"]!.AsObject();
        Assert.Equal("GoalCompleted", identifiers[Id("trigger")]!.GetValue<string>());
        Assert.Equal("Hat on", identifiers[Id("goaltext")]!.GetValue<string>());
        Assert.False(body["IgnoreRequirements"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DataUpdate_FiresOnPauseChangeOnly() {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        Dictionary<(string, string), string> config = EnabledConfig(MixItUpTrigger.TimerPaused);
        MixItUpService service = MakeService(handler, config);
        await service.StartAsync(TestContext.Current.CancellationToken);
        try {
            var subathon = new SubathonData { IsPaused = false, IsLocked = false };
            SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
            SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
            subathon.IsPaused = true;
            SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);

            RecordedRequest request = await handler.WaitForRequestAsync(TestContext.Current.CancellationToken,
                r => r.Method == HttpMethod.Post);

            JsonObject identifiers = JsonNode.Parse(request.Body)!["SpecialIdentifiers"]!.AsObject();
            Assert.Equal("TimerPaused", identifiers[Id("trigger")]!.GetValue<string>());
            Assert.Equal("True", identifiers[Id("paused")]!.GetValue<string>());
            Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        }
        finally {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PromptEnded_FiresOnlyForEndedRuns() {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        MixItUpService service = MakeService(handler, EnabledConfig(MixItUpTrigger.PromptEnded));
        await service.StartAsync(TestContext.Current.CancellationToken);
        try {
            var run = new SubathonPromptRun { Status = SubathonPromptRunStatus.Active };
            SubathonEvents.RaisePromptRunUpdate(run, null);
            run.Status = SubathonPromptRunStatus.Expired;
            SubathonEvents.RaisePromptRunUpdate(run, null);

            RecordedRequest request = await handler.WaitForRequestAsync(TestContext.Current.CancellationToken,
                r => r.Method == HttpMethod.Post);
            await Task.Delay(100, TestContext.Current.CancellationToken);

            JsonObject identifiers = JsonNode.Parse(request.Body)!["SpecialIdentifiers"]!.AsObject();
            Assert.Equal("Expired", identifiers[Id("promptstatus")]!.GetValue<string>());
            Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        }
        finally {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(SubathonEventType.ExternalDonation, false, true)]
    [InlineData(SubathonEventType.Command, false, false)]
    [InlineData(SubathonEventType.Command, true, true)]
    public async Task EventProcessed_CommandsOnlyWhenIncluded(SubathonEventType type, bool includeCommands,
        bool expectSent) {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        Dictionary<(string, string), string> config = EnabledConfig(MixItUpTrigger.SubathonEvent);
        config[(MixItUpService.ConfigSection, MixItUpService.IncludeCommandsKey)] = includeCommands.ToString();
        MixItUpService service = MakeService(handler, config);
        await service.StartAsync(TestContext.Current.CancellationToken);
        try {

            SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent {
                EventType = type, Command = SubathonCommandType.AddTime, User = "Tester", ProcessedToSubathon = true
            }, true);

            if (!expectSent) {
                await Task.Delay(200, TestContext.Current.CancellationToken);
                Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
                return;
            }

            RecordedRequest request = await handler.WaitForRequestAsync(TestContext.Current.CancellationToken,
                r => r.Method == HttpMethod.Post);

            JsonObject identifiers = JsonNode.Parse(request.Body)!["SpecialIdentifiers"]!.AsObject();
            Assert.Equal("SubathonEvent", identifiers[Id("trigger")]!.GetValue<string>());
            Assert.Equal(type.ToString(), identifiers[Id("eventtype")]!.GetValue<string>());
            Assert.Equal("Tester", identifiers[Id("user")]!.GetValue<string>());
        }
        finally {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static SubathonData WithMultiplier(Guid id, double multiplier, bool time, bool points) {
        return new SubathonData {
            Id = id, IsPaused = false, IsLocked = false,
            Multiplier = new MultiplierData {
                SubathonId = id, Multiplier = multiplier, ApplyToSeconds = time, ApplyToPoints = points,
                Duration = TimeSpan.FromMinutes(5), Started = DateTime.Today
            }
        };
    }

    [Fact]
    public async Task DataUpdate_FiresMultiplierStartAndEnd() {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        Dictionary<(string, string), string> config = EnabledConfig(MixItUpTrigger.MultiplierStarted);
        config[(MixItUpService.ConfigSection, MixItUpService.CommandConfigKey(MixItUpTrigger.MultiplierEnded))] =
            CommandId.ToString();
        MixItUpService service = MakeService(handler, config);
        await service.StartAsync(TestContext.Current.CancellationToken);
        try {
            Guid id = Guid.NewGuid();
            SubathonEvents.RaiseSubathonDataUpdate(WithMultiplier(id, 1, false, false), DateTime.Now);
            SubathonEvents.RaiseSubathonDataUpdate(WithMultiplier(id, 2, true, false), DateTime.Now);
            SubathonEvents.RaiseSubathonDataUpdate(WithMultiplier(id, 2, true, false), DateTime.Now);
            SubathonEvents.RaiseSubathonDataUpdate(new SubathonData { Id = id, IsPaused = false, IsLocked = false },
                DateTime.Now);
            SubathonEvents.RaiseSubathonDataUpdate(WithMultiplier(id, 1, true, false), DateTime.Now);

            await handler.WaitForRequestAsync(TestContext.Current.CancellationToken,
                r => r.Method == HttpMethod.Post && r.Body.Contains("MultiplierEnded"));
            await Task.Delay(100, TestContext.Current.CancellationToken);

            List<JsonObject> sent = handler.Requests.Where(r => r.Method == HttpMethod.Post)
                .Select(r => JsonNode.Parse(r.Body)!["SpecialIdentifiers"]!.AsObject()).ToList();
            Assert.Equal(2, sent.Count);

            JsonObject started = sent.Single(i => i[Id("trigger")]!.GetValue<string>() == "MultiplierStarted");
            Assert.Equal("2", started[Id("multiplier")]!.GetValue<string>());
            Assert.Equal("True", started[Id("multipliertime")]!.GetValue<string>());
            Assert.Equal("False", started[Id("multiplierpoints")]!.GetValue<string>());
            Assert.Equal("300", started[Id("multiplierduration")]!.GetValue<string>());

            JsonObject ended = sent.Single(i => i[Id("trigger")]!.GetValue<string>() == "MultiplierEnded");
            Assert.Equal("2", ended[Id("multiplier")]!.GetValue<string>());
        }
        finally {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void MultiplierIdentifiers_SendTimeAndPointsFlags(bool time, bool points) {
        Dictionary<string, string> ids = MixItUpService.MultiplierIdentifiers(
            new MixItUpService.MultiplierSnapshot(true, 1.5, time, points, null, null, false));

        Assert.Equal(time.ToString(), ids[Id("multipliertime")]);
        Assert.Equal(points.ToString(), ids[Id("multiplierpoints")]);
        Assert.Equal("1.5", ids[Id("multiplier")]);
        Assert.Equal("0", ids[Id("multiplierduration")]);
    }

    [Fact]
    public void SampleIdentifiers_CoverEveryTrigger() {
        foreach (MixItUpTrigger trigger in Enum.GetValues<MixItUpTrigger>()) {
            IReadOnlyList<string> names = MixItUpService.IdentifierNames(trigger);
            Assert.Contains($"${Id("trigger")}", names);
            Assert.True(names.Count > 1, $"{trigger} has no identifiers");
            Assert.All(names, n => Assert.Matches($"^\\${MixItUpService.IdentifierPrefix}[a-z]+$", n));
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string Body);

    private sealed class RecordingHandler(HttpStatusCode statusCode, string? body = null, bool throwConnect = false)
        : HttpMessageHandler {
        private readonly SemaphoreSlim _signal = new(0);
        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

        public async Task<RecordedRequest> WaitForRequestAsync(CancellationToken ct,
            Func<RecordedRequest, bool>? match = null) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (true) {
                RecordedRequest? found = Requests.FirstOrDefault(r => match?.Invoke(r) ?? true);
                if (found != null) return found;
                await _signal.WaitAsync(timeout.Token);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct) {
            if (throwConnect) throw new HttpRequestException("Connection refused");
            string content = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!.ToString(), content));
            _signal.Release();

            var response = new HttpResponseMessage(statusCode);
            if (body != null) response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return response;
        }
    }
}
