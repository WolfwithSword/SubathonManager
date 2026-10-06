namespace SubathonManager.Core.Enums;

public enum SubathonTrigger {
    [EnumMeta(Label = "Subathon Event", Order = 0,
        Description = "Any subathon event (subs, donations, orders, commands, etc.)")]
    SubathonEvent,

    [EnumMeta(Label = "Timer Paused", Order = 1, Description = "The timer was paused")]
    TimerPaused,

    [EnumMeta(Label = "Timer Resumed", Order = 2, Description = "The timer was resumed")]
    TimerResumed,

    [EnumMeta(Label = "Timer Locked", Order = 3, Description = "The subathon was locked")]
    TimerLocked,

    [EnumMeta(Label = "Timer Unlocked", Order = 4, Description = "The subathon was unlocked")]
    TimerUnlocked,

    [EnumMeta(Label = "Multiplier Started", Order = 6, Description = "A multiplier started")]
    MultiplierStarted,

    [EnumMeta(Label = "Multiplier Ended", Order = 7, Description = "A multiplier ended or was stopped")]
    MultiplierEnded,

    [EnumMeta(Label = "Goal Completed", Order = 8, Description = "A goal was completed")]
    GoalCompleted,

    [EnumMeta(Label = "Wheel Spin Start", Order = 9, Description = "A wheel spin started")]
    WheelSpinStart,

    [EnumMeta(Label = "Wheel Spin End", Order = 10, Description = "A wheel spin result came in")]
    WheelSpinEnd,

    [EnumMeta(Label = "Prompt Started", Order = 11, Description = "A prompt run started")]
    PromptStarted,

    [EnumMeta(Label = "Prompt Ended", Order = 12, Description = "A prompt run completed, expired or was cancelled")]
    PromptEnded,

    [EnumMeta(Label = "Timer Ended", Order = 5,
        Description = "The timer reached 0")]
    TimerEnded
}
