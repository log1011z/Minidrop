package com.minidrop.app.ui

import android.os.Bundle
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
import com.minidrop.app.sync.Staging
import com.minidrop.app.sync.SendResult
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.withContext
import kotlinx.coroutines.launch

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

        val payload = SharePayload.from(intent)
        val text = payload.text
        val uris = payload.uris

        if (uris.isEmpty() && text.isNullOrBlank()) {
            toast("分享内容为空或无法读取")
            finish()
            return
        }
        if (uris.size > Limits.MAX_FILES) {
            toast("一次最多 ${Limits.MAX_FILES} 个文件")
            finish()
            return
        }

        setContent {
            MiniDropTheme {
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
            var staged = emptyList<com.minidrop.app.sync.StagedFile>()
            var enqueued = false
            try {
                app.awaitReady()
                if (uris.isNotEmpty()) {
                    when (val copied = Staging.copyAll(app, uris, app.settingsSnapshot.maxFileBytes,
                        isCancelled = { cancelled },
                    ) { index, total ->
                        progressText = "正在复制 ${index + 1}/$total…"
                        progress = index.toFloat() / total
                    }) {
                        is Staging.CopyResult.Ok -> staged = copied.files
                        is Staging.CopyResult.TooLarge -> { toast(copied.text); return@launch }
                        is Staging.CopyResult.Failed -> { toast(copied.text); return@launch }
                    }
                }

                if (cancelled) return@launch
                progress = 1f
                progressText = "正在加入…"
                // Once committing starts, cancellation must not delete files owned by a queued job.
                val result = withContext(NonCancellable) {
                    val send = if (staged.isEmpty()) app.send.enqueueText(text.orEmpty())
                        else app.send.enqueueFiles(staged, text)
                    enqueued = send is SendResult.Ok
                    send
                }
                when (result) {
                    is SendResult.Ok -> {
                        toast(getString(com.minidrop.app.R.string.joined_minidrop))
                    }
                    is SendResult.Fail -> toast(result.text)
                }
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                toast("接收文件失败")
            } finally {
                if (!enqueued) Staging.cleanup(staged)
                finish()
            }
        }
    }

    private fun toast(text: String) {
        runOnUiThread { Toast.makeText(this, text, Toast.LENGTH_SHORT).show() }
    }

}
