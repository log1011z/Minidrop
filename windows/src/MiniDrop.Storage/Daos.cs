using MiniDrop.Domain;
using Microsoft.Data.Sqlite;

namespace MiniDrop.Storage;

public sealed class MetaDao(Database db)
{
    public const string HistoryCursorMonth = "history_cursor_month";
    public const string HistoryCursorBeforeId = "history_cursor_before_id";
    public const string HistoryInitialized = "history_initialized";
    public const string LastManualRefreshAt = "last_manual_refresh_at";
    public const string LastRemoteMaintenanceAt = "last_remote_maintenance_at";

    public string? Get(string key)
    {
        using var cmd = db.Connection.Cmd(null, "SELECT value FROM meta WHERE key = $k");
        cmd.Set("$k", key);
        var v = cmd.ExecuteScalar();
        return v is string s ? s : null;
    }

    public void Set(string key, string? value)
    {
        using var cmd = db.Connection.Cmd(null,
            """
            INSERT INTO meta(key, value) VALUES($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """);
        cmd.Set("$k", key);
        cmd.Set("$v", value);
        cmd.ExecuteNonQuery();
    }

    public long GetLong(string key, long fallback)
    {
        var s = Get(key);
        return s is not null && long.TryParse(s, out var v) ? v : fallback;
    }
}

public sealed class MessageDao(Database db)
{
    public void Insert(SqliteTransaction tx, MessageRow m)
    {
        using var cmd = db.Connection.Cmd(tx,
            """
            INSERT OR IGNORE INTO messages(id, remote_month, device_id, device_name, created_at, text, direction, received_at)
            VALUES($id, $month, $device_id, $device_name, $created_at, $text, $direction, $received_at)
            """);
        cmd.Set("$id", m.Id);
        cmd.Set("$month", m.RemoteMonth);
        cmd.Set("$device_id", m.DeviceId);
        cmd.Set("$device_name", m.DeviceName);
        cmd.Set("$created_at", m.CreatedAt);
        cmd.Set("$text", m.Text);
        cmd.Set("$direction", StateStrings.Of(m.Dir));
        cmd.Set("$received_at", m.ReceivedAt);
        cmd.ExecuteNonQuery();
    }

    public MessageRow? Get(string id)
    {
        using var cmd = db.Connection.Cmd(null, "SELECT id, remote_month, device_id, device_name, created_at, text, direction, received_at FROM messages WHERE id = $id");
        cmd.Set("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public bool Exists(string id)
    {
        using var cmd = db.Connection.Cmd(null, "SELECT 1 FROM messages WHERE id = $id");
        cmd.Set("$id", id);
        return cmd.ExecuteScalar() is not null;
    }

    public void Delete(SqliteTransaction tx, string id)
    {
        using var cmd = db.Connection.Cmd(tx, "DELETE FROM messages WHERE id = $id");
        cmd.Set("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>附录 B 时间线分页（本地分页 + job 状态联查）。</summary>
    public IReadOnlyList<TimelineRow> TimelinePage(int limit, int offset)
    {
        const string sql = """
            SELECT m.id, m.remote_month, m.device_id, m.device_name, m.created_at, m.text, m.direction, m.received_at,
                   j.state, j.attempts, j.next_attempt_at, j.bytes_done, j.bytes_total, j.error_code, j.error_message, j.enqueued_at, j.updated_at
            FROM messages m
            LEFT JOIN upload_jobs j ON j.message_id = m.id
            ORDER BY m.created_at DESC, m.id DESC
            LIMIT $limit OFFSET $offset
            """;
        using var cmd = db.Connection.Cmd(null, sql);
        cmd.Set("$limit", limit);
        cmd.Set("$offset", offset);
        using var r = cmd.ExecuteReader();
        var list = new List<TimelineRow>();
        while (r.Read())
        {
            var m = Read(r);
            JobRow? job = r.IsDBNull(8) ? null : new JobRow(
                m.Id, StateStrings.ToJobState(r.GetString(8)), r.GetInt32(9), r.Int64OrNull(10),
                r.GetInt64(11), r.GetInt64(12), r.Str(13), r.Str(14), r.Str(15), r.Str(16));
            list.Add(new TimelineRow(m, job));
        }
        return list;
    }

    public long Count()
    {
        using var cmd = db.Connection.Cmd(null, "SELECT COUNT(*) FROM messages");
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>构成"本地最近 limit 条"的月份集合（刷新停止规则用）。</summary>
    public IReadOnlyList<string> MonthsOfNewest(int limit)
    {
        using var cmd = db.Connection.Cmd(null,
            "SELECT DISTINCT remote_month FROM (SELECT remote_month FROM messages ORDER BY created_at DESC, id DESC LIMIT $l) ORDER BY remote_month DESC");
        cmd.Set("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public IReadOnlyList<string> IdsByMonth(string month)
    {
        using var cmd = db.Connection.Cmd(null, "SELECT id FROM messages WHERE remote_month = $m");
        cmd.Set("$m", month);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>候选过期消息（month <= cutoffMonth 的行再按 ULID 时间戳精确过滤）。</summary>
    public IReadOnlyList<string> IdsInMonthsUpTo(string cutoffMonth)
    {
        using var cmd = db.Connection.Cmd(null, "SELECT id FROM messages WHERE remote_month <= $m ORDER BY id");
        cmd.Set("$m", cutoffMonth);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>事务内删除消息；files/jobs 级联。</summary>
    public void DeleteCascade(SqliteTransaction tx, string id) => Delete(tx, id);

    private static MessageRow Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetString(4), r.Str(5), StateStrings.ToDirection(r.GetString(6)), r.GetString(7));
}

public sealed class FileDao(Database db)
{
    public void Insert(SqliteTransaction tx, FileRow f)
    {
        using var cmd = db.Connection.Cmd(tx,
            """
            INSERT OR REPLACE INTO files(file_id, message_id, idx, name, size, mime, sha256, direction, source_path, source_modified_at, cache_path, state)
            VALUES($fid, $mid, $idx, $name, $size, $mime, $sha, $direction, $source_path, $source_modified_at, $cache_path, $state)
            """);
        Bind(cmd, f);
        cmd.ExecuteNonQuery();
    }

    public FileRow? Get(string fileId)
    {
        using var cmd = db.Connection.Cmd(null, SelectSql + " WHERE file_id = $fid");
        cmd.Set("$fid", fileId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public IReadOnlyList<FileRow> GetByMessage(string messageId)
    {
        using var cmd = db.Connection.Cmd(null, SelectSql + " WHERE message_id = $mid ORDER BY idx");
        cmd.Set("$mid", messageId);
        using var r = cmd.ExecuteReader();
        var list = new List<FileRow>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public void SetState(string fileId, string state)
    {
        using var cmd = db.Connection.Cmd(null, "UPDATE files SET state = $s WHERE file_id = $fid");
        cmd.Set("$s", state);
        cmd.Set("$fid", fileId);
        cmd.ExecuteNonQuery();
    }

    public void SetUploaded(string fileId, string sha256)
    {
        using var cmd = db.Connection.Cmd(null, "UPDATE files SET state = 'uploaded', sha256 = $sha WHERE file_id = $fid");
        cmd.Set("$sha", sha256);
        cmd.Set("$fid", fileId);
        cmd.ExecuteNonQuery();
    }

    public void SetCachePath(string fileId, string cachePath, string state)
    {
        using var cmd = db.Connection.Cmd(null, "UPDATE files SET cache_path = $p, state = $s WHERE file_id = $fid");
        cmd.Set("$p", cachePath);
        cmd.Set("$s", state);
        cmd.Set("$fid", fileId);
        cmd.ExecuteNonQuery();
    }

    public void SetStateBulk(SqliteTransaction? tx, string messageId, string from, string to)
    {
        using var cmd = db.Connection.Cmd(tx, "UPDATE files SET state = $to WHERE message_id = $mid AND state = $from");
        cmd.Set("$to", to);
        cmd.Set("$mid", messageId);
        cmd.Set("$from", from);
        cmd.ExecuteNonQuery();
    }

    private const string SelectSql =
        "SELECT file_id, message_id, idx, name, size, mime, sha256, direction, source_path, source_modified_at, cache_path, state FROM files";

    private static FileRow Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt64(4),
        r.Str(5), r.Str(6), StateStrings.ToDirection(r.GetString(7)), r.Str(8),
        r.Int64OrNull(9), r.Str(10), r.GetString(11));

    private static void Bind(SqliteCommand cmd, FileRow f)
    {
        cmd.Set("$fid", f.FileId);
        cmd.Set("$mid", f.MessageId);
        cmd.Set("$idx", f.Idx);
        cmd.Set("$name", f.Name);
        cmd.Set("$size", f.Size);
        cmd.Set("$mime", f.Mime);
        cmd.Set("$sha", f.Sha256);
        cmd.Set("$direction", StateStrings.Of(f.Dir));
        cmd.Set("$source_path", f.SourcePath);
        cmd.Set("$source_modified_at", f.SourceModifiedAt);
        cmd.Set("$cache_path", f.CachePath);
        cmd.Set("$state", f.State);
    }
}

public sealed class JobDao(Database db)
{
    public void Insert(SqliteTransaction tx, JobRow j)
    {
        using var cmd = db.Connection.Cmd(tx,
            """
            INSERT INTO upload_jobs(message_id, state, attempts, next_attempt_at, bytes_done, bytes_total, error_code, error_message, enqueued_at, updated_at)
            VALUES($mid, $state, $attempts, $next, $done, $total, $code, $msg, $enq, $upd)
            """);
        Bind(cmd, j);
        cmd.ExecuteNonQuery();
    }

    public JobRow? Get(string messageId)
    {
        using var cmd = db.Connection.Cmd(null, SelectSql + " WHERE message_id = $mid");
        cmd.Set("$mid", messageId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>
    /// 原子 claim：选取最早 queued 或已到期 retry_wait（附录 B），事务中二次核对状态后置 uploading。
    /// 返回 null 表示没有可执行任务。
    /// </summary>
    public JobRow? ClaimNext(long nowMs)
    {
        var messageId = db.Write(tx =>
        {
            using (var sel = db.Connection.Cmd(tx,
                       """
                       SELECT message_id FROM upload_jobs
                       WHERE state = 'queued' OR (state = 'retry_wait' AND next_attempt_at <= $now)
                       ORDER BY enqueued_at LIMIT 1
                       """))
            {
                sel.Set("$now", nowMs);
                var id = sel.ExecuteScalar() as string;
                if (id is null) return (string?)null;

                using var upd = db.Connection.Cmd(tx,
                    "UPDATE upload_jobs SET state = 'uploading', error_code = NULL, error_message = NULL, updated_at = $u WHERE message_id = $mid AND state IN ('queued','retry_wait')");
                upd.Set("$u", Now());
                upd.Set("$mid", id);
                return upd.ExecuteNonQuery() == 1 ? id : null;
            }
        });
        return messageId is null ? null : Get(messageId);
    }

    public void SetState(string messageId, JobState state, int attempts, long? nextAttemptAt,
        long? bytesDone = null, string? errorCode = null, string? errorMessage = null)
    {
        using var cmd = db.Connection.Cmd(null,
            """
            UPDATE upload_jobs
            SET state = $state, attempts = $attempts, next_attempt_at = $next,
                bytes_done = COALESCE($done, bytes_done),
                error_code = $code, error_message = $msg, updated_at = $u
            WHERE message_id = $mid
            """);
        cmd.Set("$state", StateStrings.Of(state));
        cmd.Set("$attempts", attempts);
        cmd.Set("$next", nextAttemptAt);
        cmd.Set("$done", bytesDone);
        cmd.Set("$code", errorCode);
        cmd.Set("$msg", errorMessage);
        cmd.Set("$u", Now());
        cmd.Set("$mid", messageId);
        cmd.ExecuteNonQuery();
    }

    public void SetProgress(string messageId, long bytesDone)
    {
        using var cmd = db.Connection.Cmd(null, "UPDATE upload_jobs SET bytes_done = $d, updated_at = $u WHERE message_id = $mid");
        cmd.Set("$d", bytesDone);
        cmd.Set("$u", Now());
        cmd.Set("$mid", messageId);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string messageId)
    {
        using var cmd = db.Connection.Cmd(null, "DELETE FROM upload_jobs WHERE message_id = $mid");
        cmd.Set("$mid", messageId);
        cmd.ExecuteNonQuery();
    }

    public int Count(JobState state)
    {
        using var cmd = db.Connection.Cmd(null, "SELECT COUNT(*) FROM upload_jobs WHERE state = $s");
        cmd.Set("$s", StateStrings.Of(state));
        return (int)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>修改应用密码后：只重排 AUTH failed job。</summary>
    public void RequeueAuthFailed()
    {
        using var cmd = db.Connection.Cmd(null,
            "UPDATE upload_jobs SET state = 'queued', attempts = 0, next_attempt_at = NULL, error_code = NULL, error_message = NULL, updated_at = $u WHERE state = 'failed' AND error_code = 'AUTH'");
        cmd.Set("$u", Now());
        cmd.ExecuteNonQuery();
    }

    /// <summary>最早的未到期 retry_wait 时间（毫秒时间戳）；无则返回 null。供泵定时醒来消费（§4.3）。</summary>
    public long? NextRetryWaitAt(long nowMs)
    {
        using var cmd = db.Connection.Cmd(null,
            "SELECT MIN(next_attempt_at) FROM upload_jobs WHERE state = 'retry_wait' AND next_attempt_at > $now");
        cmd.Set("$now", nowMs);
        var v = cmd.ExecuteScalar();
        return v is long l ? l : null;
    }

    /// <summary>用户点重试：指定 job 转 queued。</summary>
    public bool Requeue(string messageId)
    {
        using var cmd = db.Connection.Cmd(null,
            "UPDATE upload_jobs SET state = 'queued', attempts = 0, next_attempt_at = NULL, error_code = NULL, error_message = NULL, updated_at = $u WHERE message_id = $mid AND state IN ('failed','retry_wait','uploading')");
        cmd.Set("$u", Now());
        cmd.Set("$mid", messageId);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>启动恢复：uploading → queued；文件 uploading → pending。</summary>
    public int RecoverUploading()
    {
        using var cmd = db.Connection.Cmd(null,
            "UPDATE upload_jobs SET state = 'queued', updated_at = $u WHERE state = 'uploading'");
        cmd.Set("$u", Now());
        return cmd.ExecuteNonQuery();
    }

    private const string SelectSql =
        "SELECT message_id, state, attempts, next_attempt_at, bytes_done, bytes_total, error_code, error_message, enqueued_at, updated_at FROM upload_jobs";

    private static JobRow Read(SqliteDataReader r) => new(
        r.GetString(0), StateStrings.ToJobState(r.GetString(1)), r.GetInt32(2), r.Int64OrNull(3),
        r.GetInt64(4), r.GetInt64(5), r.Str(6), r.Str(7), r.GetString(8), r.GetString(9));

    private static void Bind(SqliteCommand cmd, JobRow j)
    {
        cmd.Set("$mid", j.MessageId);
        cmd.Set("$state", StateStrings.Of(j.State));
        cmd.Set("$attempts", j.Attempts);
        cmd.Set("$next", j.NextAttemptAt);
        cmd.Set("$done", j.BytesDone);
        cmd.Set("$total", j.BytesTotal);
        cmd.Set("$code", j.ErrorCode);
        cmd.Set("$msg", j.ErrorMessage);
        cmd.Set("$enq", j.EnqueuedAt);
        cmd.Set("$upd", j.UpdatedAt);
    }

    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}

public sealed class RejectedDao(Database db)
{
    /// <summary>记录一次拒绝；同签名累加，第 3 次标记 quarantine；签名变化清零重试。</summary>
    public void RecordFailure(string remotePath, string messageId, string signature, string reasonCode, string now)
    {
        db.Write(tx =>
        {
            using (var sel = db.Connection.Cmd(tx, "SELECT remote_signature FROM rejected_items WHERE remote_path = $p"))
            {
                sel.Set("$p", remotePath);
                var prev = sel.ExecuteScalar() as string;
                if (prev is not null && prev != signature)
                {
                    using var del = db.Connection.Cmd(tx, "DELETE FROM rejected_items WHERE remote_path = $p");
                    del.Set("$p", remotePath);
                    del.ExecuteNonQuery();
                }
            }

            using (var up = db.Connection.Cmd(tx,
                       """
                       INSERT INTO rejected_items(remote_path, message_id, remote_signature, reason_code, fail_count, quarantined, last_failed_at)
                       VALUES($p, $mid, $sig, $reason, 1, 0, $now)
                       ON CONFLICT(remote_path) DO UPDATE SET
                         fail_count = fail_count + 1,
                         reason_code = excluded.reason_code,
                         last_failed_at = excluded.last_failed_at,
                         quarantined = CASE WHEN fail_count + 1 >= 3 THEN 1 ELSE 0 END
                       """))
            {
                up.Set("$p", remotePath);
                up.Set("$mid", messageId);
                up.Set("$sig", signature);
                up.Set("$reason", reasonCode);
                up.Set("$now", now);
                up.ExecuteNonQuery();
            }
        });
    }

    public bool IsQuarantined(string remotePath)
    {
        using var cmd = db.Connection.Cmd(null, "SELECT quarantined FROM rejected_items WHERE remote_path = $p");
        cmd.Set("$p", remotePath);
        return cmd.ExecuteScalar() is long q && q == 1;
    }

    /// <summary>
    /// 是否跳过 GET：quarantined 且远端签名未变 → true；
    /// 签名已变化 → 清除记录并返回 false（重新尝试）。
    /// </summary>
    public bool ShouldSkip(string remotePath, string signature)
    {
        using var cmd = db.Connection.Cmd(null,
            "SELECT quarantined, remote_signature FROM rejected_items WHERE remote_path = $p");
        cmd.Set("$p", remotePath);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return false;
        if (r.GetInt64(0) != 1) return false;
        var sig = r.Str(1);
        if (sig == signature) return true;
        Remove(remotePath);
        return false;
    }

    public void Remove(string remotePath)
    {
        using var cmd = db.Connection.Cmd(null, "DELETE FROM rejected_items WHERE remote_path = $p");
        cmd.Set("$p", remotePath);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<RejectedRow> GetAll()
    {
        using var cmd = db.Connection.Cmd(null,
            "SELECT remote_path, message_id, remote_signature, reason_code, fail_count, quarantined, last_failed_at FROM rejected_items ORDER BY last_failed_at DESC");
        using var r = cmd.ExecuteReader();
        var list = new List<RejectedRow>();
        while (r.Read())
            list.Add(new RejectedRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt64(5) == 1, r.GetString(6)));
        return list;
    }

    /// <summary>设置页"重试被拒绝条目"：清除 quarantine 并清零计数。</summary>
    public void ClearQuarantine()
    {
        using var cmd = db.Connection.Cmd(null, "UPDATE rejected_items SET quarantined = 0, fail_count = 0");
        cmd.ExecuteNonQuery();
    }

    /// <summary>应用升级且支持的 schema 版本变化时：清除版本类 quarantine。</summary>
    public void ClearVersionQuarantine()
    {
        using var cmd = db.Connection.Cmd(null,
            "DELETE FROM rejected_items WHERE reason_code = 'SCHEMA_VERSION'");
        cmd.ExecuteNonQuery();
    }
}

public sealed class SyncMonthDao(Database db)
{
    public (string? LastScan, string? ItemSig, string? TombSig) Get(string month)
    {
        using var cmd = db.Connection.Cmd(null,
            "SELECT last_scanned_at, last_item_signature, last_tombstone_signature FROM sync_months WHERE month = $m");
        cmd.Set("$m", month);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null, null);
        return (r.Str(0), r.Str(1), r.Str(2));
    }

    public void MarkScanned(string month, string? itemSig, string? tombSig, string now)
    {
        using var cmd = db.Connection.Cmd(null,
            """
            INSERT INTO sync_months(month, last_scanned_at, last_item_signature, last_tombstone_signature)
            VALUES($m, $now, $is, $ts)
            ON CONFLICT(month) DO UPDATE SET
              last_scanned_at = excluded.last_scanned_at,
              last_item_signature = excluded.last_item_signature,
              last_tombstone_signature = excluded.last_tombstone_signature
            """);
        cmd.Set("$m", month);
        cmd.Set("$now", now);
        cmd.Set("$is", itemSig);
        cmd.Set("$ts", tombSig);
        cmd.ExecuteNonQuery();
    }
}
