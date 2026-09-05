namespace MiniDrop.WebDav;

public enum DavStatus
{
    Success,
    AlreadyExistsOrCreated,
    NotFound,
    AuthError,
    RateLimited,
    QuotaError,
    ServerError,
    ClientError,
    NetworkError,
    ProtocolError,
}

/// <summary>一次 WebDAV 交互的归一结果。</summary>
public sealed record DavResult(DavStatus Status, int? HttpStatusCode, TimeSpan? RetryAfter = null, string? Detail = null)
{
    public bool Ok => Status is DavStatus.Success or DavStatus.AlreadyExistsOrCreated;
    public static DavResult NewOk(int? code = null) => new(DavStatus.Success, code);
    public static DavResult NewExists(int? code) => new(DavStatus.AlreadyExistsOrCreated, code);
}

public sealed record DavItem(
    string Name,
    bool IsCollection,
    string? Etag,
    DateTimeOffset? LastModified,
    long? Length)
{
    /// <summary>远端签名：优先 ETag；无 ETag 用 mtime+length。</summary>
    public string Signature => Etag ?? $"{LastModified?.UtcTicks ?? 0}:{Length ?? -1}";
}

public sealed record WebDavOptions
{
    public required string RootUrl { get; init; }      // 规范化为以 / 结尾
    public required string Account { get; init; }
    public required Func<string?> PasswordProvider { get; init; }
    public string UserAgent { get; init; } = "MiniDrop/1.0";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>分类到 error_code（PROTOCOL §4.1）。</summary>
public static class ErrorClassifier
{
    public static string ToErrorCode(DavResult result) => result.Status switch
    {
        DavStatus.NetworkError => Domain.ErrorCodes.Network,
        DavStatus.RateLimited => Domain.ErrorCodes.RateLimit,
        DavStatus.QuotaError => Domain.ErrorCodes.Quota,
        DavStatus.ServerError => Domain.ErrorCodes.Server,
        DavStatus.AuthError => Domain.ErrorCodes.Auth,
        DavStatus.ProtocolError => Domain.ErrorCodes.Protocol,
        DavStatus.NotFound or DavStatus.ClientError => Domain.ErrorCodes.Protocol,
        _ => Domain.ErrorCodes.Protocol,
    };
}
