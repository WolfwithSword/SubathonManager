namespace SubathonManager.Core.Interfaces;

public interface IWidgetFileSystem {
    bool Exists(string path);
    string? ReadAllText(string path);
    byte[]? ReadAllBytes(string path);
    bool IsPacked(string path);
    string? GetRealFilePath(string path);
    IEnumerable<string> EnumerateFiles(string directory);
}

public sealed class DiskWidgetFileSystem : IWidgetFileSystem {
    public bool Exists(string path) {
        return File.Exists(path);
    }

    public string? ReadAllText(string path) {
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public byte[]? ReadAllBytes(string path) {
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public bool IsPacked(string path) {
        return false;
    }

    public string? GetRealFilePath(string path) {
        return File.Exists(path) ? path : null;
    }

    public IEnumerable<string> EnumerateFiles(string directory) {
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            : [];
    }
}

public static class WidgetFiles {
    public static IWidgetFileSystem Current { get; set; } = new DiskWidgetFileSystem();
}