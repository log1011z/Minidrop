package com.minidrop.app

import android.app.Application
import androidx.room.Room
import com.minidrop.app.data.db.MiniDropDatabase
import com.minidrop.app.sync.SendService
import com.minidrop.app.sync.SendResult
import com.minidrop.app.sync.StagedFile
import com.minidrop.app.core.Limits
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.yield
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File
import java.util.UUID

@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [28])
class SendServiceTest {
    private lateinit var db: MiniDropDatabase
    private lateinit var source: File
    private val ready = CompletableDeferred<Unit>()
    private var wakes = 0
    private lateinit var send: SendService

    @Before fun setUp() {
        val context = RuntimeEnvironment.getApplication()
        db = Room.inMemoryDatabaseBuilder(context, MiniDropDatabase::class.java).build()
        source = File(context.cacheDir, "shared.txt").apply { writeText("file") }
        send = SendService(context, db, { 1024 }, { UUID.randomUUID().toString() }, { "Android" },
            trigger = { wakes++ }, awaitReady = { ready.await() })
    }

    @After fun tearDown() { db.close(); source.delete() }

    @Test fun textShareWaitsForInitializationAndQueuesWithoutFiles() = runBlocking {
        val pending = async { send.enqueueText("系统分享文字") }
        yield()
        assertFalse(pending.isCompleted)
        assertEquals(0L, db.messageDao().count())
        ready.complete(Unit)
        val result = pending.await() as SendResult.Ok
        assertEquals("系统分享文字", db.messageDao().getById(result.messageId)?.text)
        assertTrue(db.fileDao().getByMessage(result.messageId).isEmpty())
        assertNotNull(db.jobDao().getById(result.messageId))
        assertEquals(1, wakes)
    }

    @Test fun fileAndCaptionAreQueuedTogether() = runBlocking {
        ready.complete(Unit)
        val result = send.enqueueFiles(listOf(StagedFile(source, source.name)), "说明") as SendResult.Ok
        assertEquals("说明", db.messageDao().getById(result.messageId)?.text)
        assertEquals(source.absolutePath, db.fileDao().getByMessage(result.messageId).single().sourcePath)
        assertEquals(1, wakes)
    }

    @Test fun invalidCaptionOrFilenameDoesNotCreatePartialMessage() = runBlocking {
        ready.complete(Unit)
        assertTrue(send.enqueueFiles(listOf(StagedFile(source, source.name)),
            "a".repeat(Limits.MAX_TEXT_BYTES + 1)) is SendResult.Fail)
        assertTrue(send.enqueueFiles(listOf(StagedFile(source, "invalid/name")), null) is SendResult.Fail)
        assertEquals(0L, db.messageDao().count())
        assertEquals(0, wakes)
    }
}
