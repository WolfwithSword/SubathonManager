using System.Globalization;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Objects;

namespace SubathonManager.Tests.CoreUnitTests;

public class VTSWheelActionTests {

    [Fact]
    public void RoundTrip_Expression_PreservesEveryField() {
        var original = new VTSWheelAction {
            Kind = VtsTargetKind.Expression,
            Target = "cat_ears.exp3.json",
            ToggleAction = VtsToggleAction.On,
            Duration = TimeSpan.FromSeconds(30),
            AfterToggle = VtsToggleAction.Off
        };

        Assert.True(VTSWheelAction.TryParse(original.ToParameterString(), out VTSWheelAction? parsed));
        Assert.Equal(VtsTargetKind.Expression, parsed.Kind);
        Assert.Equal("cat_ears.exp3.json", parsed.Target);
        Assert.Equal(VtsToggleAction.On, parsed.ToggleAction);
        Assert.Equal(TimeSpan.FromSeconds(30), parsed.Duration);
        Assert.Equal(VtsToggleAction.Off, parsed.AfterToggle);
    }

    [Fact]
    public void RoundTrip_Parameter_PreservesBothValues() {
        var original = new VTSWheelAction {
            Kind = VtsTargetKind.Parameter,
            Target = "FaceAngleX",
            Value = 12.5,
            Duration = TimeSpan.FromMinutes(2),
            AfterParameter = VtsParameterAfterAction.SetNewValue,
            AfterValue = -3.25
        };

        Assert.True(VTSWheelAction.TryParse(original.ToParameterString(), out VTSWheelAction? parsed));
        Assert.Equal(VtsTargetKind.Parameter, parsed.Kind);
        Assert.Equal("FaceAngleX", parsed.Target);
        Assert.Equal(12.5, parsed.Value);
        Assert.Equal(TimeSpan.FromMinutes(2), parsed.Duration);
        Assert.Equal(VtsParameterAfterAction.SetNewValue, parsed.AfterParameter);
        Assert.Equal(-3.25, parsed.AfterValue);
    }

    [Fact]
    public void RoundTrip_Hotkey_PreservesAfterAction() {
        var original = new VTSWheelAction {
            Kind = VtsTargetKind.Hotkey,
            Target = "8a5f2c1d9b3e4f60a1b2c3d4e5f60718",
            Duration = TimeSpan.FromSeconds(15),
            AfterHotkey = VtsHotkeyAfterAction.TriggerAgain
        };

        Assert.True(VTSWheelAction.TryParse(original.ToParameterString(), out VTSWheelAction? parsed));
        Assert.Equal(VtsTargetKind.Hotkey, parsed.Kind);
        Assert.Equal("8a5f2c1d9b3e4f60a1b2c3d4e5f60718", parsed.Target);
        Assert.Equal(TimeSpan.FromSeconds(15), parsed.Duration);
        Assert.Equal(VtsHotkeyAfterAction.TriggerAgain, parsed.AfterHotkey);
    }

    [Fact]
    public void ToParameterString_UsesSixPipeSeparatedFields() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Expression,
            Target = "blush.exp3.json",
            ToggleAction = VtsToggleAction.Toggle,
            Duration = TimeSpan.FromSeconds(45),
            AfterToggle = VtsToggleAction.Toggle
        };

        string[] parts = action.ToParameterString().Split('|');
        Assert.Equal(6, parts.Length);
        Assert.Equal("Expression", parts[0]);
        Assert.Equal("blush.exp3.json", parts[1]);
        Assert.Equal("Toggle", parts[2]);
        Assert.Equal("45", parts[3]);
        Assert.Equal("Toggle", parts[4]);
        Assert.Equal("", parts[5]);
    }

    [Fact]
    public void ToParameterString_TrimsTarget() {
        var action = new VTSWheelAction { Kind = VtsTargetKind.Expression, Target = "  spaced.exp3.json  " };
        Assert.Equal("spaced.exp3.json", action.ToParameterString().Split('|')[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("Expression|a|On")] // too few fields
    [InlineData("Sideways|a|On|0|DoNothing|")] // unknown
    [InlineData("Expression|a|Sideways|0|DoNothing|")] // unknown toggle
    [InlineData("Parameter|a|notanumber|0|DoNothing|")] // non-numeric
    public void TryParse_Rejects_MalformedInput(string? parameter) {
        Assert.False(VTSWheelAction.TryParse(parameter, out VTSWheelAction? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void TryParse_UnknownAfterActionName_FallsBackToDefault() {
        Assert.True(VTSWheelAction.TryParse("Parameter|FaceAngleX|1|30|Release|", out VTSWheelAction? parsed));
        Assert.Equal(VtsParameterAfterAction.DoNothing, parsed.AfterParameter);
    }

    [Fact]
    public void TryParse_MissingAfterValueField_IsTolerated() {
        Assert.True(VTSWheelAction.TryParse("Expression|a.exp3.json|On|30|Off", out VTSWheelAction? parsed));
        Assert.Equal(VtsToggleAction.Off, parsed.AfterToggle);
        Assert.Equal(0d, parsed.AfterValue);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("notanumber")]
    [InlineData("")]
    public void TryParse_NonPositiveDuration_BecomesZero(string duration) {
        Assert.True(VTSWheelAction.TryParse($"Expression|a.exp3.json|On|{duration}|Off|",
            out VTSWheelAction? parsed));
        Assert.Equal(TimeSpan.Zero, parsed.Duration);
    }

    [Fact]
    public void Codec_IsCultureInvariant() {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var original = new VTSWheelAction {
                Kind = VtsTargetKind.Parameter,
                Target = "MouthSmile",
                Value = 1.5,
                Duration = TimeSpan.FromSeconds(10),
                AfterParameter = VtsParameterAfterAction.SetNewValue,
                AfterValue = 0.25
            };

            string encoded = original.ToParameterString();
            Assert.Contains("1.5", encoded);
            Assert.DoesNotContain("1,5", encoded);

            Assert.True(VTSWheelAction.TryParse(encoded, out VTSWheelAction? parsed));
            Assert.Equal(1.5, parsed.Value);
            Assert.Equal(0.25, parsed.AfterValue);
        }
        finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void HasRevert_False_WithoutDuration() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Expression,
            Target = "a.exp3.json",
            AfterToggle = VtsToggleAction.Off,
            Duration = TimeSpan.Zero
        };
        Assert.False(action.HasRevert);
    }

    [Fact]
    public void HasRevert_False_ForExpressionDoingNothingAfterwards() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Expression,
            Target = "a.exp3.json",
            Duration = TimeSpan.FromSeconds(30),
            AfterToggle = VtsToggleAction.DoNothing
        };
        Assert.False(action.HasRevert);
    }

    [Fact]
    public void HasRevert_True_ForAnyParameterWithDuration() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Parameter,
            Target = "FaceAngleX",
            Duration = TimeSpan.FromSeconds(30),
            AfterParameter = VtsParameterAfterAction.DoNothing
        };
        Assert.True(action.HasRevert);
    }

    [Fact]
    public void HasRevert_False_ForHotkeyDoingNothingAfterwards() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Hotkey,
            Target = "abc",
            Duration = TimeSpan.FromSeconds(30),
            AfterHotkey = VtsHotkeyAfterAction.DoNothing
        };
        Assert.False(action.HasRevert);
    }

    [Fact]
    public void TimerKey_MatchesForSameTarget_SoRepeatsReArmRatherThanStack() {
        var first = new VTSWheelAction { Kind = VtsTargetKind.Expression, Target = "cat_ears.exp3.json" };
        var second = new VTSWheelAction { Kind = VtsTargetKind.Expression, Target = "CAT_EARS.exp3.json" };
        Assert.Equal(first.TimerKey, second.TimerKey);
    }

    [Fact]
    public void TimerKey_DiffersByKindAndTarget() {
        var expression = new VTSWheelAction { Kind = VtsTargetKind.Expression, Target = "same" };
        var parameter = new VTSWheelAction { Kind = VtsTargetKind.Parameter, Target = "same" };
        var other = new VTSWheelAction { Kind = VtsTargetKind.Expression, Target = "different" };

        Assert.NotEqual(expression.TimerKey, parameter.TimerKey);
        Assert.NotEqual(expression.TimerKey, other.TimerKey);
    }

    [Theory]
    [InlineData(VtsTargetKind.Expression)]
    [InlineData(VtsTargetKind.Parameter)]
    [InlineData(VtsTargetKind.Hotkey)]
    public void IsValid_False_WhenTargetMissing(VtsTargetKind kind) {
        var action = new VTSWheelAction { Kind = kind, Target = "   " };
        Assert.False(action.IsValid(out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void IsValid_False_WhenExpressionRolledActionDoesNothing() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Expression,
            Target = "a.exp3.json",
            ToggleAction = VtsToggleAction.DoNothing
        };
        Assert.False(action.IsValid(out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void IsValid_False_WhenExpressionRevertHasNoDuration() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Expression,
            Target = "a.exp3.json",
            ToggleAction = VtsToggleAction.On,
            AfterToggle = VtsToggleAction.Off,
            Duration = TimeSpan.Zero
        };
        Assert.False(action.IsValid(out _));
    }

    [Fact]
    public void IsValid_False_WhenParameterSetNewValueHasNoDuration() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Parameter,
            Target = "FaceAngleX",
            AfterParameter = VtsParameterAfterAction.SetNewValue,
            Duration = TimeSpan.Zero
        };
        Assert.False(action.IsValid(out _));
    }

    [Fact]
    public void IsValid_False_WhenHotkeyTriggerAgainHasNoDuration() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Hotkey,
            Target = "abc",
            AfterHotkey = VtsHotkeyAfterAction.TriggerAgain,
            Duration = TimeSpan.Zero
        };
        Assert.False(action.IsValid(out _));
    }

    [Fact]
    public void IsValid_True_ForOneWayExpressionToggle() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Expression,
            Target = "a.exp3.json",
            ToggleAction = VtsToggleAction.Toggle,
            AfterToggle = VtsToggleAction.DoNothing,
            Duration = TimeSpan.Zero
        };
        Assert.True(action.IsValid(out string error));
        Assert.Empty(error);
    }

    [Fact]
    public void IsValid_True_ForHeldParameterWithNoDuration() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Parameter,
            Target = "MyCustomParam",
            Value = 1,
            AfterParameter = VtsParameterAfterAction.DoNothing,
            Duration = TimeSpan.Zero
        };
        Assert.True(action.IsValid(out _));
    }

    [Fact]
    public void Describe_StartsWithTarget_SoCallersCanSwapInAFriendlyName() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Hotkey,
            Target = "abc123",
            Duration = TimeSpan.FromSeconds(30),
            AfterHotkey = VtsHotkeyAfterAction.TriggerAgain
        };
        Assert.StartsWith("abc123", action.Describe());
    }

    [Fact]
    public void Describe_OmitsRevertClause_WhenNothingReverts() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Expression,
            Target = "a.exp3.json",
            ToggleAction = VtsToggleAction.On,
            AfterToggle = VtsToggleAction.DoNothing
        };
        Assert.DoesNotContain(" then ", action.Describe());
    }

    [Fact]
    public void Describe_IncludesRevertClauseAndDuration_WhenReverting() {
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Expression,
            Target = "cat_ears.exp3.json",
            ToggleAction = VtsToggleAction.On,
            Duration = TimeSpan.FromSeconds(30),
            AfterToggle = VtsToggleAction.Off
        };

        string described = action.Describe();
        Assert.Contains("cat_ears.exp3.json", described);
        Assert.Contains("On", described);
        Assert.Contains("30s", described);
        Assert.Contains(" then ", described);
        Assert.Contains("Off", described);
    }
}
