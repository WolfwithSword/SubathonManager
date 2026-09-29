namespace SubathonManager.Core.Enums;

public enum MixItUpTrigger {
    [EnumMeta(Label = "Subathon Event", Order = 0,
        Description = "Any subathon event (subs, donations, orders, etc.), and commands if enabled")]
    SubathonEvent,

    [EnumMeta(Label = "Timer Paused", Order = 1, Description = "The subathon timer was paused")]
    TimerPaused,

    [EnumMeta(Label = "Timer Resumed", Order = 2, Description = "The subathon timer was resumed")]
    TimerResumed,

    [EnumMeta(Label = "Timer Locked", Order = 3, Description = "The subathon was locked")]
    TimerLocked,

    [EnumMeta(Label = "Timer Unlocked", Order = 4, Description = "The subathon was unlocked")]
    TimerUnlocked,

    [EnumMeta(Label = "Multiplier Started", Order = 5, Description = "A multiplier started")]
    MultiplierStarted,

    [EnumMeta(Label = "Multiplier Ended", Order = 6, Description = "A multiplier ended or was stopped")]
    MultiplierEnded,

    [EnumMeta(Label = "Goal Completed", Order = 7, Description = "A goal was reached")]
    GoalCompleted,

    [EnumMeta(Label = "Wheel Spin Start", Order = 8, Description = "A wheel spin started")]
    WheelSpinStart,

    [EnumMeta(Label = "Wheel Spin End", Order = 9, Description = "A wheel spin landed on a result")]
    WheelSpinEnd,

    [EnumMeta(Label = "Prompt Started", Order = 10, Description = "A prompt run started")]
    PromptStarted,

    [EnumMeta(Label = "Prompt Ended", Order = 11, Description = "A prompt run completed, expired or was cancelled")]
    PromptEnded
}
