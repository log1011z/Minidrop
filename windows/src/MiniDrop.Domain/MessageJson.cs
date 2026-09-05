using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MiniDrop.Domain;

/// <summary>
/// 消息 JSON Schema v1 的严格解析与序列化（PROTOCOL §3）。
/// 解析：未知字段忽略；体积/字段/取值全部校验；失败返回拒绝码。
/// 序列化：固定字段、UTF-8 无 BOM、RFC 3339 UTC 毫秒、小写十六进制。
/// </summary>
public static class MessageJson
{
    private static readonly CreatedAtFormat CreatedAtRegex = new();

    private sealed class CreatedAtFormat
    {
        // RFC 3339 UTC、含毫秒：2026-09-05T06:30:00.123Z
        public bool IsValid(string s) =>
            !string.IsNullOrEmpty(s)
            && s.Length == 24
            && s.EndsWith('Z')
            && s[10] == 'T'
            && s[19] == '.'
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var v)
            && v.Offset == TimeSpan.Zero;
    }

    // ---------- 解析 ----------

    public static MessageParseResult Parse(byte[] payload, string expectedId, long maxFileBytes = Limits.DefaultMaxFileBytes)
    {
        if (payload.Length > Limits.MaxJsonBytes)
            return MessageParseResult.Fail(RejectReasons.TooLarge);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return MessageParseResult.Fail(RejectReasons.BadJson);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return MessageParseResult.Fail(RejectReasons.BadJson);

            var root = doc.RootElement;

            // version
            if (!root.TryGetProperty("version", out var versionEl) || versionEl.ValueKind != JsonValueKind.Number || !versionEl.TryGetInt32(out var version))
                return MessageParseResult.Fail(RejectReasons.SchemaVersion);
            if (version != 1)
                return MessageParseResult.Fail(RejectReasons.SchemaVersion);

            // id
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                return MessageParseResult.Fail(RejectReasons.BadUlid);
            var id = idEl.GetString();
            if (id is null || !Ulid.IsValid(id))
                return MessageParseResult.Fail(RejectReasons.BadUlid);
            if (!string.Equals(id, expectedId, StringComparison.Ordinal))
                return MessageParseResult.Fail(RejectReasons.IdMismatch);

            // device_id
            if (!root.TryGetProperty("device_id", out var deviceIdEl) || deviceIdEl.ValueKind != JsonValueKind.String
                || !IsUuidV4(deviceIdEl.GetString()))
                return MessageParseResult.Fail(RejectReasons.BadDevice);

            // device_name
            if (!root.TryGetProperty("device_name", out var deviceNameEl) || deviceNameEl.ValueKind != JsonValueKind.String)
                return MessageParseResult.Fail(RejectReasons.BadDeviceName);
            var deviceName = deviceNameEl.GetString()?.Trim();
            if (string.IsNullOrEmpty(deviceName) || deviceName.Length > 32)
                return MessageParseResult.Fail(RejectReasons.BadDeviceName);

            // created_at
            if (!root.TryGetProperty("created_at", out var createdAtEl) || createdAtEl.ValueKind != JsonValueKind.String
                || !CreatedAtRegex.IsValid(createdAtEl.GetString() ?? "")
                || !DateTimeOffset.TryParse(createdAtEl.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createdAt))
                return MessageParseResult.Fail(RejectReasons.BadCreatedAt);

            // text
            string? text = null;
            if (root.TryGetProperty("text", out var textEl) && textEl.ValueKind != JsonValueKind.Null)
            {
                if (textEl.ValueKind != JsonValueKind.String)
                    return MessageParseResult.Fail(RejectReasons.BadJson);
                text = textEl.GetString();
                if (text is not null && Encoding.UTF8.GetByteCount(text) > Limits.MaxTextBytes)
                    return MessageParseResult.Fail(RejectReasons.TextTooLong);
            }

            // files
            List<RemoteFile>? files = null;
            if (root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind != JsonValueKind.Null)
            {
                if (filesEl.ValueKind != JsonValueKind.Array)
                    return MessageParseResult.Fail(RejectReasons.BadJson);
                if (filesEl.GetArrayLength() > Limits.MaxFiles)
                    return MessageParseResult.Fail(RejectReasons.TooManyFiles);

                files = new List<RemoteFile>(filesEl.GetArrayLength());
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in filesEl.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object)
                        return MessageParseResult.Fail(RejectReasons.BadJson);

                    if (!f.TryGetProperty("id", out var fidEl) || fidEl.ValueKind != JsonValueKind.String || !IsUuidV4(fidEl.GetString()))
                        return MessageParseResult.Fail(RejectReasons.BadFileId);
                    var fid = fidEl.GetString()!;
                    if (!seen.Add(fid))
                        return MessageParseResult.Fail(RejectReasons.DupFileId);

                    if (!f.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                        return MessageParseResult.Fail(RejectReasons.BadFileName);
                    var name = nameEl.GetString();
                    if (string.IsNullOrEmpty(name) || !IsValidFileName(name))
                        return MessageParseResult.Fail(RejectReasons.BadFileName);

                    if (!f.TryGetProperty("size", out var sizeEl) || sizeEl.ValueKind != JsonValueKind.Number
                        || !sizeEl.TryGetInt64(out var size))
                        return MessageParseResult.Fail(RejectReasons.BadJson);
                    if (size < 0 || size > maxFileBytes)
                        return MessageParseResult.Fail(RejectReasons.BadFileSize);

                    string? mime = null;
                    if (f.TryGetProperty("mime", out var mimeEl) && mimeEl.ValueKind != JsonValueKind.Null)
                    {
                        if (mimeEl.ValueKind != JsonValueKind.String)
                            return MessageParseResult.Fail(RejectReasons.BadJson);
                        mime = mimeEl.GetString();
                        if (mime is not null && !IsValidMime(mime))
                            return MessageParseResult.Fail(RejectReasons.BadMime);
                    }

                    string? sha = null;
                    if (f.TryGetProperty("sha256", out var shaEl) && shaEl.ValueKind != JsonValueKind.Null)
                    {
                        if (shaEl.ValueKind != JsonValueKind.String)
                            return MessageParseResult.Fail(RejectReasons.BadJson);
                        sha = shaEl.GetString();
                        if (sha is not null && !IsValidSha256(sha))
                            return MessageParseResult.Fail(RejectReasons.BadSha256);
                    }

                    files.Add(new RemoteFile(fid, name, size, mime, sha));
                }
            }

            // 有效性：至少一段非空文字或至少一个文件
            var hasText = !string.IsNullOrWhiteSpace(text);
            if (!hasText && (files is null || files.Count == 0))
                return MessageParseResult.Fail(RejectReasons.EmptyContent);

            return MessageParseResult.Success(new RemoteMessage(id, deviceIdEl.GetString()!, deviceName, createdAt, text, files ?? []));
        }
    }

    // ---------- 序列化 ----------

    public sealed record DraftFile(string Id, string Name, long Size, string? Mime, string? Sha256);

    public sealed record Draft(
        string Id,
        string DeviceId,
        string DeviceName,
        DateTimeOffset CreatedAt,
        string? Text,
        IReadOnlyList<DraftFile>? Files);

    public static byte[] Serialize(Draft draft)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("version", 1);
        writer.WriteString("id", draft.Id);
        writer.WriteString("device_id", draft.DeviceId);
        writer.WriteString("device_name", draft.DeviceName);
        writer.WriteString("created_at", draft.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        if (draft.Text is null)
            writer.WriteNull("text");
        else
            writer.WriteString("text", draft.Text);
        if (draft.Files is null || draft.Files.Count == 0)
            writer.WriteNull("files");
        else
        {
            writer.WriteStartArray("files");
            foreach (var f in draft.Files)
            {
                writer.WriteStartObject();
                writer.WriteString("id", f.Id);
                writer.WriteString("name", f.Name);
                writer.WriteNumber("size", f.Size);
                if (f.Mime is null) writer.WriteNull("mime"); else writer.WriteString("mime", f.Mime);
                if (f.Sha256 is null) writer.WriteNull("sha256"); else writer.WriteString("sha256", f.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    // ---------- Tombstone ----------

    public static byte[] SerializeTombstone(string id, DateTimeOffset deletedAt, string deletedBy)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("version", 1);
        writer.WriteString("id", id);
        writer.WriteString("deleted_at", deletedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        writer.WriteString("deleted_by", deletedBy);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>墓碑正文只用于诊断；解析失败返回 null，不影响删除语义。</summary>
    public static Tombstone? ParseTombstone(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var ver) || ver != 1) return null;
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String || !Ulid.IsValid(idEl.GetString())) return null;
            if (!root.TryGetProperty("deleted_at", out var atEl) || atEl.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(atEl.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) return null;
            if (!root.TryGetProperty("deleted_by", out var byEl) || byEl.ValueKind != JsonValueKind.String || !IsUuidV4(byEl.GetString())) return null;
            return new Tombstone(idEl.GetString()!, at, byEl.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------- 校验工具 ----------

    public static bool IsUuidV4(string? s)
    {
        if (s is null || s.Length != 36) return false;
        if (!Guid.TryParseExact(s, "D", out var g)) return false;
        var b = g.ToByteArray();
        return (b[7] >> 4) == 4 && (b[8] & 0xC0) == 0x80;
    }

    public static bool IsValidFileName(string name)
    {
        // UTF-8 ≤ 200 字节；不含 / \ 与控制字符
        if (Encoding.UTF8.GetByteCount(name) > Limits.MaxNameBytes) return false;
        foreach (var c in name)
        {
            if (c == '/' || c == '\\' || c < ' ' || c == 0x7F) return false;
        }
        return true;
    }

    public static bool IsValidMime(string mime)
    {
        if (mime.Length > Limits.MaxMimeChars) return false;
        foreach (var c in mime)
        {
            if (c > 0x7E || c < 0x20) return false;
        }
        return true;
    }

    public static bool IsValidSha256(string? sha)
    {
        if (sha is null || sha.Length != 64) return false;
        foreach (var c in sha)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok) return false;
        }
        return true;
    }
}
