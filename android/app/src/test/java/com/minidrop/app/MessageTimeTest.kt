package com.minidrop.app

import com.minidrop.app.ui.formatMessageTime
import org.junit.Assert.assertEquals
import org.junit.Test
import java.time.ZoneId
import java.util.TimeZone

class MessageTimeTest {
    @Test fun utcTimestampDisplaysInShanghaiTime() {
        assertEquals("20:34", formatMessageTime("2026-10-03T12:34:56.789Z", ZoneId.of("Asia/Shanghai")))
    }

    @Test fun localTimeWrapsAcrossMidnight() {
        assertEquals("00:05", formatMessageTime("2026-10-03T16:05:00.000Z", ZoneId.of("Asia/Shanghai")))
        assertEquals("23:05", formatMessageTime("2026-10-03T06:05:00.000Z", ZoneId.of("America/Los_Angeles")))
    }

    @Test fun daylightSavingUsesTheOffsetAtMessageTime() {
        val zone = ZoneId.of("America/New_York")
        assertEquals("01:59", formatMessageTime("2026-03-08T06:59:00.000Z", zone))
        assertEquals("03:00", formatMessageTime("2026-03-08T07:00:00.000Z", zone))
    }

    @Test fun defaultTimeZoneIsReadFromTheDeviceOnEachCall() {
        val original = TimeZone.getDefault()
        try {
            TimeZone.setDefault(TimeZone.getTimeZone("Asia/Shanghai"))
            assertEquals("20:34", formatMessageTime("2026-10-03T12:34:56.789Z"))
            TimeZone.setDefault(TimeZone.getTimeZone("UTC"))
            assertEquals("12:34", formatMessageTime("2026-10-03T12:34:56.789Z"))
        } finally {
            TimeZone.setDefault(original)
        }
    }

    @Test fun explicitOffsetIsNotAddedTwice() {
        assertEquals("20:34", formatMessageTime("2026-10-03T20:34:56.789+08:00", ZoneId.of("Asia/Shanghai")))
    }

    @Test fun invalidTimestampUsesPlaceholderWithoutCrashing() {
        for (value in listOf("", "bad", "2026-10-03", "2026-10-03T12:34:56")) {
            assertEquals("—", formatMessageTime(value, ZoneId.of("Asia/Shanghai")))
        }
    }
}
