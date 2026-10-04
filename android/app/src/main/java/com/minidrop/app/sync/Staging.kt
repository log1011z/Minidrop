package com.minidrop.app.sync

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import com.minidrop.app.core.Limits
import com.minidrop.app.core.MessageJson
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import java.io.File

/** 分享/文件选择共用的 staging 复制（§5.5：content:// 只能读一次，先复制再入队）。 */
object Staging {

    sealed class CopyResult {
        data class Ok(val files: List<StagedFile>) : CopyResult()
        data class TooLarge(val text: String) : CopyResult()
        data class Failed(val text: String) : CopyResult()
    }

    /**
     * 把选中的 content:// 全部复制到 filesDir/staging/<uuid>；
     * 任一失败/超限即清除已复制文件并返回失败，绝不产生半条消息。
     */
    suspend fun copyAll(
        context: Context,
        uris: List<Uri>,
        maxBytes: Long,
        isCancelled: () -> Boolean = { false },
        onProgress: suspend (index: Int, total: Int) -> Unit,
    ): CopyResult {
        if (uris.size > Limits.MAX_FILES) return CopyResult.Failed("一次最多 ${Limits.MAX_FILES} 个文件")
        val staged = ArrayList<StagedFile>(uris.size)
        var completed = false
        var target: File? = null
        try {
            val result = withContext(Dispatchers.IO) {
                val stagingDir = File(context.filesDir, "staging").apply { mkdirs() }
                for ((index, uri) in uris.withIndex()) {
                    currentCoroutineContext().ensureActive()
                    if (isCancelled()) throw CancellationException("Share cancelled")
                    withContext(Dispatchers.Main) { onProgress(index, uris.size) }
                    val displayName = queryDisplayName(context, uri)?.takeIf { it.isNotBlank() }
                        ?: uri.lastPathSegment?.substringAfterLast('/')?.takeIf { it.isNotBlank() }
                        ?: "file-${index + 1}"
                    if (!MessageJson.isValidFileName(displayName)) {
                        return@withContext CopyResult.Failed("文件名不支持，请改名后重试")
                    }
                    val size = querySize(context, uri)
                    if (size != null && size > maxBytes) {
                        return@withContext CopyResult.TooLarge("单个文件不能超过 ${maxBytes / (1024 * 1024)} MB")
                    }
                    val output = File(stagingDir, java.util.UUID.randomUUID().toString())
                    target = output
                    val input = context.contentResolver.openInputStream(uri)
                        ?: return@withContext CopyResult.Failed("接收文件失败")
                    input.use {
                        output.outputStream().use { out ->
                            val buf = ByteArray(64 * 1024)
                            var total = 0L
                            while (true) {
                                currentCoroutineContext().ensureActive()
                                if (isCancelled()) throw CancellationException("Share cancelled")
                                val n = input.read(buf)
                                if (n < 0) break
                                if (n == 0) continue
                                total += n
                                if (total > maxBytes) {
                                    return@withContext CopyResult.TooLarge("单个文件不能超过 ${maxBytes / (1024 * 1024)} MB")
                                }
                                out.write(buf, 0, n)
                            }
                        }
                    }
                    staged.add(StagedFile(output, displayName))
                    target = null
                }
                CopyResult.Ok(staged)
            }
            completed = result is CopyResult.Ok
            return result
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            return CopyResult.Failed("接收文件失败，请确认来源应用允许读取文件")
        } finally {
            if (!completed) {
                target?.delete()
                cleanup(staged)
            }
        }
    }

    fun cleanup(staged: List<StagedFile>) {
        staged.forEach { try { it.file.delete() } catch (_: Exception) {} }
    }

    fun queryDisplayName(context: Context, uri: Uri): String? = try {
        context.contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            val idx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            if (cursor.moveToFirst() && idx >= 0) cursor.getString(idx) else null
        }
    } catch (_: Exception) {
        null
    }

    fun querySize(context: Context, uri: Uri): Long? = try {
        context.contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            val idx = cursor.getColumnIndex(OpenableColumns.SIZE)
            if (cursor.moveToFirst() && idx >= 0 && !cursor.isNull(idx)) cursor.getLong(idx) else null
        }
    } catch (_: Exception) {
        null
    }
}
