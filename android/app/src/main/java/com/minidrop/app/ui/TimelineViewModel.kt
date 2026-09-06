package com.minidrop.app.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.minidrop.app.MiniDropApp
import com.minidrop.app.core.FileStates
import com.minidrop.app.data.db.FileEntity
import com.minidrop.app.data.db.TimelineRow
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
    val statusText: String = "",
    val configured: Boolean = false,
    val canLoadOlder: Boolean = true,
)

class TimelineViewModel(app: Application) : AndroidViewModel(app) {

    private val ctx get() = getApplication<MiniDropApp>()

    private val input = MutableStateFlow("")
    private val busy = MutableStateFlow(false)
    private val status = MutableStateFlow("")
    private val configured = MutableStateFlow(false)
    private val canLoadOlder = MutableStateFlow(true)

    private val timelineFlow = ctx.db.messageDao().timelineFlow(500)
    private val filesFlow = ctx.db.fileDao().allFiles()

    // 配置状态实时订阅：设置页保存后回到时间线立即生效
    private val configuredFlow = ctx.settings.settings.map { it.isConfigured }

    val uiState: StateFlow<TimelineUiState> = combine(
        timelineFlow, filesFlow, input, busy, combine(status, configuredFlow, canLoadOlder) { s, c, l -> Triple(s, c, l) },
    ) { timeline, files, inputText, isBusy, (statusText, isConfigured, loadOlder) ->
        TimelineUiState(
            timeline = timeline,
            filesByMessage = files.groupBy { it.messageId },
            input = inputText,
            busy = isBusy,
            statusText = statusText,
            configured = isConfigured,
            canLoadOlder = loadOlder,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), TimelineUiState())


    fun onInputChange(value: String) {
        input.value = value
    }

    fun sendInput() {
        val text = input.value
        if (text.isBlank()) return
        input.value = ""
        viewModelScope.launch {
            busy.value = true
            when (val result = ctx.send.enqueueText(text)) {
                is com.minidrop.app.sync.SendResult.Ok -> status.value = "已加入 MiniDrop"
                is com.minidrop.app.sync.SendResult.Fail -> status.value = result.text
            }
            busy.value = false
        }
    }

    fun refresh() {
        if (busy.value) return
        viewModelScope.launch {
            busy.value = true
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
        viewModelScope.launch {
            busy.value = true
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

    /** 输入栏 + 按钮选择的文件：复制到 staging → 入队（§5.5）。 */
    fun onFilesPicked(uris: List<android.net.Uri>) {
        if (uris.isEmpty()) return
        viewModelScope.launch {
            busy.value = true
            status.value = "正在接收所选文件…"
            try {
                val app = getApplication<MiniDropApp>()
                when (val result = com.minidrop.app.sync.Staging.copyAll(
                    app, uris, app.settingsSnapshot.maxFileBytes,
                ) { index, total -> status.value = "正在复制 ${index + 1}/$total…" }) {
                    is com.minidrop.app.sync.Staging.CopyResult.TooLarge -> status.value = result.text
                    is com.minidrop.app.sync.Staging.CopyResult.Failed -> status.value = result.text
                    is com.minidrop.app.sync.Staging.CopyResult.Ok -> {
                        when (val send = ctx.send.enqueueFiles(result.files, null)) {
                            is com.minidrop.app.sync.SendResult.Ok -> status.value = "已加入 ${result.files.size} 个文件"
                            is com.minidrop.app.sync.SendResult.Fail -> {
                                status.value = send.text
                                com.minidrop.app.sync.Staging.cleanup(result.files)
                            }
                        }
                    }
                }
            } finally {
                busy.value = false
            }
        }
    }

    fun delete(messageId: String) {
        viewModelScope.launch {
            busy.value = true
            when (val result = ctx.delete.delete(messageId)) {
                is com.minidrop.app.sync.DeleteService.Result.Ok -> status.value = "已删除"
                is com.minidrop.app.sync.DeleteService.Result.Fail -> status.value = result.text
            }
            busy.value = false
        }
    }

    fun downloadFile(fileId: String) {
        viewModelScope.launch {
            when (val result = ctx.download.download(fileId)) {
                is com.minidrop.app.sync.DownloadService.Result.Ok -> status.value = "下载完成"
                is com.minidrop.app.sync.DownloadService.Result.Fail -> result.text?.let { status.value = it }
            }
        }
    }

    fun openFile(fileId: String) {
        viewModelScope.launch {
            ctx.download.ensureCacheConsistency(fileId)
            val file = ctx.db.fileDao().getById(fileId) ?: return@launch
            val path = file.cachePath ?: return@launch
            val mime = file.mime ?: "application/octet-stream"
            try {
                // SAF 目录下载的文件直接用 content uri；私有目录走 FileProvider
                val uri = if (path.startsWith("content:")) {
                    android.net.Uri.parse(path)
                } else {
                    androidx.core.content.FileProvider.getUriForFile(
                        getApplication<MiniDropApp>(), "com.minidrop.app.fileprovider", File(path),
                    )
                }
                val intent = android.content.Intent(android.content.Intent.ACTION_VIEW)
                    .setDataAndType(uri, mime)
                    .addFlags(android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION or android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
                getApplication<MiniDropApp>().startActivity(intent)
            } catch (_: Exception) {
                status.value = "没有关联应用可以打开该文件"
            }
        }
    }

    fun notifyCopied() {
        status.value = "已复制"
    }
}
