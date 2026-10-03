using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Services;
using SubathonManager.Tests.Utility;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.ServicesUnitTests;

[Collection("GlobalState")]
public class ActionServiceTests {
    private static async Task<(ActionService service, FakeRunner runner, DbContextOptions<AppDbContext> options,
        SqliteConnection conn)> SetupAsync() {
        var dbName = $"test_{Guid.NewGuid():N}";
        var connectionString = $"DataSource={dbName};Mode=Memory;Cache=Shared;Pooling=False";
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        await using (var db = new AppDbContext(options)) {
            await db.Database.EnsureCreatedAsync();
        }

        var factoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(options));

        foreach (string name in new[] { "SubathonEventCreated", "SubathonDataUpdate" })
            typeof(SubathonEvents).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
        foreach (string name in new[] { "WheelSpinStatusChanged", "OnSpinsOwedUpdateFromEvent" })
            typeof(WheelEvents).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);

        typeof(ActionEvents).GetField("CustomActionRunRequested", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        var runner = new FakeRunner();
        var service = new ActionService(factoryMock.Object, [runner],
            actionsFolder: Path.Combine(Path.GetTempPath(), $"sm_actions_{Guid.NewGuid():N}"));
        await service.StartAsync(TestContext.Current.CancellationToken);
        return (service, runner, options, connection);
    }

    private static async Task<WheelSpinHistory> AddSpinAsync(DbContextOptions<AppDbContext> options,
        WheelSpinActionType type, string parameter, WheelSpinHistoryStatus status = WheelSpinHistoryStatus.Pending) {
        await using var db = new AppDbContext(options);
        var wheel = new WheelSet();
        var item = new WheelItem { Text = "Prize", Enabled = true, LinkedWheel = wheel };
        item.Action = new WheelSpinAction { ActionType = type, Parameter = parameter, LinkedItem = item };
        var history = new WheelSpinHistory { LinkedWheel = wheel, LinkedItem = item, Status = status };
        db.WheelSpinHistories.Add(history);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return history;
    }

    private static async Task<WheelSpinHistory> ReloadAsync(DbContextOptions<AppDbContext> options, Guid id) {
        await using var db = new AppDbContext(options);
        return await db.WheelSpinHistories.AsNoTracking()
            .FirstAsync(h => h.Id == id, TestContext.Current.CancellationToken);
    }

    private static ActionStep Hotkey(string target) {
        return new ActionStep { Type = ActionStepType.VtsHotkey, Operation = ActionOperation.Trigger, Target = target };
    }

    private static ActionStep Wait(double seconds) {
        return new ActionStep { Type = ActionStepType.Wait, Seconds = seconds };
    }

    [Fact]
    public async Task InstantWheelAction_RunsBeforeTheSpinIsAnnounced_AndLandsDone() {
        (ActionService service, _, DbContextOptions<AppDbContext> options, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;
        WheelSpinHistory history = await AddSpinAsync(options, WheelSpinActionType.AddTime, "5m");

        var created = new List<SubathonEvent>();
        SubathonEvents.SubathonEventCreated += created.Add;
        var statusEvents = new List<WheelSpinHistoryStatus>();
        WheelEvents.WheelSpinStatusChanged += (h, _) => statusEvents.Add(h.Status);

        WheelSpinHistoryStatus? announced = null;
        var commandsAtAnnounce = -1;
        ActionRunResult result = await service.RunWheelSpinAsync(history.Id, status => {
            announced = status;
            commandsAtAnnounce = created.Count;
            return Task.CompletedTask;
        });

        Assert.Equal(ActionRunResult.Done, result);
        Assert.Equal(WheelSpinHistoryStatus.Done, announced);
        Assert.Equal(1, commandsAtAnnounce);
        Assert.Empty(statusEvents);

        SubathonEvent ev = Assert.Single(created);
        Assert.Equal(SubathonCommandType.AddTime, ev.Command);
        Assert.Equal(SubathonEventSource.WheelSpin, ev.Source);
        Assert.Equal(300, ev.SecondsValue);
        Assert.Equal(WheelSpinHistoryStatus.Done, (await ReloadAsync(options, history.Id)).Status);
    }

    [Fact]
    public async Task VtsChain_IsAnnouncedPending_RunsThroughRunning_AndFinishesDone() {
        (ActionService service, FakeRunner runner, DbContextOptions<AppDbContext> options, SqliteConnection conn) =
            await SetupAsync();
        await using SqliteConnection _ = conn;
        var vts = new VTSWheelAction {
            Kind = VtsTargetKind.Hotkey, Target = "spin", Duration = TimeSpan.FromSeconds(1),
            AfterHotkey = VtsHotkeyAfterAction.TriggerAgain
        };
        WheelSpinHistory history = await AddSpinAsync(options, WheelSpinActionType.VTubeStudio,
            vts.ToParameterString());

        var statusEvents = new ConcurrentQueue<WheelSpinHistoryStatus>();
        WheelEvents.WheelSpinStatusChanged += (h, _) => statusEvents.Enqueue(h.Status);

        WheelSpinHistoryStatus? announced = null;
        ActionRunResult result = await service.RunWheelSpinAsync(history.Id, status => {
            announced = status;
            return Task.CompletedTask;
        });

        Assert.Equal(ActionRunResult.Done, result);
        Assert.Equal(WheelSpinHistoryStatus.Pending, announced);
        Assert.Equal([WheelSpinHistoryStatus.Running, WheelSpinHistoryStatus.Done], statusEvents.ToArray());
        Assert.Equal(["spin", "spin"], runner.Ran.ToArray());

        WheelSpinHistory saved = await ReloadAsync(options, history.Id);
        Assert.Equal(WheelSpinHistoryStatus.Done, saved.Status);
        Assert.Null(saved.ActionProgress);
    }

    [Fact]
    public async Task Branches_RunInParallel_AndAJoinWaitsForEveryInput() {
        (ActionService service, FakeRunner runner, _, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;
        //////////////////////////////////////////
        //      +-> wait 0.3s -> slow --+
        // start                        +-> join
        //      +-> fast ---------------+
        //////////////////////////////////////////
        var graph = new ActionGraph {
            Nodes = [
                new ActionNode { Id = "start", Step = Hotkey("start") },
                new ActionNode { Id = "wait", Step = Wait(0.3) },
                new ActionNode { Id = "slow", Step = Hotkey("slow") },
                new ActionNode { Id = "fast", Step = Hotkey("fast") },
                new ActionNode { Id = "join", Step = Hotkey("join") }
            ],
            Edges = [
                new ActionEdge { From = "start", To = "wait" },
                new ActionEdge { From = "wait", To = "slow" },
                new ActionEdge { From = "start", To = "fast" },
                new ActionEdge { From = "slow", To = "join" },
                new ActionEdge { From = "fast", To = "join" }
            ]
        };

        ActionRunResult result = await service.RunAsync(Guid.NewGuid(), graph,
            new ActionContext(SubathonEventSource.WheelSpin, "test", "branches"));

        Assert.Equal(ActionRunResult.Done, result);
        Assert.Equal(["start", "fast", "slow", "join"], runner.Ran.ToArray());
    }

    [Fact]
    public async Task FailedStep_PausesTheRun_AndResumeContinuesFromThatStep() {
        (ActionService service, FakeRunner runner, _, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;
        runner.FailOnce.Add("flaky");

        var graph = new ActionGraph {
            Nodes = [
                new ActionNode { Id = "1", Step = Hotkey("first") },
                new ActionNode { Id = "2", Step = Hotkey("flaky") },
                new ActionNode { Id = "3", Step = Hotkey("last") },
                new ActionNode { Id = "4", Step = Wait(0.5) },
                new ActionNode { Id = "5", Step = Hotkey("late") }
            ],
            Edges = [
                new ActionEdge { From = "1", To = "2" },
                new ActionEdge { From = "2", To = "3" },
                new ActionEdge { From = "1", To = "4" },
                new ActionEdge { From = "4", To = "5" }
            ]
        };
        var ctx = new ActionContext(SubathonEventSource.WheelSpin, "test", "resume");
        var progress = new ActionRunProgress();

        Assert.Equal(ActionRunResult.Paused, await service.RunAsync(Guid.NewGuid(), graph, ctx, progress));
        Assert.Equal(["1"], progress.Done.Order());
        Assert.Equal(["first"], runner.Ran.ToArray());

        progress = ActionRunProgress.Parse(progress.ToJson());
        Assert.Equal(ActionRunResult.Done, await service.RunAsync(Guid.NewGuid(), graph, ctx, progress));
        Assert.Equal(["first", "flaky", "last", "late"], runner.Ran.ToArray());
    }

    [Fact]
    public async Task MarkingARunningSpinDone_StopsTheChain_AndKeepsTheNewStatus() {
        (ActionService service, FakeRunner runner, DbContextOptions<AppDbContext> options, SqliteConnection conn) =
            await SetupAsync();
        await using SqliteConnection _ = conn;
        var vts = new VTSWheelAction {
            Kind = VtsTargetKind.Hotkey, Target = "spin", Duration = TimeSpan.FromSeconds(30),
            AfterHotkey = VtsHotkeyAfterAction.TriggerAgain
        };
        WheelSpinHistory history = await AddSpinAsync(options, WheelSpinActionType.VTubeStudio,
            vts.ToParameterString());

        Task<ActionRunResult> run = service.RunWheelSpinAsync(history.Id);
        for (var i = 0; i < 100 && !service.IsRunning(history.Id); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(service.IsRunning(history.Id));

        await using (var db = new AppDbContext(options)) {
            WheelSpinHistory tracked = await db.WheelSpinHistories.FirstAsync(h => h.Id == history.Id,
                TestContext.Current.CancellationToken);

            tracked.Status = WheelSpinHistoryStatus.Done;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            WheelEvents.RaiseWheelSpinStatusChanged(tracked, 0);
        }

        Assert.Equal(ActionRunResult.Cancelled, await run);
        Assert.Equal(["spin"], runner.Ran.ToArray());
        Assert.Equal(WheelSpinHistoryStatus.Done, (await ReloadAsync(options, history.Id)).Status);
    }

    [Fact]
    public async Task RepeatOnTheSameKey_RestartsTheChain_SoOnlyTheNewestRevertFires() {
        (ActionService service, FakeRunner runner, _, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;
        ActionGraph graph = ActionGraph.Sequence(Hotkey("on"), Wait(0.4), Hotkey("off"));
        var ctx = new ActionContext(SubathonEventSource.WheelSpin, "test", "same-target");

        Task<ActionRunResult> first = service.RunAsync(Guid.NewGuid(), graph, ctx);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Task<ActionRunResult> second = service.RunAsync(Guid.NewGuid(), graph, ctx);

        Assert.Equal(ActionRunResult.Done, await first);
        Assert.Equal(ActionRunResult.Done, await second);
        Assert.Equal(["on", "on", "off"], runner.Ran.ToArray());
    }

    [Fact]
    public async Task Startup_ReturnsRunsInterruptedByARestartToPending() {
        (ActionService service, _, DbContextOptions<AppDbContext> options, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;
        WheelSpinHistory history = await AddSpinAsync(options, WheelSpinActionType.VTubeStudio, "",
            WheelSpinHistoryStatus.Running);

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WheelSpinHistoryStatus.Pending, (await ReloadAsync(options, history.Id)).Status);
    }

    [Fact]
    public void VtsParameterAction_ConvertsToAChain_ThatSurvivesJson() {
        var vts = new VTSWheelAction {
            Kind = VtsTargetKind.Parameter, Target = "FaceAngleX", Value = 30,
            Duration = TimeSpan.FromSeconds(10), AfterParameter = VtsParameterAfterAction.ResetToOriginal
        };

        Assert.True(ActionGraph.TryParse(vts.ToActionGraph().ToJson(), out ActionGraph? graph));
        Assert.True(graph.IsValid(out _));
        Assert.Equal([ActionOperation.Hold, ActionOperation.None, ActionOperation.Restore], graph.Nodes.Select(n => n.Step.Operation));
        Assert.Equal("FaceAngleX = 30 (held) -> Wait 10s -> Restore FaceAngleX", graph.Describe());

        graph.Edges.Add(new ActionEdge { From = "3", To = "1" });
        Assert.False(graph.IsValid(out string error));
        Assert.Contains("loop", error);
    }

    [Fact]
    public void ObsWheelParameter_IsTheGraph_AndRepeatsShareAKeyPerTarget() {
        var show = new ActionStep {
            Type = ActionStepType.ObsSourceVisibility, Operation = ActionOperation.Show, Target = "Hat", Scope = "Main"
        };
        var hide = new ActionStep {
            Type = ActionStepType.ObsSourceVisibility, Operation = ActionOperation.Hide, Target = "Hat", Scope = "Main"
        };
        string longer = ActionGraph.Sequence(show, Wait(30), hide).ToJson();
        string shorter = ActionGraph.Sequence(show, Wait(5), hide).ToJson();

        ActionGraph? graph = WheelSpinActionType.OBS.BuildActionGraph(longer);
        Assert.NotNull(graph);
        Assert.True(graph.IsValid(out _));
        Assert.Equal("Show \"Hat\" in \"Main\" -> Wait 30s -> Hide \"Hat\" in \"Main\"", graph.Describe());
        Assert.Equal(WheelSpinActionType.OBS.RepeatKey(longer, Guid.NewGuid()),
            WheelSpinActionType.OBS.RepeatKey(shorter, Guid.NewGuid()));

        show.Scope = null;
        Assert.False(ActionGraph.Sequence(show).IsValid(out string error));
        Assert.Contains("scene", error);
        Assert.Null(WheelSpinActionType.OBS.BuildActionGraph("not json"));
    }

    [Fact]
    public async Task CustomActions_LiveAsFiles_KeepTheirIdentity_AndRunFromAWheelItem() {
        (ActionService service, FakeRunner runner, DbContextOptions<AppDbContext> options, SqliteConnection conn) =
            await SetupAsync();
        await using SqliteConnection _ = conn;
        string folder = Path.Combine(Path.GetTempPath(), $"sm_actions_{Guid.NewGuid():N}");
        try {
            var editor = new ActionService(null!, [runner], actionsFolder: folder);
            await editor.LoadLibraryAsync();
            var action = new CustomAction {
                Name = "Hat Bit", Graph = ActionGraph.Sequence(Hotkey("on"), Hotkey("off"))
            };
            await editor.SaveCustomActionAsync(action);

            action.Name = "Hat Bit (long)";
            await editor.SaveCustomActionAsync(action);
            string file = Assert.Single(Directory.GetFiles(folder, $"*{CustomAction.FileExtension}"));
            Assert.Equal("hat-bit.sma", Path.GetFileName(file));
            var reopened = new ActionService(null!, [runner], actionsFolder: folder);
            await reopened.LoadLibraryAsync();
            Assert.Equal("Hat Bit (long)", reopened.GetCustomAction(action.Id)?.Name);

            (CustomAction Action, bool Replaced)? imported = await service.ImportCustomActionAsync(file);
            Assert.False(imported?.Replaced);
            WheelSpinHistory history = await AddSpinAsync(options, WheelSpinActionType.CustomAction,
                action.Id.ToString());
            var logged = new List<SubathonEvent>();
            SubathonEvents.SubathonEventCreated += logged.Add;
            Assert.Equal(ActionRunResult.Done, await service.RunWheelSpinAsync(history.Id));
            Assert.Equal(["on", "off"], runner.Ran.ToArray());

            SubathonEvent record = Assert.Single(logged);
            Assert.Equal(SubathonCommandType.RunAction, record.Command);
            Assert.Equal("WheelSpin", record.User);
            Assert.Equal("RunAction Hat Bit (long)", record.Value);
            ActionEvents.RaiseCustomActionRunRequested(record);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(2, runner.Ran.Count);

            imported = await service.ImportCustomActionAsync(file);
            Assert.True(imported?.Replaced);
            Assert.Single(service.CustomActions);
        }
        finally {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task OpeningASmaFromOutside_AlwaysAddsACopy_RenamingClashes() {
        string folder = Path.Combine(Path.GetTempPath(), $"sm_actions_{Guid.NewGuid():N}");
        string outside = Path.Combine(Path.GetTempPath(), $"sm_shared_{Guid.NewGuid():N}.sma");
        try {
            var original = new CustomAction { Name = "Hat Bit", Graph = ActionGraph.Sequence(Hotkey("on")) };
            await File.WriteAllTextAsync(outside, original.ToJson(), TestContext.Current.CancellationToken);

            var service = new ActionService(null!, [new FakeRunner()], actionsFolder: folder);
            CustomAction? first = await service.AddCustomActionCopyAsync(outside);
            CustomAction? second = await service.AddCustomActionCopyAsync(outside);
            CustomAction? third = await service.AddCustomActionCopyAsync(outside);

            Assert.Equal("Hat Bit", first?.Name);
            Assert.Equal(original.Id, first?.Id);
            Assert.Equal("Hat Bit (Copy 1)", second?.Name);
            Assert.Equal("Hat Bit (Copy 2)", third?.Name);
            Assert.Equal(3, new[] { first!.Id, second!.Id, third!.Id }.Distinct().Count());

            CustomAction duplicate = await service.DuplicateCustomActionAsync(third);
            Assert.Equal("Hat Bit (Copy 3)", duplicate.Name);
            Assert.NotEqual(third.Id, duplicate.Id);
            Assert.Equal(4, service.CustomActions.Count);

            var reopened = new ActionService(null!, [new FakeRunner()], actionsFolder: folder);
            await reopened.LoadLibraryAsync();
            Assert.Equal(4, reopened.CustomActions.Count);
            Assert.Null(await service.AddCustomActionCopyAsync(Path.Combine(folder, "missing.sma")));
        }
        finally {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            if (File.Exists(outside)) File.Delete(outside);
        }
    }

    [Fact]
    public async Task DisabledSteps_AreSkipped_AndWhatFollowsStillRuns() {
        (ActionService service, FakeRunner runner, _, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;

        var graph = new ActionGraph {
            Nodes = [
                new ActionNode { Id = "1", Step = Hotkey("first") },
                new ActionNode { Id = "2", Step = Hotkey("skipped"), Disabled = true },
                new ActionNode { Id = "3", Step = Hotkey(""), Disabled = true },
                new ActionNode { Id = "4", Step = Hotkey("last") }
            ],
            Edges = [
                new ActionEdge { From = "1", To = "2" },
                new ActionEdge { From = "1", To = "3" },
                new ActionEdge { From = "2", To = "4" },
                new ActionEdge { From = "3", To = "4" }
            ]
        };

        Assert.True(graph.IsValid(out _));
        Assert.True(ActionGraph.TryParse(graph.ToJson(), out ActionGraph? reloaded));
        Assert.Equal([false, true, true, false], reloaded.Nodes.Select(n => n.Disabled));

        ActionRunResult result = await service.RunAsync(Guid.NewGuid(), reloaded,
            new ActionContext(SubathonEventSource.WheelSpin, "test", "disabled"));
        Assert.Equal(ActionRunResult.Done, result);
        Assert.Equal(["first", "last"], runner.Ran.ToArray());
    }

    [Fact]
    public async Task RunActionCommand_RunsTheNamedAction_AsWhoeverAskedOrSystem() {
        (ActionService service, FakeRunner runner, _, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;
        await service.SaveCustomActionAsync(new CustomAction {
            Name = "Hat Bit", Graph = ActionGraph.Sequence(Hotkey("hat"))
        });

        ActionEvents.RaiseCustomActionRunRequested(new SubathonEvent {
            Source = SubathonEventSource.Twitch, User = "SomeMod", Value = "  hat bit "
        });
        for (var i = 0; i < 100 && runner.Ran.IsEmpty; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(["hat"], runner.Ran.ToArray());
        Assert.Equal("SomeMod", runner.LastUser);

        ActionEvents.RaiseCustomActionRunRequested(new SubathonEvent { Value = "RunAction Hat Bit" });
        for (var i = 0; i < 100 && runner.Ran.Count < 2; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal("SYSTEM", runner.LastUser);

        ActionEvents.RaiseCustomActionRunRequested(new SubathonEvent { Value = "Hat" });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(2, runner.Ran.Count);
        Assert.Null(service.FindCustomAction("Hat"));
    }

    [Fact]
    public async Task Variables_ResolveFromTheActiveSubathon_AndUnknownTokensAreLeftAlone() {
        (_, _, DbContextOptions<AppDbContext> options, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;
        await using (var db = new AppDbContext(options)) {
            foreach (SubathonData existing in db.SubathonDatas) existing.IsActive = false;
            foreach (SubathonGoalSet existing in db.SubathonGoalSets) existing.IsActive = false;
            db.SubathonDatas.Add(new SubathonData {
                IsActive = true, IsLocked = true, IsPaused = false, Points = 150, MoneySum = 12.5, Currency = "",
                MillisecondsCumulative = 3_725_000, MillisecondsElapsed = 0,
                Multiplier = new MultiplierData {
                    Multiplier = 2, ApplyToPoints = true, ApplyToSeconds = false,
                    Duration = TimeSpan.FromMinutes(10), Started = DateTime.Now
                }
            });
            db.SubathonGoalSets.Add(new SubathonGoalSet {
                IsActive = true, Name = "Main", Type = GoalsType.Points,
                Goals = [
                    new SubathonGoal { Text = "C", Points = 300 },
                    new SubathonGoal { Text = "A", Points = 100 },
                    new SubathonGoal { Text = "B", Points = 200 }
                ]
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = new Mock<IDbContextFactory<AppDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(options));
        IConfig config = MockConfig.MakeMockConfig(new Dictionary<(string, string), string> {
            { ("Currency", "Primary"), "CAD" }
        });

        string filled = await ActionVariableResolver.FillAsync(factory.Object,
            "%points% %money% %currency% %timer_locked% %timer_paused% %time_remaining% %seconds_remaining% | " +
            "%goal_text% %goal_index%/%goal_count% last=%last_goal_text% | x%multiplier_amount% " +
            "pts=%multiplier_points% time=%multiplier_time% | %user% %not_a_variable%",
            new ActionContext(SubathonEventSource.WheelSpin, "tester", "key"), config);

        Assert.Equal("150 12.50 CAD true false 01:02:05 3725 | B 2/3 last=A | x2 pts=true time=false | " +
                     "tester %not_a_variable%", filled);
    }

    [Fact]
    public void StepArguments_AreNameValueLines() {
        List<(string Name, string Value)> args = ActionStep
            .ParseArguments("points=%points%\r\n  bad line \nmessage = hi = there\n=nameless\n").ToList();
        Assert.Equal(new[] { ("points", "%points%"), ("message", "hi = there") }, args);
    }

    private sealed class FakeRunner : IActionStepRunner {
        public ConcurrentQueue<string> Ran { get; } = new();
        public HashSet<string> FailOnce { get; } = [];
        public string? LastUser { get; private set; }

        public IReadOnlyCollection<ActionStepType> StepTypes { get; } = [ActionStepType.VtsHotkey];

        public Task<bool> RunStepAsync(ActionStep step, ActionContext ctx, ActionRunProgress progress,
            CancellationToken ct) {
            lock (FailOnce) {
                if (FailOnce.Remove(step.Target)) return Task.FromResult(false);
            }

            LastUser = ctx.User;
            Ran.Enqueue(step.Target);
            return Task.FromResult(true);
        }
    }
}
