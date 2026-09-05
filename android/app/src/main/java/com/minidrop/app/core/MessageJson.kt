package com.minidrop.app.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import java.io.ByteArrayOutputStream
import java.util.UUID

/**
 * 消息 JSON Schema v1 严格解析与序列化（PROTOCOL §3）。
 * 与 C# MiniDrop.Domain.MessageJson 行为一致，由共享 fixtures 测试保证。
 */
object MessageJson {
    private val json = Json { ignoreUnknownKeys = true }
    private val createdAtRegex = Regex("""^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$""")
    private val uuidRegex = Regex("""^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$""")

    fun parse(payload: ByteArray, expectedId: String, maxFileBytes: Long = Limits.DEFAULT_MAX_FILE_BYTES): MessageParseResult {
        if (payload.size > Limits.MAX_JSON_BYTES) {
            return MessageParseResult(false, null, RejectReasons.TOO_LARGE)
        }
        val root = try {
            json.parseToJsonElement(payload.decodeToString()).jsonObject
        } catch (_: Exception) {
            return MessageParseResult(false, null, RejectReasons.BAD_JSON)
        }

        // version
        val versionEl = root["version"] as? JsonPrimitive
        val version = if (versionEl != null && !versionEl.isString) versionEl.longOrNull else null
        if (version == null || version != 1L) {
            return MessageParseResult(false, null, RejectReasons.SCHEMA_VERSION)
        }

        // id
        val id = (root["id"] as? JsonPrimitive)?.contentOrNull
            ?: return MessageParseResult(false, null, RejectReasons.BAD_ULID)
        if (!Ulid.isValid(id)) {
            return MessageParseResult(false, null, RejectReasons.BAD_ULID)
        }
        if (id != expectedId) {
            return MessageParseResult(false, null, RejectReasons.ID_MISMATCH)
        }

        // device_id
        val deviceId = (root["device_id"] as? JsonPrimitive)?.contentOrNull
        if (!isUuidV4(deviceId)) {
            return MessageParseResult(false, null, RejectReasons.BAD_DEVICE)
        }

        // device_name
        val deviceName = (root["device_name"] as? JsonPrimitive)?.contentOrNull?.trim()
        if (deviceName.isNullOrEmpty() || deviceName.length > 32) {
            return MessageParseResult(false, null, RejectReasons.BAD_DEVICE_NAME)
        }

        // created_at
        val createdAt = (root["created_at"] as? JsonPrimitive)?.contentOrNull
        if (createdAt == null || !createdAtRegex.matches(createdAt)) {
            return MessageParseResult(false, null, RejectReasons.BAD_CREATED_AT)
        }

        // text
        var text: String? = null
        when (val textEl = root["text"]) {
            null, is JsonNull -> {}
            is JsonPrimitive -> {
                if (!textEl.isString) {
                    return MessageParseResult(false, null, RejectReasons.BAD_JSON)
                }
                text = textEl.content
                if (text.toByteArray(Charsets.UTF_8).size > Limits.MAX_TEXT_BYTES) {
                    return MessageParseResult(false, null, RejectReasons.TEXT_TOO_LONG)
                }
            }
            else -> return MessageParseResult(false, null, RejectReasons.BAD_JSON)
        }

        // files
        var files: List<RemoteFile>? = null
        when (val filesEl = root["files"]) {
            null, is JsonNull -> {}
            is JsonArray -> {
                if (filesEl.size > Limits.MAX_FILES) {
                    return MessageParseResult(false, null, RejectReasons.TOO_MANY_FILES)
                }
                val seen = HashSet<String>()
                val list = ArrayList<RemoteFile>(filesEl.size)
                for (f in filesEl) {
                    val obj = f as? JsonObject
                        ?: return MessageParseResult(false, null, RejectReasons.BAD_JSON)

                    val fidEl = (obj["id"] as? JsonPrimitive)?.contentOrNull
                    if (fidEl == null || !isUuidV4(fidEl)) {
                        return MessageParseResult(false, null, RejectReasons.BAD_FILE_ID)
                    }
                    val fid = fidEl
                    if (!seen.add(fid)) {
                        return MessageParseResult(false, null, RejectReasons.DUP_FILE_ID)
                    }

                    val nameEl = (obj["name"] as? JsonPrimitive)?.contentOrNull
                    if (nameEl == null || !isValidFileName(nameEl)) {
                        return MessageParseResult(false, null, RejectReasons.BAD_FILE_NAME)
                    }
                    val name = nameEl

                    val sizeEl = obj["size"] as? JsonPrimitive
                    if (sizeEl == null || sizeEl.isString) {
                        return MessageParseResult(false, null, RejectReasons.BAD_JSON)
                    }
                    val size = sizeEl.longOrNull
                        ?: return MessageParseResult(false, null, RejectReasons.BAD_JSON)
                    if (size < 0 || size > maxFileBytes) {
                        return MessageParseResult(false, null, RejectReasons.BAD_FILE_SIZE)
                    }

                    var mime: String? = null
                    when (val mimeEl = obj["mime"]) {
                        null, is JsonNull -> {}
                        is JsonPrimitive -> {
                            if (!mimeEl.isString) {
                                return MessageParseResult(false, null, RejectReasons.BAD_JSON)
                            }
                            mime = mimeEl.content
                            if (!isValidMime(mime)) {
                                return MessageParseResult(false, null, RejectReasons.BAD_MIME)
                            }
                        }
                        else -> return MessageParseResult(false, null, RejectReasons.BAD_JSON)
                    }

                    var sha: String? = null
                    when (val shaEl = obj["sha256"]) {
                        null, is JsonNull -> {}
                        is JsonPrimitive -> {
                            if (!shaEl.isString) {
                                return MessageParseResult(false, null, RejectReasons.BAD_JSON)
                            }
                            sha = shaEl.content
                            if (!isValidSha256(sha)) {
                                return MessageParseResult(false, null, RejectReasons.BAD_SHA256)
                            }
                        }
                        else -> return MessageParseResult(false, null, RejectReasons.BAD_JSON)
                    }

                    list.add(RemoteFile(fid, name, size, mime, sha))
                }
                files = list
            }
            else -> return MessageParseResult(false, null, RejectReasons.BAD_JSON)
        }

        // 至少一段非空文字或至少一个文件
        val hasText = !text.isNullOrBlank()
        if (!hasText && (files.isNullOrEmpty())) {
            return MessageParseResult(false, null, RejectReasons.EMPTY_CONTENT)
        }

        return MessageParseResult(
            true,
            RemoteMessage(id, deviceId!!, deviceName, createdAt, text, files ?: emptyList()),
            null,
        )
    }

    data class DraftFile(val id: String, val name: String, val size: Long, val mime: String?, val sha256: String?)

    data class Draft(
        val id: String,
        val deviceId: String,
        val deviceName: String,
        val createdAt: String,
        val text: String?,
        val files: List<DraftFile>?,
    )

    fun serialize(draft: Draft): ByteArray {
        val root = buildMap<String, JsonElement> {
            put("version", JsonPrimitive(1))
            put("id", JsonPrimitive(draft.id))
            put("device_id", JsonPrimitive(draft.deviceId))
            put("device_name", JsonPrimitive(draft.deviceName))
            put("created_at", JsonPrimitive(draft.createdAt))
            put("text", draft.text?.let { JsonPrimitive(it) } ?: JsonNull)
            put(
                "files",
                if (draft.files.isNullOrEmpty()) {
                    JsonNull
                } else {
                    JsonArray(draft.files.map { f ->
                        buildMap<String, JsonElement> {
                            put("id", JsonPrimitive(f.id))
                            put("name", JsonPrimitive(f.name))
                            put("size", JsonPrimitive(f.size))
                            put("mime", f.mime?.let { JsonPrimitive(it) } ?: JsonNull)
                            put("sha256", f.sha256?.let { JsonPrimitive(it) } ?: JsonNull)
                        }.let { JsonObject(it) }
                    })
                },
            )
        }
        return JsonObject(root).toString().toByteArray(Charsets.UTF_8)
    }

    data class Tombstone(val id: String, val deletedAt: String, val deletedBy: String)

    fun serializeTombstone(id: String, deletedAt: String, deletedBy: String): ByteArray {
        val root = JsonObject(
            mapOf(
                "version" to JsonPrimitive(1),
                "id" to JsonPrimitive(id),
                "deleted_at" to JsonPrimitive(deletedAt),
                "deleted_by" to JsonPrimitive(deletedBy),
            ),
        )
        return root.toString().toByteArray(Charsets.UTF_8)
    }

    /** 墓碑正文只用于诊断；解析失败返回 null，不影响删除语义。 */
    fun parseTombstone(payload: ByteArray): Tombstone? {
        return try {
            val root = json.parseToJsonElement(payload.decodeToString()).jsonObject
            val version = (root["version"] as? JsonPrimitive)?.longOrNull ?: return null
            if (version != 1L) return null
            val id = (root["id"] as? JsonPrimitive)?.contentOrNull ?: return null
            if (!Ulid.isValid(id)) return null
            val at = (root["deleted_at"] as? JsonPrimitive)?.contentOrNull ?: return null
            val by = (root["deleted_by"] as? JsonPrimitive)?.contentOrNull ?: return null
            if (!isUuidV4(by)) return null
            Tombstone(id, at, by)
        } catch (_: Exception) {
            null
        }
    }

    // ---------- 校验工具 ----------

    fun isUuidV4(s: String?): Boolean {
        if (s == null || s.length != 36 || !uuidRegex.matches(s)) return false
        return try {
            val uuid = UUID.fromString(s)
            uuid.version() == 4 && uuid.variant() == 2
        } catch (_: IllegalArgumentException) {
            false
        }
    }

    fun isValidFileName(name: String): Boolean {
        if (name.toByteArray(Charsets.UTF_8).size > Limits.MAX_NAME_BYTES) return false
        return name.all { it != '/' && it != '\\' && it.code >= 0x20 && it.code != 0x7F }
    }

    fun isValidMime(mime: String): Boolean =
        mime.length <= Limits.MAX_MIME_CHARS && mime.all { it.code in 0x20..0x7E }

    fun isValidSha256(sha: String?): Boolean {
        if (sha == null || sha.length != 64) return false
        return sha.all { it in '0'..'9' || it in 'a'..'f' }
    }
}
