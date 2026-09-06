package com.minidrop.app

import android.app.Application
import com.minidrop.app.data.MiniDropSettings
import com.minidrop.app.data.SettingsStore
import com.minidrop.app.data.db.MiniDropDatabase
import com.minidrop.app.sync.DeleteService
import com.minidrop.app.sync.DownloadService
import com.minidrop.app.sync.MaintenanceService
import com.minidrop.app.sync.SendService
import com.minidrop.app.sync.StartupRecovery
import com.minidrop.app.sync.SyncCoordinator
import com.minidrop.app.sync.UploadPump
import com.minidrop.app.sync.UploadPumpWorker
import com.minidrop.app.webdav.WebDavClient
import kotlinx.coroutines.launch

/** 组合根：全局唯一数据库 / WebDAV 工厂 / 各服务。 */
class MiniDropApp : Application() {

    lateinit var db: MiniDropDatabase
        private set
    lateinit var settings: SettingsStore
        private set
    lateinit var send: SendService
        private set
    lateinit var sync: SyncCoordinator
        private set
    lateinit var maintenance: MaintenanceService
        private set
    lateinit var delete: DeleteService
        private set
    lateinit var download: DownloadService
        private set
    lateinit var pump: UploadPump
        private set

    private lateinit var recovery: StartupRecovery

    /** 设置与密码的内存快照：WebDAV 工厂在非挂起上下文中读取。 */
    @Volatile
    var settingsSnapshot: MiniDropSettings =
        MiniDropSettings(SettingsStore.DEFAULT_ROOT_URL, "", "", "Android", 500L * 1024 * 1024, true)
        private set

    @Volatile
    var passwordSnapshot: String? = null
        private set

    override fun onCreate() {
        super.onCreate()

        // 崩溃日志：写入私有目录，下次启动在应用内展示（便于远程诊断）
        val defaultHandler = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, throwable ->
            try {
                java.io.File(filesDir, "crash-latest.txt").writeText(
                    buildString {
                        appendLine("time=" + java.text.SimpleDateFormat("yyyy-MM-dd HH:mm:ss", java.util.Locale.US).format(java.util.Date()))
                        appendLine("thread=" + thread.name)
                        appendLine(android.util.Log.getStackTraceString(throwable))
                    },
                )
            } catch (_: Exception) {
            }
            defaultHandler?.uncaughtException(thread, throwable)
        }

        db = MiniDropDatabase.build(this)
        settings = SettingsStore(this)
        recovery = StartupRecovery(this, db)

        pump = UploadPump(db, ::davFactory) { settingsSnapshot.maxFileBytes }
        sync = SyncCoordinator(db, ::davFactory) { settingsSnapshot.maxFileBytes }
        maintenance = MaintenanceService(db, ::davFactory, { settingsSnapshot.maxFileBytes }, sync)
        delete = DeleteService(db, ::davFactory, { settingsSnapshot.deviceId }, sync)
        download = DownloadService(this, db, ::davFactory) { settingsSnapshot.downloadTreeUri }
        send = SendService(
            context = this,
            db = db,
            maxFileBytes = { settingsSnapshot.maxFileBytes },
            deviceId = { settings.current().deviceId },
            deviceName = { settings.current().deviceName },
            trigger = { UploadPumpWorker.enqueueNow(this) },
        )

        // 设置流收集 + 凭据快照刷新
        appScope.launch {
            settings.settings.collect { settingsSnapshot = it }
        }
        appScope.launch {
            passwordSnapshot = settings.loadPassword()
        }

        // 启动恢复与本地清理（零网络），随后唤醒 pump 消费遗留队列
        appScope.launch {
            recovery.recover()
            recovery.localCleanup()
            UploadPumpWorker.enqueueNow(this@MiniDropApp)
        }
    }

    private fun davFactory(): WebDavClient {
        val snapshot = settingsSnapshot
        return WebDavClient(
            rootUrl = snapshot.rootUrl,
            account = snapshot.account,
            passwordProvider = { passwordSnapshot },
        )
    }

    /** 设置保存后调用：刷新密码快照，仅重排 AUTH failed job。 */
    suspend fun refreshCredentials() {
        passwordSnapshot = settings.loadPassword()
        settingsSnapshot = settings.current()
        db.jobDao().requeueAuthFailed(com.minidrop.app.data.db.nowUtcString())
        UploadPumpWorker.enqueueNow(this)
    }

    companion object {
        // 轻量应用级协程作用域
        val appScope: kotlinx.coroutines.CoroutineScope =
            kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.SupervisorJob() + kotlinx.coroutines.Dispatchers.IO)
    }
}

