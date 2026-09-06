package com.minidrop.app.sync

import android.content.Context
import androidx.room.withTransaction
import com.minidrop.app.core.ErrorCodes
import com.minidrop.app.core.FileStates
import com.minidrop.app.core.JobStates
import com.minidrop.app.core.Limits
import com.minidrop.app.core.MessageJson
import com.minidrop.app.core.RemotePaths
import com.minidrop.app.core.Ulid
import com.minidrop.app.core.UlidClock
import com.minidrop.app.data.db.FileEntity
import com.minidrop.app.data.db.JobDao
import com.minidrop.app.data.db.MetaDao
import com.minidrop.app.data.db.MetaEntity
import com.minidrop.app.data.db.MiniDropDatabase
import com.minidrop.app.data.db.MessageEntity
import com.minidrop.app.data.db.RejectedDao
import com.minidrop.app.data.db.nowUtcString
import com.minidrop.app.webdav.DavStatus
import com.minidrop.app.webdav.ErrorClassifier
import com.minidrop.app.webdav.WebDavClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File


sealed class SendResult {
    data class Ok(val messageId: String) : SendResult()
    data class Fail(val text: String) : SendResult()
}

/** 入队（§5.1）：校验 → 生成 ULID/UUID → 单事务写 messages+files+queued job → 唤醒 pump。 */
class SendService(
    private val context: Context,
    private val db: MiniDropDatabase,
    private val maxFileBytes: () -> Long,
    private val deviceId: suspend () -> String,
    private val deviceName: suspend () -> String,
    private val trigger: () -> Unit,
) {
    suspend fun enqueueText(text: String): SendResult {
        if (text.isBlank()) return SendResult.Fail("内容为空")
        if (text.toByteArray(Charsets.UTF_8).size > Limits.MAX_TEXT_BYTES) {
            return SendResult.Fail("文字太长（上限 100 KiB）")
        }
        return enqueue(text, emptyList())
    }

    suspend fun enqueueFiles(files: List<StagedFile>, text: String?): SendResult {
        if (files.isEmpty()) return SendResult.Fail("没有文件")
        if (files.size > Limits.MAX_FILES) return SendResult.Fail("一次最多 ${Limits.MAX_FILES} 个文件")
        val maxBytes = maxFileBytes()
        for (f in files) {
            if (f.file.length() > maxBytes) {
                return SendResult.Fail("单个文件不能超过 ${maxBytes / (1024 * 1024)} MB")
            }
            if (!f.file.canRead()) return SendResult.Fail("文件不可读：" + f.displayName)
        }
        return enqueue(text?.takeIf { it.isNotBlank() }, files)
    }

    private suspend fun enqueue(text: String?, files: List<StagedFile>): SendResult {
        val id = Ulid.newUlid()
        val month = UlidClock.monthOf(id)
        val now = nowUtcString()
        val bytesTotal = files.sumOf { it.file.length() }
        val devId = deviceId()
        val devName = deviceName()

        db.withTransaction {
            db.messageDao().upsert(
                MessageEntity(
                    id = id, remoteMonth = month, deviceId = devId, deviceName = devName,
                    createdAt = now, text = text, direction = "out", receivedAt = now,
                ),
            )
            files.forEachIndexed { i, f ->
                db.fileDao().upsert(
                    FileEntity(
                        fileId = java.util.UUID.randomUUID().toString(),
                        messageId = id, idx = i, name = f.displayName,
                        size = f.file.length(),
                        mime = com.minidrop.app.core.MimeTypes.fromName(f.displayName),
                        sha256 = null,
                        direction = "out", sourcePath = f.file.absolutePath,
                        sourceModifiedAt = f.file.lastModified(), cachePath = null,
                        state = FileStates.PENDING,
                    ),
                )
            }
            db.jobDao().insert(
                com.minidrop.app.data.db.UploadJobEntity(
                    messageId = id, state = JobStates.QUEUED, attempts = 0, nextAttemptAt = null,
                    bytesDone = 0, bytesTotal = bytesTotal, errorCode = null, errorMessage = null,
                    enqueuedAt = now, updatedAt = now,
                ),
            )
        }
        trigger()
        return SendResult.Ok(id)
    }
}

data class StagedFile(val file: File, val displayName: String)

/**
 * 上传泵（§5.2–5.4）：由唯一 UploadPumpWorker 调用；SQLite 是唯一队列真相源。
 */
class UploadPump(
    private val db: MiniDropDatabase,
    private val dav: () -> WebDavClient,
    private val maxFileBytes: () -> Long,
) {
    suspend fun drain(): Int {
        var processed = 0
        while (true) {
            val job = db.jobDao().claimNext(System.currentTimeMillis()) ?: return processed
            processed++
            processClaimed(job.messageId)
        }
    }

    private suspend fun processClaimed(messageId: String) {
        val msg = db.messageDao().getById(messageId) ?: run {
            db.jobDao().delete(messageId)
            return
        }
        val files = db.fileDao().getByMessage(messageId)
        val job = db.jobDao().getById(messageId) ?: return

        try {
            uploadJob(msg, files, job)
        } catch (_: kotlinx.coroutines.CancellationException) {
            db.fileDao().setStateBulk(messageId, FileStates.UPLOADING, FileStates.PENDING)
            db.jobDao().setState(messageId, JobStates.QUEUED, job.attempts, null, null, null, nowUtcString())
            throw kotlinx.coroutines.CancellationException("cancelled")
        } catch (_: Exception) {
            db.jobDao().setState(messageId, JobStates.FAILED, job.attempts + 1, null, ErrorCodes.PROTOCOL, "内部错误", nowUtcString())
        }
    }

    private suspend fun uploadJob(
        msg: MessageEntity,
        files: List<FileEntity>,
        job: com.minidrop.app.data.db.UploadJobEntity,
    ) {
        val client = dav()

        val mk = client.ensureDirectory(RemotePaths.itemsMonthDir(msg.remoteMonth))
        if (!mk.ok) {
            classifyFailure(job, mk, "月份目录创建失败")
            return
        }

        var completed = job.bytesDone
        for (file in files) {
            if (file.state == FileStates.UPLOADED) continue

            if (file.sourcePath == null || !File(file.sourcePath).exists()) {
                failJob(job.messageId, job.attempts, ErrorCodes.SOURCE_MISSING, "原文件不存在或已移动")
                return
            }
            val f = File(file.sourcePath!!)
            if (f.length() != file.size || f.lastModified() != file.sourceModifiedAt) {
                failJob(job.messageId, job.attempts, ErrorCodes.SOURCE_CHANGED, "文件在发送后发生变化，请重新发送")
                return
            }

            db.fileDao().setState(file.fileId, FileStates.UPLOADING)
            val (put, sha) = client.putFile(RemotePaths.filePath(file.fileId), f) { pos ->
                // 进度节流由调用方节流；此处直接写库（Worker 中低频）
            }
            if (put.ok && sha != null) {
                db.fileDao().setUploaded(file.fileId, sha)
                completed += file.size
                db.jobDao().setProgress(job.messageId, completed, nowUtcString())
            } else {
                db.fileDao().setState(file.fileId, FileStates.PENDING)
                classifyFailure(job, put, "上传失败，请稍后重试")
                return
            }
        }

        // 发布前再核对源文件（前后核对，Android 只核 size 与 mtime）
        for (file in files) {
            val f = file.sourcePath?.let(::File)
            if (f == null || !f.exists() || f.length() != file.size || f.lastModified() != file.sourceModifiedAt) {
                failJob(job.messageId, job.attempts, ErrorCodes.SOURCE_CHANGED, "文件在发送后发生变化，请重新发送")
                return
            }
        }

        val draft = MessageJson.Draft(
            id = msg.id,
            deviceId = msg.deviceId,
            deviceName = msg.deviceName,
            createdAt = msg.createdAt,
            text = msg.text,
            files = files.map { MessageJson.DraftFile(it.fileId, it.name, it.size, it.mime, it.sha256) },
        )
        val putJson = client.put(RemotePaths.messagePath(msg.remoteMonth, msg.id), MessageJson.serialize(draft))
        if (putJson.ok) {
            db.jobDao().delete(job.messageId)
            return
        }
        classifyFailure(job, putJson, "上传失败，请稍后重试")
    }

    private suspend fun classifyFailure(job: com.minidrop.app.data.db.UploadJobEntity, result: com.minidrop.app.webdav.DavResult, detail: String) {
        val code = ErrorClassifier.toErrorCode(result)
        if (ErrorCodes.isTransient(code)) {
            toRetryWait(job.messageId, job.attempts, code, result.retryAfter, result.detail)
        } else {
            failJob(job.messageId, job.attempts, code, detail)
        }
    }

    private suspend fun toRetryWait(
        messageId: String,
        attempts: Int,
        code: String,
        retryAfterMs: Long?,
        detail: String?,
    ) {
        val nextAttempts = attempts + 1
        if (nextAttempts >= ErrorCodes.MAX_TRANSIENT_ATTEMPTS) {
            failJob(messageId, attempts, code, "连续失败达到上限")
            return
        }
        val local = ErrorCodes.BACKOFF[(attempts).coerceIn(0, ErrorCodes.BACKOFF.size - 1)]
        val delayMs = retryAfterMs?.takeIf { it > local } ?: local
        db.jobDao().setState(
            messageId, JobStates.RETRY_WAIT, nextAttempts,
            System.currentTimeMillis() + delayMs, code, detail, nowUtcString(),
        )
    }

    private suspend fun failJob(messageId: String, attempts: Int, code: String, detail: String) {
        db.jobDao().setState(messageId, JobStates.FAILED, attempts + 1, null, code, detail, nowUtcString())
    }
}

/** 删除整条消息（§7.1）：墓碑 commit → 本地删 → 文件 → 最后 item。 */
class DeleteService(
    private val db: MiniDropDatabase,
    private val dav: () -> WebDavClient,
    private val deviceId: suspend () -> String,
    private val coordinator: SyncCoordinator,
) {
    sealed class Result {
        object Ok : Result()
        data class Fail(val text: String) : Result()
    }

    suspend fun delete(messageId: String): Result {
        val msg = db.messageDao().getById(messageId) ?: return Result.Ok

        val mkTomb = dav().ensureDirectory(RemotePaths.tombstonesMonthDir(msg.remoteMonth))
        if (!mkTomb.ok) return Result.Fail("删除失败，请检查网络")

        val tombstone = MessageJson.serializeTombstone(msg.id, nowUtcString(), deviceId())
        val put = dav().put(RemotePaths.tombstonePath(msg.remoteMonth, msg.id), tombstone)
        if (!put.ok) return Result.Fail("删除失败，请检查网络")

        val fileRows = db.fileDao().getByMessage(messageId)
        coordinator.deleteLocal(messageId)

        var allFilesOk = true
        for (f in fileRows) {
            val del = dav().delete(RemotePaths.filePath(f.fileId))
            if (!del.ok) allFilesOk = false
        }
        if (allFilesOk) {
            dav().delete(RemotePaths.messagePath(msg.remoteMonth, msg.id))
        }
        return Result.Ok
    }
}

/** 文件下载与打开（§8）：用户动作触发；.part + SHA-256 校验 + 原子改名。 */
class DownloadService(
    private val context: Context,
    private val db: MiniDropDatabase,
    private val dav: () -> WebDavClient,
    private val downloadTreeUri: () -> String? = { null }, // SAF 下载目录；null = 应用私有目录
) {
    sealed class Result {
        data class Ok(val cachePath: String) : Result()
        data class Fail(val text: String?) : Result()
    }

    suspend fun download(fileId: String): Result {
        val file = db.fileDao().getById(fileId) ?: return Result.Fail("文件不存在")
        if (file.state == FileStates.CACHED && !file.cachePath.isNullOrEmpty()) {
            return Result.Ok(file.cachePath!!)
        }

        // SAF 下载目录模式：文件落在用户选择的目录，文件管理器可见
        val treeUri = downloadTreeUri()
            ?.takeIf { SafeDownloads.isPersisted(context, it) }
            ?.let { android.net.Uri.parse(it) }
        if (treeUri != null) {
            return downloadToSaf(fileId, file, treeUri)
        }
        return downloadToPrivate(fileId, file)
    }

    private suspend fun downloadToSaf(fileId: String, file: com.minidrop.app.data.db.FileEntity, treeUri: android.net.Uri): Result {
        db.fileDao().setState(fileId, FileStates.DOWNLOADING)
        var documentUri: android.net.Uri? = null
        return try {
            val target = SafeDownloads.ensureDocument(
                context, treeUri, file.name,
                file.mime?.takeIf { it != "application/octet-stream" }
                    ?: com.minidrop.app.core.MimeTypes.fromName(file.name)
                    ?: "application/octet-stream",
            ) ?: run {
                db.fileDao().setState(fileId, FileStates.FAILED)
                return Result.Fail("下载目录不可用，请到设置中重新选择")
            }
            documentUri = target

            val sink = context.contentResolver.openOutputStream(target, "w")
                ?: run {
                    db.fileDao().setState(fileId, FileStates.FAILED)
                    return Result.Fail("下载目录不可用，请到设置中重新选择")
                }

            val digest = java.security.MessageDigest.getInstance("SHA-256")
            var total = 0L
            val result = sink.use { out ->
                dav().getToStream(RemotePaths.filePath(fileId), out) { chunk ->
                    digest.update(chunk)
                    total += chunk.size
                }
            }
            if (!result.ok) {
                SafeDownloads.delete(context, target)
                db.fileDao().setState(fileId, FileStates.FAILED)
                return Result.Fail("下载失败，请重试")
            }
            val sha = digest.digest().joinToString("") { "%02x".format(it) }
            if (total != file.size || (file.sha256 != null && file.sha256 != sha)) {
                SafeDownloads.delete(context, target)
                db.fileDao().setState(fileId, FileStates.FAILED)
                return Result.Fail("文件校验失败，请重试")
            }
            db.fileDao().setCachePath(fileId, target.toString(), FileStates.CACHED)
            Result.Ok(target.toString())
        } catch (_: kotlinx.coroutines.CancellationException) {
            documentUri?.let { SafeDownloads.delete(context, it) }
            db.fileDao().setState(fileId, FileStates.REMOTE)
            Result.Fail(null)
        } catch (_: Exception) {
            documentUri?.let { SafeDownloads.delete(context, it) }
            db.fileDao().setState(fileId, FileStates.FAILED)
            Result.Fail("下载失败，请重试")
        }
    }

    private suspend fun downloadToPrivate(fileId: String, file: com.minidrop.app.data.db.FileEntity): Result {
        val dir = File(context.filesDir, "downloads").apply { mkdirs() }
        val part = File(dir, "$fileId.part")

        db.fileDao().setState(fileId, FileStates.DOWNLOADING)
        return try {
            val (result, sha) = dav().getToFile(RemotePaths.filePath(fileId), part) { }
            if (!result.ok) {
                part.delete()
                db.fileDao().setState(fileId, FileStates.FAILED)
                return Result.Fail("下载失败，请重试")
            }
            if (part.length() != file.size || (file.sha256 != null && sha != file.sha256)) {
                part.delete()
                db.fileDao().setState(fileId, FileStates.FAILED)
                return Result.Fail("文件校验失败，请重试")
            }
            val finalPath = File(dir, buildCacheName(fileId, file.name))
            if (!part.renameTo(finalPath)) {
                part.delete()
                db.fileDao().setState(fileId, FileStates.FAILED)
                return Result.Fail("下载失败，请重试")
            }
            db.fileDao().setCachePath(fileId, finalPath.absolutePath, FileStates.CACHED)
            Result.Ok(finalPath.absolutePath)
        } catch (_: kotlinx.coroutines.CancellationException) {
            part.delete()
            db.fileDao().setState(fileId, FileStates.REMOTE)
            Result.Fail(null)
        } catch (_: Exception) {
            part.delete()
            db.fileDao().setState(fileId, FileStates.FAILED)
            Result.Fail("下载失败，请重试")
        }
    }

    private fun isCachePresent(cachePath: String?): Boolean {
        if (cachePath == null) return false
        return if (cachePath.startsWith("content:")) {
            SafeDownloads.exists(context, android.net.Uri.parse(cachePath))
        } else {
            File(cachePath).exists()
        }
    }

    /** cached 但文件丢失 → 回到 remote。 */
    suspend fun ensureCacheConsistency(fileId: String) {
        val file = db.fileDao().getById(fileId) ?: return
        if (file.state == FileStates.CACHED && !isCachePresent(file.cachePath)) {
            db.fileDao().setState(fileId, FileStates.REMOTE)
        }
    }

    companion object {
        fun buildCacheName(fileId: String, displayName: String): String {
            val ext = displayName.substringAfterLast('.', "")
            val safe = if (ext.isNotEmpty() && ext.length <= 10 && ext.all { it.isLetterOrDigit() }) ".$ext" else ".bin"
            return "$fileId$safe"
        }
    }
}

/** 远端维护（§12）：设置页手动触发；只扫 90 天边界及更老月份；单次 ≤ 100 条。 */
class MaintenanceService(
    private val db: MiniDropDatabase,
    private val dav: () -> WebDavClient,
    private val maxFileBytes: () -> Long,
    private val coordinator: SyncCoordinator,
) {
    suspend fun maintain(): MaintainOutcome = coordinator.mutex.withLock {
        runCore()
    }

    /** 刷新完成后的受限维护（已在刷新互斥作用域内调用，不再取锁）。 */
    suspend fun maybeRunAfterRefresh() {
        val meta = db.metaDao().get(SyncCoordinator.K_LAST_MAINTENANCE)
        val last = meta?.let {
            try {
                java.time.Instant.parse(it).toEpochMilli()
            } catch (_: Exception) {
                null
            }
        } ?: 0L
        if (System.currentTimeMillis() - last < 7L * 24 * 3600 * 1000) return
        runCore()
    }

    private suspend fun runCore(): MaintainOutcome {
        val cutoffMonth = UlidClock.expiryMonth()
        var budget = 100
        var processed = 0
        var scanError = false
        var month = cutoffMonth

        for (i in 0 until 12) {
            if (budget <= 0) break
            val scan = try {
                coordinator.scanMonth(month)
            } catch (_: SyncException) {
                scanError = true
                break
            }
            val tombIds = scan.tombstones.map { it.id }.toHashSet()

            // 1) R∩T 收敛
            for (item in scan.items.filter { it.id in tombIds }) {
                if (budget <= 0) break
                if (coordinator.converge(month, item.id)) processed++
                budget--
            }

            // 2) R−T 过期项：GET item → 删文件 → 删 item；损坏 JSON 只删 item（可能孤儿）
            for (item in scan.items.filter { it.id !in tombIds }) {
                if (budget <= 0) break
                budget--
                val itemPath = RemotePaths.messagePath(month, item.id)
                val (res, content) = dav().getBytes(itemPath, Limits.MAX_JSON_BYTES)
                if (res.status == DavStatus.NOT_FOUND) {
                    dav().delete(RemotePaths.tombstonePath(month, item.id))
                    processed++
                    continue
                }
                if (!res.ok || content == null) continue
                val parsed = MessageJson.parse(content, item.id, maxFileBytes())
                if (parsed.ok && parsed.message != null) {
                    var allDeleted = true
                    for (f in parsed.message.files) {
                        if (!dav().delete(RemotePaths.filePath(f.id)).ok) allDeleted = false
                    }
                    if (allDeleted) {
                        dav().delete(itemPath)
                        processed++
                    }
                } else {
                    dav().delete(itemPath)
                    processed++
                }
            }

            month = UlidClock.previousMonth(month)
        }

        if (!scanError) {
            db.metaDao().upsert(
                MetaEntity(SyncCoordinator.K_LAST_MAINTENANCE, nowUtcString()),
            )
        }
        return MaintainOutcome(processed, scanError)
    }
}

/** 启动恢复与本地清理（§4.3 末、§12.1）：零网络。 */
class StartupRecovery(
    private val context: Context,
    private val db: MiniDropDatabase,
) {
    suspend fun recover(): Int {
        val jobs = db.jobDao().recoverUploading(nowUtcString())
        db.fileDao().setStateBulkAllUploading(FileStates.PENDING)
        return jobs
    }

    /** 本地清理：90 天生命周期（按 ULID 时间戳）、孤儿 .part（48h）。 */
    suspend fun localCleanup(): Int {
        val nowMs = System.currentTimeMillis()
        val cutoffMonth = UlidClock.expiryMonth(nowMs)
        val cutoffMs = nowMs - UlidClock.RETENTION_DAYS * 24L * 3600 * 1000
        var removed = 0

        for (id in db.messageDao().idsInMonthsUpTo(cutoffMonth)) {
            val ts = com.minidrop.app.core.Ulid.timestampMs(id)
            if (ts != null && ts < cutoffMs) {
                val files = db.fileDao().getByMessage(id)
                db.messageDao().delete(id) // 外键级联 files/jobs
                for (f in files) f.cachePath?.let { p -> try { File(p).delete() } catch (_: Exception) {} }
                removed++
            }
        }

        val staging = File(context.filesDir, "staging")
        if (staging.exists()) {
            val cutoff = nowMs - 48L * 3600 * 1000
            staging.listFiles()?.forEach { f ->
                if (f.lastModified() < cutoff) try { f.delete() } catch (_: Exception) {}
            }
        }
        return removed
    }
}
