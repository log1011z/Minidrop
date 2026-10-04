package com.minidrop.app.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.minidrop.app.MiniDropApp
import java.io.File

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            MiniDropTheme {
                Surface {
                    TimelineScreen(
                        onOpenSettings = { startActivity(android.content.Intent(this, SettingsActivity::class.java)) },
                    )
                    CrashReportDialog()
                }
            }
        }
    }
}

/** 上次崩溃的堆栈：重启后弹出，可一键复制反馈。 */
@Composable
private fun CrashReportDialog() {
    val context = LocalContext.current
    var crashText by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) {
        val file = File((context.applicationContext as MiniDropApp).filesDir, "crash-latest.txt")
        if (file.exists() && file.length() > 0) {
            crashText = runCatching { file.readText() }.getOrNull()
        }
    }

    crashText?.let { text ->
        AlertDialog(
            onDismissRequest = { },
            title = { Text("上次运行崩溃了") },
            text = {
                Column(
                    Modifier
                        .fillMaxWidth()
                        .height(280.dp)
                        .verticalScroll(rememberScrollState()),
                ) {
                    Text(text, style = MaterialTheme.typography.bodySmall)
                }
            },
            confirmButton = {
                TextButton(onClick = {
                    val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                    cm.setPrimaryClip(ClipData.newPlainText("MiniDrop crash", text))
                }) { Text("复制全部") }
            },
            dismissButton = {
                TextButton(onClick = {
                    runCatching {
                        File((context.applicationContext as MiniDropApp).filesDir, "crash-latest.txt").delete()
                    }
                    crashText = null
                }) { Text("忽略并清除") }
            },
        )
    }
}
