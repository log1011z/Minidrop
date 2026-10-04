package com.minidrop.app.data.db

import androidx.room.ColumnInfo
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.ForeignKey
import androidx.room.Index
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.Transaction
import androidx.sqlite.db.SupportSQLiteDatabase
import android.content.Context

@Entity(tableName = "meta")
data class MetaEntity(@PrimaryKey val key: String, val value: String)

/** direction: in/out；files.state 一列按 direction 区分上传/下载两套词表（与 C# 端一致）。 */
@Entity(
    tableName = "messages",
    indices = [
        Index(value = ["created_at", "id"], orders = [Index.Order.DESC, Index.Order.DESC], name = "idx_messages_timeline"),
        Index(value = ["remote_month", "id"], name = "idx_messages_month"),
    ],
)
data class MessageEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "remote_month") val remoteMonth: String,
    @ColumnInfo(name = "device_id") val deviceId: String,
    @ColumnInfo(name = "device_name") val deviceName: String,
    @ColumnInfo(name = "created_at") val createdAt: String,
    val text: String?,
    val direction: String,
    @ColumnInfo(name = "received_at") val receivedAt: String,
)

@Entity(
    tableName = "files",
    indices = [
        Index(value = ["message_id", "idx"], unique = true, name = "idx_files_message"),
    ],
    foreignKeys = [
        ForeignKey(
            entity = MessageEntity::class, parentColumns = ["id"], childColumns = ["message_id"],
            onDelete = ForeignKey.CASCADE,
        ),
    ],
)
data class FileEntity(
    @PrimaryKey @ColumnInfo(name = "file_id") val fileId: String,
    @ColumnInfo(name = "message_id") val messageId: String,
    val idx: Int,
    val name: String,
    val size: Long,
    val mime: String?,
    @ColumnInfo(name = "sha256") val sha256: String?,
    val direction: String,
    @ColumnInfo(name = "source_path") val sourcePath: String?,
    @ColumnInfo(name = "source_modified_at") val sourceModifiedAt: Long?,
    @ColumnInfo(name = "cache_path") val cachePath: String?,
    val state: String,
)

@Entity(
    tableName = "upload_jobs",
    indices = [Index(value = ["state", "next_attempt_at", "enqueued_at"], name = "idx_jobs_next")],
    foreignKeys = [
        ForeignKey(
            entity = MessageEntity::class, parentColumns = ["id"], childColumns = ["message_id"],
            onDelete = ForeignKey.CASCADE,
        ),
    ],
)
data class UploadJobEntity(
    @PrimaryKey @ColumnInfo(name = "message_id") val messageId: String,
    val state: String,
    val attempts: Int,
    @ColumnInfo(name = "next_attempt_at") val nextAttemptAt: Long?,
    @ColumnInfo(name = "bytes_done") val bytesDone: Long,
    @ColumnInfo(name = "bytes_total") val bytesTotal: Long,
    @ColumnInfo(name = "error_code") val errorCode: String?,
    @ColumnInfo(name = "error_message") val errorMessage: String?,
    @ColumnInfo(name = "enqueued_at") val enqueuedAt: String,
    @ColumnInfo(name = "updated_at") val updatedAt: String,
)

@Entity(tableName = "rejected_items")
data class RejectedItemEntity(
    @PrimaryKey @ColumnInfo(name = "remote_path") val remotePath: String,
    @ColumnInfo(name = "message_id") val messageId: String,
    @ColumnInfo(name = "remote_signature") val remoteSignature: String,
    @ColumnInfo(name = "reason_code") val reasonCode: String,
    @ColumnInfo(name = "fail_count") val failCount: Int,
    val quarantined: Int,
    @ColumnInfo(name = "last_failed_at") val lastFailedAt: String,
)

@Entity(tableName = "sync_months")
data class SyncMonthEntity(
    @PrimaryKey val month: String,
    @ColumnInfo(name = "last_scanned_at") val lastScannedAt: String?,
    @ColumnInfo(name = "last_item_signature") val lastItemSignature: String?,
    @ColumnInfo(name = "last_tombstone_signature") val lastTombstoneSignature: String?,
)

data class TimelineRow(
    val id: String,
    val remoteMonth: String,
    val deviceId: String,
    val deviceName: String,
    val createdAt: String,
    val text: String?,
    val direction: String,
    val receivedAt: String,
    val jobState: String?,
    val jobAttempts: Int?,
    val jobBytesDone: Long?,
    val jobBytesTotal: Long?,
    val jobErrorCode: String?,
)

@Dao
interface MetaDao {
    @Query("SELECT value FROM meta WHERE key = :key")
    suspend fun get(key: String): String?

    @Query("SELECT value FROM meta WHERE key = :key")
    fun getBlocking(key: String): String?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entity: MetaEntity)

    @Query("DELETE FROM meta")
    suspend fun clear()
}

@Dao
interface MessageDao {
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun upsert(entity: MessageEntity)

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    fun upsertBlocking(entity: MessageEntity)

    @Query("SELECT * FROM messages WHERE id = :id")
    suspend fun getById(id: String): MessageEntity?

    @Query("SELECT EXISTS(SELECT 1 FROM messages WHERE id = :id)")
    suspend fun exists(id: String): Boolean

    @Query("DELETE FROM messages WHERE id = :id")
    suspend fun delete(id: String)

    @Query("DELETE FROM messages")
    suspend fun deleteAll()

    @Query("SELECT * FROM messages ORDER BY created_at DESC, id DESC LIMIT :limit OFFSET :offset")
    suspend fun timelinePage(limit: Int, offset: Int): List<MessageEntity>

    @Query("SELECT COUNT(*) FROM messages")
    suspend fun count(): Long

    @Query("SELECT remote_month FROM (SELECT remote_month FROM messages ORDER BY created_at DESC, id DESC LIMIT :limit) GROUP BY remote_month ORDER BY remote_month DESC")
    suspend fun monthsOfNewest(limit: Int): List<String>

    @Query("SELECT id FROM messages WHERE remote_month = :month")
    suspend fun idsByMonth(month: String): List<String>

    @Query("SELECT id FROM messages WHERE remote_month <= :cutoffMonth ORDER BY id")
    suspend fun idsInMonthsUpTo(cutoffMonth: String): List<String>

    @Query(
        """SELECT m.id AS id, m.remote_month AS remoteMonth, m.device_id AS deviceId,
           m.device_name AS deviceName, m.created_at AS createdAt, m.text AS text,
           m.direction AS direction, m.received_at AS receivedAt,
           j.state AS jobState, j.attempts AS jobAttempts,
           j.bytes_done AS jobBytesDone, j.bytes_total AS jobBytesTotal,
           j.error_code AS jobErrorCode
           FROM messages m LEFT JOIN upload_jobs j ON j.message_id = m.id
           ORDER BY m.created_at DESC, m.id DESC LIMIT :limit""",
    )
    fun timelineFlow(limit: Int): kotlinx.coroutines.flow.Flow<List<TimelineRow>>
}

@Dao
interface FileDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entity: FileEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun upsertBlocking(entity: FileEntity)

    @Query("SELECT * FROM files WHERE file_id = :fileId")
    suspend fun getById(fileId: String): FileEntity?

    @Query("SELECT * FROM files WHERE message_id = :messageId ORDER BY idx")
    suspend fun getByMessage(messageId: String): List<FileEntity>

    @Query("UPDATE files SET state = :state WHERE file_id = :fileId")
    suspend fun setState(fileId: String, state: String)

    @Query("UPDATE files SET state = 'uploaded', sha256 = :sha256 WHERE file_id = :fileId")
    suspend fun setUploaded(fileId: String, sha256: String)

    @Query("UPDATE files SET cache_path = :path, state = :state WHERE file_id = :fileId")
    suspend fun setCachePath(fileId: String, path: String, state: String)

    @Query("UPDATE files SET state = :to WHERE message_id = :messageId AND state = :from")
    suspend fun setStateBulk(messageId: String, from: String, to: String)

    @Query("UPDATE files SET state = :to WHERE state = 'uploading'")
    suspend fun setStateBulkAllUploading(to: String)

    @Query("SELECT * FROM files")
    fun allFiles(): kotlinx.coroutines.flow.Flow<List<FileEntity>>

    @Query("SELECT source_path FROM files WHERE source_path IS NOT NULL")
    suspend fun referencedSourcePaths(): List<String>
}

@Dao
interface JobDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insert(entity: UploadJobEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun insertBlocking(entity: UploadJobEntity)

    @Query("SELECT * FROM upload_jobs WHERE message_id = :messageId")
    suspend fun getById(messageId: String): UploadJobEntity?

    @Query("SELECT * FROM upload_jobs WHERE message_id = :messageId")
    fun getByIdBlocking(messageId: String): UploadJobEntity?

    /** 原子 claim：事务中选取最早 queued 或到期 retry_wait 并置 uploading。 */
    @Transaction
    suspend fun claimNext(nowMs: Long): UploadJobEntity? {
        val candidate = selectNext(nowMs) ?: return null
        val updated = updateToUploading(candidate.messageId, nowMs)
        return if (updated > 0) getById(candidate.messageId) else null
    }

    @Query(
        """SELECT * FROM upload_jobs
           WHERE state = 'queued' OR (state = 'retry_wait' AND next_attempt_at <= :nowMs)
           ORDER BY enqueued_at LIMIT 1""",
    )
    suspend fun selectNext(nowMs: Long): UploadJobEntity?

    @Query(
        """UPDATE upload_jobs SET state = 'uploading', error_code = NULL, error_message = NULL,
           updated_at = :nowMs WHERE message_id = :messageId AND state IN ('queued','retry_wait')""",
    )
    suspend fun updateToUploading(messageId: String, nowMs: Long): Int

    @Query(
        """UPDATE upload_jobs SET state = :state, attempts = :attempts, next_attempt_at = :nextAttemptAt,
           error_code = :errorCode, error_message = :errorMessage, updated_at = :now WHERE message_id = :messageId""",
    )
    suspend fun setState(
        messageId: String,
        state: String,
        attempts: Int,
        nextAttemptAt: Long?,
        errorCode: String?,
        errorMessage: String?,
        now: String,
    )

    @Query("UPDATE upload_jobs SET bytes_done = :bytesDone, updated_at = :now WHERE message_id = :messageId")
    suspend fun setProgress(messageId: String, bytesDone: Long, now: String)

    @Query("DELETE FROM upload_jobs WHERE message_id = :messageId")
    suspend fun delete(messageId: String)

    @Query("SELECT COUNT(*) FROM upload_jobs WHERE state = :state")
    suspend fun count(state: String): Long

    @Query(
        """UPDATE upload_jobs SET state = 'queued', attempts = 0, next_attempt_at = NULL,
           error_code = NULL, error_message = NULL, updated_at = :now
           WHERE state = 'failed' AND error_code = 'AUTH'""",
    )
    suspend fun requeueAuthFailed(now: String): Int

    @Query(
        """UPDATE upload_jobs SET state = 'queued', attempts = 0, next_attempt_at = NULL,
           error_code = NULL, error_message = NULL, updated_at = :now
           WHERE message_id = :messageId AND state IN ('failed','retry_wait','uploading')""",
    )
    suspend fun requeue(messageId: String, now: String): Int

    @Query(
        """UPDATE upload_jobs SET state = 'queued', attempts = 0, next_attempt_at = NULL,
           error_code = NULL, error_message = NULL, updated_at = :now
           WHERE message_id = :messageId AND state IN ('failed','retry_wait')""",
    )
    suspend fun retryFromTimeline(messageId: String, now: String): Int

    @Query("UPDATE upload_jobs SET state = 'queued', updated_at = :now WHERE state = 'uploading'")
    suspend fun recoverUploading(now: String): Int

    @Query("SELECT MIN(next_attempt_at) FROM upload_jobs WHERE state = 'retry_wait' AND next_attempt_at > :nowMs")
    suspend fun nextRetryWaitAt(nowMs: Long): Long?
}

@Dao
interface RejectedDao {
    @Query("SELECT * FROM rejected_items WHERE remote_path = :path")
    suspend fun getByPath(path: String): RejectedItemEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entity: RejectedItemEntity)

    @Query("DELETE FROM rejected_items WHERE remote_path = :path")
    suspend fun remove(path: String)

    @Query("UPDATE rejected_items SET quarantined = 0, fail_count = 0")
    suspend fun clearQuarantine()

    /** 记录一次拒绝；同签名累加，第 3 次标记 quarantine；签名变化清零重试。 */
    @Transaction
    suspend fun recordFailure(
        remotePath: String,
        messageId: String,
        signature: String,
        reasonCode: String,
        now: String,
    ) {
        val prev = getByPath(remotePath)
        if (prev != null && prev.remoteSignature != signature) {
            remove(remotePath)
        }
        val count = if (prev != null && prev.remoteSignature == signature) prev.failCount + 1 else 1
        upsert(
            RejectedItemEntity(
                remotePath, messageId, signature, reasonCode, count,
                if (count >= 3) 1 else 0, now,
            ),
        )
    }

    /** quarantined 且签名未变 → true；签名变化 → 清除记录并重新尝试。 */
    @Transaction
    suspend fun shouldSkip(remotePath: String, signature: String): Boolean {
        val row = getByPath(remotePath) ?: return false
        if (row.quarantined != 1) return false
        if (row.remoteSignature == signature) return true
        remove(remotePath)
        return false
    }
}

@Dao
interface SyncMonthDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entity: SyncMonthEntity)
}

@Database(
    entities = [
        MetaEntity::class, MessageEntity::class, FileEntity::class,
        UploadJobEntity::class, RejectedItemEntity::class, SyncMonthEntity::class,
    ],
    version = 1,
    exportSchema = false,
)
abstract class MiniDropDatabase : RoomDatabase() {
    abstract fun metaDao(): MetaDao
    abstract fun messageDao(): MessageDao
    abstract fun fileDao(): FileDao
    abstract fun jobDao(): JobDao
    abstract fun rejectedDao(): RejectedDao
    abstract fun syncMonthDao(): SyncMonthDao

    companion object {
        fun build(context: Context): MiniDropDatabase {
            val db = Room.databaseBuilder(context, MiniDropDatabase::class.java, "minidrop.db")
                .addCallback(object : androidx.room.RoomDatabase.Callback() {
                    override fun onCreate(db: SupportSQLiteDatabase) {
                        // 与设计文档 §4.1 一致的外键（Room 不生成 CHECK/外键时在此补齐）
                        db.execSQL("PRAGMA foreign_keys = ON")
                    }

                    override fun onOpen(db: SupportSQLiteDatabase) {
                        db.execSQL("PRAGMA foreign_keys = ON")
                    }
                })
                .build()
            return db
        }
    }
}

fun nowUtcString(): String =
    java.time.format.DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'")
        .withZone(java.time.ZoneOffset.UTC)
        .format(java.time.Instant.now())
