package com.minidrop.app.ui

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.minidrop.app.MiniDropApp
import com.minidrop.app.data.SettingsStore
import com.minidrop.app.webdav.WebDavClient
import kotlinx.coroutines.launch

/** 设置（§11）：测试连接成功才保存；变更行为按 §11.3。 */
@OptIn(ExperimentalMaterial3Api::class)
class SettingsActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val app = application as MiniDropApp
        enableEdgeToEdge()
        setContent {
            MaterialTheme {
                Surface {
                    SettingsScreen(app = app, onFinish = { finish() })
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SettingsScreen(app: MiniDropApp, onFinish: () -> Unit) {
    val scope = rememberCoroutineScope()
    var rootUrl by remember { mutableStateOf(app.settingsSnapshot.rootUrl) }
    var account by remember { mutableStateOf(app.settingsSnapshot.account) }
    var password by remember { mutableStateOf("") }
    var deviceName by remember { mutableStateOf(app.settingsSnapshot.deviceName) }
    var maxFileMb by remember { mutableStateOf((app.settingsSnapshot.maxFileBytes / (1024 * 1024)).toString()) }
    var status by remember { mutableStateOf("") }
    var testing by remember { mutableStateOf(false) }
    var originalAccount by remember { mutableStateOf(app.settingsSnapshot.account) }

    Scaffold(
        topBar = {
            TopAppBar(title = { Text("设置") })
        },
    ) { padding ->
        Column(
            Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            OutlinedTextField(
                value = rootUrl, onValueChange = { rootUrl = it },
                label = { Text("WebDAV 根 URL") }, modifier = Modifier.fillMaxWidth(), singleLine = true,
            )
            OutlinedTextField(
                value = account, onValueChange = { account = it },
                label = { Text("账号（坚果云）") }, modifier = Modifier.fillMaxWidth(), singleLine = true,
            )
            OutlinedTextField(
                value = password, onValueChange = { password = it },
                label = { Text("应用密码" + if (app.passwordSnapshot != null) "（已保存，留空则不变）" else "") },
                modifier = Modifier.fillMaxWidth(), singleLine = true,
                visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation(),
            )
            OutlinedTextField(
                value = deviceName, onValueChange = { deviceName = it.take(32) },
                label = { Text("设备名") }, modifier = Modifier.fillMaxWidth(), singleLine = true,
            )
            OutlinedTextField(
                value = maxFileMb, onValueChange = { maxFileMb = it.filter { c -> c.isDigit() } },
                label = { Text("单文件上限 (MB)") }, modifier = Modifier.fillMaxWidth(), singleLine = true,
            )

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(
                    enabled = !testing,
                    onClick = {
                        scope.launch {
                            testing = true
                            status = "正在测试连接…"
                            val (ok, error) = testConnection(rootUrl, account, password.ifBlank { app.passwordSnapshot ?: "" })
                            status = if (ok) "测试成功 ✓" else (error ?: "测试失败")
                            testing = false
                        }
                    },
                ) { Text("测试连接") }

                Button(
                    enabled = !testing,
                    onClick = {
                        scope.launch {
                            testing = true
                            val effectivePassword = password.ifBlank { app.passwordSnapshot ?: "" }
                            val (ok, error) = testConnection(rootUrl, account, effectivePassword)
                            if (!ok) {
                                status = error ?: "保存失败：连接测试未通过"
                                testing = false
                                return@launch
                            }
                            // 数据集切换确认（§11.3）
                            val accountChanged = account.trim() != originalAccount && originalAccount.isNotBlank()
                            if (accountChanged) {
                                status = "账号变更将清空本地索引（下次刷新重新拉取）"
                            }
                            val normalized = try {
                                WebDavClient.normalizeRootUrl(rootUrl)
                            } catch (_: IllegalArgumentException) {
                                status = "只允许 HTTPS 根 URL"
                                testing = false
                                return@launch
                            }
                            app.settings.save(
                                rootUrl = normalized,
                                account = account.trim(),
                                deviceName = deviceName.trim().ifBlank { "Android" },
                                maxFileBytes = (maxFileMb.toLongOrNull() ?: 500).coerceAtLeast(1) * 1024 * 1024,
                                notifyOnSend = true,
                            )
                            if (password.isNotBlank()) app.settings.savePassword(password)
                            app.refreshCredentials()
                            originalAccount = account.trim()
                            status = "已保存 ✓"
                            testing = false
                            onFinish()
                        }
                    },
                ) { Text("保存") }
            }

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = {
                    scope.launch {
                        val outcome = app.maintenance.maintain()
                        status = if (outcome.scanError) "维护失败，请检查网络" else "已清理 ${outcome.processed} 条远端记录"
                    }
                }) { Text("清理 90 天前远端记录") }

                OutlinedButton(onClick = {
                    scope.launch {
                        app.db.rejectedDao().clearQuarantine()
                        status = "已清除拒绝标记，下次刷新将重新尝试"
                    }
                }) { Text("重试被拒绝条目") }
            }

            if (status.isNotBlank()) {
                Text(status, color = MaterialTheme.colorScheme.primary)
            }
        }
    }
}

private suspend fun testConnection(rootUrl: String, account: String, password: String): Pair<Boolean, String?> {
    val normalized = try {
        WebDavClient.normalizeRootUrl(rootUrl)
    } catch (_: IllegalArgumentException) {
        return false to "只允许 HTTPS 根 URL"
    }
    val client = WebDavClient(normalized, account, { password }, timeoutSeconds = 15)
    val (propfind, _) = client.propfindDir("")
    if (propfind.status == com.minidrop.app.webdav.DavStatus.AUTH_ERROR) return false to "账号或应用密码不可用"
    if (!propfind.ok && propfind.status != com.minidrop.app.webdav.DavStatus.NOT_FOUND) {
        return false to "服务不可达：" + (propfind.detail ?: propfind.status.toString())
    }
    for (dir in listOf(
        com.minidrop.app.core.RemotePaths.ITEMS_ROOT,
        com.minidrop.app.core.RemotePaths.TOMBSTONES_ROOT,
        com.minidrop.app.core.RemotePaths.FILES_ROOT,
    )) {
        val mk = client.mkCol(dir)
        if (!mk.ok && mk.httpCode != 405) return false to "无法创建目录 $dir（" + (mk.detail ?: mk.status.toString()) + "）"
    }
    return true to null
}
