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
using SubathonManager.Core.Security.Interfaces;
using SubathonManager.Data;
using SubathonManager.Services;
using SubathonManager.Tests.Utility;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.ServicesUnitTests;

[Collection("GlobalState")]
public class ActionServiceTests {
    private static async Task<(ActionService service, FakeRunner runner, DbContextOptions<AppDbContext> options,
        SqliteConnection conn)> SetupAsync(ISecureStorage? secureStorage = null) {
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

        foreach (string name in new[] { "SubathonEventCreated", "SubathonDataUpdate", "SubathonEventProcessed" })
            typeof(SubathonEvents).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
        foreach (string name in new[] { "WheelSpinStatusChanged", "OnSpinsOwedUpdateFromEvent" })
            typeof(WheelEvents).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);

        typeof(ActionEvents).GetField("CustomActionRunRequested", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        var runner = new FakeRunner();
        var service = new ActionService(factoryMock.Object, [runner],
            actionsFolder: Path.Combine(Path.GetTempPath(), $"sm_actions_{Guid.NewGuid():N}"),
            secureStorage: secureStorage);
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
        int commandsAtAnnounce = -1;
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
        for (var i = 0; i < 100 && !service.IsRunning(history.Id); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
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
        Assert.Equal([ActionOperation.Hold, ActionOperation.None, ActionOperation.Restore],
            graph.Nodes.Select(n => n.Step.Operation));
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
        for (var i = 0; i < 100 && runner.Ran.Count < 2; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task IfElse_RunsOnlyTakenSide_JoinOfBothSidesRunsOnce() {
        (ActionService service, FakeRunner runner, _, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;

        ActionGraph Build() {
            return new ActionGraph {
                Nodes = [
                    new ActionNode { Id = "1", Step = Hotkey("fetch") },
                    new ActionNode {
                        Id = "2", Step = new ActionStep {
                            Type = ActionStepType.Condition, Operation = ActionOperation.MoreThan,
                            Scope = "%resp.data.items[1].count%", Target = "3"
                        }
                    },
                    new ActionNode { Id = "3", Step = Hotkey("then") },
                    new ActionNode { Id = "4", Step = Hotkey("then-more") },
                    new ActionNode { Id = "5", Step = Hotkey("else") },
                    new ActionNode { Id = "6", Step = Hotkey("join") }
                ],
                Edges = [
                    new ActionEdge { From = "1", To = "2" },
                    new ActionEdge { From = "2", To = "3" },
                    new ActionEdge { From = "3", To = "4" },
                    new ActionEdge { From = "2", To = "5", Port = ActionEdge.ElsePort },
                    new ActionEdge { From = "4", To = "6" },
                    new ActionEdge { From = "5", To = "6" }
                ]
            };
        }

        Assert.True(ActionGraph.TryParse(Build().ToJson(), out ActionGraph? reloaded));
        Assert.Equal(ActionEdge.ElsePort, reloaded.Edges.Single(e => e.To == "5").Port);
        Assert.DoesNotContain("\"port\"", ActionGraph.Sequence(Hotkey("a"), Hotkey("b")).ToJson());

        runner.SetVariables["fetch"] = ("resp", """{"data":{"items":[{"count":1},{"count":5}]}}""");
        var progress = new ActionRunProgress();
        Assert.Equal(ActionRunResult.Done, await service.RunAsync(Guid.NewGuid(), reloaded,
            new ActionContext(SubathonEventSource.WheelSpin, "test", "if-true"), progress));
        Assert.Equal(["fetch", "then", "then-more", "join"], runner.Ran.ToArray());
        Assert.Equal(["5"], progress.Skipped.ToArray());

        runner.Ran.Clear();
        runner.SetVariables["fetch"] = ("resp", """{"data":{"items":[{"count":1},{"count":2}]}}""");
        progress = new ActionRunProgress();
        Assert.Equal(ActionRunResult.Done, await service.RunAsync(Guid.NewGuid(), Build(),
            new ActionContext(SubathonEventSource.WheelSpin, "test", "if-false"), progress));
        Assert.Equal(["fetch", "else", "join"], runner.Ran.ToArray());
        Assert.Equal(["3", "4"], progress.Skipped.Order().ToArray());

        ActionRunProgress resumed = ActionRunProgress.Parse(progress.ToJson());
        Assert.Equal(ActionEdge.ElsePort, resumed.Ports["2"]);
        Assert.True(resumed.TryReadVariable("RESP.data.items[0].count", out string count));
        Assert.Equal("1", count);
    }

    [Fact]
    public async Task IgnoreErrors_LetsTheRunCarryOn_WhileOtherFailuresStillPause() {
        (ActionService service, FakeRunner runner, _, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;

        ActionGraph graph = ActionGraph.Sequence(Hotkey("flaky"), Hotkey("after"));
        graph.Nodes[0].IgnoreErrors = true;
        Assert.True(ActionGraph.TryParse(graph.ToJson(), out ActionGraph? reloaded));
        Assert.True(reloaded.Nodes[0].IgnoreErrors);
        Assert.DoesNotContain("ignore_errors", ActionGraph.Sequence(Hotkey("a")).ToJson());

        runner.FailOnce.Add("flaky");
        Assert.Equal(ActionRunResult.Done, await service.RunAsync(Guid.NewGuid(), reloaded,
            new ActionContext(SubathonEventSource.WheelSpin, "test", "ignore")));
        Assert.Equal(["after"], runner.Ran.ToArray());

        runner.FailOnce.Add("flaky");
        reloaded.Nodes[0].IgnoreErrors = false;
        Assert.Equal(ActionRunResult.Paused, await service.RunAsync(Guid.NewGuid(), reloaded,
            new ActionContext(SubathonEventSource.WheelSpin, "test", "no-ignore")));
        Assert.Equal(["after"], runner.Ran.ToArray());
    }

    [Fact]
    public async Task Secrets_FillIntoSteps_AreAddedEmptyForSavedActions_AndCanBeDeleted() {
        var store = new Dictionary<string, string>();
        var storage = new Mock<ISecureStorage>();
        storage.Setup(s => s.Set(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string key, string value) => {
                store[key] = value;
                return true;
            });
        storage.Setup(s => s.Get(It.IsAny<string>())).Returns((string key) => store.GetValueOrDefault(key));
        storage.Setup(s => s.Delete(It.IsAny<string>())).Returns((string key) => store.Remove(key));

        (ActionService service, FakeRunner runner, _, SqliteConnection conn) = await SetupAsync(storage.Object);
        await using SqliteConnection __ = conn;

        Assert.False(await service.SetSecretAsync("bad name", "x"));
        Assert.True(await service.SetSecretAsync("Api_Key", "abc123"));
        Assert.Equal("abc123", service.GetSecretValue("api_key"));

        var step = new ActionStep {
            Type = ActionStepType.MixItUpCommand, Operation = ActionOperation.Run, Target = "cmd",
            Body = "key=%secret.API_KEY%\nother=%secret.missing_one%\nleft=%resp.x%"
        };
        await service.SaveCustomActionAsync(new CustomAction
            { Name = "Uses secrets", Graph = ActionGraph.Sequence(step) });

        Assert.Equal(["Api_Key", "missing_one"],
            (await service.GetGlobalsAsync(ActionStoreKind.Secret)).Select(s => s.Name)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Null(service.GetSecretValue("missing_one"));
        Assert.Single(service.ActionsUsing(ActionStoreKind.Secret, "api_key"));

        Assert.Equal(ActionRunResult.Done, await service.RunAsync(Guid.NewGuid(), ActionGraph.Sequence(step),
            new ActionContext(SubathonEventSource.WheelSpin, "test", "secrets")));
        Assert.Equal("key=abc123\nother=\nleft=%resp.x%", runner.LastBody);

        await service.DeleteGlobalAsync(ActionStoreKind.Secret, "API_KEY");
        Assert.Null(service.GetSecretValue("api_key"));
        Assert.Equal(["missing_one"],
            (await service.GetGlobalsAsync(ActionStoreKind.Secret)).Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task Globals_AreTyped_SetGlobalStepsCheckTheType_AndTypeChangesConvertOrClear() {
        (ActionService service, FakeRunner runner, _, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;

        Assert.NotNull(await service.SetGlobalAsync("deaths", ActionValueType.Number, "nope"));
        Assert.Null(await service.SetGlobalAsync("Deaths", ActionValueType.Number, " 2.50 "));
        Assert.Null(await service.SetGlobalAsync("hat_on", ActionValueType.Boolean, "yes"));
        Assert.Null(await service.SetGlobalAsync("greeting", ActionValueType.Text, "hi there "));

        ActionStep Set(string name, ActionOperation op, string? body = null) {
            return new ActionStep { Type = ActionStepType.SetGlobal, Operation = op, Target = name, Body = body };
        }

        var report = new ActionStep {
            Type = ActionStepType.MixItUpCommand, Operation = ActionOperation.Run, Target = "report",
            Body = "d=%global.deaths%\nh=%global.HAT_ON%\ng=%global.greeting%\nm=%global.missing%"
        };
        ActionGraph graph = ActionGraph.Sequence(Set("deaths", ActionOperation.Adjust, "1"),
            Set("hat_on", ActionOperation.Toggle), Set("greeting", ActionOperation.Set, "%user% says hi"), report);
        Assert.Equal(ActionRunResult.Done, await service.RunAsync(Guid.NewGuid(), graph,
            new ActionContext(SubathonEventSource.WheelSpin, "Bob", "globals")));
        Assert.Equal("d=3.5\nh=false\ng=Bob says hi\nm=", runner.LastBody);

        Assert.Equal(ActionRunResult.Paused, await service.RunAsync(Guid.NewGuid(),
            ActionGraph.Sequence(Set("hat_on", ActionOperation.Set, "maybe")),
            new ActionContext(SubathonEventSource.WheelSpin, "Bob", "bad-type")));
        Assert.Equal(ActionRunResult.Paused, await service.RunAsync(Guid.NewGuid(),
            ActionGraph.Sequence(Set("greeting", ActionOperation.Adjust, "1")),
            new ActionContext(SubathonEventSource.WheelSpin, "Bob", "bad-op")));
        Assert.Equal(ActionRunResult.Paused, await service.RunAsync(Guid.NewGuid(),
            ActionGraph.Sequence(Set("nobody", ActionOperation.Set, "x")),
            new ActionContext(SubathonEventSource.WheelSpin, "Bob", "missing")));

        Assert.True(await service.SetGlobalTypeAsync("hat_on", ActionValueType.Number));
        Assert.False(await service.SetGlobalTypeAsync("greeting", ActionValueType.Number));
        Assert.True(await service.SetGlobalTypeAsync("deaths", ActionValueType.Text));
        Dictionary<string, ActionGlobal> globals = (await service.GetGlobalsAsync(ActionStoreKind.Global))
            .ToDictionary(g => g.Name);
        Assert.Equal(("0", ActionValueType.Number), (globals["hat_on"].Value, globals["hat_on"].ValueType));
        Assert.Equal("0", globals["greeting"].Value);
        Assert.Equal("3.5", globals["Deaths"].Value);

        await service.SaveCustomActionAsync(new CustomAction {
            Name = "Counter", Graph = ActionGraph.Sequence(Set("wins", ActionOperation.Adjust, "1"), report)
        });
        ActionGlobal wins = (await service.GetGlobalsAsync(ActionStoreKind.Global)).Single(g => g.Name == "wins");
        Assert.Equal(ActionValueType.Number, wins.ValueType);
        Assert.Contains(await service.GetGlobalsAsync(ActionStoreKind.Global), g => g.Name == "missing");
        Assert.Single(service.ActionsUsing(ActionStoreKind.Global, "WINS"));
    }

    [Fact]
    public async Task TriggerSteps_StartOnlyForTheirTrigger_PassItsValues_AndManualRunsSkipThem() {
        (ActionService service, FakeRunner runner, _, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;

        var graph = new ActionGraph {
            Nodes = [
                new ActionNode {
                    Id = "1", Step = new ActionStep {
                        Type = ActionStepType.Trigger, Trigger = SubathonTrigger.SubathonEvent,
                        EventTypes = [$"{SubathonEventType.TwitchSub}"], IgnoreSimulated = true
                    }
                },
                new ActionNode {
                    Id = "2", Step = new ActionStep {
                        Type = ActionStepType.MixItUpCommand, Operation = ActionOperation.Run, Target = "thanks",
                        Body = "who=%trigger.user%\ntype=%trigger.eventtype%"
                    }
                },
                new ActionNode { Id = "3", Step = Hotkey("manual") }
            ],
            Edges = [new ActionEdge { From = "1", To = "2" }]
        };

        Assert.True(ActionGraph.TryParse(graph.ToJson(), out ActionGraph? reloaded));
        Assert.Equal(SubathonTrigger.SubathonEvent, reloaded.Nodes[0].Step.Trigger);
        Assert.False(ActionStep.IsValidOutputName("trigger"));
        await service.SaveCustomActionAsync(new CustomAction { Name = "Sub thanks", Graph = reloaded });

        async Task FireAsync(SubathonEvent ev, int expectRuns) {
            SubathonEvents.RaiseSubathonEventProcessed(ev, true);
            for (var i = 0; i < 100 && runner.Ran.Count < expectRuns; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        await FireAsync(new SubathonEvent {
            EventType = SubathonEventType.TwitchSub, Source = SubathonEventSource.Twitch, User = "Bob",
            ProcessedToSubathon = true
        }, 1);
        Assert.Equal(["thanks"], runner.Ran.ToArray());
        Assert.Equal("who=Bob\ntype=TwitchSub", runner.LastBody);

        await FireAsync(new SubathonEvent {
            EventType = SubathonEventType.TwitchFollow, Source = SubathonEventSource.Twitch, ProcessedToSubathon = true
        }, 0);
        await FireAsync(new SubathonEvent {
            EventType = SubathonEventType.TwitchSub, Source = SubathonEventSource.Simulated, ProcessedToSubathon = true
        }, 0);
        await FireAsync(new SubathonEvent {
            EventType = SubathonEventType.TwitchSub, Source = SubathonEventSource.Twitch, ProcessedToSubathon = false
        }, 0);
        await FireAsync(new SubathonEvent {
            EventType = SubathonEventType.TwitchSub, Source = SubathonEventSource.Twitch, ProcessedToSubathon = true,
            EventTypeMeta = ActionService.FromActionMeta
        }, 0);

        Assert.Single(runner.Ran);

        CustomAction saved = service.FindCustomAction("Sub thanks")!;
        Assert.Equal(ActionRunResult.Done, await service.RunManuallyAsync(saved));
        Assert.Equal(["thanks", "manual"], runner.Ran.ToArray());

        Assert.Equal(ActionRunResult.Done, await service.TestTriggerAsync(saved, "1"));
        Assert.Equal(["thanks", "manual", "thanks"], runner.Ran.ToArray());
        Assert.Equal("who=TestUser\ntype=TwitchSub", runner.LastBody);

        reloaded.Edges.Add(new ActionEdge { From = "3", To = "1" });
        Assert.False(reloaded.IsValid(out _));
    }

    [Fact]
    public async Task QueuedRepeats_WaitTheirTurn_AndTriggerActionsNeverRestartOrIgnore() {
        (ActionService service, FakeRunner runner, _, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;

        async Task<string[]> RunTwiceAsync(ActionRepeatMode mode) {
            runner.Ran.Clear();
            ActionGraph graph = ActionGraph.Sequence(Hotkey("start"), Wait(0.3), Hotkey("end"));
            graph.OnRepeat = mode;
            var ctx = new ActionContext(SubathonEventSource.WheelSpin, "test", $"queue-{mode}");
            Task<ActionRunResult> first = service.RunAsync(Guid.NewGuid(), graph, ctx);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Task<ActionRunResult> second = service.RunAsync(Guid.NewGuid(), graph, ctx);
            Assert.Equal([ActionRunResult.Done, ActionRunResult.Done], await Task.WhenAll(first, second));
            return runner.Ran.ToArray();
        }

        Assert.Equal(["start", "end", "start", "end"], await RunTwiceAsync(ActionRepeatMode.Queue));
        Assert.Equal(["start", "start", "end", "end"], await RunTwiceAsync(ActionRepeatMode.Parallel));

        var withTrigger = new ActionGraph {
            OnRepeat = ActionRepeatMode.Restart,
            Nodes = [
                new ActionNode {
                    Id = "1", Step = new ActionStep { Type = ActionStepType.Trigger, Trigger = SubathonTrigger.TimerPaused }
                }
            ]
        };

        Assert.Equal(ActionRepeatMode.Parallel, withTrigger.EffectiveRepeat);
        withTrigger.OnRepeat = ActionRepeatMode.Queue;
        Assert.Equal(ActionRepeatMode.Queue, withTrigger.EffectiveRepeat);
    }

    [Fact]
    public void TimerEnded_FiresOnceAtZero_AndAgainOnlyAfterTimeWasAddedBack() {
        typeof(SubathonEvents).GetField("SubathonDataUpdate", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        var fired = new List<SubathonTrigger>();
        var watcher = new SubathonTriggerWatcher((t, _, _) => fired.Add(t));
        watcher.Start();
        try {
            var id = Guid.NewGuid();
            void Update(long cumulative, long elapsed) {
                SubathonEvents.RaiseSubathonDataUpdate(new SubathonData {
                    Id = id, MillisecondsCumulative = cumulative, MillisecondsElapsed = elapsed
                }, DateTime.Now);
            }

            Update(10_000, 0);
            Update(10_000, 10_000);
            Update(10_000, 12_000);
            Assert.Equal([SubathonTrigger.TimerEnded], fired.Where(t => t == SubathonTrigger.TimerEnded).ToArray());

            Update(70_000, 12_000);
            Update(70_000, 70_000);
            Assert.Equal(2, fired.Count(t => t == SubathonTrigger.TimerEnded));
        }
        finally {
            watcher.Stop();
        }
    }

    [Fact]
    public async Task RunActionSteps_RunOthers_SkipTurnedOffOnes_AndStopRunningInCircles() {
        (ActionService service, FakeRunner runner, _, SqliteConnection conn) = await SetupAsync();
        await using SqliteConnection __ = conn;

        var child = new CustomAction { Name = "Child", Graph = ActionGraph.Sequence(Hotkey("child")) };
        await service.SaveCustomActionAsync(child);

        ActionStep Step(ActionStepType type, ActionOperation op, CustomAction target) {
            return new ActionStep { Type = type, Operation = op, Target = target.Id.ToString(), TargetName = target.Name };
        }

        var parent = new CustomAction {
            Name = "Parent",
            Graph = ActionGraph.Sequence(Step(ActionStepType.RunAction, ActionOperation.Run, child), Hotkey("after"))
        };
        Assert.Equal(ActionRunResult.Done, await service.RunManuallyAsync(parent));
        Assert.Equal(["child", "after"], runner.Ran.ToArray());

        runner.Ran.Clear();
        Assert.Equal(ActionRunResult.Done, await service.RunAsync(Guid.NewGuid(),
            ActionGraph.Sequence(Step(ActionStepType.SetActionEnabled, ActionOperation.Toggle, child)),
            new ActionContext(SubathonEventSource.WheelSpin, "test", "toggle")));
        Assert.True(service.GetCustomAction(child.Id)!.Disabled);
        
        Assert.Equal(ActionRunResult.Done, await service.RunManuallyAsync(parent));
        Assert.Equal(["after"], runner.Ran.ToArray());
        Assert.Null(service.ResolveGraph(WheelSpinActionType.CustomAction, child.Id.ToString()));

        Assert.True(await service.SetCustomActionEnabledAsync(child.Id, true));
        Assert.Equal(child.Id, service.ResolveActionTarget(new ActionStep {
            Type = ActionStepType.RunAction, Target = Guid.NewGuid().ToString(), TargetName = "child"
        })?.Id);

        var loop = new CustomAction { Name = "Loop" };
        loop.Graph = ActionGraph.Sequence(Hotkey("loop"), Step(ActionStepType.RunAction, ActionOperation.Run, loop));
        loop.Graph.OnRepeat = ActionRepeatMode.Parallel;
        await service.SaveCustomActionAsync(loop);
        runner.Ran.Clear();
        Assert.Equal(ActionRunResult.Paused, await service.RunManuallyAsync(service.GetCustomAction(loop.Id)!));
        Assert.Equal(ActionService.MaxActionDepth + 1, runner.Ran.Count);
    }

    [Fact]
    public void JsonPaths_ReadTextNumbersAndNesting_AndAnythingMissingIsEmpty() {
        const string json = """{"name":"Bob","n":2.5,"ok":true,"list":[{"a":"x"},{"a":"y"}],"obj":{"k":[1,2]}}""";
        Assert.Equal("Bob", ActionStepTypeHelper.ReadJsonPath(json, ".name"));
        Assert.Equal("2.5", ActionStepTypeHelper.ReadJsonPath(json, ".n"));
        Assert.Equal("true", ActionStepTypeHelper.ReadJsonPath(json, ".ok"));
        Assert.Equal("y", ActionStepTypeHelper.ReadJsonPath(json, ".list[1].a"));
        Assert.Equal("[1,2]", ActionStepTypeHelper.ReadJsonPath(json, ".obj.k"));
        Assert.Equal("", ActionStepTypeHelper.ReadJsonPath(json, ".list[5].a"));
        Assert.Equal("", ActionStepTypeHelper.ReadJsonPath(json, ".nope"));
        Assert.Equal("", ActionStepTypeHelper.ReadJsonPath("not json", ".a"));

        Assert.True(ActionStepTypeHelper.Compare("10", ActionOperation.MoreThan, "9"));
        Assert.False(ActionStepTypeHelper.Compare("10", ActionOperation.MoreThan, "9x"));
        Assert.True(ActionStepTypeHelper.Compare("True", ActionOperation.IsEqual, "true"));
        Assert.True(ActionStepTypeHelper.Compare(" ", ActionOperation.IsEmpty, ""));

        Assert.False(ActionStep.IsValidOutputName("points"));
        Assert.False(ActionStep.IsValidOutputName("secret"));
        Assert.True(ActionStep.IsValidOutputName("resp_2"));
    }

    private sealed class FakeRunner : IActionStepRunner {
        public ConcurrentQueue<string> Ran { get; } = new();
        public HashSet<string> FailOnce { get; } = [];
        public string? LastUser { get; private set; }
        public string? LastBody { get; private set; }

        public Dictionary<string, (string Name, string Value)> SetVariables { get; } = [];

        public IReadOnlyCollection<ActionStepType> StepTypes { get; } =
            [ActionStepType.VtsHotkey, ActionStepType.MixItUpCommand];

        public Task<bool> RunStepAsync(ActionStep step, ActionContext ctx, ActionRunProgress progress,
            CancellationToken ct) {
            lock (FailOnce) {
                if (FailOnce.Remove(step.Target)) return Task.FromResult(false);
            }

            LastUser = ctx.User;
            LastBody = step.Body;
            if (SetVariables.TryGetValue(step.Target, out (string Name, string Value) set))
                progress.SetVariable(set.Name, set.Value);
            Ran.Enqueue(step.Target);
            return Task.FromResult(true);
        }
    }
}