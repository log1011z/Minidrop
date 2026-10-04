package com.minidrop.app.ui

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.DateTimeParseException
import java.util.Locale

private val messageTimeFormatter = DateTimeFormatter.ofPattern("HH:mm", Locale.ROOT)

/** Stored timestamps stay in UTC; message cards show the device's local time. */
internal fun formatMessageTime(createdAt: String, zone: ZoneId = ZoneId.systemDefault()): String =
    try {
        messageTimeFormatter.format(Instant.parse(createdAt).atZone(zone))
    } catch (_: DateTimeParseException) {
        "—"
    }
