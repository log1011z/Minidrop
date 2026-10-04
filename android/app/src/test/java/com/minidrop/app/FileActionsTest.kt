package com.minidrop.app

import android.app.Application
import android.content.Intent
import android.net.Uri
import androidx.lifecycle.viewModelScope
import androidx.room.Room
import com.minidrop.app.core.FileStates
import com.minidrop.app.data.SettingsStore
import com.minidrop.app.data.db.MiniDropDatabase
import com.minidrop.app.sync.DownloadService
import com.minidrop.app.sync.SendResult
import com.minidrop.app.sync.SendService
import com.minidrop.app.sync.StagedFile
import com.minidrop.app.ui.TimelineViewModel
import com.minidrop.app.webdav.WebDavClient
import kotlinx.coroutines.*
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.annotation.Implements
import org.robolectric.annotation.Implementation
import java.io.File
import java.util.UUID

@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [28], shadows = [HostFileProvider::class])
class FileActionsTest {
    private lateinit var db: MiniDropDatabase
    private lateinit var vm: TimelineViewModel
    private lateinit var app: MiniDropApp
    private lateinit var fileId: String
    private var gets = 0
    private var responseCode = 200
    private val content = "download content".toByteArray()

    @Before fun setup() = runBlocking {
        Dispatchers.setMain(Dispatchers.Unconfined)
        app = MiniDropApp()
        android.content.ContextWrapper::class.java.getDeclaredMethod("attachBaseContext", android.content.Context::class.java)
            .apply { isAccessible = true }.invoke(app, RuntimeEnvironment.getApplication())
        db = Room.inMemoryDatabaseBuilder(app, MiniDropDatabase::class.java).build()
        val dav = WebDavClient("https://test.invalid/MiniDrop/", "test", { "test" })
        val transport = OkHttpClient.Builder().addInterceptor { chain ->
            gets++
            Response.Builder().request(chain.request()).protocol(Protocol.HTTP_1_1)
                .code(responseCode).message("test").body(content.toResponseBody()).build()
        }.build()
        WebDavClient::class.java.getDeclaredField("http").apply { isAccessible = true }.set(dav, transport)
        val send = SendService(app, db, { 1024 }, { UUID.randomUUID().toString() }, { "Android" }, {})
        for ((name, value) in mapOf("db" to db, "settings" to SettingsStore(app), "download" to DownloadService(app, db, { dav }))) {
            MiniDropApp::class.java.getDeclaredField(name).apply { isAccessible = true }.set(app, value)
        }
        val source = File(app.cacheDir, "document.txt").apply { writeBytes(content) }
        val id = (send.enqueueFiles(listOf(StagedFile(source, "document.txt")), null) as SendResult.Ok).messageId
        val row = db.fileDao().getByMessage(id).single()
        fileId = row.fileId
        db.fileDao().upsert(row.copy(state = FileStates.REMOTE, sourcePath = null, mime = "text/plain"))
        vm = TimelineViewModel(app)
    }

    @After fun close() {
        vm.viewModelScope.cancel()
        db.close()
        Dispatchers.resetMain()
    }

    private suspend fun awaitAction() {
        @Suppress("UNCHECKED_CAST")
        val active = TimelineViewModel::class.java.getDeclaredField("activeFileActions")
            .apply { isAccessible = true }.get(vm) as Set<String>
        withTimeout(5000) { while (active.isNotEmpty()) delay(10) }
    }

    @Test fun oneClickDownloadsAndOpensThenSharesCachedFile() = runBlocking {
        vm.downloadFile(fileId)
        awaitAction()
        assertEquals(FileStates.CACHED, db.fileDao().getById(fileId)?.state)
        val shadow = shadowOf(app)
        val opened = shadow.nextStartedActivity
        assertNotNull(opened)
        assertEquals(Intent.ACTION_VIEW, opened.action)
        assertEquals("text/plain", opened.type)
        assertEquals("content", opened.data?.scheme)
        assertEquals(FileStates.CACHED, db.fileDao().getById(fileId)?.state)
        assertEquals(1, gets)
        vm.shareFile(fileId)
        awaitAction()
        val chooser = shadow.nextStartedActivity
        assertEquals(Intent.ACTION_CHOOSER, chooser.action)
        @Suppress("DEPRECATION")
        val shared = chooser.getParcelableExtra<Intent>(Intent.EXTRA_INTENT)!!
        assertEquals(Intent.ACTION_SEND, shared.action)
        assertEquals("text/plain", shared.type)
        assertTrue(shared.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
        @Suppress("DEPRECATION")
        val uri = shared.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)
        assertEquals(opened.data, uri)
        assertEquals(uri, shared.clipData?.getItemAt(0)?.uri)
        assertEquals(1, gets)
    }

    @Test fun failedDownloadDoesNotLaunchAnAppAndCanBeRetried() = runBlocking {
        responseCode = 503
        vm.downloadFile(fileId)
        awaitAction()
        assertNull(shadowOf(app).nextStartedActivity)
        assertEquals(FileStates.FAILED, db.fileDao().getById(fileId)?.state)
        responseCode = 200
        vm.downloadFile(fileId)
        awaitAction()
        assertEquals(Intent.ACTION_VIEW, shadowOf(app).nextStartedActivity.action)
    }

    @Test fun downloadedImageOpensBuiltInPreviewAndReusesCache() = runBlocking {
        val file = db.fileDao().getById(fileId)!!
        db.fileDao().upsert(file.copy(name = "photo.png", mime = "image/png"))
        vm.openFile(fileId)
        awaitAction()
        assertEquals("photo.png", vm.imagePreview.value?.name)
        assertEquals(db.fileDao().getById(fileId)?.cachePath, vm.imagePreview.value?.path)
        assertNull(shadowOf(app).nextStartedActivity)
        vm.dismissImagePreview()
        assertNull(vm.imagePreview.value)
        vm.openFile(fileId)
        awaitAction()
        assertEquals(1, gets)
    }

    @Test fun outgoingImagePreviewsSourceWithoutDownloading() = runBlocking {
        val source = File(app.cacheDir, "source.png").apply { writeBytes(content) }
        val file = db.fileDao().getById(fileId)!!
        db.fileDao().upsert(file.copy(name = "source.png", mime = "image/png", sourcePath = source.path))
        vm.openFile(fileId)
        awaitAction()
        assertEquals(source.path, vm.imagePreview.value?.path)
        assertEquals(0, gets)
    }
}

// Android FileProvider uses '/' for canonical containment checks, which cannot match Windows
// host paths under Robolectric. These tests exercise downloads and outgoing intents, not the provider.
@Implements(value = androidx.core.content.FileProvider::class, isInAndroidSdk = false)
class HostFileProvider {
    companion object {
        @JvmStatic
        @Implementation
        fun getUriForFile(context: android.content.Context, authority: String, file: File): Uri {
            check(file.exists())
            return Uri.Builder().scheme("content").authority(authority).appendPath("downloads").appendPath(file.name).build()
        }
    }
}
