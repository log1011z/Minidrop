using Microsoft.Data.Sqlite;

namespace MiniDrop.Storage;

/// <summary>
/// 本地 SQLite 事实源。单连接 + 写锁串行（队列与索引都在此库）。
/// DDL 与设计文档 §4.1 一致；raw_json 不存在。
/// </summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public Database(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();
        Execute("PRAGMA foreign_keys = ON;");
        Execute("PRAGMA journal_mode = WAL;");
        Execute("PRAGMA synchronous = NORMAL;");
        Migrate();
    }

    public SqliteConnection Connection => _connection;

    /// <summary>写事务（同步版）：在写锁内执行，异常回滚。</summary>
    public T Write<T>(Func<SqliteTransaction, T> action)
    {
        _writeLock.Wait();
        try
        {
            using var tx = _connection.BeginTransaction();
            try
            {
                var result = action(tx);
                tx.Commit();
                return result;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Write(Action<SqliteTransaction> action) => Write<object?>(tx => { action(tx); return null; });

    /// <summary>写事务：在写锁内执行，异常回滚。</summary>
    public async Task<T> WriteAsync<T>(Func<SqliteTransaction, Task<T>> action)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                var result = await action(tx).ConfigureAwait(false);
                tx.Commit();
                return result;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task WriteAsync(Func<SqliteTransaction, Task> action) =>
        WriteAsync<object?>(async tx => { await action(tx).ConfigureAwait(false); return null; });

    public void Dispose()
    {
        _connection.Dispose();
        _writeLock.Dispose();
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Migrate()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS meta (
              key   TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS messages (
              id           TEXT PRIMARY KEY,
              remote_month TEXT NOT NULL,
              device_id    TEXT NOT NULL,
              device_name  TEXT NOT NULL,
              created_at   TEXT NOT NULL,
              text         TEXT,
              direction    TEXT NOT NULL CHECK(direction IN ('in','out')),
              received_at  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_messages_timeline
              ON messages(created_at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS idx_messages_month
              ON messages(remote_month, id DESC);

            CREATE TABLE IF NOT EXISTS files (
              file_id      TEXT PRIMARY KEY,
              message_id   TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
              idx          INTEGER NOT NULL,
              name         TEXT NOT NULL,
              size         INTEGER NOT NULL,
              mime         TEXT,
              sha256       TEXT,
              direction    TEXT NOT NULL CHECK(direction IN ('in','out')),
              source_path  TEXT,
              source_modified_at INTEGER,
              cache_path   TEXT,
              state        TEXT NOT NULL,
              UNIQUE(message_id, idx)
            );
            CREATE INDEX IF NOT EXISTS idx_files_message ON files(message_id, idx);

            CREATE TABLE IF NOT EXISTS upload_jobs (
              message_id       TEXT PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
              state            TEXT NOT NULL CHECK(state IN
                                ('queued','uploading','retry_wait','failed')),
              attempts         INTEGER NOT NULL DEFAULT 0,
              next_attempt_at  INTEGER,
              bytes_done       INTEGER NOT NULL DEFAULT 0,
              bytes_total      INTEGER NOT NULL DEFAULT 0,
              error_code       TEXT,
              error_message    TEXT,
              enqueued_at      TEXT NOT NULL,
              updated_at       TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_jobs_next
              ON upload_jobs(state, next_attempt_at, enqueued_at);

            CREATE TABLE IF NOT EXISTS rejected_items (
              remote_path      TEXT PRIMARY KEY,
              message_id       TEXT NOT NULL,
              remote_signature TEXT NOT NULL,
              reason_code      TEXT NOT NULL,
              fail_count       INTEGER NOT NULL,
              quarantined      INTEGER NOT NULL DEFAULT 0,
              last_failed_at   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sync_months (
              month                    TEXT PRIMARY KEY,
              last_scanned_at          TEXT,
              last_item_signature      TEXT,
              last_tombstone_signature TEXT
            );
            """);
    }
}
