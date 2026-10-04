package com.minidrop.app

import android.app.Application
import android.net.Uri
import com.minidrop.app.sync.Staging
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import java.io.File

@OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [28])
class StagingTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private val staging get() = File(context.filesDir, "staging")
    private lateinit var source: File
    private lateinit var sourceUri: Uri

    @Before fun setUp() {
        Dispatchers.setMain(Dispatchers.Unconfined)
        source = File(context.cacheDir, "分享 空格.txt").apply { writeText("文件内容") }
        sourceUri = Uri.Builder().scheme("content").authority("shared").appendPath(source.name).build()
        shadowOf(context.contentResolver).registerInputStreamSupplier(sourceUri) { source.inputStream() }
    }

    @After fun tearDown() {
        staging.listFiles()?.forEach { it.delete() }
        source.delete()
        Dispatchers.resetMain()
    }

    @Test fun copiesBeforeEnqueueAndPreservesFilename() = runBlocking {
        val result = Staging.copyAll(context, listOf(sourceUri), 1024) { _, _ -> }
        assertTrue(result.toString(), result is Staging.CopyResult.Ok)
        val copied = (result as Staging.CopyResult.Ok).files.single()
        assertEquals(source.name, copied.displayName)
        assertEquals(source.readText(), copied.file.readText())
    }

    @Test fun failureOnSecondFileRemovesFirstAndPartialFile() = runBlocking {
        val missing = Uri.parse("content://shared/missing.txt")
        shadowOf(context.contentResolver).registerInputStreamSupplier(missing) { throw java.io.FileNotFoundException() }
        val result = Staging.copyAll(context, listOf(sourceUri, missing), 1024) { _, _ -> }
        assertTrue(result is Staging.CopyResult.Failed)
        assertEquals(0, staging.listFiles().orEmpty().size)
    }

    @Test fun unknownReportedSizeStillEnforcesByteLimitAndCleansUp() = runBlocking {
        val result = Staging.copyAll(context, listOf(sourceUri), 1) { _, _ -> }
        assertTrue(result.toString(), result is Staging.CopyResult.TooLarge)
        assertEquals(0, staging.listFiles().orEmpty().size)
    }

    @Test fun cancellationAfterFirstFileRemovesStagedFiles() = runBlocking {
        var cancelled = false
        try {
            Staging.copyAll(context, listOf(sourceUri, sourceUri), 1024,
                isCancelled = { cancelled },
            ) { index, _ -> if (index == 1) cancelled = true }
            fail("Expected cancellation")
        } catch (_: CancellationException) {
            assertEquals(0, staging.listFiles().orEmpty().size)
        }
    }
}
