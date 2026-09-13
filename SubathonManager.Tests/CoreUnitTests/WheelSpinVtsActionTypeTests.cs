using SubathonManager.Core.Enums;

namespace SubathonManager.Tests.CoreUnitTests;

public class WheelSpinVtsActionTypeTests {
    [Fact]
    public void VTubeStudio_HasAConfigurableAction() {
        Assert.True(WheelSpinActionType.VTubeStudio.HasAction());
    }

    [Fact]
    public void VTubeStudio_IsNotACommand() {
        Assert.False(WheelSpinActionType.VTubeStudio.IsCommand());
        Assert.Equal(SubathonCommandType.Unknown, WheelSpinActionType.VTubeStudio.ToCommandType());
    }

    [Fact]
    public void VTubeStudio_IsNotDoneImmediately() {
        Assert.False(WheelSpinActionType.VTubeStudio.IsDoneImmediately());
    }

    [Fact]
    public void VTubeStudio_GetsAPlayButtonInHistory() {
        Assert.True(WheelSpinActionType.VTubeStudio.HasPlayAction());
    }

    [Fact]
    public void HasPlayAction_CoversCommandsAndVTubeStudioOnly() {
        Assert.True(WheelSpinActionType.AddTime.HasPlayAction());
        Assert.True(WheelSpinActionType.SubtractTime.HasPlayAction());
        Assert.True(WheelSpinActionType.SetMultiplier.HasPlayAction());
        Assert.True(WheelSpinActionType.VTubeStudio.HasPlayAction());

        Assert.False(WheelSpinActionType.Manual.HasPlayAction());
        Assert.False(WheelSpinActionType.Reroll.HasPlayAction());
    }

    [Fact]
    public void VTubeStudio_HasAFriendlyLabel() {
        Assert.Equal("VTube Studio", WheelSpinActionType.VTubeStudio.GetLabel());
    }

    [Fact]
    public void VTubeStudio_DoesNotCollideWithAnotherActionValue() {
        WheelSpinActionType[] all = Enum.GetValues<WheelSpinActionType>();
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.Contains(WheelSpinActionType.VTubeStudio, all);
    }

    [Fact]
    public void EveryActionType_HasALabel() {
        foreach (WheelSpinActionType type in Enum.GetValues<WheelSpinActionType>())
            Assert.False(string.IsNullOrWhiteSpace(type.GetLabel()));
    }

    [Fact]
    public void VTubeStudioSource_IsGroupedUnderExternalSoftware() {
        Assert.Equal(SubathonSourceGroup.ExternalSoftware, SubathonEventSource.VTubeStudio.GetGroup());
        Assert.Equal("VTube Studio", SubathonEventSource.VTubeStudio.GetDescription());
    }
}
