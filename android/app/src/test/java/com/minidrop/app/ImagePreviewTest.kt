package com.minidrop.app

import android.app.Application
import android.graphics.Bitmap
import com.minidrop.app.ui.PreviewImages
import com.minidrop.app.ui.isPreviewImage
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File

@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [28])
class ImagePreviewTest {
    @Test fun localImageIsSampledAndBadImageReturnsFallback() {
        org.robolectric.shadows.ShadowBitmapFactory.setAllowInvalidImageData(false)
        val context = RuntimeEnvironment.getApplication()
        val source = File(context.cacheDir, "preview.png")
        val original = Bitmap.createBitmap(1200, 800, Bitmap.Config.ARGB_8888)
        source.outputStream().use { original.compress(Bitmap.CompressFormat.PNG, 100, it) }
        val thumbnail = PreviewImages.load(context, source.path, 512)
        assertNotNull(thumbnail)
        assertTrue(thumbnail!!.width <= 512 && thumbnail.height <= 512)
        assertSame(thumbnail, PreviewImages.load(context, source.path, 512))
        val broken = File(context.cacheDir, "broken.png").apply { writeText("not an image") }
        assertNull(PreviewImages.load(context, broken.path, 512))
        assertNull(PreviewImages.load(context, File(context.cacheDir, "missing.png").path, 512))
    }

    @Test fun recognizesImagesAndKeepsOtherAttachmentsAsFiles() {
        assertTrue(isPreviewImage("截图.PNG", null))
        assertTrue(isPreviewImage("photo", "image/jpeg"))
        assertTrue(isPreviewImage("photo.webp", null))
        assertFalse(isPreviewImage("report.pdf", "application/pdf"))
    }
}
