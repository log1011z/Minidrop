package com.minidrop.app.ui

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.lifecycleScope
import com.minidrop.app.MiniDropApp
import com.minidrop.app.core.Limits
import com.minidrop.app.sync.StagedFile
import java.io.File
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * 分享接收（§10.3）：SEND / SEND_MULTIPLE。
 * content:// 可能只允许读取一次 → 先全部复制到 filesDir/staging/<uuid>，
 * 全部成功后原子入队；任一失败都不创建半条消息。复制期间显示进度与取消。
 */
class ShareReceiverActivity : ComponentActivity() {

    private var progress by mutableStateOf(0f)
    private var progressText by mutableStateOf("正在接收分享…")
    private var showCancelDialog by mutableStateOf(false)
    @Volatile
    private var cancelled = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val app = application as MiniDropApp

        val text = when (intent?.action) {
            Intent.ACTION_SEND -> intent.getStringExtra(Intent.EXTRA_TEXT)
            else -> null
        }
        val uris: List<Uri> = when (intent?.action) {
            Intent.ACTION_SEND_MULTIPLE -> intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM).orEmpty().filterNotNull()
            Intent.ACTION_SEND -> listOfNotNull(intent.getParcelableExtra(Intent.EXTRA_STREAM) as? Uri)
            else -> emptyList()
        }

        if (uris.isEmpty() && text.isNullOrBlank()) {
            finish()
            return
        }

        setContent {
            MaterialTheme {
                Surface {
                    Column(
                        Modifier
                            .fillMaxSize()
                            .padding(32.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.Center,
                    ) {
                        Text("加入 MiniDrop", style = MaterialTheme.typography.titleLarge)
                        Text(
                            progressText,
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(vertical = 16.dp),
                        )
                        LinearProgressIndicator(
                            progress = { progress },
                            modifier = Modifier.fillMaxWidth(),
                        )
                        TextButton(
                            onClick = { showCancelDialog = true },
                            modifier = Modifier.padding(top = 12.dp),
                        ) { Text("取消") }
                    }
                }
                if (showCancelDialog) {
                    AlertDialog(
                        onDismissRequest = { showCancelDialog = false },
                        title = { Text("取消分享？") },
                        text = { Text("将丢弃本次选择的文件，不加入 MiniDrop。") },
                        confirmButton = {
                            TextButton(onClick = {
                                cancelled = true
                                showCancelDialog = false
                                finish()
                            }) { Text("取消并退出") }
                        },
                        dismissButton = {
                            TextButton(onClick = { showCancelDialog = false }) { Text("继续") }
                        },
                    )
                }
            }
        }

        lifecycleScope.launch {
            val staged = ArrayList<StagedFile>()
            try {
                val stagingDir = File(app.filesDir, "staging").apply { mkdirs() }
                val maxBytes = app.settingsSnapshot.maxFileBytes

                for ((index, uri) in uris.withIndex()) {
                    if (cancelled) return@launch
                    progressText = "正在复制 ${index + 1}/${uris.size}…"
                    progress = index.toFloat() / uris.size

                    val displayName = queryDisplayName(uri) ?: "file-${index + 1}"
                    val size = querySize(uri)
                    if (size != null && size > maxBytes) {
                        toast("单个文件不能超过 ${maxBytes / (1024 * 1024)} MB")
                        cleanup(staged)
                        finish()
                        return@launch
                    }

                    val target = File(stagingDir, java.util.UUID.randomUUID().toString())
                    val copied = withContext(Dispatchers.IO) {
                        runCatching {
                            contentResolver.openInputStream(uri)?.use { input ->
                                target.outputStream().use { out ->
                                    val buf = ByteArray(64 * 1024)
                                    var total = 0L
                                    while (true) {
                                        if (cancelled) return@runCatching false
                                        val n = input.read(buf)
                                        if (n <= 0) break
                                        total += n
                                        if (total > maxBytes) {
                                            toast("单个文件不能超过 ${maxBytes / (1024 * 1024)} MB")
                                            return@runCatching false
                                        }
                                        out.write(buf, 0, n)
                                    }
                                }
                            } ?: return@runCatching false
                            true
                        }.getOrDefault(false)
                    }
                    if (!copied) {
                        target.delete()
                        if (!cancelled) toast("接收文件失败")
                        cleanup(staged)
                        finish()
                        return@launch
                    }
                    staged.add(StagedFile(target, displayName))
                }

                if (cancelled) return@launch
                progress = 1f
                progressText = "正在加入…"
                val result = app.send.enqueueFiles(staged, text)
                when (result) {
                    is com.minidrop.app.sync.SendResult.Ok -> toast(getString(com.minidrop.app.R.string.joined_minidrop))
                    is com.minidrop.app.sync.SendResult.Fail -> {
                        toast(result.text)
                        cleanup(staged)
                    }
                }
            } catch (_: Exception) {
                cleanup(staged)
                toast("接收文件失败")
            } finally {
                finish()
            }
        }
    }

    private fun cleanup(staged: List<StagedFile>) {
        staged.forEach { try { it.file.delete() } catch (_: Exception) {} }
    }

    private fun toast(text: String) {
        runOnUiThread { Toast.makeText(this, text, Toast.LENGTH_SHORT).show() }
    }

    private fun queryDisplayName(uri: Uri): String? = try {
        contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            val nameIdx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            if (cursor.moveToFirst() && nameIdx >= 0) cursor.getString(nameIdx) else null
        }
    } catch (_: Exception) {
        null
    }

    private fun querySize(uri: Uri): Long? = try {
        contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            val sizeIdx = cursor.getColumnIndex(OpenableColumns.SIZE)
            if (cursor.moveToFirst() && sizeIdx >= 0 && !cursor.isNull(sizeIdx)) cursor.getLong(sizeIdx) else null
        }
    } catch (_: Exception) {
        null
    }
}
