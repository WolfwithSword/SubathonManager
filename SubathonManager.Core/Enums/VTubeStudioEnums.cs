namespace SubathonManager.Core.Enums;

public enum VtsTargetKind {
    Expression,
    Parameter,
    Hotkey
}

public enum VtsToggleAction {
    DoNothing,
    On,
    Off,
    Toggle
}

public enum VtsParameterAfterAction {
    DoNothing,
    ResetToOriginal,
    SetNewValue
}

public enum VtsHotkeyAfterAction {
    DoNothing,
    TriggerAgain
}