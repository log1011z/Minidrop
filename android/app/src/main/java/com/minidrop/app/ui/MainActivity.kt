package com.minidrop.app.ui

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import com.minidrop.app.MiniDropApp

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            MaterialTheme {
                Surface {
                    TimelineScreen(
                        onOpenSettings = { startActivity(android.content.Intent(this, SettingsActivity::class.java)) },
                    )
                }
            }
        }
    }
}
