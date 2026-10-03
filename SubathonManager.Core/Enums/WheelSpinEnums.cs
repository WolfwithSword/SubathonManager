using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Objects;

namespace SubathonManager.Core.Enums;

public enum WheelSpinActionType {
    [WheelSpinActionMeta(HasAction = false, Label = "Manual / Other", QuickLabel = "M")]
    Manual = 0,

    [WheelSpinActionMeta(IsCommand = true, AutoRun = true, Label = "Add Time", QuickLabel = "+Time")]
    AddTime = SubathonCommandType.AddTime,

    [WheelSpinActionMeta(IsCommand = true, AutoRun = true, Label = "Subtract Time", QuickLabel = "-Time")]
    SubtractTime = SubathonCommandType.SubtractTime,

    [WheelSpinActionMeta(IsCommand = true, Label = "Multiplier",  QuickLabel = "Mult")]
    SetMultiplier = SubathonCommandType.SetMultiplier,

    [WheelSpinActionMeta(AutoRun = true, Label = "Add Rerolls",  QuickLabel = "Reroll")]
    Reroll = 1000,

    [WheelSpinActionMeta(AutoRun = true, Label = "VTube Studio", QuickLabel = "VTS")]
    VTubeStudio = 1001,

    [WheelSpinActionMeta(AutoRun = true, Label = "OBS",  QuickLabel = "OBS")]
    OBS = 1002,

    [WheelSpinActionMeta(AutoRun = true, Label = "Custom Action", QuickLabel = "Custom")]
    CustomAction = 1003
}

[ExcludeFromCodeCoverage]
public static class WheelSpinActionTypeHelper {

    public static string GetQuickLabel(this WheelSpinActionType type) {
        return EnumMetaCache.Get<WheelSpinActionMetaAttribute>(type)?.QuickLabel ?? $"{type}";
    }
    public static string GetLabel(this WheelSpinActionType type) {
        return EnumMetaCache.Get<WheelSpinActionMetaAttribute>(type)?.Label ?? $"{type}";
    }

    public static bool HasAction(this WheelSpinActionType type) {
        return EnumMetaCache.Get<WheelSpinActionMetaAttribute>(type)?.HasAction ?? true;
    }

    public static bool IsCommand(this WheelSpinActionType type) {
        return EnumMetaCache.Get<WheelSpinActionMetaAttribute>(type)?.IsCommand ?? false;
    }

    public static bool IsAutoRun(this WheelSpinActionType type) {
        return EnumMetaCache.Get<WheelSpinActionMetaAttribute>(type)?.AutoRun ?? false;
    }

    public static bool IsAvailable(this WheelSpinActionType type) {
        if (type == WheelSpinActionType.VTubeStudio && !FeatureFlags.VTubeStudioEnabled)
            return false;
        return true;
    }

    public static bool HasPlayAction(this WheelSpinActionType type) {
        return type.IsAvailable() && type.HasAction() && type != WheelSpinActionType.Reroll;
    }

    public static SubathonCommandType ToCommandType(this WheelSpinActionType type) {
        if (!type.IsCommand())
            return SubathonCommandType.Unknown;
        return (SubathonCommandType)(int)type;
    }

    public static ActionGraph? BuildActionGraph(this WheelSpinActionType type, string? parameter) {
        parameter = (parameter ?? "").Trim();
        switch (type) {
            case WheelSpinActionType.AddTime:
            case WheelSpinActionType.SubtractTime: {
                TimeSpan duration = Utils.ParseDurationString(parameter);
                if (duration <= TimeSpan.Zero) return null;
                return ActionGraph.Sequence(new ActionStep {
                    Type = type == WheelSpinActionType.AddTime ? ActionStepType.AddTime : ActionStepType.SubtractTime,
                    Seconds = duration.TotalSeconds
                });
            }
            case WheelSpinActionType.SetMultiplier: {
                string[] parts = parameter.Split('|');
                if (parts.Length < 4 || !Utils.TryParseAmount(parts[0], out double amount)) return null;
                TimeSpan duration = Utils.ParseDurationString(parts[1]);
                bool.TryParse(parts[2], out bool points);
                bool.TryParse(parts[3], out bool time);
                return ActionGraph.Sequence(new ActionStep {
                    Type = ActionStepType.SetMultiplier,
                    Operation = points == time ? ActionOperation.PointsAndTime : points ? ActionOperation.Points : ActionOperation.Time,
                    Value = amount,
                    Seconds = duration > TimeSpan.Zero ? duration.TotalSeconds : null
                });
            }
            case WheelSpinActionType.Reroll:
                if (!int.TryParse(parameter, out int count) || count < 1) return null;
                return ActionGraph.Sequence(new ActionStep { Type = ActionStepType.Reroll, Value = count });
            case WheelSpinActionType.VTubeStudio:
                return VTSWheelAction.TryParse(parameter, out VTSWheelAction? vts) ? vts.ToActionGraph() : null;
            case WheelSpinActionType.OBS:
                return ActionGraph.TryParse(parameter, out ActionGraph? graph) && graph.Nodes.Count > 0 ? graph : null;
            default:
                return null;
        }
    }

    public static string RepeatKey(this WheelSpinActionType type, string? parameter, Guid itemId) {
        switch (type) {
            case WheelSpinActionType.VTubeStudio when VTSWheelAction.TryParse(parameter, out VTSWheelAction? vts):
                return vts.TimerKey;
            case WheelSpinActionType.OBS when ActionGraph.TryParse(parameter, out ActionGraph? graph)
                                              && graph.Nodes.Count > 0: {
                ActionStep first = graph.Nodes[0].Step;
                return $"obs-{first.Type}-{first.Scope}-{first.Target}".ToLowerInvariant();
            }
            case WheelSpinActionType.CustomAction:
                return $"custom-action-{parameter}".ToLowerInvariant();
            default:
                return $"wheel-item-{itemId}";
        }
    }
}

public enum WheelSpinHistoryStatus {
    Pending,
    Done,
    Cancelled,
    Running
}