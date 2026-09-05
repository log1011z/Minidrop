using MiniDrop.Domain;
using Xunit;

namespace MiniDrop.Tests;

public class UlidTests
{
    [Fact]
    public void MonthCases_FromFixtures_AreConsistent()
    {
        foreach (var c in FixtureLoader.UlidCases())
        {
            Assert.True(Ulid.IsValid(c.Ulid), c.Ulid);
            Assert.True(Ulid.TryGetTimestampMs(c.Ulid, out var ms));
            Assert.Equal(c.TimestampMs, ms);
            Assert.Equal(c.Month, UlidClock.MonthOf(c.Ulid));
        }
    }

    [Fact]
    public void New_Is26CharUppercaseCrockford()
    {
        for (var i = 0; i < 1000; i++)
        {
            var id = Ulid.New();
            Assert.Equal(26, id.Length);
            Assert.True(Ulid.IsValid(id));
        }
    }

    [Fact]
    public void New_IsMonotonicWithinProcess()
    {
        var seen = new List<string>();
        for (var i = 0; i < 5000; i++)
            seen.Add(Ulid.New());
        var sorted = seen.OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(seen, sorted); // 字典序 = 时间序（同毫秒内计数器递增）
    }

    [Fact]
    public void TryGetTimestamp_RejectsIllegalCharacters()
    {
        Assert.False(Ulid.TryGetTimestampMs("0IM1R44YMRABCDEFGHJKMNPQRS", out _));
        Assert.False(Ulid.IsValid("01m1r44ymrabcdefghjkmnpqrs")); // 必须大写
        Assert.False(Ulid.IsValid("01M1R44YMRABCDEFGHJKMNPQR"));  // 25 位
        Assert.True(Ulid.TryGetTimestampMs("01m1r44ymrabcdefghjkmnpqrs".ToUpperInvariant(), out _));
    }

    [Fact]
    public void PreviousMonth_WrapsYear()
    {
        Assert.Equal("2026-08", UlidClock.PreviousMonth("2026-09"));
        Assert.Equal("2025-12", UlidClock.PreviousMonth("2026-01"));
    }

    [Fact]
    public void IsExpired_UsesUlidTimestampOnly()
    {
        var now = DateTimeOffset.Parse("2026-09-05T00:00:00.000Z");
        var old = "01HQRMZ0ZZ0000000000000000"; // 2024-02-29 → 已过期
        var fresh = Ulid.New(now.ToUnixTimeMilliseconds());
        Assert.True(UlidClock.IsExpired(old, now));
        Assert.False(UlidClock.IsExpired(fresh, now));
    }
}

public class RemotePathsTests
{
    [Fact]
    public void Paths_FollowAppendixA()
    {
        var id = "01M1R44YMRABCDEFGHJKMNPQRS";
        Assert.Equal("items/2026-09/01M1R44YMRABCDEFGHJKMNPQRS.json", RemotePaths.MessagePath("2026-09", id));
        Assert.Equal("tombstones/2026-09/01M1R44YMRABCDEFGHJKMNPQRS.json", RemotePaths.TombstonePath("2026-09", id));
        Assert.Equal("files/550e8400-e29b-41d4-a716-446655440000", RemotePaths.FilePath("550e8400-e29b-41d4-a716-446655440000"));
    }

    [Fact]
    public void MessagePath_MonthComesFromUlid()
    {
        var id = Ulid.NewAt(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        Assert.Equal("items/2026-01/" + id + ".json", RemotePaths.MessagePath(UlidClock.MonthOf(id), id));
    }
}
