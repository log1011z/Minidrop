using MiniDrop.Domain;
using Microsoft.Data.Sqlite;

namespace MiniDrop.Storage;

// 行模型（无 raw_json；发送时由结构化行生成 JSON，接收时校验后拆表保存）

public sealed record MessageRow(
    string Id, string RemoteMonth, string DeviceId, string DeviceName,
    string CreatedAt, string? Text, Direction Dir, string ReceivedAt);

/// <summary>
/// files.state 原始取值：同一列按 direction 区分两套词表——
/// outgoing: pending/uploading/uploaded；incoming: remote/downloading/cached/failed。
/// </summary>
public static class FileStates
{
    public const string Pending = "pending";
    public const string Uploading = "uploading";
    public const string Uploaded = "uploaded";
    public const string Remote = "remote";
    public const string Downloading = "downloading";
    public const string Cached = "cached";
    public const string Failed = "failed";
}

public sealed record FileRow(
    string FileId, string MessageId, int Idx, string Name, long Size,
    string? Mime, string? Sha256, Direction Dir, string? SourcePath,
    long? SourceModifiedAt, string? CachePath, string State);

public sealed record JobRow(
    string MessageId, JobState State, int Attempts, long? NextAttemptAt,
    long BytesDone, long BytesTotal, string? ErrorCode, string? ErrorMessage,
    string EnqueuedAt, string UpdatedAt);

public sealed record RejectedRow(
    string RemotePath, string MessageId, string RemoteSignature, string ReasonCode,
    int FailCount, bool Quarantined, string LastFailedAt);

public sealed record TimelineRow(
    MessageRow Message, JobRow? Job);

internal static class Sql
{
    public static SqliteCommand Cmd(this SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    public static void Set(this SqliteCommand cmd, string name, object? value)
    {
        cmd.Parameters.Add(new SqliteParameter(name, value ?? DBNull.Value));
    }

    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? Int64OrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}

public static class StateStrings
{
    public static string Of(JobState s) => s switch
    {
        JobState.Queued => "queued", JobState.Uploading => "uploading",
        JobState.RetryWait => "retry_wait", JobState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static JobState ToJobState(string s) => s switch
    {
        "queued" => JobState.Queued, "uploading" => JobState.Uploading,
        "retry_wait" => JobState.RetryWait, "failed" => JobState.Failed,
        _ => throw new FormatException("未知 job 状态：" + s),
    };

    public static string Of(Direction d) => d == Direction.In ? "in" : "out";
    public static Direction ToDirection(string s) => s == "in" ? Direction.In : Direction.Out;
}
