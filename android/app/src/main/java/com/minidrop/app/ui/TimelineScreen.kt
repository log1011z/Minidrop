package com.minidrop.app.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.minidrop.app.R

/** 时间线 + 输入栏（§10.4）：只读 Room，刷新/加载更早为显式用户动作。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TimelineScreen(
    onOpenSettings: () -> Unit,
    vm: TimelineViewModel = viewModel(),
) {
    val state by vm.uiState.collectAsState()
    val context = LocalContext.current
    var deleteTarget by remember { mutableStateOf<String?>(null) }
    val listState = rememberLazyListState()

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("MiniDrop") },
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
            Column {
                HorizontalDivider()
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(8.dp),
                    verticalAlignment = Alignment.Bottom,
                ) {
                    OutlinedTextField(
                        value = state.input,
                        onValueChange = vm::onInputChange,
                        modifier = Modifier.weight(1f),
                        placeholder = { Text(stringResourceCompat(context, R.string.input_hint)) },
                        maxLines = 4,
                    )
                    Button(
                        onClick = { vm.sendInput() },
                        enabled = !state.busy && state.input.isNotBlank(),
                        modifier = Modifier.padding(start = 8.dp),
                    ) {
                        Text(stringResourceCompat(context, R.string.send))
                    }
                }
                Row(Modifier.padding(horizontal = 12.dp, vertical = 4.dp)) {
                    Text(
                        state.statusText,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        },
    ) { padding ->
        PullToRefreshBox(
            isRefreshing = state.busy && state.statusText.startsWith("正在刷新"),
            onRefresh = { vm.refresh() },
            modifier = Modifier
                .fillMaxSize()
                .padding(padding),
        ) {
            LazyColumn(state = listState, modifier = Modifier.fillMaxSize()) {
                item {
                    Box(Modifier.fillMaxWidth().padding(vertical = 4.dp), contentAlignment = Alignment.Center) {
                        if (state.canLoadOlder) {
                            OutlinedButton(onClick = { vm.loadOlder() }, enabled = !state.busy) {
                                Text(stringResourceCompat(context, R.string.load_older))
                            }
                        }
                    }
                }
                if (!state.configured) {
                    item {
                        Text(
                            stringResourceCompat(context, R.string.not_configured),
                            color = MaterialTheme.colorScheme.error,
                            modifier = Modifier.padding(16.dp),
                        )
                    }
                }
                items(state.timeline.size, key = { state.timeline[it].id }) { i ->
                    MessageCard(
                        row = state.timeline[i],
                        files = state.filesByMessage[state.timeline[i].id].orEmpty(),
                        onDownload = vm::downloadFile,
                        onOpen = vm::openFile,
                        onDeleteRequest = { deleteTarget = state.timeline[i].id },
                        onCopy = { text ->
                            val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                            cm.setPrimaryClip(ClipData.newPlainText("MiniDrop", text))
                            vm.notifyCopied()
                        },
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
    onDeleteRequest: () -> Unit,
    onCopy: (String) -> Unit,
) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 4.dp)
            .combinedClickable(onClick = {}, onLongClick = onDeleteRequest),
    ) {
        Column(Modifier.padding(12.dp)) {
            Row {
                Text(
                    "${row.createdAt.substring(11, 16)}  ${row.deviceName}",
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Spacer(Modifier.weight(1f))
                StatusText(row)
            }
            if (!row.text.isNullOrBlank()) {
                Text(
                    row.text,
                    style = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier
                        .padding(top = 4.dp)
                        .combinedClickable(onClick = { onCopy(row.text) }, onLongClick = onDeleteRequest),
                )
            }
            files.forEach { f ->
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(top = 6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(f.name, style = MaterialTheme.typography.bodyMedium)
                        Text(
                            fileStateText(f),
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.primary,
                        )
                    }
                    TextButton(onClick = {
                        if (f.state == com.minidrop.app.core.FileStates.CACHED) onOpen(f.fileId) else onDownload(f.fileId)
                    }) {
                        Text(
                            when (f.state) {
                                com.minidrop.app.core.FileStates.CACHED -> "打开"
                                com.minidrop.app.core.FileStates.DOWNLOADING -> "…"
                                com.minidrop.app.core.FileStates.FAILED -> "重试"
                                else -> "下载"
                            },
                        )
                    }
                }
            }
        }
    }
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
