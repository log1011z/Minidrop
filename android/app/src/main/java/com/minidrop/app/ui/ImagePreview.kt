package com.minidrop.app.ui

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.*
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.minidrop.app.data.db.FileEntity
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

data class ImagePreviewTarget(val path: String, val name: String)

internal fun isPreviewImage(name: String, mime: String?): Boolean =
    mime?.startsWith("image/", ignoreCase = true) == true ||
        name.substringAfterLast('.', "").lowercase() in setOf("png", "jpg", "jpeg", "gif", "webp", "bmp", "heic", "heif")

internal object PreviewImages {
    private val cache = object : android.util.LruCache<String, Bitmap>(16 * 1024 * 1024) {
        override fun sizeOf(key: String, value: Bitmap) = value.allocationByteCount
    }

    fun load(context: Context, path: String, maxPixels: Int): Bitmap? {
        val key = "$path:$maxPixels:" + if (path.startsWith("content:")) "" else File(path).lastModified()
        cache.get(key)?.let { return it }
        return try {
            fun stream() = if (path.startsWith("content:")) context.contentResolver.openInputStream(Uri.parse(path)) else File(path).inputStream()
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            stream()?.use { BitmapFactory.decodeStream(it, null, bounds) }
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
            var sample = 1
            while (maxOf(bounds.outWidth, bounds.outHeight) / sample > maxPixels) sample *= 2
            val options = BitmapFactory.Options().apply { inSampleSize = sample }
            stream()?.use { BitmapFactory.decodeStream(it, null, options) }?.also { cache.put(key, it) }
        } catch (_: Exception) { null }
    }
}

@Composable
internal fun ImageThumbnail(file: FileEntity, onClick: () -> Unit) {
    val path = file.cachePath?.takeIf { it.startsWith("content:") || File(it).exists() }
        ?: file.sourcePath?.takeIf { File(it).exists() }
    val context = LocalContext.current
    var loaded by remember(path, file.state) { mutableStateOf(false) }
    val bitmap by produceState<Bitmap?>(null, path, file.state) {
        value = path?.let { withContext(Dispatchers.IO) { PreviewImages.load(context, it, 512) } }
        loaded = true
    }
    Box(Modifier.fillMaxWidth().height(180.dp).clickable(onClick = onClick), contentAlignment = Alignment.Center) {
        if (bitmap != null) Image(bitmap!!.asImageBitmap(), contentDescription = "预览 ${file.name}",
            modifier = Modifier.fillMaxSize(), contentScale = ContentScale.Fit)
        else Text(if (file.state == "downloading") "正在下载图片…" else if (path == null) "点击加载图片"
            else if (!loaded) "正在加载图片…" else "无法生成缩略图，点击预览")
    }
}

@Composable
internal fun ImagePreviewDialog(target: ImagePreviewTarget, onDismiss: () -> Unit) {
    val context = LocalContext.current
    var loaded by remember(target.path) { mutableStateOf(false) }
    val bitmap by produceState<Bitmap?>(null, target.path) {
        value = withContext(Dispatchers.IO) { PreviewImages.load(context, target.path, 2048) }
        loaded = true
    }
    var scale by remember(target.path) { mutableFloatStateOf(1f) }
    var offset by remember(target.path) { mutableStateOf(Offset.Zero) }
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(Modifier.fillMaxSize().background(Color(0xFF181A1E)).systemBarsPadding()) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(target.name, color = Color.White, modifier = Modifier.weight(1f), maxLines = 1)
                TextButton(onClick = { scale = 1f; offset = Offset.Zero }) { Text("复位", color = Color.White) }
                TextButton(onClick = onDismiss) { Text("关闭", color = Color.White) }
            }
            Box(Modifier.weight(1f).fillMaxWidth().clipToBounds().pointerInput(target.path) {
                detectTransformGestures { _, pan, zoom, _ ->
                    scale = (scale * zoom).coerceIn(1f, 5f)
                    val next = offset + pan
                    val maxX = size.width * (scale - 1f) / 2f
                    val maxY = size.height * (scale - 1f) / 2f
                    offset = Offset(next.x.coerceIn(-maxX, maxX), next.y.coerceIn(-maxY, maxY))
                }
            }, contentAlignment = Alignment.Center) {
                if (bitmap != null) Image(bitmap!!.asImageBitmap(), target.name,
                    modifier = Modifier.fillMaxSize().graphicsLayer { scaleX = scale; scaleY = scale; translationX = offset.x; translationY = offset.y },
                    contentScale = ContentScale.Fit)
                else Text(if (loaded) "无法预览此图片" else "正在加载图片…", color = Color.White)
            }
        }
    }
}
