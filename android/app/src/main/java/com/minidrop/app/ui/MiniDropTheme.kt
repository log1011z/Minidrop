package com.minidrop.app.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

private val LightColors = lightColorScheme(
    primary = Color(0xFF3867DE), onPrimary = Color.White,
    primaryContainer = Color(0xFFEAF0FF), onPrimaryContainer = Color(0xFF243C78),
    background = Color(0xFFF4F7FB), onBackground = Color(0xFF24334B),
    surface = Color.White, onSurface = Color(0xFF24334B),
    surfaceVariant = Color(0xFFF1F5FB), onSurfaceVariant = Color(0xFF758297),
    outline = Color(0xFFB3C1D5), outlineVariant = Color(0xFFE2E9F2),
)

private val DarkColors = darkColorScheme(
    primary = Color(0xFFAEC4FF), onPrimary = Color(0xFF123575),
    primaryContainer = Color(0xFF243C78), onPrimaryContainer = Color(0xFFDCE5FF),
    background = Color(0xFF141B28), onBackground = Color(0xFFE3EAF6),
    surface = Color(0xFF1E293A), onSurface = Color(0xFFE3EAF6),
    surfaceVariant = Color(0xFF253247), onSurfaceVariant = Color(0xFFAFBDD3),
    outline = Color(0xFF7988A0), outlineVariant = Color(0xFF344259),
)

@Composable
fun MiniDropTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = if (isSystemInDarkTheme()) DarkColors else LightColors, content = content)
}
