package com.minidrop.app

import android.app.Application
import androidx.lifecycle.viewModelScope
import androidx.room.Room
import com.minidrop.app.core.Limits
import com.minidrop.app.data.SettingsStore
import com.minidrop.app.data.db.MiniDropDatabase
import com.minidrop.app.sync.SendService
import com.minidrop.app.sync.StagedFile
import android.net.Uri
import org.robolectric.Shadows.shadowOf
import java.io.File
import com.minidrop.app.ui.TimelineViewModel
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RuntimeEnvironment
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.util.UUID

@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [28])
class DraftRetentionTest {
    private lateinit var db: MiniDropDatabase
    private lateinit var vm: TimelineViewModel
    private lateinit var app: MiniDropApp
    private val ready = CompletableDeferred<Unit>()

    @Before fun setup() {
        Dispatchers.setMain(Dispatchers.Unconfined)
        // Attach the real application without starting recovery or background network work.
        app = MiniDropApp()
        android.content.ContextWrapper::class.java.getDeclaredMethod("attachBaseContext", android.content.Context::class.java)
            .apply { isAccessible = true }.invoke(app, RuntimeEnvironment.getApplication())
        db = Room.inMemoryDatabaseBuilder(app, MiniDropDatabase::class.java).build()
        val send = SendService(app, db, { 1024 }, { UUID.randomUUID().toString() }, { "Android" }, {}, { ready.await() })
        for ((name, value) in mapOf("db" to db, "settings" to SettingsStore(app), "send" to send)) {
            MiniDropApp::class.java.getDeclaredField(name).apply { isAccessible = true }.set(app, value)
        }
        vm = TimelineViewModel(app)
        @Suppress("UNCHECKED_CAST")
        val appReady = MiniDropApp::class.java.getDeclaredField("ready").apply { isAccessible = true }.get(app) as CompletableDeferred<Unit>
        appReady.complete(Unit)
    }

    @After fun close() {
        vm.viewModelScope.cancel()
        db.close()
        Dispatchers.resetMain()
    }

    @Suppress("UNCHECKED_CAST")
    private fun <T> state(name: String): MutableStateFlow<T> =
        TimelineViewModel::class.java.getDeclaredField(name).apply { isAccessible = true }.get(vm) as MutableStateFlow<T>

    private suspend fun awaitIdle() = withTimeout(5000) { state<Boolean>("sending").first { !it } }

    @Test fun invalidTextPreservesDraft() = runBlocking {
        val text = "a".repeat(Limits.MAX_TEXT_BYTES + 1)
        vm.onInputChange(text)
        vm.sendInput()
        awaitIdle()
        assertEquals(text, state<String>("input").value)
        assertEquals(0L, db.messageDao().count())
    }

    @Test fun successfulSendClearsOnlySubmittedDraft() = runBlocking {
        ready.complete(Unit)
        vm.onInputChange("submitted")
        vm.sendInput()
        awaitIdle()
        assertEquals("", state<String>("input").value)
        assertEquals("submitted", db.messageDao().timelinePage(1, 0).single().text)
    }

    @Test fun editingWhileSendWaitsPreservesNewDraftAndPreventsDuplicateSend() = runBlocking {
        vm.onInputChange("first draft")
        vm.sendInput()
        assertEquals("first draft", state<String>("input").value)
        vm.onInputChange("next draft")
        vm.sendInput()
        ready.complete(Unit)
        awaitIdle()
        assertEquals("next draft", state<String>("input").value)
        assertEquals(1L, db.messageDao().count())
        assertEquals("first draft", db.messageDao().timelinePage(1, 0).single().text)
    }

    @Test fun initializationFailureKeepsDraftAndAllowsRetry() = runBlocking {
        vm.onInputChange("keep me")
        vm.sendInput()
        ready.completeExceptionally(IllegalStateException("test failure"))
        awaitIdle()
        assertEquals("keep me", state<String>("input").value)
        assertEquals("发送失败，请重试", state<String>("status").value)
        assertEquals(0L, db.messageDao().count())
    }

    private suspend fun pickAttachment(): StagedFile {
        val source = File(app.cacheDir, "caption-file.txt").apply { writeText("attachment") }
        val uri = Uri.parse("content://shared/caption-file.txt")
        shadowOf(app.contentResolver).registerInputStreamSupplier(uri) { source.inputStream() }
        vm.onFilesPicked(listOf(uri))
        awaitIdle()
        return state<List<StagedFile>>("attachments").value.last()
    }

    @Test fun fileSelectionWaitsForSendAndIncludesCaption() = runBlocking {
        ready.complete(Unit)
        val attachment = pickAttachment()
        assertEquals(0L, db.messageDao().count())
        vm.onInputChange("文件说明")
        vm.sendInput()
        awaitIdle()
        val message = db.messageDao().timelinePage(1, 0).single()
        assertEquals("文件说明", message.text)
        assertEquals(attachment.file.absolutePath, db.fileDao().getByMessage(message.id).single().sourcePath)
        assertTrue(state<List<StagedFile>>("attachments").value.isEmpty())
        assertTrue(attachment.file.exists())
    }

    @Test fun attachmentOnlyCanSendWhileRefreshIsBusy() = runBlocking {
        ready.complete(Unit)
        pickAttachment()
        state<Boolean>("busy").value = true
        vm.sendInput()
        awaitIdle()
        assertEquals(1L, db.messageDao().count())
        assertTrue(state<Boolean>("busy").value)
        assertTrue(state<List<StagedFile>>("attachments").value.isEmpty())
    }

    @Test fun failedCaptionKeepsAttachmentsAndRemovingOneDoesNotSend() = runBlocking {
        ready.complete(Unit)
        val attachment = pickAttachment()
        vm.onInputChange("a".repeat(Limits.MAX_TEXT_BYTES + 1))
        vm.sendInput()
        awaitIdle()
        assertEquals(listOf(attachment), state<List<StagedFile>>("attachments").value)
        assertTrue(attachment.file.exists())
        vm.removeAttachment(attachment)
        assertFalse(attachment.file.exists())
        assertTrue(state<List<StagedFile>>("attachments").value.isEmpty())
        assertEquals(0L, db.messageDao().count())
    }

    @Test fun timelineRetryQueuesFailedJobsWithoutInterruptingActiveUpload() = runBlocking {
        ready.complete(Unit)
        vm.onInputChange("retry")
        vm.sendInput()
        awaitIdle()
        val id = db.messageDao().timelinePage(1, 0).single().id
        val queued = db.jobDao().getById(id)!!
        for (state in listOf("failed", "retry_wait")) {
            db.jobDao().insert(queued.copy(state = state, attempts = 5, nextAttemptAt = Long.MAX_VALUE, errorCode = "NETWORK"))
            assertEquals(1, db.jobDao().retryFromTimeline(id, "now"))
            assertEquals("queued", db.jobDao().getById(id)?.state)
            assertNull(db.jobDao().getById(id)?.nextAttemptAt)
            assertEquals(0, db.jobDao().getById(id)?.attempts)
            assertEquals(0, db.jobDao().retryFromTimeline(id, "now"))
        }
        db.jobDao().insert(queued.copy(state = "uploading"))
        assertEquals(0, db.jobDao().retryFromTimeline(id, "now"))
        assertEquals("uploading", db.jobDao().getById(id)?.state)
    }
}
