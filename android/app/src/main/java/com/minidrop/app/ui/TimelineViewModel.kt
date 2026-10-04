package com.minidrop.app.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.minidrop.app.MiniDropApp
import com.minidrop.app.core.FileStates
import com.minidrop.app.data.db.FileEntity
import com.minidrop.app.data.db.TimelineRow
import com.minidrop.app.sync.StagedFile
import com.minidrop.app.sync.Staging
import com.minidrop.app.sync.UploadPumpWorker
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.withContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import java.io.File

data class TimelineUiState(
    val timeline: List<TimelineRow> = emptyList(),
    val filesByMessage: Map<String, List<FileEntity>> = emptyMap(),
    val input: String = "",
    val busy: Boolean = false,
    val sending: Boolean = false,
    val attachments: List<StagedFile> = emptyList(),
    val statusText: String = "",
    val configured: Boolean = false,
    val canLoadOlder: Boolean = true,
)

class TimelineViewModel(app: Application) : AndroidViewModel(app) {

    private val ctx get() = getApplication<MiniDropApp>()

    private val input = MutableStateFlow("")
    private val busy = MutableStateFlow(false)
    private val sending = MutableStateFlow(false)
    private val attachments = MutableStateFlow<List<StagedFile>>(emptyList())
    private var cleared = false
    private val status = MutableStateFlow("")
    private val configured = MutableStateFlow(false)
    private val canLoadOlder = MutableStateFlow(true)

    private val timelineFlow = ctx.db.messageDao().timelineFlow(500)
    private val filesFlow = ctx.db.fileDao().allFiles()

    // 配置状态实时订阅：设置页保存后回到时间线立即生效
    private val configuredFlow = ctx.settings.settings.map { it.isConfigured }

    val uiState: StateFlow<TimelineUiState> = combine(
        timelineFlow, filesFlow, combine(input, sending, attachments) { text, active, files -> Triple(text, active, files) },
        busy, combine(status, configuredFlow, canLoadOlder) { s, c, l -> Triple(s, c, l) },
    ) { timeline, files, (inputText, isSending, draftFiles), isBusy, (statusText, isConfigured, loadOlder) ->
        TimelineUiState(
            timeline = timeline,
            filesByMessage = files.groupBy { it.messageId },
            input = inputText,
            busy = isBusy,
            sending = isSending,
            attachments = draftFiles,
            statusText = statusText,
            configured = isConfigured,
            canLoadOlder = loadOlder,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), TimelineUiState())


    fun onInputChange(value: String) {
        input.value = value
    }

    fun sendInput() {
        if (sending.value) return
        val text = input.value
        val submitted = attachments.value
        if (text.isBlank() && submitted.isEmpty()) return
        sending.value = true
        viewModelScope.launch {
            try {
                // Once committing starts, the queued job owns its files even if the screen closes.
                withContext(NonCancellable) {
                    val result = if (submitted.isEmpty()) ctx.send.enqueueText(text)
                        else ctx.send.enqueueFiles(submitted, text)
                    when (result) {
                        is com.minidrop.app.sync.SendResult.Ok -> {
                            if (input.value == text) input.value = ""
                            attachments.value = attachments.value - submitted.toSet()
                            status.value = "已加入 MiniDrop"
                        }
                        is com.minidrop.app.sync.SendResult.Fail -> status.value = result.text
                    }
                }
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (_: Exception) {
                status.value = "发送失败，请重试"
            } finally {
                sending.value = false
                if (cleared) clearAttachments()
            }
        }
    }

    fun refresh() {
        if (busy.value) return
        busy.value = true
        viewModelScope.launch {
            status.value = "正在刷新…"
            try {
                val outcome = ctx.sync.refresh()
                status.value = when {
                    outcome.scanError -> "刷新失败，请检查网络"
                    outcome.failed > 0 -> "已获取 ${outcome.added} 条，${outcome.failed} 条暂时无法读取"
                    outcome.added > 0 -> "已刷新，新增 ${outcome.added} 条"
                    else -> "已是最新"
                }
            } finally {
                busy.value = false
            }
        }
    }

    fun loadOlder() {
        if (busy.value) return
        busy.value = true
        viewModelScope.launch {
            status.value = "正在加载更早…"
            try {
                val outcome = ctx.sync.loadOlder()
                status.value = when {
                    outcome.scanError -> "加载失败，请检查网络"
                    outcome.added > 0 -> "已加载 ${outcome.added} 条"
                    outcome.noMore -> "没有更早记录"
                    else -> "已是本地最早"
                }
            } finally {
                busy.value = false
            }
        }
    }

    /** Copy selected files into the composer; sending commits text and attachments together. */
    fun onFilesPicked(uris: List<android.net.Uri>) {
        if (uris.isEmpty() || sending.value) return
        sending.value = true
        viewModelScope.launch {
            status.value = "正在接收所选文件…"
            try {
                val app = getApplication<MiniDropApp>()
                app.awaitReady()
                when (val result = com.minidrop.app.sync.Staging.copyAll(
                    app, uris, app.settingsSnapshot.maxFileBytes,
                ) { index, total -> status.value = "正在复制 ${index + 1}/$total…" }) {
                    is com.minidrop.app.sync.Staging.CopyResult.TooLarge -> status.value = result.text
                    is com.minidrop.app.sync.Staging.CopyResult.Failed -> status.value = result.text
                    is com.minidrop.app.sync.Staging.CopyResult.Ok -> {
                        attachments.value = attachments.value + result.files
                        status.value = "已选择 ${attachments.value.size} 个附件，可补充文字后发送"
                    }
                }
            } finally {
                sending.value = false
                if (cleared) clearAttachments()
            }
        }
    }

    fun removeAttachment(file: StagedFile) {
        if (sending.value) return
        attachments.value = attachments.value - file
        Staging.cleanup(listOf(file))
    }

    private fun clearAttachments() {
        Staging.cleanup(attachments.value)
        attachments.value = emptyList()
    }

    override fun onCleared() {
        cleared = true
        if (!sending.value) clearAttachments()
        super.onCleared()
    }

    fun retryUpload(messageId: String) {
        viewModelScope.launch {
            if (ctx.db.jobDao().retryFromTimeline(messageId, com.minidrop.app.data.db.nowUtcString()) > 0) {
                UploadPumpWorker.enqueueNow(ctx)
                status.value = "已重新排队"
            }
        }
    }

    fun delete(messageId: String) {
        viewModelScope.launch {
            when (val result = ctx.delete.delete(messageId)) {
                is com.minidrop.app.sync.DeleteService.Result.Ok -> status.value = "已删除"
                is com.minidrop.app.sync.DeleteService.Result.Fail -> status.value = result.text
            }
        }
    }

    fun downloadFile(fileId: String) = useFile(fileId, share = false)

    fun openFile(fileId: String) = useFile(fileId, share = false)

    fun shareFile(fileId: String) = useFile(fileId, share = true)

    private val activeFileActions = mutableSetOf<String>()
    private val preview = MutableStateFlow<ImagePreviewTarget?>(null)
    val imagePreview: StateFlow<ImagePreviewTarget?> = preview
    fun dismissImagePreview() { preview.value = null }

    private fun useFile(fileId: String, share: Boolean) {
        if (!activeFileActions.add(fileId)) return
        viewModelScope.launch {
            try {
                ctx.download.ensureCacheConsistency(fileId)
                val existing = ctx.db.fileDao().getById(fileId) ?: return@launch
                if (!share && isPreviewImage(existing.name, existing.mime) && existing.sourcePath?.let { File(it).exists() } == true) {
                    preview.value = ImagePreviewTarget(existing.sourcePath!!, existing.name)
                    return@launch
                }
                if (existing.state != FileStates.CACHED) {
                    when (val result = ctx.download.download(fileId)) {
                        is com.minidrop.app.sync.DownloadService.Result.Ok -> status.value = "下载完成"
                        is com.minidrop.app.sync.DownloadService.Result.Fail -> {
                            result.text?.let { status.value = it }
                            return@launch
                        }
                    }
                }
                val file = ctx.db.fileDao().getById(fileId) ?: return@launch
                val path = file.cachePath ?: return@launch
                if (!share && isPreviewImage(file.name, file.mime)) {
                    preview.value = ImagePreviewTarget(path, file.name)
                    return@launch
                }
                // 旧消息可能没有 mime：优先扩展名推断，避免用 octet-stream 打不开
                val mime = file.mime?.takeIf { it.isNotBlank() && it != "application/octet-stream" }
                    ?: com.minidrop.app.core.MimeTypes.fromName(file.name)
                    ?: "application/octet-stream"
                try {
                    // SAF 目录下载的文件直接用 content uri；私有目录走 FileProvider
                    val uri = if (path.startsWith("content:")) {
                        android.net.Uri.parse(path)
                    } else {
                        androidx.core.content.FileProvider.getUriForFile(
                            getApplication<MiniDropApp>(), "com.minidrop.app.fileprovider", File(path),
                        )
                    }
                    val intent = if (share) {
                        val send = android.content.Intent(android.content.Intent.ACTION_SEND)
                            .setType(mime)
                            .putExtra(android.content.Intent.EXTRA_STREAM, uri)
                            .apply { clipData = android.content.ClipData.newRawUri(file.name, uri) }
                            .addFlags(android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION)
                        android.content.Intent.createChooser(send, "分享文件")
                            .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK or android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION)
                    } else {
                        android.content.Intent(android.content.Intent.ACTION_VIEW)
                            .setDataAndType(uri, mime)
                            .addFlags(android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION or android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
                    }
                    getApplication<MiniDropApp>().startActivity(intent)
                } catch (_: Exception) {
                    status.value = if (share) "无法分享该文件" else "没有关联应用可以打开该文件"
                }
            } finally {
                activeFileActions.remove(fileId)
            }
        }
    }

    fun notifyCopied() {
        status.value = "已复制"
    }
}
