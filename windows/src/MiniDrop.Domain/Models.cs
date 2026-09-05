using System.Text;

namespace MiniDrop.Domain;

public enum Direction { In, Out }

public enum JobState { Queued, Uploading, RetryWait, Failed }

public enum FileTransferState { Pending, Uploading, Uploaded }

public enum DownloadState { Remote, Downloading, Cached, Failed }

/// <summary>远端消息解析产物（校验通过后）。</summary>
public sealed record RemoteMessage(
    string Id,
    string DeviceId,
    string DeviceName,
    DateTimeOffset CreatedAt,
    string? Text,
    IReadOnlyList<RemoteFile> Files);

public sealed record RemoteFile(
    string Id,
    string Name,
    long Size,
    string? Mime,
    string? Sha256);

public sealed record Tombstone(
    string Id,
    DateTimeOffset DeletedAt,
    string DeletedBy);

/// <summary>消息解析结果：Ok=false 时 Reason 为拒绝码。</summary>
public sealed record MessageParseResult(bool Ok, RemoteMessage? Message, string? Reason)
{
    public static MessageParseResult Fail(string reason) => new(false, null, reason);
    public static MessageParseResult Success(RemoteMessage m) => new(true, m, null);
}
