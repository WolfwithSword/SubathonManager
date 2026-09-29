using SubathonManager.Core.Models;

namespace SubathonManager.Tests.CoreUnitTests;

public class ScheduleItemTests {
    [Theory]
    [InlineData("9", 9 * 60)]
    [InlineData("09", 9 * 60)]
    [InlineData("930", 9 * 60 + 30)]
    [InlineData("0930", 9 * 60 + 30)]
    [InlineData("9:30", 9 * 60 + 30)]
    [InlineData("21:05", 21 * 60 + 5)]
    [InlineData("21.05", 21 * 60 + 5)]
    [InlineData("7:", 7 * 60)]
    [InlineData(" 23:59 ", 23 * 60 + 59)]
    [InlineData("0:00", 0)]
    public void TryParseTime_ValidInput_ReturnsMinutes(string input, int expected) {
        Assert.True(ScheduleItem.TryParseTime(input, out int? minute));
        Assert.Equal(expected, minute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseTime_Blank_ReturnsTrueWithNull(string? input) {
        Assert.True(ScheduleItem.TryParseTime(input, out int? minute));
        Assert.Null(minute);
    }

    [Theory]
    [InlineData("24")]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("960")]
    [InlineData("12345")]
    [InlineData("ab")]
    [InlineData("1:2:3")]
    [InlineData("-1")]
    public void TryParseTime_Invalid_ReturnsFalse(string input) {
        Assert.False(ScheduleItem.TryParseTime(input, out int? minute));
        Assert.Null(minute);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(9 * 60 + 5, "09:05")]
    [InlineData(1440, "00:00")]
    [InlineData(-30, "23:30")]
    public void FormatMinute_WrapsAndPads(int minute, string expected) {
        Assert.Equal(expected, ScheduleItem.FormatMinute(minute));
    }

    [Fact]
    public void TimeLabel_AllDay() {
        var item = new ScheduleItem();
        Assert.True(item.IsAllDay);
        Assert.Equal("All day", item.TimeLabel());
    }

    [Fact]
    public void TimeLabel_StartOnly() {
        var item = new ScheduleItem { StartMinute = 14 * 60 };
        Assert.Equal("14:00", item.TimeLabel());
    }

    [Fact]
    public void TimeLabel_StartAndEnd() {
        var item = new ScheduleItem { StartMinute = 14 * 60, EndMinute = 16 * 60 + 30 };
        Assert.Equal("14:00 - 16:30", item.TimeLabel());
    }

    [Fact]
    public void TimeLabel_PastMidnight_MarksNextDay() {
        var item = new ScheduleItem { StartMinute = 22 * 60, EndMinute = 2 * 60 };
        Assert.Equal("22:00 - 02:00 (+1)", item.TimeLabel());
    }
}
