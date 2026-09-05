package com.minidrop.app.core

/** 传输/任务错误码（PROTOCOL §4.1）。 */
object ErrorCodes {
    const val NETWORK = "NETWORK"
    const val RATE_LIMIT = "RATE_LIMIT"
    const val SERVER = "SERVER"
    const val AUTH = "AUTH"
    const val SOURCE_MISSING = "SOURCE_MISSING"
    const val SOURCE_CHANGED = "SOURCE_CHANGED"
    const val FILE_TOO_LARGE = "FILE_TOO_LARGE"
    const val QUOTA = "QUOTA"
    const val PROTOCOL = "PROTOCOL"

    fun isTransient(code: String) = code == NETWORK || code == RATE_LIMIT || code == SERVER

    /** 暂时错误退避序列：10s、30s、2m、5m、15m；第 5 次失败转 failed。 */
    val BACKOFF = longArrayOf(
        10_000L, 30_000L, 2 * 60_000L, 5 * 60_000L, 15 * 60_000L,
    )
    const val MAX_TRANSIENT_ATTEMPTS = 5
}

/** 远端 JSON 校验拒绝码（PROTOCOL §4.2）。 */
object RejectReasons {
    const val BAD_JSON = "BAD_JSON"
    const val TOO_LARGE = "TOO_LARGE"
    const val SCHEMA_VERSION = "SCHEMA_VERSION"
    const val BAD_ULID = "BAD_ULID"
    const val ID_MISMATCH = "ID_MISMATCH"
    const val BAD_DEVICE = "BAD_DEVICE"
    const val BAD_DEVICE_NAME = "BAD_DEVICE_NAME"
    const val BAD_CREATED_AT = "BAD_CREATED_AT"
    const val TEXT_TOO_LONG = "TEXT_TOO_LONG"
    const val TOO_MANY_FILES = "TOO_MANY_FILES"
    const val BAD_FILE_ID = "BAD_FILE_ID"
    const val DUP_FILE_ID = "DUP_FILE_ID"
    const val BAD_FILE_NAME = "BAD_FILE_NAME"
    const val BAD_FILE_SIZE = "BAD_FILE_SIZE"
    const val BAD_MIME = "BAD_MIME"
    const val BAD_SHA256 = "BAD_SHA256"
    const val EMPTY_CONTENT = "EMPTY_CONTENT"
}

object Limits {
    const val MAX_JSON_BYTES = 2L * 1024 * 1024
    const val MAX_TEXT_BYTES = 100 * 1024
    const val MAX_FILES = 50
    const val MAX_NAME_BYTES = 200
    const val MAX_MIME_CHARS = 127
    const val DEFAULT_MAX_FILE_BYTES = 500L * 1024 * 1024
}

data class RemoteFile(
    val id: String,
    val name: String,
    val size: Long,
    val mime: String?,
    val sha256: String?,
)

data class RemoteMessage(
    val id: String,
    val deviceId: String,
    val deviceName: String,
    val createdAt: String,
    val text: String?,
    val files: List<RemoteFile>,
)

data class MessageParseResult(val ok: Boolean, val message: RemoteMessage?, val reason: String?)

object JobStates {
    const val QUEUED = "queued"
    const val UPLOADING = "uploading"
    const val RETRY_WAIT = "retry_wait"
    const val FAILED = "failed"
}

object FileStates {
    const val PENDING = "pending"
    const val UPLOADING = "uploading"
    const val UPLOADED = "uploaded"
    const val REMOTE = "remote"
    const val DOWNLOADING = "downloading"
    const val CACHED = "cached"
    const val FAILED = "failed"
}
