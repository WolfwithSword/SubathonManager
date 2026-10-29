using System.Globalization;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;

namespace SubathonManager.Core.Objects;

public sealed class SubathonTriggerWatcher(Action<SubathonTrigger, Dictionary<string, string>, SubathonEvent?> fire) {
    private readonly Lock _lock = new();
    private bool _lastLocked;
    private MultiplierSnapshot? _lastMultiplier;
    private bool _lastPaused;
    private bool _lastEnded;
    private Guid? _trackedSubathonId;
    private bool _started;

    public void Start() {
        Stop();
        SubathonEvents.SubathonEventProcessed += OnSubathonEventProcessed;
        SubathonEvents.SubathonDataUpdate += OnSubathonDataUpdate;
        SubathonEvents.SubathonGoalCompleted += OnGoalCompleted;
        SubathonEvents.PromptRunStarted += OnPromptRunStarted;
        SubathonEvents.PromptRunUpdate += OnPromptRunUpdate;
        WheelEvents.WheelSpinStarted += OnWheelSpinStarted;
        WheelEvents.WheelSpinResult += OnWheelSpinResult;
        _started = true;
    }

    public void Stop() {
        SubathonEvents.SubathonEventProcessed -= OnSubathonEventProcessed;
        SubathonEvents.SubathonDataUpdate -= OnSubathonDataUpdate;
        SubathonEvents.SubathonGoalCompleted -= OnGoalCompleted;
        SubathonEvents.PromptRunStarted -= OnPromptRunStarted;
        SubathonEvents.PromptRunUpdate -= OnPromptRunUpdate;
        WheelEvents.WheelSpinStarted -= OnWheelSpinStarted;
        WheelEvents.WheelSpinResult -= OnWheelSpinResult;
        if (!_started) return;
        _started = false;
        lock (_lock) {
            _trackedSubathonId = null;
            _lastMultiplier = null;
        }
    }

    private void OnSubathonEventProcessed(SubathonEvent subathonEvent, bool effective) {
        fire(SubathonTrigger.SubathonEvent, EventValues(subathonEvent), subathonEvent);
    }

    private void OnSubathonDataUpdate(SubathonData subathon, DateTime timestamp) {
        var fired = new List<(SubathonTrigger, Dictionary<string, string>)>(3);
        MultiplierSnapshot? multiplier = subathon.Multiplier?.SubathonId == subathon.Id
            ? MultiplierSnapshot.From(subathon.Multiplier) : null;

        lock (_lock) {
            if (_trackedSubathonId != subathon.Id) {
                _trackedSubathonId = subathon.Id;
                _lastPaused = subathon.IsPaused;
                _lastLocked = subathon.IsLocked;
                _lastEnded = HasEnded(subathon);
                _lastMultiplier = multiplier;
                return;
            }

            if (subathon.IsPaused != _lastPaused) {
                _lastPaused = subathon.IsPaused;
                fired.Add((subathon.IsPaused ? SubathonTrigger.TimerPaused : SubathonTrigger.TimerResumed,
                    TimerValues(subathon)));
            }

            if (subathon.IsLocked != _lastLocked) {
                _lastLocked = subathon.IsLocked;
                fired.Add((subathon.IsLocked ? SubathonTrigger.TimerLocked : SubathonTrigger.TimerUnlocked,
                    TimerValues(subathon)));
            }

            bool ended = HasEnded(subathon);
            if (ended != _lastEnded) {
                _lastEnded = ended;
                if (ended) fired.Add((SubathonTrigger.TimerEnded, TimerValues(subathon)));
            }

            if (multiplier != null) {
                MultiplierSnapshot? previous = _lastMultiplier;
                _lastMultiplier = multiplier;
                if (previous != null) {
                    if (multiplier.Running && multiplier != previous)
                        fired.Add((SubathonTrigger.MultiplierStarted, MultiplierValues(multiplier)));
                    else if (!multiplier.Running && previous.Running)
                        fired.Add((SubathonTrigger.MultiplierEnded, MultiplierValues(previous)));
                }
            }
        }

        foreach ((SubathonTrigger trigger, Dictionary<string, string> values) in fired)
            fire(trigger, values, null);
    }

    private void OnGoalCompleted(SubathonGoal goal, long currentValue) {
        fire(SubathonTrigger.GoalCompleted, new Dictionary<string, string> {
            ["goaltext"] = goal.Text,
            ["goaltarget"] = Number(goal.Points),
            ["goalcurrent"] = Number(currentValue)
        }, null);
    }

    private void OnWheelSpinStarted(WheelSet wheel, int delaySeconds) {
        fire(SubathonTrigger.WheelSpinStart, new Dictionary<string, string> {
            ["wheelname"] = wheel.Name,
            ["wheelid"] = wheel.Id.ToString(),
            ["spindelay"] = Number(delaySeconds)
        }, null);
    }

    private void OnWheelSpinResult(WheelSet wheel, WheelItem? item, WheelSpinHistory history, int spinsOwed) {
        fire(SubathonTrigger.WheelSpinEnd, new Dictionary<string, string> {
            ["wheelname"] = wheel.Name,
            ["wheelid"] = wheel.Id.ToString(),
            ["wheelitem"] = item?.Text ?? "",
            ["spinstatus"] = history.Status.ToString(),
            ["spinsowed"] = Number(spinsOwed)
        }, null);
    }

    private void OnPromptRunStarted(SubathonPromptRun run, SubathonPrompt? prompt) {
        fire(SubathonTrigger.PromptStarted, PromptValues(run, prompt), null);
    }

    private void OnPromptRunUpdate(SubathonPromptRun run, SubathonPrompt? prompt) {
        if (run.IsActive) return;
        fire(SubathonTrigger.PromptEnded, PromptValues(run, prompt), null);
    }

    private static bool HasEnded(SubathonData subathon) {
        return !subathon.IsSubathonReversed() && subathon.MillisecondsRemaining() <= 0;
    }

    private static string Number(IFormattable value) {
        return value.ToString(null, CultureInfo.InvariantCulture);
    }

    public static Dictionary<string, string> EventValues(SubathonEvent subathonEvent) {
        var eventType = $"{subathonEvent.EventType}";
        string? trueSource = subathonEvent.EventType.GetTypeTrueSource(subathonEvent.EventTypeMeta);
        if (subathonEvent.EventType == SubathonEventType.GoAffProOrder
            && GoAffProOrderHelper.TryGetStore(subathonEvent.EventTypeMeta, out GoAffProStore? store)) {
            trueSource = store.InternalName;
            eventType = store.InternalEventName;
        }

        double seconds = subathonEvent.GetFinalSecondsValueRaw() < 0.5 ? 0 : subathonEvent.GetFinalSecondsValue();
        return new Dictionary<string, string> {
            ["eventtype"] = eventType,
            ["source"] = $"{subathonEvent.Source}",
            ["truesource"] = trueSource ?? "",
            ["subtype"] = $"{subathonEvent.EventType.GetSubType()}",
            ["user"] = subathonEvent.User ?? "",
            ["value"] = subathonEvent.Value,
            ["amount"] = Number(subathonEvent.Amount),
            ["currency"] = subathonEvent.Currency ?? "",
            ["command"] = $"{subathonEvent.Command}",
            ["secondsadded"] = Number(seconds),
            ["pointsadded"] = Number(subathonEvent.GetFinalPointsValue()),
            ["secondaryvalue"] = subathonEvent.SecondaryValue,
            ["tertiaryvalue"] = subathonEvent.TertiaryValue,
            ["reversed"] = $"{subathonEvent.WasReversed}"
        };
    }

    public static Dictionary<string, string> TimerValues(SubathonData subathon) {
        TimeSpan remaining = subathon.TimeRemainingRounded();
        return new Dictionary<string, string> {
            ["timeremaining"] = $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}",
            ["secondsremaining"] = Number((long)remaining.TotalSeconds),
            ["points"] = Number(subathon.Points),
            ["paused"] = $"{subathon.IsPaused}",
            ["locked"] = $"{subathon.IsLocked}"
        };
    }

    public static Dictionary<string, string> MultiplierValues(MultiplierSnapshot multiplier) {
        return new Dictionary<string, string> {
            ["multiplier"] = Number(multiplier.Multiplier),
            ["multipliertime"] = $"{multiplier.Time}",
            ["multiplierpoints"] = $"{multiplier.Points}",
            ["multiplierduration"] = Number((long)(multiplier.Duration?.TotalSeconds ?? 0)),
            ["multiplierhypetrain"] = $"{multiplier.FromHypeTrain}"
        };
    }

    public static Dictionary<string, string> PromptValues(SubathonPromptRun run, SubathonPrompt? prompt) {
        prompt ??= run.LinkedPrompt;
        return new Dictionary<string, string> {
            ["prompttext"] = prompt?.Text ?? "",
            ["prompttype"] = $"{prompt?.Type}",
            ["prompttarget"] = Number(prompt?.Value ?? run.SnapshotTargetValue),
            ["promptduration"] =
                Number((long)(prompt?.CompletionDuration.TotalSeconds ?? (run.ExpiresAt - run.StartedAt).TotalSeconds)),
            ["promptstatus"] = $"{run.Status}"
        };
    }

    public static Dictionary<string, string> SampleValues(SubathonTrigger trigger) {
        switch (trigger) {
            case SubathonTrigger.SubathonEvent:
                return EventValues(new SubathonEvent {
                    Source = SubathonEventSource.Simulated,
                    EventType = SubathonEventType.ExternalDonation,
                    User = "TestUser",
                    Value = "5",
                    Currency = "USD",
                    SecondsValue = 600,
                    PointsValue = 5,
                    ProcessedToSubathon = true
                });
            case SubathonTrigger.MultiplierStarted:
            case SubathonTrigger.MultiplierEnded:
                return MultiplierValues(new MultiplierSnapshot(trigger == SubathonTrigger.MultiplierStarted,
                    2, true, true, TimeSpan.FromMinutes(10), DateTime.Now, false));
            case SubathonTrigger.TimerPaused:
            case SubathonTrigger.TimerResumed:
            case SubathonTrigger.TimerLocked:
            case SubathonTrigger.TimerUnlocked:
            case SubathonTrigger.TimerEnded:
                return new Dictionary<string, string> {
                    ["timeremaining"] = "12:34:56",
                    ["secondsremaining"] = "45296",
                    ["points"] = "250",
                    ["paused"] = (trigger == SubathonTrigger.TimerPaused).ToString(),
                    ["locked"] = (trigger == SubathonTrigger.TimerLocked).ToString()
                };
            case SubathonTrigger.GoalCompleted:
                return new Dictionary<string, string> {
                    ["goaltext"] = "Test Goal",
                    ["goaltarget"] = "100",
                    ["goalcurrent"] = "100"
                };
            case SubathonTrigger.WheelSpinStart:
                return new Dictionary<string, string> {
                    ["wheelname"] = "Test Wheel",
                    ["wheelid"] = Guid.Empty.ToString(),
                    ["spindelay"] = "0"
                };
            case SubathonTrigger.WheelSpinEnd:
                return new Dictionary<string, string> {
                    ["wheelname"] = "Test Wheel",
                    ["wheelid"] = Guid.Empty.ToString(),
                    ["wheelitem"] = "Test Item",
                    ["spinstatus"] = "Pending",
                    ["spinsowed"] = "0"
                };
            case SubathonTrigger.PromptStarted:
            case SubathonTrigger.PromptEnded:
                return new Dictionary<string, string> {
                    ["prompttext"] = "Test Prompt",
                    ["prompttype"] = $"{SubathonPromptType.Points}",
                    ["prompttarget"] = "10",
                    ["promptduration"] = "300",
                    ["promptstatus"] = trigger == SubathonTrigger.PromptStarted
                        ? $"{SubathonPromptRunStatus.Active}"
                        : $"{SubathonPromptRunStatus.Completed}"
                };
            default:
                return new Dictionary<string, string>();
        }
    }
}

public sealed record MultiplierSnapshot(
    bool Running,
    double Multiplier,
    bool Time,
    bool Points,
    TimeSpan? Duration,
    DateTime? Started,
    bool FromHypeTrain) {
    public static MultiplierSnapshot From(MultiplierData data) {
        return new MultiplierSnapshot(data.IsRunning(), data.Multiplier, data.ApplyToSeconds,
            data.ApplyToPoints, data.Duration, data.Started, data.FromHypeTrain);
    }
}
