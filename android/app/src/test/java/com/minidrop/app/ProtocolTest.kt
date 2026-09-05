package com.minidrop.app

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JSONArray
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test
import java.io.File

/** 定位仓库根目录的共享 fixtures/（与 C# 测试共用同一批文件）。 */
object FixtureLoader {
    fun fixturesDir(): File {
        var dir: File? = File(System.getProperty("user.dir") ?: ".")
        while (dir != null) {
            val candidate = File(dir, "fixtures")
            if (candidate.isDirectory) return candidate
            dir = dir.parentFile
        }
        throw IllegalStateException("未找到 fixtures 目录")
    }

    fun readText(vararg parts: String): String = File(fixturesDir(), parts.joinToString("/")).readText()

    fun readBytes(vararg parts: String): ByteArray = File(fixturesDir(), parts.joinToString("/")).readBytes()
}

class UlidTest {
    data class Case(val ulid: String, val timestampMs: Long, val month: String)

    private fun cases(): List<Case> {
        val root = Json.parseToJsonElement(FixtureLoader.readText("ulid_month_cases.json")).jsonObject
        return root["cases"]!!.jsonArray.map { c ->
            val obj = c.jsonObject
            Case(
                obj["ulid"]!!.jsonPrimitive.content,
                obj["timestamp_ms"]!!.jsonPrimitive.long,
                obj["month"]!!.jsonPrimitive.content,
            )
        }
    }

    @Test
    fun `month cases from fixtures are consistent`() {
        for (c in cases()) {
            assertTrue(c.ulid, com.minidrop.app.core.Ulid.isValid(c.ulid))
            assertEquals(c.timestampMs, com.minidrop.app.core.Ulid.timestampMs(c.ulid))
            assertEquals(c.month, com.minidrop.app.core.UlidClock.monthOf(c.ulid))
        }
    }

    @Test
    fun `new ulid is 26 char uppercase crockford`() {
        repeat(1000) {
            val id = com.minidrop.app.core.Ulid.newUlid()
            assertEquals(26, id.length)
            assertTrue(com.minidrop.app.core.Ulid.isValid(id))
        }
    }

    @Test
    fun `new ulid is monotonic within process`() {
        val seen = (0 until 5000).map { com.minidrop.app.core.Ulid.newUlid() }
        assertEquals(seen, seen.sorted())
    }

    @Test
    fun `illegal characters rejected`() {
        assertFalse(com.minidrop.app.core.Ulid.timestampMs("0IM1R44YMRABCDEFGHJKMNPQRS") != null)
        assertFalse(com.minidrop.app.core.Ulid.isValid("01m1r44ymrabcdefghjkmnpqrs"))
        assertFalse(com.minidrop.app.core.Ulid.isValid("01M1R44YMRABCDEFGHJKMNPQR"))
        assertNotNull(com.minidrop.app.core.Ulid.timestampMs("01m1r44ymrabcdefghjkmnpqrs".uppercase()))
    }

    @Test
    fun `previous month wraps year`() {
        assertEquals("2026-08", com.minidrop.app.core.UlidClock.previousMonth("2026-09"))
        assertEquals("2025-12", com.minidrop.app.core.UlidClock.previousMonth("2026-01"))
    }

    @Test
    fun `expiry uses ulid timestamp only`() {
        val nowMs = java.time.Instant.parse("2026-09-05T00:00:00Z").toEpochMilli()
        val old = "01HQVMZ0ZZ0000000000000000" // 2024-02-29
        val fresh = com.minidrop.app.core.Ulid.newUlidAt(nowMs)
        assertTrue(com.minidrop.app.core.UlidClock.isExpired(old, nowMs))
        assertFalse(com.minidrop.app.core.UlidClock.isExpired(fresh, nowMs))
    }
}

class RemotePathsTest {
    @Test
    fun `paths follow appendix a`() {
        val id = "01M1R44YNVABCDEFGHJKMNPQRS"
        assertEquals("items/2026-09/$id.json", com.minidrop.app.core.RemotePaths.messagePath("2026-09", id))
        assertEquals("tombstones/2026-09/$id.json", com.minidrop.app.core.RemotePaths.tombstonePath("2026-09", id))
        assertEquals("files/550e8400-e29b-41d4-a716-446655440000", com.minidrop.app.core.RemotePaths.filePath("550e8400-e29b-41d4-a716-446655440000"))
    }
}

/** 夹具驱动：Kotlin 与 C# 对同一批 fixtures 必须得出一致的接受/拒绝结论。 */
class MessageJsonFixtureTest {

    private data class Entry(val file: String, val filenameUlid: String, val reason: String?)

    private fun manifest(): Pair<List<Entry>, List<Entry>> {
        val root = Json.parseToJsonElement(FixtureLoader.readText("messages", "manifest.json")).jsonObject
        val valid = root["valid"]!!.jsonArray.map { v ->
            val obj = v.jsonObject
            Entry(obj["file"]!!.jsonPrimitive.content, obj["filename_ulid"]!!.jsonPrimitive.content, null)
        }
        val invalid = root["invalid"]!!.jsonArray.map { v ->
            val obj = v.jsonObject
            Entry(obj["file"]!!.jsonPrimitive.content, obj["filename_ulid"]!!.jsonPrimitive.content, obj["reason"]!!.jsonPrimitive.content)
        }
        return valid to invalid
    }

    @Test
    fun `valid fixtures are accepted`() {
        val (valid, _) = manifest()
        for (e in valid) {
            val payload = FixtureLoader.readBytes("messages", "valid", e.file)
            val result = com.minidrop.app.core.MessageJson.parse(payload, e.filenameUlid)
            assertTrue("期望接受 ${e.file}，实际拒绝：${result.reason}", result.ok)
            assertEquals(e.filenameUlid, result.message?.id)
        }
    }

    @Test
    fun `invalid fixtures are rejected with reason`() {
        val (_, invalid) = manifest()
        for (e in invalid) {
            val payload = FixtureLoader.readBytes("messages", "invalid", e.file)
            val result = com.minidrop.app.core.MessageJson.parse(payload, e.filenameUlid)
            assertFalse("期望拒绝 ${e.file}（${e.reason}），实际被接受", result.ok)
            assertEquals("${e.file}", e.reason, result.reason)
        }
    }

    @Test
    fun `oversize json rejected as too large`() {
        val big = ByteArray((2L * 1024 * 1024).toInt() + 1) { 'a'.code.toByte() }
        val result = com.minidrop.app.core.MessageJson.parse(big, com.minidrop.app.core.Ulid.newUlid())
        assertFalse(result.ok)
        assertEquals("TOO_LARGE", result.reason)
    }

    @Test
    fun `text over 100kib rejected`() {
        val id = com.minidrop.app.core.Ulid.newUlid()
        val draft = com.minidrop.app.core.MessageJson.Draft(
            id, "be7f3a2c-1b9d-4c2e-8f0a-3d5e6b7a9c1d", "Desktop",
            "2026-09-05T06:30:00.123Z", "字".repeat(100 * 1024), null,
        )
        val json = com.minidrop.app.core.MessageJson.serialize(draft)
        val result = com.minidrop.app.core.MessageJson.parse(json, id)
        assertFalse(result.ok)
        assertEquals("TEXT_TOO_LONG", result.reason)
    }

    @Test
    fun `serialize then parse round trips`() {
        val id = com.minidrop.app.core.Ulid.newUlid()
        val draft = com.minidrop.app.core.MessageJson.Draft(
            id, "be7f3a2c-1b9d-4c2e-8f0a-3d5e6b7a9c1d", "桌面设备",
            "2026-09-05T06:30:00.123Z", "往返测试",
            listOf(
                com.minidrop.app.core.MessageJson.DraftFile(
                    "550e8400-e29b-41d4-a716-446655440000", "文件 名.zip", 12345678,
                    "application/zip", "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
                ),
            ),
        )
        val json = com.minidrop.app.core.MessageJson.serialize(draft)
        val result = com.minidrop.app.core.MessageJson.parse(json, id)
        assertTrue(result.reason, result.ok)
        val m = result.message!!
        assertEquals("往返测试", m.text)
        assertEquals("桌面设备", m.deviceName)
        assertEquals(1, m.files.size)
        assertEquals("文件 名.zip", m.files[0].name)
        assertEquals(12345678L, m.files[0].size)
    }

    @Test
    fun `tombstone serialize parse`() {
        val id = com.minidrop.app.core.Ulid.newUlid()
        val json = com.minidrop.app.core.MessageJson.serializeTombstone(
            id, "2026-09-05T07:00:00.000Z", "be7f3a2c-1b9d-4c2e-8f0a-3d5e6b7a9c1d",
        )
        val tomb = com.minidrop.app.core.MessageJson.parseTombstone(json)
        assertNotNull(tomb)
        assertEquals(id, tomb!!.id)
        assertNull(com.minidrop.app.core.MessageJson.parseTombstone("{oops".toByteArray()))
    }
}
