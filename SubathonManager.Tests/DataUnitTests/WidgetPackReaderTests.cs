using SubathonManager.Data.Widgets;
using SubathonManager.Tests.Utility;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.DataUnitTests;

[Collection("WorkingDirectory")]
public class WidgetPackReaderTests
{
    private static WidgetPackReader MakeReader(TempWorkspace ws, string packFile)
        => new(packFile, Path.Combine(ws.Root, "cache", Guid.NewGuid().ToString("N")));

    private static string MakePack(TempWorkspace ws, params (string entry, string content)[] files)
        => TestPacks.WriteZip(ws.Path_("packs", $"{Guid.NewGuid():N}.smw"),
            files.Select(f => new KeyValuePair<string, string>(f.entry, f.content)));

    [Fact]
    public void MissingPackFile_BehavesLikeAnEmptyArchive()
    {
        using var ws = new TempWorkspace("packreader");
        var reader = MakeReader(ws, Path.Combine(ws.Root, "nope.smw"));

        Assert.Empty(reader.Entries);
        Assert.False(reader.Contains("a.txt"));
        Assert.Equal(-1, reader.LengthOf("a.txt"));
        Assert.Null(reader.Read("a.txt"));
        Assert.Null(reader.ReadText("a.txt"));
    }

    [Fact]
    public void CorruptPackFile_BehavesLikeAnEmptyArchive()
    {
        using var ws = new TempWorkspace("packreader");
        var reader = MakeReader(ws, ws.WriteFile("broken.smw", "not a zip at all"));

        Assert.Empty(reader.Entries);
        Assert.Null(reader.Read("a.txt"));
    }

    [Fact]
    public void Entries_AreNormalisedAndDirectoriesSkipped()
    {
        using var ws = new TempWorkspace("packreader");
        string pack = MakePack(ws,
            ("widget.json", "{}"),
            ("content/widget.html", "<html></html>"),
            ("content/sub/style.css", "body{}"));

        var reader = MakeReader(ws, pack);
        var entries = reader.Entries.ToList();

        Assert.Equal(3, entries.Count);
        Assert.Contains("content/widget.html", entries);
        Assert.Contains("content/sub/style.css", entries);
        Assert.DoesNotContain(entries, e => e.EndsWith('/'));
    }

    [Theory]
    [InlineData("content/widget.html")]
    [InlineData("CONTENT/WIDGET.HTML")]
    [InlineData("content\\widget.html")]
    [InlineData("/content/widget.html")]
    public void Contains_NormalisesTheLookupKey(string probe)
    {
        using var ws = new TempWorkspace("packreader");
        var reader = MakeReader(ws, MakePack(ws, ("content/widget.html", "<html></html>")));

        Assert.True(reader.Contains(probe));
    }

    [Fact]
    public void Contains_UnknownEntry_IsFalse()
    {
        using var ws = new TempWorkspace("packreader");
        var reader = MakeReader(ws, MakePack(ws, ("content/widget.html", "x")));

        Assert.False(reader.Contains("content/missing.html"));
    }

    [Fact]
    public void LengthOf_KnownEntry_ReturnsUncompressedLength()
    {
        using var ws = new TempWorkspace("packreader");
        var reader = MakeReader(ws, MakePack(ws, ("a.txt", "0123456789")));

        Assert.Equal(10, reader.LengthOf("a.txt"));
    }

        
    [Fact]
    public void Read_ReturnsTheEntryBytes()
    {
        using var ws = new TempWorkspace("packreader");
        var reader = MakeReader(ws, MakePack(ws, ("a.txt", "hello")));

        Assert.Equal("hello"u8.ToArray(), reader.Read("a.txt"));
    }

    [Fact]
    public void ReadText_StripsUtf8Bom()
    {
        using var ws = new TempWorkspace("packreader");
        string pack = TestPacks.WriteZip(ws.Path_("bom.smw"), new List<KeyValuePair<string, byte[]>>
        {
            new("a.txt", [0xEF, 0xBB, 0xBF, .. "hello"u8.ToArray()])
        });

        Assert.Equal("hello", MakeReader(ws, pack).ReadText("a.txt"));
    }

    [Fact]
    public void ReadText_WithoutBom_ReturnsTextVerbatim()
    {
        using var ws = new TempWorkspace("packreader");
        var reader = MakeReader(ws, MakePack(ws, ("a.txt", "héllo wörld")));

        Assert.Equal("héllo wörld", reader.ReadText("a.txt"));
    }

    [Fact]
    public void Read_SmallEntry_IsServedFromTheMemoryCacheOnSecondCall()
    {
        using var ws = new TempWorkspace("packreader");
        string pack = MakePack(ws, ("a.txt", "cached"));
        var reader = MakeReader(ws, pack);

        Assert.Equal("cached", reader.ReadText("a.txt"));
        File.Delete(pack);
        Assert.Equal("cached", reader.ReadText("a.txt"));
    }

    [Fact]
    public void Index_IsRebuilt_WhenThePackFileChanges()
    {
        using var ws = new TempWorkspace("packreader");
        string pack = ws.Path_("packs", "mutating.smw");
        TestPacks.WriteZip(pack, new Dictionary<string, string> { ["a.txt"] = "first" });
        var reader = MakeReader(ws, pack);

        Assert.Equal("first", reader.ReadText("a.txt"));
        TestPacks.WriteZip(pack, new Dictionary<string, string> { ["a.txt"] = "second", ["b.txt"] = "new" });
        File.SetLastWriteTimeUtc(pack, DateTime.UtcNow.AddMinutes(1));
        Assert.True(reader.Contains("b.txt"));
        Assert.Equal("second", reader.ReadText("a.txt"));
    }

        
    [Fact]
    public void Materialize_UnknownEntry_ReturnsNull()
    {
        using var ws = new TempWorkspace("packreader");
        var reader = MakeReader(ws, MakePack(ws, ("a.txt", "x")));

        Assert.Null(reader.Materialize("missing.txt"));
    }

    [Fact]
    public void Materialize_SmallEntry_ReturnsNull_BecauseItIsCachedInMemory()
    {
        using var ws = new TempWorkspace("packreader");
        var reader = MakeReader(ws, MakePack(ws, ("a.txt", "small")));

        Assert.Null(reader.Materialize("a.txt"));
    }

    [Fact]
    public void Materialize_LargeEntry_WritesItToTheCacheDirectory()
    {
        using var ws = new TempWorkspace("packreader");
        string big = new('x', (int)WidgetPackMemoryCache.MaxEntrySize + 16);
        string cacheDir = Path.Combine(ws.Root, "cache", "big");
        var reader = new WidgetPackReader(MakePack(ws, ("media/big.txt", big)), cacheDir);

        string? path = reader.Materialize("media/big.txt");

        Assert.NotNull(path);
        Assert.Equal(Path.Combine(cacheDir, "media", "big.txt"), path);
        Assert.Equal(big, File.ReadAllText(path!));
    }

    [Fact]
    public void Materialize_Twice_ReusesTheExistingFile()
    {
        using var ws = new TempWorkspace("packreader");
        string big = new('x', (int)WidgetPackMemoryCache.MaxEntrySize + 16);
        var reader = new WidgetPackReader(MakePack(ws, ("big.txt", big)), Path.Combine(ws.Root, "cache", "big2"));

        string path = reader.Materialize("big.txt")!;
        var firstWrite = File.GetLastWriteTimeUtc(path);

        Assert.Equal(path, reader.Materialize("big.txt"));
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Materialize_LeavesNoPartialFileBehind()
    {
        using var ws = new TempWorkspace("packreader");
        string big = new('x', (int)WidgetPackMemoryCache.MaxEntrySize + 16);
        string cacheDir = Path.Combine(ws.Root, "cache", "big3");
        var reader = new WidgetPackReader(MakePack(ws, ("big.txt", big)), cacheDir);

        reader.Materialize("big.txt");

        Assert.Empty(Directory.EnumerateFiles(cacheDir, "*.partial", SearchOption.AllDirectories));
    }

        
    [Fact]
    public void ExtractAll_WritesEveryEntry()
    {
        using var ws = new TempWorkspace("packreader");
        var reader = MakeReader(ws, MakePack(ws,
            ("widget.json", "{}"),
            ("content/widget.html", "<html></html>"),
            ("content/sub/style.css", "body{}")));

        string target = Path.Combine(ws.Root, "out");
        Assert.True(reader.ExtractAll(target));

        Assert.True(File.Exists(Path.Combine(target, "widget.json")));
        Assert.Equal("<html></html>", File.ReadAllText(Path.Combine(target, "content", "widget.html")));
        Assert.True(File.Exists(Path.Combine(target, "content", "sub", "style.css")));
    }

    [Fact]
    public void ExtractAll_SkipsZipSlipEntries()
    {
        using var ws = new TempWorkspace("packreader");
        var reader = MakeReader(ws, MakePack(ws, ("../escaped.txt", "pwned"), ("safe.txt", "fine")));

        string target = Path.Combine(ws.Root, "out");
        Assert.True(reader.ExtractAll(target));

        Assert.True(File.Exists(Path.Combine(target, "safe.txt")));
        Assert.Empty(Directory.EnumerateFiles(ws.Root, "escaped.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public void ExtractAll_MissingPack_ReturnsFalse()
    {
        using var ws = new TempWorkspace("packreader");
        var reader = MakeReader(ws, Path.Combine(ws.Root, "nope.smw"));

        Assert.False(reader.ExtractAll(Path.Combine(ws.Root, "out")));
    }

    [Fact]
    public void ExtractAll_Twice_OverwritesCleanly()
    {
        using var ws = new TempWorkspace("packreader");
        var reader = MakeReader(ws, MakePack(ws, ("a.txt", "content")));
        string target = Path.Combine(ws.Root, "out");

        Assert.True(reader.ExtractAll(target));
        Assert.True(reader.ExtractAll(target));
        Assert.Equal("content", File.ReadAllText(Path.Combine(target, "a.txt")));
    }

}

[Collection("WorkingDirectory")]
public class WidgetPackMemoryCacheTests
{
    private static string Key(string suffix) => WidgetPackMemoryCache.MakeKey($"pack-{Guid.NewGuid():N}", suffix);

    [Fact]
    public void MakeKey_JoinsPackAndEntryWithAPipe()
        => Assert.Equal(@"C:\packs\a.smw|content/widget.html",
            WidgetPackMemoryCache.MakeKey(@"C:\packs\a.smw", "content/widget.html"));

    [Fact]
    public void TryGet_Miss_ReturnsFalseAndEmptyArray()
    {
        WidgetPackMemoryCache.Clear();

        Assert.False(WidgetPackMemoryCache.TryGet(Key("nope"), out var data));
        Assert.Empty(data);
    }

    [Fact]
    public void SetThenTryGet_RoundTrips()
    {
        WidgetPackMemoryCache.Clear();
        string key = Key("a");
        byte[] payload = [1, 2, 3];

        WidgetPackMemoryCache.Set(key, payload);

        Assert.True(WidgetPackMemoryCache.TryGet(key, out var data));
        Assert.Same(payload, data);
        Assert.Equal(3, WidgetPackMemoryCache.CurrentSize);
    }

    [Fact]
    public void Set_OversizedEntry_IsRejected()
    {
        WidgetPackMemoryCache.Clear();
        string key = Key("big");

        WidgetPackMemoryCache.Set(key, new byte[WidgetPackMemoryCache.MaxEntrySize]);

        Assert.False(WidgetPackMemoryCache.TryGet(key, out _));
        Assert.Equal(0, WidgetPackMemoryCache.CurrentSize);
    }

    [Fact]
    public void Set_SameKeyTwice_ReplacesWithoutDoubleCountingSize()
    {
        WidgetPackMemoryCache.Clear();
        string key = Key("a");

        WidgetPackMemoryCache.Set(key, new byte[100]);
        WidgetPackMemoryCache.Set(key, new byte[10]);

        Assert.True(WidgetPackMemoryCache.TryGet(key, out var data));
        Assert.Equal(10, data.Length);
        Assert.Equal(10, WidgetPackMemoryCache.CurrentSize);
    }

    [Fact]
    public void Set_OverBudget_EvictsTheLeastRecentlyUsed()
    {
        WidgetPackMemoryCache.Clear();
        const int chunk = 1024 * 1024;
        string pack = $"pack-{Guid.NewGuid():N}";

        var keys = new List<string>();
        for (int i = 0; i < 33; i++)
        {
            string key = WidgetPackMemoryCache.MakeKey(pack, $"entry-{i}");
            keys.Add(key);
            WidgetPackMemoryCache.Set(key, new byte[chunk]);
        }

        Assert.False(WidgetPackMemoryCache.TryGet(keys[0], out _));
        Assert.True(WidgetPackMemoryCache.TryGet(keys[^1], out _));
        Assert.True(WidgetPackMemoryCache.CurrentSize <= 32L * 1024 * 1024);
    }

    [Fact]
    public void TryGet_PromotesTheEntry_SoItSurvivesEviction()
    {
        WidgetPackMemoryCache.Clear();
        const int chunk = 1024 * 1024;
        string pack = $"pack-{Guid.NewGuid():N}";
        string first = WidgetPackMemoryCache.MakeKey(pack, "entry-0");

        WidgetPackMemoryCache.Set(first, new byte[chunk]);
        for (int i = 1; i < 20; i++)
            WidgetPackMemoryCache.Set(WidgetPackMemoryCache.MakeKey(pack, $"entry-{i}"), new byte[chunk]);

        Assert.True(WidgetPackMemoryCache.TryGet(first, out _));

        for (int i = 20; i < 40; i++)
            WidgetPackMemoryCache.Set(WidgetPackMemoryCache.MakeKey(pack, $"entry-{i}"), new byte[chunk]);

        Assert.True(WidgetPackMemoryCache.TryGet(first, out _));
    }

    [Fact]
    public void InvalidatePack_DropsOnlyThatPacksEntries()
    {
        WidgetPackMemoryCache.Clear();
        string mine = $"pack-{Guid.NewGuid():N}";
        string other = $"pack-{Guid.NewGuid():N}";

        WidgetPackMemoryCache.Set(WidgetPackMemoryCache.MakeKey(mine, "a"), new byte[10]);
        WidgetPackMemoryCache.Set(WidgetPackMemoryCache.MakeKey(mine, "b"), new byte[10]);
        WidgetPackMemoryCache.Set(WidgetPackMemoryCache.MakeKey(other, "a"), new byte[10]);
        WidgetPackMemoryCache.InvalidatePack(mine);

        Assert.False(WidgetPackMemoryCache.TryGet(WidgetPackMemoryCache.MakeKey(mine, "a"), out _));
        Assert.False(WidgetPackMemoryCache.TryGet(WidgetPackMemoryCache.MakeKey(mine, "b"), out _));
        Assert.True(WidgetPackMemoryCache.TryGet(WidgetPackMemoryCache.MakeKey(other, "a"), out _));
        Assert.Equal(10, WidgetPackMemoryCache.CurrentSize);
    }

    [Fact]
    public void InvalidatePack_UnknownPack_IsANoOp()
    {
        WidgetPackMemoryCache.Clear();
        WidgetPackMemoryCache.Set(Key("a"), new byte[10]);
        WidgetPackMemoryCache.InvalidatePack($"pack-{Guid.NewGuid():N}");

        Assert.Equal(10, WidgetPackMemoryCache.CurrentSize);
    }

    [Fact]
    public void Clear_EmptiesEverything()
    {
        WidgetPackMemoryCache.Set(Key("a"), new byte[10]);
        WidgetPackMemoryCache.Set(Key("b"), new byte[10]);

        WidgetPackMemoryCache.Clear();

        Assert.Equal(0, WidgetPackMemoryCache.CurrentSize);
    }
}
