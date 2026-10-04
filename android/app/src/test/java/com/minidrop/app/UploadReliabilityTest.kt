package com.minidrop.app

import android.app.Application
import androidx.room.Room
import com.minidrop.app.core.JobStates
import com.minidrop.app.core.MessageJson
import com.minidrop.app.core.Ulid
import com.minidrop.app.core.UlidClock
import com.minidrop.app.data.db.MiniDropDatabase
import com.minidrop.app.data.db.nowUtcString
import com.minidrop.app.sync.*
import com.minidrop.app.webdav.WebDavClient
import kotlinx.coroutines.runBlocking
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import okio.Buffer
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File
import java.security.MessageDigest
import java.util.UUID

@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [28])
class UploadReliabilityTest {
    private lateinit var db: MiniDropDatabase
    private lateinit var send: SendService
    private val sources = mutableListOf<File>()
    private val context get() = RuntimeEnvironment.getApplication()

    @Before fun setup() {
        db = Room.inMemoryDatabaseBuilder(context, MiniDropDatabase::class.java).build()
        send = SendService(context, db, { 1024 }, { UUID.randomUUID().toString() }, { "Android" }, {})
    }

    @After fun close() { db.close(); sources.forEach { it.delete() } }

    private fun source(old: Boolean = false): File =
        File(File(context.filesDir, "staging").apply { mkdirs() }, UUID.randomUUID().toString()).apply {
            writeText("shared file $name")
            if (old) assertTrue(setLastModified(System.currentTimeMillis() - 49L * 3600 * 1000))
            sources.add(this)
        }

    @Test fun cleanupPreservesSourcesForEveryOutstandingJobState() = runBlocking {
        for (state in listOf(JobStates.QUEUED, JobStates.UPLOADING, JobStates.RETRY_WAIT, JobStates.FAILED)) {
            val file = source(old = true)
            val result = send.enqueueFiles(listOf(StagedFile(file, "shared.txt")), null) as SendResult.Ok
            db.jobDao().insert(db.jobDao().getById(result.messageId)!!.copy(state = state))
            StartupRecovery(context, db).localCleanup()
            assertTrue("Source lost for $state", file.exists())
            assertNotNull(db.jobDao().getById(result.messageId))
        }
    }

    @Test fun cleanupRemovesOnlyOldOrphansAndPreservesPublishedSources() = runBlocking {
        val referenced = source(old = true)
        val result = send.enqueueFiles(listOf(StagedFile(referenced, "shared.txt")), null) as SendResult.Ok
        db.jobDao().delete(result.messageId)
        val orphan = source(old = true)
        val fresh = source()
        StartupRecovery(context, db).localCleanup()
        assertTrue(referenced.exists())
        assertFalse(orphan.exists())
        assertTrue(fresh.exists())
    }

    @Test fun messageRetentionDoesNotRemovePendingUpload() = runBlocking {
        val file = source(old = true)
        val result = send.enqueueFiles(listOf(StagedFile(file, "shared.txt")), null) as SendResult.Ok
        val id = Ulid.newUlid(System.currentTimeMillis() - 100L * 24 * 3600 * 1000)
        val message = db.messageDao().getById(result.messageId)!!
        val row = db.fileDao().getByMessage(result.messageId).single()
        val job = db.jobDao().getById(result.messageId)!!
        db.messageDao().delete(result.messageId)
        db.messageDao().upsert(message.copy(id = id, remoteMonth = UlidClock.monthOf(id)))
        db.fileDao().upsert(row.copy(messageId = id))
        db.jobDao().insert(job.copy(messageId = id))
        StartupRecovery(context, db).localCleanup()
        assertNotNull(db.jobDao().getById(id))
        assertTrue(file.exists())
    }

    @Test fun firstMultiFileUploadPublishesChecksums() = runBlocking { checkPublishedChecksums(false) }

    @Test fun retryingMessagePublicationRetainsChecksumsWithoutReuploadingFiles() = runBlocking {
        checkPublishedChecksums(true)
    }

    private suspend fun checkPublishedChecksums(failFirstPublication: Boolean) {
        val first = source()
        val second = source()
        val result = send.enqueueFiles(listOf(StagedFile(first, "first.txt"), StagedFile(second, "second.txt")), null) as SendResult.Ok
        val puts = mutableMapOf<String, ByteArray>()
        var filePuts = 0
        var jsonPuts = 0
        val client = WebDavClient("https://test.invalid/MiniDrop/", "test", { "test" })
        val transport = OkHttpClient.Builder().addInterceptor { chain ->
            val request = chain.request()
            var status = 201
            if (request.method == "PUT") {
                val buffer = Buffer()
                request.body!!.writeTo(buffer)
                puts[request.url.encodedPath] = buffer.readByteArray()
                if (request.url.encodedPath.endsWith(".json")) {
                    jsonPuts++
                    if (failFirstPublication && jsonPuts == 1) status = 503
                } else filePuts++
            }
            Response.Builder().request(request).protocol(Protocol.HTTP_1_1).code(status).message("test")
                .body(ByteArray(0).toResponseBody()).build()
        }.build()
        WebDavClient::class.java.getDeclaredField("http").apply { isAccessible = true }.set(client, transport)
        val pump = UploadPump(db, { client }, { 1024 })
        pump.drain()
        if (failFirstPublication) {
            assertEquals(JobStates.RETRY_WAIT, db.jobDao().getById(result.messageId)!!.state)
            db.jobDao().requeue(result.messageId, nowUtcString())
            pump.drain()
        }
        assertNull(db.jobDao().getById(result.messageId))
        assertEquals(2, filePuts)
        val json = puts.entries.single { it.key.endsWith(".json") }.value
        val published = MessageJson.parse(json, result.messageId)
        assertTrue(published.ok)
        val expected = listOf(first, second).map { file ->
            MessageDigest.getInstance("SHA-256").digest(file.readBytes()).joinToString("") { "%02x".format(it) }
        }
        assertEquals(expected, published.message!!.files.map { it.sha256 })
        assertEquals(expected, db.fileDao().getByMessage(result.messageId).map { it.sha256 })
    }
}
