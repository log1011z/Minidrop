package com.minidrop.app.core

import android.webkit.MimeTypeMap

/** 按文件名扩展名推断 MIME；未知返回 null（调用方回退 octet-stream）。 */
object MimeTypes {
    fun fromName(name: String): String? {
        val ext = name.substringAfterLast('.', "").lowercase()
        if (ext.isEmpty()) return null
        return MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext)
    }
}

/** 下载字节数超过消息声明的文件大小时抛出（SAF 实时字节上限）。 */
class DownloadSizeExceeded : Exception("total > declared size")
