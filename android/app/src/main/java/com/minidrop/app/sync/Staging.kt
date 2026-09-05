package com.minidrop.app.sync

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
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
        onProgress: suspend (index: Int, total: Int) -> Unit,
    ): CopyResult {
        val staged = ArrayList<StagedFile>(uris.size)
        val stagingDir = File(context.filesDir, "staging").apply { mkdirs() }

        for ((index, uri) in uris.withIndex()) {
            onProgress(index, uris.size)
            val displayName = queryDisplayName(context, uri) ?: "file-${index + 1}"
            val size = querySize(context, uri)
            if (size != null && size > maxBytes) {
                cleanup(staged)
                return CopyResult.TooLarge("单个文件不能超过 ${maxBytes / (1024 * 1024)} MB")
            }

            val target = File(stagingDir, java.util.UUID.randomUUID().toString())
            val copied = try {
                context.contentResolver.openInputStream(uri)?.use { input ->
                    target.outputStream().use { out ->
                        val buf = ByteArray(64 * 1024)
                        var total = 0L
                        while (true) {
                            val n = input.read(buf)
                            if (n <= 0) break
                            total += n
                            if (total > maxBytes) {
                                target.delete()
                                cleanup(staged)
                                return CopyResult.TooLarge("单个文件不能超过 ${maxBytes / (1024 * 1024)} MB")
                            }
                            out.write(buf, 0, n)
                        }
                    }
                    true
                } ?: false
            } catch (_: Exception) {
                false
            }
            if (!copied) {
                target.delete()
                cleanup(staged)
                return CopyResult.Failed("接收文件失败")
            }
            staged.add(StagedFile(target, displayName))
        }
        return CopyResult.Ok(staged)
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
