package com.minidrop.app.ui

internal data class MessageLink(val start: Int, val end: Int, val url: String)

internal fun messageLinks(text: String): List<MessageLink> =
    Regex("""(?i)(?<![a-z0-9_])(?:https?://|www\.)[^\s<>"“”‘’，。！？；、（）]+""").findAll(text).mapNotNull { match ->
        var value = match.value.trimEnd('.', ',', '!', '?', ';', ':')
        while (value.endsWith(')') && value.count { it == ')' } > value.count { it == '(' }) value = value.dropLast(1)
        while (value.endsWith(']') && value.count { it == ']' } > value.count { it == '[' }) value = value.dropLast(1)
        val url = if (value.startsWith("www.", ignoreCase = true)) "https://$value" else value
        val valid = runCatching { java.net.URI(url).let { !it.rawAuthority.isNullOrBlank() } }.getOrDefault(false)
        if (valid) MessageLink(match.range.first, match.range.first + value.length, url) else null
    }.toList()
