package com.minidrop.app

import com.minidrop.app.ui.messageLinks
import org.junit.Assert.*
import org.junit.Test

class MessageLinksTest {
    @Test fun recognizesWebLinksWithoutSurroundingPunctuation() {
        val text = "中文https://example.com/a?q=1&b=2，另见 (https://example.com/wiki/A_(B)). www.example.org!"
        val links = messageLinks(text)
        assertEquals(listOf("https://example.com/a?q=1&b=2", "https://example.com/wiki/A_(B)", "https://www.example.org"), links.map { it.url })
        assertEquals("www.example.org", text.substring(links.last().start, links.last().end))
    }

    @Test fun plainTextAndIncompleteLinksStayUnchanged() {
        assertTrue(messageLinks("普通文字 https:// 以及 file:///tmp/a").isEmpty())
        val text = "第一行\nhttps://example.com\n最后一行"
        val link = messageLinks(text).single()
        assertEquals("https://example.com", text.substring(link.start, link.end))
    }
}
