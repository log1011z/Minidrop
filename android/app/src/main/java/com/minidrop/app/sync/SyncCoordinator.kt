package com.minidrop.app.sync

import com.minidrop.app.core.ErrorCodes
import com.minidrop.app.core.Limits
import com.minidrop.app.core.MessageJson
import com.minidrop.app.core.RejectReasons
import com.minidrop.app.core.RemotePaths
import com.minidrop.app.core.UlidClock
import com.minidrop.app.core.FileStates
import com.minidrop.app.data.db.FileEntity
import com.minidrop.app.data.db.JobDao
import com.minidrop.app.data.db.FileDao
import com.minidrop.app.data.db.MessageDao
import com.minidrop.app.data.db.MessageEntity
import com.minidrop.app.data.db.MetaDao
import com.minidrop.app.data.db.MetaEntity
import com.minidrop.app.data.db.MiniDropDatabase
import com.minidrop.app.data.db.RejectedDao
import com.minidrop.app.data.db.RejectedItemEntity
import com.minidrop.app.data.db.SyncMonthDao
import com.minidrop.app.data.db.SyncMonthEntity
import com.minidrop.app.data.db.nowUtcString
import com.minidrop.app.webdav.DavItem
import com.minidrop.app.webdav.DavStatus
import com.minidrop.app.webdav.ErrorClassifier
import com.minidrop.app.webdav.WebDavClient
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.security.MessageDigest

data class RemoteRef(val id: String, val signature: String)

data class MonthScan(val items: List<RemoteRef>, val tombstones: List<RemoteRef>)

data class RefreshOutcome(val added: Int, val failed: Int, val scanError: Boolean) {
    val success get() = !scanError && failed == 0
}

data class LoadOlderOutcome(val added: Int, val failed: Int, val noMore: Boolean, val scanError: Boolean)

data class MaintainOutcome(val processed: Int, val scanError: Boolean)

class SyncException(val errorCode: String) : Exception("sync failed: $errorCode")

/**
 * 手动同步（§6）：scanMonth 原语、最近 20 条刷新、历史游标与加载更早、拒绝与 quarantine。
 * 无任何自动触发：只由用户刷新 / 加载更早 / 维护入口调用。
 */
class SyncCoordinator(
    private val db: MiniDropDatabase,
    private val dav: () -> WebDavClient,
    private val maxFileBytes: () -> Long,
    val mutex: Mutex = Mutex(),
) {
    companion object {
        const val WINDOW_SIZE = 20
        const val MAX_MONTHS_BACK = 3
        const val CONVERGE_BATCH = 20
        const val K_CURSOR_MONTH = "history_cursor_month"
        const val K_CURSOR_BEFORE_ID = "history_cursor_before_id"
        const val K_INITIALIZED = "history_initialized"
        const val K_LAST_REFRESH = "last_manual_refresh_at"
        const val K_LAST_MAINTENANCE = "last_remote_maintenance_at"
    }

    private val messageDao: MessageDao get() = db.messageDao()
    private val fileDao: FileDao get() = db.fileDao()
    private val jobDao: JobDao get() = db.jobDao()
    private val metaDao: MetaDao get() = db.metaDao()
    private val rejectedDao: RejectedDao get() = db.rejectedDao()
    private val syncMonthDao: SyncMonthDao get() = db.syncMonthDao()

    private suspend fun metaSet(key: String, value: String?) {
        if (value == null) return
        metaDao.upsert(MetaEntity(key, value))
    }

    // ---------- 刷新：最近 20 条 ----------

    suspend fun refresh(): RefreshOutcome = mutex.withLock {
        val now = nowUtcString()
        val initialized = metaDao.get(K_INITIALIZED) == "1"
        val localCount = messageDao.count()
        val localMonths = if (initialized && localCount >= WINDOW_SIZE) {
            messageDao.monthsOfNewest(WINDOW_SIZE).toHashSet()
        } else {
            null
        }

        val selected = ArrayList<RemoteRef>()
        var scanError = false
        var month = UlidClock.currentUtcMonth()

        var back = 0
        while (back < MAX_MONTHS_BACK && selected.size < WINDOW_SIZE) {
            val scan = try {
                scanMonth(month)
            } catch (_: SyncException) {
                scanError = true
                break
            }

            processTombstones(month, scan, CONVERGE_BATCH)

            val tombIds = scan.tombstones.map { it.id }.toHashSet()
            val visible = scan.items.filter { it.id !in tombIds }.sortedByDescending { it.id }

            if (back == 0 && initialized && visible.size >= WINDOW_SIZE) {
                selected.addAll(visible.take(WINDOW_SIZE))
                break
            }

            for (v in visible) {
                if (selected.size >= WINDOW_SIZE) break
                selected.add(v)
            }
            if (selected.size >= WINDOW_SIZE) break

            val prev = UlidClock.previousMonth(month)
            if (localMonths != null && prev !in localMonths) break
            month = prev
            back++
        }

        val (added, failed) = fetchSelected(selected)

        // 游标：首次初始化指向已检查窗口边界；刷新发现更新消息时不移动旧游标
        if (!initialized) {
            if (selected.isNotEmpty()) {
                val oldest = selected.last().id
                metaSet(K_CURSOR_MONTH, UlidClock.monthOf(oldest))
                metaSet(K_CURSOR_BEFORE_ID, selected.minOf { it.id })
            } else if (!scanError) {
                metaSet(K_CURSOR_MONTH, UlidClock.currentUtcMonth())
            }
            metaSet(K_INITIALIZED, "1")
        }
        if (!scanError) metaSet(K_LAST_REFRESH, now)

        RefreshOutcome(added, failed, scanError)
    }

    // ---------- 加载更早：每批 20 条 ----------

    suspend fun loadOlder(): LoadOlderOutcome = mutex.withLock {
        if (metaDao.get(K_INITIALIZED) != "1") {
            return LoadOlderOutcome(0, 0, noMore = true, scanError = false)
        }
        var month = metaDao.get(K_CURSOR_MONTH) ?: UlidClock.currentUtcMonth()
        var before: String? = metaDao.get(K_CURSOR_BEFORE_ID)
        val cutoffMonth = UlidClock.expiryMonth()

        val selected = ArrayList<RemoteRef>()
        var scanError = false
        var noMore = false
        var emptyMonths = 0

        var back = 0
        while (back < MAX_MONTHS_BACK && selected.size < WINDOW_SIZE) {
            if (UlidClock.compareMonths(month, cutoffMonth) < 0) {
                noMore = true
                break
            }
            val scan = try {
                scanMonth(month)
            } catch (_: SyncException) {
                scanError = true
                break
            }

            processTombstones(month, scan, CONVERGE_BATCH)

            val tombIds = scan.tombstones.map { it.id }.toHashSet()
            val candidates = scan.items
                .filter { it.id !in tombIds }
                .filter { before == null || it.id < before }
                .sortedByDescending { it.id }

            val taken = candidates.take(WINDOW_SIZE - selected.size)
            selected.addAll(taken)

            if (taken.size < candidates.size || (taken.isNotEmpty() && selected.size >= WINDOW_SIZE)) {
                // 该月未耗尽：游标停在已选边界
                before = selected.minOf { it.id }
            } else {
                // 该月耗尽：前移；统计连续空月
                if (candidates.isEmpty() && scan.items.size == scan.tombstones.size) emptyMonths++ else emptyMonths = 0
                month = UlidClock.previousMonth(month)
                before = null
                if (emptyMonths >= MAX_MONTHS_BACK) {
                    noMore = true
                    break
                }
            }
            back++
        }

        if (selected.isNotEmpty()) before = selected.minOf { it.id }

        // 无论 GET 成败都前移扫描边界；这里持久化新游标
        metaSet(K_CURSOR_MONTH, month)
        if (before != null) metaSet(K_CURSOR_BEFORE_ID, before)

        val (added, failed) = fetchSelected(selected)
        LoadOlderOutcome(added, failed, noMore && added == 0, scanError)
    }

    // ---------- 原语 ----------

    /** scanMonth：分页读 items+tombstones，过滤非法文件名（§6.2）。 */
    suspend fun scanMonth(month: String): MonthScan {
        val items = scanDir(RemotePaths.itemsMonthDir(month))
        val tombstones = scanDir(RemotePaths.tombstonesMonthDir(month))
        syncMonthDao.upsert(
            SyncMonthEntity(month, nowUtcString(), aggregate(items), aggregate(tombstones)),
        )
        return MonthScan(items, tombstones)
    }

    private suspend fun scanDir(dir: String): List<RemoteRef> {
        val (result, raw) = dav().propfindDir(dir)
        if (!result.ok && result.status != DavStatus.NOT_FOUND) {
            throw SyncException(ErrorClassifier.toErrorCode(result))
        }
        return raw.filter { !it.isCollection }
            .filter { it.name.endsWith(".json") && it.name.length == 26 + 5 }
            .mapNotNull { item ->
                val id = item.name.removeSuffix(".json")
                if (com.minidrop.app.core.Ulid.isValid(id)) RemoteRef(id, item.signature) else null
            }
    }

    private fun aggregate(refs: List<RemoteRef>): String? {
        if (refs.isEmpty()) return null
        val joined = refs.sortedBy { it.id }.joinToString("|") { "${it.id}:${it.signature}" }
        return MessageDigest.getInstance("SHA-256").digest(joined.toByteArray())
            .joinToString("") { "%02x".format(it) }
    }

    /** 墓碑处理：本地删除 + 远端 R∩T 收敛（最多 limit 条）（§6.2 步骤 5/6）。 */
    suspend fun processTombstones(month: String, scan: MonthScan, limit: Int) {
        val tombIds = scan.tombstones.map { it.id }.toHashSet()
        for (localId in messageDao.idsByMonth(month)) {
            if (localId in tombIds) deleteLocal(localId)
        }
        val both = scan.items.filter { it.id in tombIds }.take(limit)
        for (ref in both) {
            converge(month, ref.id)
        }
    }

    /** 收敛：GET item 取文件 UUID → DELETE 全部文件（404 成功）→ 全部成功后最后 DELETE item。 */
    suspend fun converge(month: String, id: String): Boolean {
        val client = dav()
        val itemPath = RemotePaths.messagePath(month, id)
        val (res, content) = client.getBytes(itemPath, Limits.MAX_JSON_BYTES)
        if (res.status == DavStatus.NOT_FOUND) return true
        if (!res.ok || content == null) return false

        val parsed = MessageJson.parse(content, id, maxFileBytes())
        if (!parsed.ok || parsed.message == null) return false

        var allDeleted = true
        for (f in parsed.message.files) {
            val del = client.delete(RemotePaths.filePath(f.id))
            if (!del.ok) allDeleted = false
        }
        if (!allDeleted) return false
        return client.delete(itemPath).ok
    }

    /** 本地删除：删行（外键级联 files/jobs）→ 尽力删缓存文件（不删源文件）。 */
    suspend fun deleteLocal(id: String) {
        val files = fileDao.getByMessage(id)
        messageDao.delete(id)
        for (f in files) {
            f.cachePath?.let { p ->
                try { java.io.File(p).delete() } catch (_: Exception) {}
            }
        }
    }

    /** 对选中项 GET 缺失 JSON：跳过本地已有与 quarantine；结果入库。 */
    private suspend fun fetchSelected(selected: List<RemoteRef>): Pair<Int, Int> {
        var added = 0
        var failed = 0
        val client = dav()
        val maxFile = maxFileBytes()
        for (sel in selected) {
            val month = UlidClock.monthOf(sel.id)
            val path = RemotePaths.messagePath(month, sel.id)

            if (messageDao.exists(sel.id)) continue
            if (rejectedDao.shouldSkip(path, sel.signature)) continue

            val (res, content) = client.getBytes(path, Limits.MAX_JSON_BYTES)
            if (!res.ok) {
                failed++
                continue
            }
            if (content == null) {
                rejectedDao.recordFailure(path, sel.id, sel.signature, RejectReasons.TOO_LARGE, nowUtcString())
                continue
            }
            val parsed = MessageJson.parse(content, sel.id, maxFile)
            if (!parsed.ok || parsed.message == null) {
                rejectedDao.recordFailure(
                    path, sel.id, sel.signature,
                    parsed.reason ?: RejectReasons.BAD_JSON, nowUtcString(),
                )
                continue
            }
            insertIncoming(parsed.message)
            rejectedDao.remove(path)
            added++
        }
        return added to failed
    }

    /** 接收入库：messages + files 单事务。 */
    suspend fun insertIncoming(m: com.minidrop.app.core.RemoteMessage) {
        androidx.room.withTransaction(db) {
            messageDao.upsert(
                MessageEntity(
                    id = m.id,
                    remoteMonth = UlidClock.monthOf(m.id),
                    deviceId = m.deviceId,
                    deviceName = m.deviceName,
                    createdAt = m.createdAt,
                    text = m.text,
                    direction = "in",
                    receivedAt = nowUtcString(),
                ),
            )
            m.files.forEachIndexed { idx, f ->
                fileDao.upsert(
                    FileEntity(
                        fileId = f.id, messageId = m.id, idx = idx, name = f.name,
                        size = f.size, mime = f.mime, sha256 = f.sha256,
                        direction = "in", sourcePath = null, sourceModifiedAt = null,
                        cachePath = null, state = FileStates.REMOTE,
                    ),
                )
            }
        }
    }
}
