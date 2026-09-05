using System.Text.Json;

namespace MiniDrop.Tests;

/// <summary>定位仓库根目录下的共享 fixtures/（C# 与 Kotlin 测试共用）。</summary>
public static class FixtureLoader
{
    public static string FixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到 fixtures 目录");
    }

    public static string ReadText(params string[] parts) => File.ReadAllText(Path.Combine([FixturesDir(), .. parts]));

    public static byte[] ReadBytes(params string[] parts) => File.ReadAllBytes(Path.Combine([FixturesDir(), .. parts]));

    public sealed record UlidCase(string Ulid, long TimestampMs, string Month, string? Note);

    public static IReadOnlyList<UlidCase> UlidCases()
    {
        using var doc = JsonDocument.Parse(ReadText("ulid_month_cases.json"));
        var list = new List<UlidCase>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            list.Add(new UlidCase(
                c.GetProperty("ulid").GetString()!,
                c.GetProperty("timestamp_ms").GetInt64(),
                c.GetProperty("month").GetString()!,
                c.TryGetProperty("note", out var n) ? n.GetString() : null));
        }
        return list;
    }

    public sealed record ManifestEntry(string File, string FilenameUlid, string? Reason);

    public static (IReadOnlyList<ManifestEntry> Valid, IReadOnlyList<ManifestEntry> Invalid) MessageManifest()
    {
        using var doc = JsonDocument.Parse(ReadText("messages", "manifest.json"));
        var valid = new List<ManifestEntry>();
        var invalid = new List<ManifestEntry>();
        foreach (var v in doc.RootElement.GetProperty("valid").EnumerateArray())
            valid.Add(new ManifestEntry(v.GetProperty("file").GetString()!, v.GetProperty("filename_ulid").GetString()!, null));
        foreach (var i in doc.RootElement.GetProperty("invalid").EnumerateArray())
            invalid.Add(new ManifestEntry(i.GetProperty("file").GetString()!, i.GetProperty("filename_ulid").GetString()!, i.GetProperty("reason").GetString()!));
        return (valid, invalid);
    }
}
