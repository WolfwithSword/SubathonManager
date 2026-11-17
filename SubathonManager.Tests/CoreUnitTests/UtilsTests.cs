using System.Globalization;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;

namespace SubathonManager.Tests.CoreUnitTests;

public class UtilsTests {
    [Fact]
    public void ParseDurationString() {
        Assert.Equal(TimeSpan.Zero, Utils.ParseDurationString(string.Empty));
        Assert.Equal(TimeSpan.Zero, Utils.ParseDurationString(" "));
        Assert.Equal(TimeSpan.FromHours(1), Utils.ParseDurationString("1:00:00"));
        Assert.Equal(TimeSpan.FromHours(1), Utils.ParseDurationString("1h"));

        Assert.Equal(TimeSpan.FromSeconds(3 + 7 * 60 + 3 * 60 * 60), Utils.ParseDurationString("3h 7m3s"));
        Assert.Equal(TimeSpan.FromSeconds(3 + 7 * 60 + 3 * 60 * 60), Utils.ParseDurationString("3:07:03"));
        Assert.Equal(TimeSpan.FromSeconds(5 * 60 + 1 * 60 * 60 + 9 * 24 * 60 * 60),
            Utils.ParseDurationString("9.1:05:00"));
        Assert.Equal(TimeSpan.FromSeconds(5 * 60 + 1 * 60 * 60 + 9 * 24 * 60 * 60),
            Utils.ParseDurationString("9d5m1h"));

        Assert.Equal(TimeSpan.FromSeconds(10), Utils.ParseDurationString("10"));
        Assert.Equal(TimeSpan.FromSeconds(90), Utils.ParseDurationString("90")); // technically valid

        Assert.Equal(TimeSpan.Zero, Utils.ParseDurationString("10q"));
        Assert.Equal(TimeSpan.Zero, Utils.ParseDurationString("abcd1234"));
        Assert.Equal(TimeSpan.FromHours(1), Utils.ParseDurationString("1h 9f"));

        Assert.Equal(TimeSpan.FromMinutes(5), Utils.ParseDurationString("5:00"));
        Assert.Equal(TimeSpan.FromSeconds(8), Utils.ParseDurationString("0:08"));
        Assert.Equal(TimeSpan.FromSeconds(87), Utils.ParseDurationString("0:87")); // technically valid
    }

    [Fact]
    public void DescribeTokenPointRate() {
        Assert.Equal("= 295 bits / point", Utils.DescribeTokenPointRate("0.34", "bits"));
        Assert.Equal("= 300 bits / point", Utils.DescribeTokenPointRate("0.334", "bits"));
        Assert.Equal("= 200 bits / point", Utils.DescribeTokenPointRate("0.5", "bits"));
        Assert.Equal("= 100 bits / point", Utils.DescribeTokenPointRate("1", "bits"));
        Assert.Equal("= 10 beets / point", Utils.DescribeTokenPointRate("10", "beets"));
        Assert.Equal("= 1 bit / point", Utils.DescribeTokenPointRate("100", "bits"));
        Assert.Equal("= 1 kudos / point", Utils.DescribeTokenPointRate("100", "kudos", "kudos"));

        Assert.Equal("= 1 bit / 2.5 points", Utils.DescribeTokenPointRate("250", "bits"));
        Assert.Equal("= 1 bit / 200 points", Utils.DescribeTokenPointRate("20000", "bits"));
        Assert.Equal("= 1 beet / 1.5 points", Utils.DescribeTokenPointRate("150", "beets"));
        Assert.Equal("= 1 kudos / 12,345.67 points", Utils.DescribeTokenPointRate("1234567", "kudos", "kudos"));
        Assert.Equal("= 10,000 tokens / point", Utils.DescribeTokenPointRate("0.01", "tokens"));

        Assert.Equal("", Utils.DescribeTokenPointRate("0", "bits"));
        Assert.Equal("", Utils.DescribeTokenPointRate("-5", "bits"));
        Assert.Equal("", Utils.DescribeTokenPointRate("", "bits"));
        Assert.Equal("", Utils.DescribeTokenPointRate(null, "bits"));
        Assert.Equal("", Utils.DescribeTokenPointRate("abc", "bits"));
        Assert.Equal("", Utils.DescribeTokenPointRate("0.00000000000000000000000000001", "bits"));
    }

    [Fact]
    public void DescribeMoneyPointRate() {
        Assert.Equal("= 2.95 USD / point", Utils.DescribeMoneyPointRate("0.34", "USD"));
        Assert.Equal("= 0.34 CAD / point", Utils.DescribeMoneyPointRate("3", "CAD"));
        Assert.Equal("= 0.5 EUR / point", Utils.DescribeMoneyPointRate("2", "EUR"));
        Assert.Equal("= 1 USD / point", Utils.DescribeMoneyPointRate("1", "USD"));
        Assert.Equal("= 0.01 USD / point", Utils.DescribeMoneyPointRate("100", "USD"));

        Assert.Equal("= 1 USD / 20,000 points", Utils.DescribeMoneyPointRate("20000", "USD"));
        Assert.Equal("= 1 CAD / 150.5 points", Utils.DescribeMoneyPointRate("150.5", "CAD"));
        Assert.Equal("= 1 / 200 points", Utils.DescribeMoneyPointRate("200", ""));
        Assert.Equal("= 10 JPY / point", Utils.DescribeMoneyPointRate("0.1", "JPY"));
        Assert.Equal("= 1,000 USD / point", Utils.DescribeMoneyPointRate("0.001", "USD"));
        Assert.Equal("= 2.95 / point", Utils.DescribeMoneyPointRate("0.34", ""));
        Assert.Equal("= 3 / point", Utils.DescribeMoneyPointRate("0.334", ""));

        Assert.Equal("", Utils.DescribeMoneyPointRate("0", "USD"));
        Assert.Equal("", Utils.DescribeMoneyPointRate("-2", "USD"));
        Assert.Equal("", Utils.DescribeMoneyPointRate("", "USD"));
        Assert.Equal("", Utils.DescribeMoneyPointRate(null, "USD"));
        Assert.Equal("", Utils.DescribeMoneyPointRate("abc", "USD"));
        Assert.Equal("", Utils.DescribeMoneyPointRate("0.00000000000000000000000000001", "USD"));
    }

    [Fact]
    public void GenerateHashFromString() {
        Assert.Equal(Guid.Parse("bdc43e17-41dc-fb50-8c1c-54a72b1ec93e"),
            Utils.CreateGuidFromUniqueString("subathonmanager"));
    }

    [Fact]
    public void ParseCurrency() {
        Assert.Equal("USD", Utils.TryParseCurrency("USD"));
        Assert.Equal("CAD", Utils.TryParseCurrency("CAD"));
        Assert.Equal("CAD", Utils.TryParseCurrency("CA$"));
        Assert.Equal("CAD", Utils.TryParseCurrency("CA$10.24"));
        Assert.Equal("KRW", Utils.TryParseCurrency("₩"));
        Assert.Equal("AUD", Utils.TryParseCurrency("A$"));
        Assert.Equal("PKR", Utils.TryParseCurrency("PK₨"));
        Assert.Equal("TWD", Utils.TryParseCurrency("NT$"));
        Assert.Equal("VND", Utils.TryParseCurrency("₫"));
        Assert.Equal("VND", Utils.TryParseCurrency("₫10000"));

        Assert.Equal("AAA", Utils.TryParseCurrency("AAA"));

        Assert.Equal(string.Empty, Utils.TryParseCurrency("123"));
        Assert.Equal(string.Empty, Utils.TryParseCurrency(string.Empty));

        Assert.Equal("BRL", Utils.TryParseCurrency("R$"));
        Assert.Equal("HKD", Utils.TryParseCurrency("HK$"));
        Assert.Equal("MXN", Utils.TryParseCurrency("MX$"));
        Assert.Equal("NZD", Utils.TryParseCurrency("NZ$"));

        Assert.Equal("LKR", Utils.TryParseCurrency("LK₨"));
        Assert.Equal("MUR", Utils.TryParseCurrency("MU₨"));
        Assert.Equal("NPR", Utils.TryParseCurrency("NP₨"));

        Assert.Equal("PHP", Utils.TryParseCurrency("₱"));
        Assert.Equal("NGN", Utils.TryParseCurrency("₦"));
        Assert.Equal("UAH", Utils.TryParseCurrency("₴1200"));
        Assert.Equal("PYG", Utils.TryParseCurrency("₲"));
        Assert.Equal("PYG", Utils.TryParseCurrency("PYG"));
        Assert.Equal("CRC", Utils.TryParseCurrency("₡"));
        Assert.Equal("TRY", Utils.TryParseCurrency("₺"));
        Assert.Equal("AZN", Utils.TryParseCurrency("₼"));
        Assert.Equal("KZT", Utils.TryParseCurrency("₸"));
        Assert.Equal("LAK", Utils.TryParseCurrency("₭"));
        Assert.Equal("GEL", Utils.TryParseCurrency("₾"));
        Assert.Equal("MNT", Utils.TryParseCurrency("₮"));
        Assert.Equal("INR", Utils.TryParseCurrency("₹"));
        Assert.Equal("CHF", Utils.TryParseCurrency("₣"));
    }

    [Theory]
    [InlineData("fr-CA")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void TryParseAmount_AcceptsDotRegardlessOfCulture(string culture) {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.True(Utils.TryParseAmount("10.00", out double tip));
            Assert.Equal(10, tip);
            Assert.True(Utils.TryParseAmount(" 0.5 ", out double half));
            Assert.Equal(0.5, half);
            Assert.False(Utils.TryParseAmount("abc", out _));
            Assert.False(Utils.TryParseAmount(null, out _));

            // making sure js will still be fine
            var jsVar = new JsVariable {
                Name = "speed", Value = "0.5", Type = WidgetVariableType.Float
            };
            Assert.Contains("= 0.5", jsVar.GetInjectLine());
        }
        finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("fr-CA")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void TryParseAmount_TreatsTrailingCommaDigitsAsDecimal(string culture) {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.True(Utils.TryParseAmount("0,5", out double half));
            Assert.Equal(0.5, half);
            Assert.True(Utils.TryParseAmount("12,50", out double tip));
            Assert.Equal(12.5, tip);
            Assert.True(Utils.TryParseAmount("-3,25", out double negative));
            Assert.Equal(-3.25, negative);
        }
        finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TryParseAmount_KeepsThousandsCommaInDotCulture() {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");

            Assert.True(Utils.TryParseAmount("1,250", out double thousands));
            Assert.Equal(1250, thousands);
            Assert.True(Utils.TryParseAmount("1,250.00", out double withCents));
            Assert.Equal(1250, withCents);
        }
        finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task ReconnectState_IsReconnecting_PeeksWithoutTakingLock() {
        using var state = new Utils.ServiceReconnectState();

        Assert.False(state.IsReconnecting());
        Assert.False(state.IsReconnecting());
        Assert.True(await state.Lock.WaitAsync(0, TestContext.Current.CancellationToken));
        Assert.True(state.IsReconnecting());

        state.Lock.Release();
        Assert.False(state.IsReconnecting());
    }

    [Fact]
    public async Task ReconnectState_RunAsync_RetriesUntilConnected_ThenStops() {
        using var state =
            new Utils.ServiceReconnectState(TimeSpan.FromMilliseconds(1), 10, TimeSpan.FromMilliseconds(4));
        var attempts = 0;

        await state.RunAsync("test", SubathonEventSource.Unknown, () => attempts < 3, _ => {
            attempts++;
            if (attempts == 1) throw new InvalidOperationException("first try fails");
            return Task.CompletedTask;
        }, null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, attempts);
        Assert.Equal(3, state.Retries);
        Assert.Equal(TimeSpan.FromMilliseconds(4), state.Backoff);
        Assert.False(state.IsReconnecting());
    }

    [Fact]
    public async Task ReconnectState_RunAsync_GivesUpAtMaxRetries_AndSkipsWhenAlreadyRunning() {
        using var state =
            new Utils.ServiceReconnectState(TimeSpan.FromMilliseconds(1), 2, TimeSpan.FromMilliseconds(1));
        var attempts = 0;

        await state.RunAsync("test", SubathonEventSource.Unknown, () => true, _ => {
            attempts++;
            return Task.CompletedTask;
        }, null, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, attempts);

        await state.Lock.WaitAsync(TestContext.Current.CancellationToken);
        await state.RunAsync("test", SubathonEventSource.Unknown, () => true, _ => {
            attempts++;
            return Task.CompletedTask;
        }, null, ct: TestContext.Current.CancellationToken);
        state.Lock.Release();
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(SubathonEventType.TwitchCheer, 1.0)]
    [InlineData(SubathonEventType.PicartoTip, 1.0)]
    [InlineData(SubathonEventType.YouTubeJewels, 0.5)]
    public void AltCurrencyUseAsDonation_UsesFixedModifiers(SubathonEventType type, double expected) {
        var config = new Mock<IConfig>();
        config.Setup(c => c.GetBool("Currency", "BitsLikeAsDonation", It.IsAny<bool>())).Returns(true);
        config.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>())).Returns("3");

        (bool asDonation, double modifier) = Utils.GetAltCurrencyUseAsDonation(config.Object, type);
        Assert.True(asDonation);
        Assert.Equal(expected, modifier);
    }
}
