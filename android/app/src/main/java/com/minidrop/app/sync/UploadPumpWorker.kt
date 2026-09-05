package com.minidrop.app.sync

import com.minidrop.app.MiniDropApp
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.pm.ServiceInfo
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import java.util.concurrent.TimeUnit

/**
 * 唯一后台上传执行器（§5.5）：minidrop_upload_pump，KEEP，约束网络可用。
 * Worker 循环消费数据库队列；耗尽后若仍有未到期 retry_wait，则按最早到期时间自我重排一次。
 */
class UploadPumpWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {

    override suspend fun doWork(): Result {
        val app = applicationContext as MiniDropApp
        try {
            setForeground(foregroundInfo())
        } catch (_: Exception) {
            // 前台化失败（如通知权限）不阻塞上传
        }

        val processed = app.pump.drain()

        // 是否存在未到期 retry_wait：有 → 按最早到期自我重排
        val next = app.db.jobDao().nextRetryWaitAt(System.currentTimeMillis())
        if (next != null && next > System.currentTimeMillis()) {
            val delayMs = (next - System.currentTimeMillis()).coerceIn(1000L, TimeUnit.HOURS.toMillis(1))
            enqueueDelayed(applicationContext, delayMs)
        }
        return Result.success()
    }

    private fun foregroundInfo(): androidx.work.ForegroundInfo {
        val context = applicationContext
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= 26) {
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "上传", NotificationManager.IMPORTANCE_LOW),
            )
        }
        val notification: Notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_upload)
            .setContentTitle("MiniDrop")
            .setContentText("正在上传…")
            .setOngoing(true)
            .build()
        return if (Build.VERSION.SDK_INT >= 29) {
            androidx.work.ForegroundInfo(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            androidx.work.ForegroundInfo(NOTIFICATION_ID, notification)
        }
    }

    companion object {
        const val UNIQUE_NAME = "minidrop_upload_pump"
        private const val CHANNEL_ID = "minidrop_upload"
        private const val NOTIFICATION_ID = 1001

        fun enqueueNow(context: Context) {
            val request = OneTimeWorkRequestBuilder<UploadPumpWorker>()
                .setConstraints(
                    Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build(),
                )
                .build()
            WorkManager.getInstance(context)
                .enqueueUniqueWork(UNIQUE_NAME, ExistingWorkPolicy.KEEP, request)
        }

        private fun enqueueDelayed(context: Context, delayMs: Long) {
            val request = OneTimeWorkRequestBuilder<UploadPumpWorker>()
                .setConstraints(
                    Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build(),
                )
                .setInitialDelay(delayMs, TimeUnit.MILLISECONDS)
                .build()
            // REPLACE：当前 worker 即将结束，用延迟任务顶替
            WorkManager.getInstance(context)
                .enqueueUniqueWork(UNIQUE_NAME, ExistingWorkPolicy.REPLACE, request)
        }
    }
}
