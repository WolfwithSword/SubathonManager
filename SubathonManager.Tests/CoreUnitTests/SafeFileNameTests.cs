namespace SubathonManager.Tests.CoreUnitTests;

public class SafeFileNameTests
{
    
    [Theory]
    [InlineData('"')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('|')]
    [InlineData(':')]
    [InlineData('*')]
    [InlineData('?')]
    [InlineData('\\')]
    [InlineData('/')]
    [InlineData('\0')]
    public void IsInvalidChar_WindowsSet_IsRejectedOnEveryPlatform(char value)
        => Assert.True(SafeFileName.IsInvalidChar(value));

    [Theory]
    [InlineData('\u0001')]
    [InlineData('\u001F')]
    [InlineData('\u007F')] // if someone does a del character they deserve it to crash tbh
    public void IsInvalidChar_ControlCharacters_AreRejected(char value)
        => Assert.True(SafeFileName.IsInvalidChar(value));

    [Theory]
    [InlineData('a')]
    [InlineData('Z')]
    [InlineData('0')]
    [InlineData('-')]
    [InlineData('_')]
    [InlineData('.')]
    [InlineData(' ')]
    [InlineData('(')]
    [InlineData('ü')]
    public void IsInvalidChar_OrdinaryCharacters_AreAllowed(char value)
        => Assert.False(SafeFileName.IsInvalidChar(value));

    [Fact]
    public void IsInvalidChar_CoversEverythingThePlatformReports()
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            Assert.True(SafeFileName.IsInvalidChar(c), $"platform rejects U+{(int)c:X4} but we allow it");
    }
        
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Sanitize_BlankInput_ReturnsFallback(string? value)
    {
        Assert.Equal(string.Empty, SafeFileName.Sanitize(value));
        Assert.Equal("export", SafeFileName.Sanitize(value, fallback: "export"));
    }

    [Fact]
    public void Sanitize_NothingSurvives_ReturnsFallback()
    {
        Assert.Equal("export", SafeFileName.Sanitize("///", replacement: string.Empty, fallback: "export"));
        Assert.Equal("export", SafeFileName.Sanitize("...", fallback: "export"));
        Assert.Equal("export", SafeFileName.Sanitize(" . . ", fallback: "export"));
    }

    [Fact]
    public void Sanitize_AllInvalidButReplaced_KeepsTheReplacements()
        => Assert.Equal("___", SafeFileName.Sanitize("<>|", fallback: "export"));

        
    [Fact]
    public void Sanitize_LeavesAnAlreadyValidNameAlone()
        => Assert.Equal("My Timer v1.0.0", SafeFileName.Sanitize("My Timer v1.0.0"));

    [Theory]
    [InlineData("A/B", "A_B")]
    [InlineData("A\\B", "A_B")]
    [InlineData("A:B", "A_B")] // valid on linux but making it xplat compat
    [InlineData("A*B", "A_B")]
    [InlineData("A?B", "A_B")]
    [InlineData("A<B", "A_B")]
    [InlineData("A>B", "A_B")]
    [InlineData("A|B", "A_B")]
    [InlineData("A\"B", "A_B")]
    public void Sanitize_ReplacesEachInvalidCharacter(string input, string expected)
        => Assert.Equal(expected, SafeFileName.Sanitize(input));

    [Fact]
    public void Sanitize_ReplacesEveryOccurrence()
        => Assert.Equal("a_b_c_d", SafeFileName.Sanitize("a:b*c?d"));

    [Fact]
    public void Sanitize_EmptyReplacement_DropsInvalidCharacters()
        => Assert.Equal("AB", SafeFileName.Sanitize("A:B", replacement: string.Empty));

    [Fact]
    public void Sanitize_CustomReplacement_IsUsedVerbatim()
        => Assert.Equal("A-B", SafeFileName.Sanitize("A:B", replacement: "-"));

    [Fact]
    public void Sanitize_StripsControlCharacters()
        => Assert.Equal("ab", SafeFileName.Sanitize("a\u0001b", replacement: string.Empty));

    [Fact]
    public void Sanitize_KeepsNonAsciiAndSpaces()
        => Assert.Equal("Über Timer", SafeFileName.Sanitize("Über Timer"));

        
    [Fact]
    public void Sanitize_TrimsSurroundingWhitespace()
        => Assert.Equal("Timer", SafeFileName.Sanitize("   Timer   "));

    [Theory]
    [InlineData("report.", "report")]
    [InlineData("report...", "report")]
    [InlineData("report ", "report")]
    [InlineData("report . . ", "report")]
    public void Sanitize_StripsTrailingDotsAndSpaces(string input, string expected)
    {
        Assert.Equal(expected, SafeFileName.Sanitize(input));
    }

    [Fact]
    public void Sanitize_KeepsInteriorAndLeadingDots()
    {
        Assert.Equal("v1.0.0", SafeFileName.Sanitize("v1.0.0"));
        Assert.Equal(".hidden", SafeFileName.Sanitize(".hidden"));
    }

            // sneaky....

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    public void Sanitize_ReservedDeviceNames_ArePrefixed(string name)
        => Assert.Equal("_" + name, SafeFileName.Sanitize(name));

    [Fact]
    public void Sanitize_ReservedNameWithExtension_IsAlsoPrefixed()
    {
        Assert.Equal("_CON.csv", SafeFileName.Sanitize("CON.csv"));
        Assert.Equal("_nul.txt", SafeFileName.Sanitize("nul.txt"));
    }

    [Theory]
    [InlineData("CONTROL")]
    [InlineData("CONSOLE")]
    [InlineData("COM")]
    [InlineData("COM10")]
    [InlineData("MyCON")]
    public void Sanitize_NamesThatMerelyLookReserved_AreLeftAlone(string name)
        => Assert.Equal(name, SafeFileName.Sanitize(name));

        
    [Fact]
    public void Sanitize_OverlongName_IsTruncated()
    {
        string result = SafeFileName.Sanitize(new string('a', 400));

        Assert.Equal(SafeFileName.MaxLength, result.Length);
    }

    [Fact]
    public void Sanitize_TruncationDoesNotLeaveATrailingDot()
    {
        string result = SafeFileName.Sanitize(new string('a', 254) + "." + new string('b', 20));

        Assert.Equal(254, result.Length);
        Assert.DoesNotContain('.', result);
    }

    [Fact]
    public void Sanitize_NameAtTheLimit_IsUntouched()
    {
        string name = new('a', SafeFileName.MaxLength);
        Assert.Equal(name, SafeFileName.Sanitize(name));
    }

        
    [Theory]
    [InlineData("Timer")]
    [InlineData("My Timer v1.0.0")]
    [InlineData(".hidden")]
    [InlineData("Über")]
    public void IsSafe_UsableNames_AreTrue(string value)
        => Assert.True(SafeFileName.IsSafe(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A:B")]
    [InlineData("A/B")]
    [InlineData("report.")]
    [InlineData("  padded  ")]
    [InlineData("CON")]
    public void IsSafe_ProblemNames_AreFalse(string? value)
        => Assert.False(SafeFileName.IsSafe(value));

    [Fact]
    public void IsSafe_IsTrueForAnythingSanitizeProduces()
    {
        foreach (var input in new[] { "A:B", "  spaced  ", "report.", "CON", "<>|", new string('a', 400) })
            Assert.True(SafeFileName.IsSafe(SafeFileName.Sanitize(input, fallback: "export")),
                $"Sanitize({input}) produced something IsSafe rejects");
    }

        
    [Theory]
    [InlineData("A:B")]
    [InlineData("report.")]
    [InlineData("CON")]
    [InlineData("  spaced  ")]
    [InlineData("nul.txt")]
    public void Sanitize_IsIdempotent(string input)
    {
        string once = SafeFileName.Sanitize(input, fallback: "export");
        Assert.Equal(once, SafeFileName.Sanitize(once, fallback: "export"));
    }

}
