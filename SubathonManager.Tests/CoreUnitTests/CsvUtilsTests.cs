using System.Globalization;
using SubathonManager.Core.Enums;

namespace SubathonManager.Tests.CoreUnitTests;

public class CsvUtilsTests {
    [Fact]
    public void Escape_QuotesOnlyWhenNeeded() {
        Assert.Equal("Test", CsvUtils.Escape("Test"));
        Assert.Equal(string.Empty, CsvUtils.Escape(string.Empty));
        Assert.Equal(string.Empty, CsvUtils.Escape("   "));
        Assert.Equal(string.Empty, CsvUtils.Escape(null));
        Assert.Equal("\"Test1,Test2\"", CsvUtils.Escape("Test1,Test2"));
        Assert.Equal("\"\"\"Test1\"\"\"", CsvUtils.Escape("\"Test1\""));
        Assert.Equal("\"Test\r\nTest2\"", CsvUtils.Escape("Test\r\nTest2"));
    }

    [Fact]
    public void Escape_FormatsValuesInvariantRegardlessOfCulture() {
        CultureInfo original = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.5", CsvUtils.Escape(1.5));
            Assert.Equal("1234.25", CsvUtils.Escape(1234.25m));
            Assert.Equal("2026-10-03 14:05:09", CsvUtils.Escape(new DateTime(2026, 10, 3, 14, 5, 9)));
            Assert.Equal("True", CsvUtils.Escape(true));
            Assert.Equal(nameof(ScheduleItemKind.Event), CsvUtils.Escape(ScheduleItemKind.Event));
            Assert.Equal(string.Empty, CsvUtils.Escape((int?)null));
        }
        finally {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Write_EmitsHeaderRowsThenProjectedRows() {
        string csv = CsvUtils.Write(
            [["#Meta=1", "Other=2"], [], ["Name", "Value"]],
            new[] { ("a,b", 1), ("plain", 2) },
            r => [r.Item1, r.Item2]);

        Assert.Equal(["#Meta=1,Other=2", "", "Name,Value", "\"a,b\",1", "plain,2"],
            csv.TrimEnd().Split(Environment.NewLine));
    }

    [Fact]
    public void ReadRecords_HandlesQuotedCommasQuotesAndNewlines() {
        const string csv = "Name,Note\r\n\"a,b\",\"line one\nline \"\"two\"\"\"\r\n\r\nlast,\n";

        List<(int Line, string[] Fields)> records = CsvUtils.ReadRecords(csv);

        Assert.Equal(3, records.Count);
        Assert.Equal(["Name", "Note"], records[0].Fields);
        Assert.Equal(["a,b", "line one\nline \"two\""], records[1].Fields);
        Assert.Equal(2, records[1].Line);
        Assert.Equal(["last", ""], records[2].Fields);
        Assert.Equal(5, records[2].Line);
    }

    [Fact]
    public void ReadRecords_StripsBom() {
        List<(int Line, string[] Fields)> records = CsvUtils.ReadRecords((char)0xFEFF + "A,B");
        Assert.Equal(["A", "B"], records.Single().Fields);
    }

    [Fact]
    public async Task ExportAsync_ThenReadFileAsync_RoundTrips() {
        string path = await CsvUtils.ExportAsync("csv utils test: round/trip",
            [["Text", "Value"]],
            [("needs, quoting", 1.5),("multi\nline", 2.0)],
            r => [r.Item1, r.Item2]);
        try {
            Assert.Equal(CsvUtils.ExportFolder, Path.GetDirectoryName(path));
            Assert.StartsWith("csv utils test_ round_trip-", Path.GetFileName(path));

            List<string[]>? rows = await CsvUtils.ReadFileAsync(path);
            Assert.NotNull(rows);
            Assert.Equal(3, rows.Count);
            Assert.Equal(["needs, quoting", "1.5"], rows[1]);
            Assert.Equal(["multi\nline", "2"], rows[2]);
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadFileAsync_ReturnsNullForMissingFile() {
        Assert.Null(await CsvUtils.ReadFileAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv")));
    }
}
