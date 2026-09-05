namespace MiniDrop.Domain;

/// <summary>传输/任务错误码（PROTOCOL §4.1）。IsTransient 决定 retry_wait 或 failed。</summary>
public static class ErrorCodes
{
    public const string Network = "NETWORK";
    public const string RateLimit = "RATE_LIMIT";
    public const string Server = "SERVER";
    public const string Auth = "AUTH";
    public const string SourceMissing = "SOURCE_MISSING";
    public const string SourceChanged = "SOURCE_CHANGED";
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string Quota = "QUOTA";
    public const string Protocol = "PROTOCOL";

    public static bool IsTransient(string code) => code is Network or RateLimit or Server;

    /// <summary>暂时错误退避序列：10s、30s、2m、5m、15m；第 5 次失败转 failed。</summary>
    public static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    ];

    public const int MaxTransientAttempts = 5;
}

/// <summary>远端 JSON 校验拒绝码（PROTOCOL §4.2）。</summary>
public static class RejectReasons
{
    public const string BadJson = "BAD_JSON";
    public const string TooLarge = "TOO_LARGE";
    public const string SchemaVersion = "SCHEMA_VERSION";
    public const string BadUlid = "BAD_ULID";
    public const string IdMismatch = "ID_MISMATCH";
    public const string BadDevice = "BAD_DEVICE";
    public const string BadDeviceName = "BAD_DEVICE_NAME";
    public const string BadCreatedAt = "BAD_CREATED_AT";
    public const string TextTooLong = "TEXT_TOO_LONG";
    public const string TooManyFiles = "TOO_MANY_FILES";
    public const string BadFileId = "BAD_FILE_ID";
    public const string DupFileId = "DUP_FILE_ID";
    public const string BadFileName = "BAD_FILE_NAME";
    public const string BadFileSize = "BAD_FILE_SIZE";
    public const string BadMime = "BAD_MIME";
    public const string BadSha256 = "BAD_SHA256";
    public const string EmptyContent = "EMPTY_CONTENT";
}

public static class Limits
{
    public const long MaxJsonBytes = 2 * 1024 * 1024;      // JSON 响应体上限 2 MiB
    public const int MaxTextBytes = 100 * 1024;             // text 100 KiB UTF-8
    public const int MaxFiles = 50;
    public const int MaxNameBytes = 200;
    public const int MaxMimeChars = 127;
    public const long DefaultMaxFileBytes = 500L * 1024 * 1024;
}
