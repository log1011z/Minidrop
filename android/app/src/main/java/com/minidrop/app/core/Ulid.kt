package com.minidrop.app.core

import java.security.SecureRandom
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/**
 * 26 字符大写 Crockford Base32 ULID（48bit 毫秒时间戳 + 80bit 随机数）。
 * [newUlid] 保证进程内单调不减（同毫秒随机段大端 +1）；[newUlidAt] 不做钳制（测试播种用）。
 */
object Ulid {
    const val LENGTH = 26
    const val ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    private const val TIMESTAMP_CHARS = 10

    private val lock = ReentrantLock()
    private var lastMs = -1L
    private val lastRandom = ByteArray(10)
    private val rng = SecureRandom()

    fun newUlid(): String = newUlid(System.currentTimeMillis())

    fun newUlid(timestampMs: Long): String = lock.withLock {
        var ts = timestampMs
        if (ts < lastMs) ts = lastMs
        if (ts == lastMs) {
            incrementRandom()
        } else {
            rng.nextBytes(lastRandom)
            lastMs = ts
        }
        encodeTimestamp(ts) + encodeRandom(lastRandom)
    }

    /** 不做单调钳制，不影响 newUlid 状态。 */
    fun newUlidAt(timestampMs: Long): String {
        val random = ByteArray(10)
        rng.nextBytes(random)
        return encodeTimestamp(timestampMs) + encodeRandom(random)
    }

    fun isValid(value: String?): Boolean {
        if (value.isNullOrEmpty() || value.length != LENGTH) return false
        return value.all { ALPHABET.indexOf(it) >= 0 }
    }

    /** 解码前 10 字符时间戳，忽略输入大小写。 */
    fun timestampMs(value: String): Long? {
        if (value.length < TIMESTAMP_CHARS) return null
        var acc = 0L
        for (i in 0 until TIMESTAMP_CHARS) {
            val idx = ALPHABET.indexOf(value[i].uppercaseChar())
            if (idx < 0) return null
            acc = (acc shl 5) or idx.toLong()
        }
        if ((acc ushr 48) != 0L) return null
        return acc
    }

    fun encodeTimestamp(timestampMs: Long): String {
        val chars = CharArray(TIMESTAMP_CHARS)
        var ts = timestampMs
        for (i in TIMESTAMP_CHARS - 1 downTo 0) {
            chars[i] = ALPHABET[(ts and 0x1F).toInt()]
            ts = ts shr 5
        }
        return String(chars)
    }

    private fun encodeRandom(random: ByteArray): String {
        val sb = StringBuilder(16)
        var buffer = 0L
        var bits = 0
        for (b in random) {
            buffer = (buffer shl 8) or (b.toLong() and 0xFF)
            bits += 8
            while (bits >= 5) {
                bits -= 5
                sb.append(ALPHABET[((buffer shr bits) and 0x1F).toInt()])
            }
        }
        return sb.toString(0, 16)
    }

    private fun incrementRandom() {
        for (i in lastRandom.indices.reversed()) {
            val v = (lastRandom[i].toInt() and 0xFF) + 1
            lastRandom[i] = v.toByte()
            if (v <= 0xFF) return
        }
        lastMs += 1
        rng.nextBytes(lastRandom)
    }
}

/** ULID 时间戳派生：UTC 月份与 90 天生命周期。 */
object UlidClock {
    const val RETENTION_DAYS = 90

    fun monthOf(ulid: String): String {
        val ms = Ulid.timestampMs(ulid) ?: throw IllegalArgumentException("非法 ULID: $ulid")
        return monthFromMs(ms)
    }

    fun monthFromMs(ms: Long): String {
        val cal = java.util.Calendar.getInstance(java.util.TimeZone.getTimeZone("UTC"))
        cal.timeInMillis = ms
        return "%04d-%02d".format(cal.get(java.util.Calendar.YEAR), cal.get(java.util.Calendar.MONTH) + 1)
    }

    fun currentUtcMonth(nowMs: Long = System.currentTimeMillis()): String = monthFromMs(nowMs)

    fun previousMonth(month: String): String {
        val (y, m) = month.split("-").map { it.toInt() }
        return if (m == 1) "%04d-12".format(y - 1) else "%04d-%02d".format(y, m - 1)
    }

    fun compareMonths(a: String, b: String): Int = a.compareTo(b)

    fun isExpired(ulid: String, nowMs: Long = System.currentTimeMillis()): Boolean {
        val ms = Ulid.timestampMs(ulid) ?: return false
        return nowMs - ms >= RETENTION_DAYS * 24L * 3600 * 1000
    }

    /** 90 天边界所在月份（该月及更早的 ULID 已到期）。 */
    fun expiryMonth(nowMs: Long = System.currentTimeMillis()): String =
        monthFromMs(nowMs - RETENTION_DAYS * 24L * 3600 * 1000)
}

/** 协议路径只由 ID 决定（附录 A）；根前缀由 WebDAV 层拼接。 */
object RemotePaths {
    fun messagePath(month: String, id: String) = "items/$month/$id.json"
    fun tombstonePath(month: String, id: String) = "tombstones/$month/$id.json"
    fun filePath(fileId: String) = "files/$fileId"
    fun itemsMonthDir(month: String) = "items/$month"
    fun tombstonesMonthDir(month: String) = "tombstones/$month"
    const val ITEMS_ROOT = "items"
    const val TOMBSTONES_ROOT = "tombstones"
    const val FILES_ROOT = "files"
}
