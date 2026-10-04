package com.minidrop.app.ui

import android.content.Intent
import android.net.Uri
import androidx.core.content.IntentCompat

/** System file managers use EXTRA_STREAM, ClipData, or (occasionally) data. */
data class SharePayload(val text: String?, val uris: List<Uri>) {
    companion object {
        fun from(intent: Intent): SharePayload {
            if (intent.action != Intent.ACTION_SEND && intent.action != Intent.ACTION_SEND_MULTIPLE) {
                return SharePayload(null, emptyList())
            }
            val uris = linkedSetOf<Uri>()
            if (intent.action == Intent.ACTION_SEND_MULTIPLE) {
                IntentCompat.getParcelableArrayListExtra(intent, Intent.EXTRA_STREAM, Uri::class.java)
                    ?.let { uris.addAll(it) }
            } else {
                IntentCompat.getParcelableExtra(intent, Intent.EXTRA_STREAM, Uri::class.java)?.let(uris::add)
            }
            val clip = intent.clipData
            if (clip != null) {
                for (index in 0 until clip.itemCount) clip.getItemAt(index).uri?.let(uris::add)
            }
            if (uris.isEmpty()) intent.data?.let(uris::add)
            val text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()
                ?: if (uris.isEmpty() && clip != null) {
                    (0 until clip.itemCount).mapNotNull { clip.getItemAt(it).text?.toString() }
                        .joinToString("\n").takeIf { it.isNotBlank() }
                } else null
            return SharePayload(text, uris.toList())
        }
    }
}
