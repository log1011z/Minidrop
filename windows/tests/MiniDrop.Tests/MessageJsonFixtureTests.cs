using MiniDrop.Domain;
using Xunit;

namespace MiniDrop.Tests;

/// <summary>夹具驱动：C# 与 Kotlin 对同一批 fixtures 必须得出一致的接受/拒绝结论。</summary>
public class MessageJsonFixtureTests
{
    [Theory]
    [MemberData(nameof(ValidCases))]
    public void Valid_Fixtures_Accepted(string file, string filenameUlid)
    {
        var payload = FixtureLoader.ReadBytes("messages", "valid", file);
        var result = MessageJson.Parse(payload, filenameUlid);
        Assert.True(result.Ok, $"期望接受 {file}，实际拒绝：{result.Reason}");
        Assert.NotNull(result.Message);
        Assert.Equal(filenameUlid, result.Message!.Id);
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Invalid_Fixtures_Rejected_WithReason(string file, string filenameUlid, string reason)
    {
        var payload = FixtureLoader.ReadBytes("messages", "invalid", file);
        var result = MessageJson.Parse(payload, filenameUlid);
        Assert.False(result.Ok, $"期望拒绝 {file}（{reason}），实际被接受");
        Assert.Equal(reason, result.Reason);
    }

    public static TheoryData<string, string> ValidCases()
    {
        var data = new TheoryData<string, string>();
        var (valid, _) = FixtureLoader.MessageManifest();
        foreach (var v in valid) data.Add(v.File, v.FilenameUlid);
        return data;
    }

    public static TheoryData<string, string, string> InvalidCases()
    {
        var data = new TheoryData<string, string, string>();
        var (_, invalid) = FixtureLoader.MessageManifest();
        foreach (var i in invalid) data.Add(i.File, i.FilenameUlid, i.Reason!);
        return data;
    }

    // ---------- 程序化边界（太大不适合放仓库夹具） ----------

    [Fact]
    public void Oversize_Json_Rejected_AsTooLarge()
    {
        var big = new byte[Limits.MaxJsonBytes + 1];
        big.AsSpan().Fill((byte)'a');
        var result = MessageJson.Parse(big, Ulid.New());
        Assert.False(result.Ok);
        Assert.Equal(RejectReasons.TooLarge, result.Reason);
    }

    [Fact]
    public void Text_Over100KiB_Rejected()
    {
        var id = Ulid.New();
        var draft = new MessageJson.Draft(id, TestEnv.DeviceId, "Desktop",
            DateTimeOffset.UtcNow, new string('字', Limits.MaxTextBytes), null);
        var json = MessageJson.Serialize(draft);
        var result = MessageJson.Parse(json, id);
        Assert.False(result.Ok);
        Assert.Equal(RejectReasons.TextTooLong, result.Reason);
    }

    [Fact]
    public void Serialize_Then_Parse_RoundTrips()
    {
        var id = Ulid.New();
        var draft = new MessageJson.Draft(id, TestEnv.DeviceId, "桌面设备",
            new DateTimeOffset(2026, 9, 5, 6, 30, 0, 123, TimeSpan.Zero),
            "往返测试",
            [
                new MessageJson.DraftFile(Guid.NewGuid().ToString("D"), "文件 名.zip", 12345678, "application/zip",
                    "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"),
            ]);
        var json = MessageJson.Serialize(draft);
        var result = MessageJson.Parse(json, id);
        Assert.True(result.Ok, result.Reason);
        var m = result.Message!;
        Assert.Equal("往返测试", m.Text);
        Assert.Equal("桌面设备", m.DeviceName);
        var f = Assert.Single(m.Files);
        Assert.Equal("文件 名.zip", f.Name);
        Assert.Equal(12345678, f.Size);
    }

    [Fact]
    public void Tombstone_Serialize_Parse()
    {
        var id = Ulid.New();
        var at = new DateTimeOffset(2026, 9, 5, 7, 0, 0, TimeSpan.Zero);
        var json = MessageJson.SerializeTombstone(id, at, TestEnv.DeviceId);
        var tomb = MessageJson.ParseTombstone(json);
        Assert.NotNull(tomb);
        Assert.Equal(id, tomb!.Id);
        Assert.Equal(TestEnv.DeviceId, tomb.DeletedBy);

        Assert.Null(MessageJson.ParseTombstone("{oops"u8.ToArray()));
    }
}

public static class TestEnv
{
    public static readonly string DeviceId = "be7f3a2c-1b9d-4c2e-8f0a-3d5e6b7a9c1d";
}
