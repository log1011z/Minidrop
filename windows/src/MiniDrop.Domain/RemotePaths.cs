namespace MiniDrop.Domain;

/// <summary>协议路径只由 ID 决定（附录 A）；根前缀由 WebDAV 层拼接。</summary>
public static class RemotePaths
{
    public static string MessagePath(string month, string messageId) => $"items/{month}/{messageId}.json";
    public static string TombstonePath(string month, string messageId) => $"tombstones/{month}/{messageId}.json";
    public static string FilePath(string fileId) => $"files/{fileId}";

    public static string ItemsMonthDir(string month) => $"items/{month}";
    public static string TombstonesMonthDir(string month) => $"tombstones/{month}";
    public static string ItemsRoot => "items";
    public static string TombstonesRoot => "tombstones";
    public static string FilesRoot => "files";
}
