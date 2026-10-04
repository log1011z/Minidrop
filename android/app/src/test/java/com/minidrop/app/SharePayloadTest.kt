package com.minidrop.app

import android.app.Application
import android.content.ClipData
import android.content.Intent
import android.net.Uri
import android.text.SpannableString
import com.minidrop.app.ui.SharePayload
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [28])
class SharePayloadTest {
    private val first = Uri.parse("content://files/first.txt")
    private val second = Uri.parse("content://files/second.txt")

    @Test fun clipDataOnlyFileIsReceived() {
        val intent = Intent(Intent.ACTION_SEND).apply {
            clipData = ClipData.newRawUri("file", first)
        }
        assertEquals(listOf(first), SharePayload.from(intent).uris)
    }

    @Test fun multipleStreamsAndClipDataAreMergedWithoutDuplicates() {
        val intent = Intent(Intent.ACTION_SEND_MULTIPLE).apply {
            putParcelableArrayListExtra(Intent.EXTRA_STREAM, arrayListOf(first, second))
            clipData = ClipData.newRawUri("file", first).apply { addItem(ClipData.Item(second)) }
            putExtra(Intent.EXTRA_TEXT, "说明")
        }
        val payload = SharePayload.from(intent)
        assertEquals(listOf(first, second), payload.uris)
        assertEquals("说明", payload.text)
    }

    @Test fun styledTextIsAcceptedAsPlainText() {
        val intent = Intent(Intent.ACTION_SEND).putExtra(Intent.EXTRA_TEXT, SpannableString("纯文本分享"))
        assertEquals("纯文本分享", SharePayload.from(intent).text)
        assertTrue(SharePayload.from(intent).uris.isEmpty())
    }

    @Test fun singleStreamAndDataFallbackAreReceived() {
        assertEquals(listOf(first), SharePayload.from(
            Intent(Intent.ACTION_SEND).putExtra(Intent.EXTRA_STREAM, first)).uris)
        assertEquals(listOf(second), SharePayload.from(Intent(Intent.ACTION_SEND).setData(second)).uris)
    }

    @Test fun clipTextAndUnsupportedActions() {
        val intent = Intent(Intent.ACTION_SEND).apply { clipData = ClipData.newPlainText("text", "文字") }
        assertEquals("文字", SharePayload.from(intent).text)
        assertEquals(SharePayload(null, emptyList()), SharePayload.from(Intent(Intent.ACTION_VIEW).setData(first)))
    }
}
