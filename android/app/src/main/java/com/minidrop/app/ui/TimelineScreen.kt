package com.minidrop.app.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.withLink
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Share
import androidx.compose.material.icons.filled.CloseFullscreen
import androidx.compose.material.icons.filled.OpenInFull
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.Inbox
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.Surface
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.minidrop.app.R

/** 时间线 + 输入栏（§10.4）：只读 Room，刷新/加载更早为显式用户动作。 */
@OptIn(ExperimentalMaterial3Api::class, androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable
fun TimelineScreen(
    onOpenSettings: () -> Unit,
    vm: TimelineViewModel = viewModel(),
) {
    val state by vm.uiState.collectAsStateWithLifecycle()
    val imagePreview by vm.imagePreview.collectAsStateWithLifecycle()
    imagePreview?.let { ImagePreviewDialog(it, vm::dismissImagePreview) }
    val context = LocalContext.current
    var deleteTarget by remember { mutableStateOf<String?>(null) }
    val listState = rememberLazyListState()

    val pickFiles = rememberLauncherForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.OpenMultipleDocuments(),
    ) { uris -> vm.onFilesPicked(uris) }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        modifier = Modifier
            .fillMaxSize()
            .imePadding(), // 输入法调起时输入栏浮在键盘上方
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text("MiniDrop", style = MaterialTheme.typography.titleLarge)
                        Text("文件与文字，随手送达", style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background),
                actions = {
                    IconButton(onClick = { vm.refresh() }) {
                        Icon(Icons.Filled.Refresh, contentDescription = stringResourceCompat(context, R.string.refresh))
                    }
                    IconButton(onClick = onOpenSettings) {
                        Icon(Icons.Filled.Settings, contentDescription = stringResourceCompat(context, R.string.settings))
                    }
                },
            )
        },
        bottomBar = {
            Column(Modifier.navigationBarsPadding().padding(horizontal = 12.dp, vertical = 8.dp)) {
                if (state.attachments.isNotEmpty()) {
                    Column(Modifier.fillMaxWidth().heightIn(max = 120.dp).verticalScroll(rememberScrollState())) {
                        state.attachments.forEach { file ->
                            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                                Icon(Icons.Filled.Description, contentDescription = null, modifier = Modifier.size(20.dp))
                                Text(file.displayName, modifier = Modifier.weight(1f).padding(horizontal = 8.dp),
                                    maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
                                IconButton(onClick = { vm.removeAttachment(file) }, enabled = !state.sending) {
                                    Icon(Icons.Filled.Close, contentDescription = "移除附件 ${file.displayName}")
                                }
                            }
                        }
                    }
                }
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(vertical = 4.dp),
                    verticalAlignment = Alignment.Bottom,
                ) {
                    var expanded by rememberSaveable { mutableStateOf(false) }

                    // 输入框容器：内嵌 + 号与放大按钮
                    Surface(
                        shape = RoundedCornerShape(16.dp),
                        color = MaterialTheme.colorScheme.surface,
                        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant),
                        modifier = Modifier.weight(1f),
                    ) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            IconButton(
                                onClick = { pickFiles.launch(arrayOf("*/*")) },
                                enabled = !state.sending,
                                modifier = Modifier.size(44.dp),
                            ) {
                                Icon(
                                    Icons.Filled.Add,
                                    contentDescription = "添加文件",
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            BasicTextField(
                                value = state.input,
                                onValueChange = vm::onInputChange,
                                modifier = Modifier
                                    .weight(1f)
                                    .padding(vertical = 8.dp)
                                    .heightIn(min = 20.dp)
                                    .heightIn(max = if (expanded) 220.dp else 80.dp)
                                    .verticalScroll(rememberScrollState()),
                                textStyle = LocalTextStyle.current.copy(fontSize = 15.sp),
                                maxLines = if (expanded) 12 else 4,
                                cursorBrush = SolidColor(MaterialTheme.colorScheme.primary),
                                decorationBox = { inner ->
                                    Box {
                                        if (state.input.isEmpty()) {
                                            Text(
                                                stringResourceCompat(context, R.string.input_hint),
                                                style = LocalTextStyle.current.copy(
                                                    fontSize = 15.sp,
                                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                                ),
                                                maxLines = 1,
                                            )
                                        }
                                        inner()
                                    }
                                },
                            )
                            IconButton(
                                onClick = { expanded = !expanded },
                                modifier = Modifier.size(44.dp),
                            ) {
                                Icon(
                                    if (expanded) Icons.Filled.CloseFullscreen else Icons.Filled.OpenInFull,
                                    contentDescription = if (expanded) "收起输入框" else "放大输入框",
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    }

                    Button(
                        onClick = { vm.sendInput() },
                        enabled = !state.sending && (state.input.isNotBlank() || state.attachments.isNotEmpty()),
                        modifier = Modifier
                            .padding(start = 8.dp)
                            .height(46.dp),
                        shape = RoundedCornerShape(14.dp),
                        contentPadding = PaddingValues(horizontal = 12.dp),
                    ) {
                        Icon(Icons.AutoMirrored.Filled.Send, contentDescription = "发送", modifier = Modifier.size(20.dp))
                    }
                }
                if (state.statusText.isNotBlank()) Row(Modifier.padding(horizontal = 6.dp, vertical = 4.dp)) {
                    Text(
                        state.statusText,
                        maxLines = 1, overflow = TextOverflow.Ellipsis,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        },
    ) { padding ->
        PullToRefreshBox(
            isRefreshing = state.busy,
            onRefresh = { vm.refresh() },
            modifier = Modifier
                .fillMaxSize()
                .padding(padding),
        ) {
            LazyColumn(state = listState, modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(bottom = 8.dp),
            ) {
                item(key = "history", contentType = "header") {
                    Box(Modifier.fillMaxWidth().padding(vertical = 4.dp), contentAlignment = Alignment.Center) {
                        if (state.canLoadOlder) {
                            OutlinedButton(onClick = { vm.loadOlder() }, enabled = !state.busy) {
                                Text(stringResourceCompat(context, R.string.load_older))
                            }
                        }
                    }
                }
                if (!state.configured) {
                    item(key = "configuration", contentType = "notice") {
                        Text(
                            stringResourceCompat(context, R.string.not_configured),
                            color = MaterialTheme.colorScheme.error,
                            modifier = Modifier.padding(16.dp),
                        )
                    }
                }
                if (state.timeline.isEmpty()) item(key = "empty", contentType = "notice") {
                    Column(Modifier.fillMaxWidth().padding(vertical = 64.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                    ) {
                        Icon(Icons.Filled.Inbox, contentDescription = null, Modifier.size(44.dp),
                            tint = MaterialTheme.colorScheme.outline)
                        Text("从一次投递开始", style = MaterialTheme.typography.titleMedium,
                            modifier = Modifier.padding(top = 16.dp, bottom = 6.dp))
                        Text("分享一个文件，或写点什么", style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                items(state.timeline.size, key = { state.timeline[it].id },
                    contentType = { if (state.filesByMessage[state.timeline[it].id].isNullOrEmpty()) "text" else "files" },
                ) { i ->
                    MessageCard(
                        row = state.timeline[i],
                        files = state.filesByMessage[state.timeline[i].id].orEmpty(),
                        onDownload = vm::downloadFile,
                        onOpen = vm::openFile,
                        onShare = vm::shareFile,
                        onRetryUpload = { vm.retryUpload(state.timeline[i].id) },
                        onDeleteRequest = { deleteTarget = state.timeline[i].id },
                    )
                }
            }
        }
    }

    deleteTarget?.let { id ->
        AlertDialog(
            onDismissRequest = { deleteTarget = null },
            title = { Text(stringResourceCompat(context, R.string.delete)) },
            text = { Text(stringResourceCompat(context, R.string.delete_confirm)) },
            confirmButton = {
                TextButton(onClick = { vm.delete(id); deleteTarget = null }) {
                    Text(stringResourceCompat(context, R.string.delete))
                }
            },
            dismissButton = {
                TextButton(onClick = { deleteTarget = null }) { Text("取消") }
            },
        )
    }
}

private fun stringResourceCompat(context: Context, res: Int): String = context.getString(res)

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun MessageCard(
    row: com.minidrop.app.data.db.TimelineRow,
    files: List<com.minidrop.app.data.db.FileEntity>,
    onDownload: (String) -> Unit,
    onOpen: (String) -> Unit,
    onShare: (String) -> Unit,
    onRetryUpload: () -> Unit,
    onDeleteRequest: () -> Unit,
) {
    Card(
        shape = RoundedCornerShape(16.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant),
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 5.dp),
    ) {
        Column(Modifier.padding(16.dp)) {
            Row(modifier = Modifier.fillMaxWidth().combinedClickable(onClick = {}, onLongClick = onDeleteRequest),
                verticalAlignment = Alignment.CenterVertically) {
                Text(
                    row.deviceName,
                    maxLines = 1, overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Text(formatMessageTime(row.createdAt), style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 10.dp))
                StatusText(row)
                if (row.jobState == "failed" || row.jobState == "retry_wait") {
                    TextButton(onClick = onRetryUpload, contentPadding = PaddingValues(horizontal = 6.dp)) {
                        Text("重试")
                    }
                }
            }
            if (!row.text.isNullOrBlank()) {
                var expanded by rememberSaveable(row.id) { mutableStateOf(false) }
                var overflowing by rememberSaveable(row.id, row.text) { mutableStateOf(false) }
                val uriHandler = LocalUriHandler.current
                val context = LocalContext.current
                val linkColor = MaterialTheme.colorScheme.primary
                val linkedText = remember(row.text, linkColor, uriHandler) {
                    buildAnnotatedString {
                        var offset = 0
                        messageLinks(row.text).forEach { link ->
                            append(row.text.substring(offset, link.start))
                            withLink(LinkAnnotation.Url(link.url,
                                styles = TextLinkStyles(SpanStyle(color = linkColor, textDecoration = TextDecoration.Underline)),
                                linkInteractionListener = {
                                    runCatching { uriHandler.openUri(link.url) }.onFailure {
                                        android.widget.Toast.makeText(context, "无法打开链接", android.widget.Toast.LENGTH_SHORT).show()
                                    }
                                },
                            )) { append(row.text.substring(link.start, link.end)) }
                            offset = link.end
                        }
                        append(row.text.substring(offset))
                    }
                }
                SelectionContainer {
                Text(
                    linkedText,
                    maxLines = if (expanded) Int.MAX_VALUE else 5,
                    overflow = TextOverflow.Ellipsis,
                    onTextLayout = { if (!expanded) overflowing = it.hasVisualOverflow },
                    style = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier
                        .padding(top = 10.dp),
                )
                }
                if (overflowing || expanded) {
                    TextButton(onClick = { expanded = !expanded }) {
                        Text(if (expanded) "收起" else "展开全文")
                    }
                }
            }
            files.forEach { f ->
                Surface(color = MaterialTheme.colorScheme.surfaceVariant, shape = RoundedCornerShape(12.dp),
                    modifier = Modifier.fillMaxWidth().padding(top = 10.dp),
                ) {
                Column {
                if (isPreviewImage(f.name, f.mime)) ImageThumbnail(f) { onOpen(f.fileId) }
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(start = 10.dp, end = 4.dp, top = 6.dp, bottom = 6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Icon(Icons.Filled.Description, contentDescription = null,
                        tint = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.padding(end = 10.dp).size(24.dp))
                    Column(Modifier.weight(1f)) {
                        Text(f.name, style = MaterialTheme.typography.bodyMedium,
                            maxLines = 2, overflow = TextOverflow.Ellipsis)
                        Text(
                            listOf(formatFileSize(f.size), fileStateText(f)).filter { it.isNotBlank() }.joinToString(" · "),
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.primary,
                        )
                    }
                    TextButton(enabled = f.state != com.minidrop.app.core.FileStates.DOWNLOADING, onClick = {
                        if (f.state == com.minidrop.app.core.FileStates.CACHED) onOpen(f.fileId) else onDownload(f.fileId)
                    }) {
                        Text(
                            when (f.state) {
                                com.minidrop.app.core.FileStates.CACHED -> if (isPreviewImage(f.name, f.mime)) "预览" else "打开"
                                com.minidrop.app.core.FileStates.DOWNLOADING -> "…"
                                com.minidrop.app.core.FileStates.FAILED -> "重试"
                                else -> if (isPreviewImage(f.name, f.mime)) "预览" else "下载并打开"
                            },
                        )
                    }
                    IconButton(enabled = f.state != com.minidrop.app.core.FileStates.DOWNLOADING,
                        onClick = { onShare(f.fileId) }) {
                        Icon(Icons.Filled.Share, contentDescription = "分享文件")
                    }
                }
                }
                }
            }
        }
    }
}

private fun formatFileSize(bytes: Long): String = when {
    bytes >= 1024L * 1024 * 1024 -> String.format(java.util.Locale.getDefault(), "%.1f GB", bytes / (1024.0 * 1024 * 1024))
    bytes >= 1024L * 1024 -> String.format(java.util.Locale.getDefault(), "%.1f MB", bytes / (1024.0 * 1024))
    bytes >= 1024L -> String.format(java.util.Locale.getDefault(), "%.1f KB", bytes / 1024.0)
    else -> "$bytes B"
}

@Composable
private fun StatusText(row: com.minidrop.app.data.db.TimelineRow) {
    if (row.jobState == null) return
    val text = when (row.jobState) {
        "queued" -> "排队中"
        "uploading" -> if ((row.jobBytesTotal ?: 0) > 0) {
            "正在上传 ${(row.jobBytesDone ?: 0) * 100 / row.jobBytesTotal!!}%"
        } else {
            "正在上传…"
        }
        "retry_wait" -> "等待重试"
        "failed" -> "上传失败"
        else -> ""
    }
    Text(text, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary)
}

private fun fileStateText(f: com.minidrop.app.data.db.FileEntity): String = when (f.state) {
    com.minidrop.app.core.FileStates.REMOTE -> ""
    com.minidrop.app.core.FileStates.DOWNLOADING -> "正在下载…"
    com.minidrop.app.core.FileStates.CACHED -> "已下载"
    com.minidrop.app.core.FileStates.FAILED -> "下载失败，点击重试"
    else -> ""
}
