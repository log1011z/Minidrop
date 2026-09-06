package com.minidrop.app.sync

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract

/**
 * SAF（存储访问框架）下载目录支持：用户在设置中选择任意目录（如 Download/MiniDrop），
 * 下载的文件落在该目录、文件管理器可见。
 *
 * 取舍：放在用户目录的文件视为"用户的下载"，删除消息时不随之删除
 * （与浏览器下载行为一致）；应用私有目录模式仍随消息删除。
 */
object SafeDownloads {

    fun isPersisted(context: Context, treeUri: String?): Boolean {
        if (treeUri.isNullOrBlank()) return false
        return try {
            val uri = Uri.parse(treeUri)
            context.contentResolver.persistedUriPermissions.any {
                it.uri == uri && it.isWritePermission
            }
        } catch (_: Exception) {
            false
        }
    }

    /** 每次下载都创建新文档；同名时由系统追加 " (1)"，绝不覆盖用户已有文件。失败返回 null。 */
    fun createDocument(context: Context, treeUri: Uri, displayName: String, mime: String): Uri? = try {
        val treeDoc = DocumentsContract.buildDocumentUriUsingTree(
            treeUri, DocumentsContract.getTreeDocumentId(treeUri),
        )
        DocumentsContract.createDocument(context.contentResolver, treeDoc, mime, displayName)
    } catch (_: Exception) {
        null
    }

    /** 重命名文档；提供方不支持时保留原名（内容已校验，不影响使用）。 */
    fun renameDocument(context: Context, uri: Uri, newName: String): Uri = try {
        DocumentsContract.renameDocument(context.contentResolver, uri, newName)
            ?: uri // 某些提供方返回 null 表示名称未变
    } catch (_: Exception) {
        uri
    }

    /** 清理崩溃残留的 *.minidrop-part 临时文档（超过 maxAgeMs 才删，避免误删进行中的下载）。 */
    fun cleanupStaleParts(context: Context, treeUri: Uri, maxAgeMs: Long = 48L * 3600 * 1000) {
        try {
            val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(
                treeUri, DocumentsContract.getTreeDocumentId(treeUri),
            )
            val cutoff = System.currentTimeMillis() - maxAgeMs
            val stale = mutableListOf<Uri>()
            context.contentResolver.query(
                childrenUri,
                arrayOf(
                    DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                    DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                    DocumentsContract.Document.COLUMN_LAST_MODIFIED,
                ),
                null, null, null,
            )?.use { cursor ->
                while (cursor.moveToNext()) {
                    val name = cursor.getString(1) ?: continue
                    if (!name.endsWith(".minidrop-part")) continue
                    val modified = if (cursor.isNull(2)) 0L else cursor.getLong(2)
                    if (modified in 1 until cutoff) {
                        stale.add(DocumentsContract.buildDocumentUriUsingTree(treeUri, cursor.getString(0)))
                    }
                }
            }
            stale.forEach { delete(context, it) }
        } catch (_: Exception) {
        }
    }

    fun exists(context: Context, documentUri: Uri): Boolean = try {
        context.contentResolver.getType(documentUri) != null
    } catch (_: Exception) {
        false
    }

    fun delete(context: Context, documentUri: Uri) {
        try {
            DocumentsContract.deleteDocument(context.contentResolver, documentUri)
        } catch (_: Exception) {
        }
    }

    /** 从 tree uri 提取可读目录名（如 "Download/MiniDrop"），仅用于设置页展示。 */
    fun treeDisplayName(treeUri: String): String? = try {
        val decoded = java.net.URLDecoder.decode(treeUri.substringAfter("tree/", ""), "UTF-8")
        decoded.substringAfter(':', "").ifEmpty { null }
    } catch (_: Exception) {
        null
    }
}
