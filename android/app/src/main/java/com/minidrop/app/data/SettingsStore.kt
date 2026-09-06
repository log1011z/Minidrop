package com.minidrop.app.data

import android.content.Context
import android.util.Base64
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import java.security.KeyStore
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

private val Context.dataStore by preferencesDataStore(name = "minidrop_settings")

/** 配置（§11.1）。应用密码不在此处，见 SecretStore。 */
data class MiniDropSettings(
    val rootUrl: String,
    val account: String,
    val deviceId: String,
    val deviceName: String,
    val maxFileBytes: Long,
    val notifyOnSend: Boolean,
    val downloadTreeUri: String? = null, // SAF 目录；null = 应用私有目录
) {
    val isConfigured: Boolean get() = account.isNotBlank()
}

class SettingsStore(private val context: Context) {

    private object Keys {
        val ROOT_URL = stringPreferencesKey("root_url")
        val ACCOUNT = stringPreferencesKey("account")
        val DEVICE_ID = stringPreferencesKey("device_id")
        val DEVICE_NAME = stringPreferencesKey("device_name")
        val MAX_FILE_BYTES = longPreferencesKey("max_file_bytes")
        val NOTIFY_ON_SEND = stringPreferencesKey("notify_on_send")
        val SECRET_BLOB = stringPreferencesKey("webdav_password_blob")
        val DOWNLOAD_TREE = stringPreferencesKey("download_tree_uri")
    }

    companion object {
        const val DEFAULT_ROOT_URL = "https://dav.jianguoyun.com/dav/MiniDrop/"
    }

    val settings: Flow<MiniDropSettings> = context.dataStore.data.map { p ->
        MiniDropSettings(
            rootUrl = p[Keys.ROOT_URL] ?: DEFAULT_ROOT_URL,
            account = p[Keys.ACCOUNT] ?: "",
            deviceId = p[Keys.DEVICE_ID] ?: java.util.UUID.randomUUID().toString(),
            deviceName = p[Keys.DEVICE_NAME] ?: android.os.Build.MODEL?.take(32)?.ifBlank { "Android" } ?: "Android",
            maxFileBytes = p[Keys.MAX_FILE_BYTES] ?: 500L * 1024 * 1024,
            notifyOnSend = p[Keys.NOTIFY_ON_SEND] != "false",
            downloadTreeUri = p[Keys.DOWNLOAD_TREE],
        )
    }

    suspend fun current(): MiniDropSettings = settings.first()

    suspend fun ensureDeviceId(): String {
        val existing = context.dataStore.data.first()[Keys.DEVICE_ID]
        if (!existing.isNullOrBlank()) return existing
        val id = java.util.UUID.randomUUID().toString()
        context.dataStore.edit { it[Keys.DEVICE_ID] = id }
        return id
    }

    suspend fun save(
        rootUrl: String,
        account: String,
        deviceName: String,
        maxFileBytes: Long,
        notifyOnSend: Boolean,
    ) {
        context.dataStore.edit { p ->
            p[Keys.ROOT_URL] = rootUrl
            p[Keys.ACCOUNT] = account
            p[Keys.DEVICE_NAME] = deviceName
            p[Keys.MAX_FILE_BYTES] = maxFileBytes
            p[Keys.NOTIFY_ON_SEND] = if (notifyOnSend) "true" else "false"
        }
    }

    /** 切换数据集（§11.3）：清空本地索引与游标（不删原始文件）。 */
    suspend fun clearForDatasetSwitch() {
        context.dataStore.edit { p ->
            p.remove(Keys.ROOT_URL)
            p.remove(Keys.ACCOUNT)
        }
    }

    /** 下载目录（SAF tree uri）；null = 应用私有目录。 */
    suspend fun saveDownloadTree(uri: String?) {
        context.dataStore.edit { p ->
            if (uri == null) p.remove(Keys.DOWNLOAD_TREE) else p[Keys.DOWNLOAD_TREE] = uri
        }
    }

    // ---------- 应用密码（Keystore AES-256-GCM，§10.1） ----------

    suspend fun savePassword(password: String) {
        if (password.isEmpty()) {
            context.dataStore.edit { it.remove(Keys.SECRET_BLOB) }
            return
        }
        val (iv, ct) = SecretStore.encrypt(password.toByteArray(Charsets.UTF_8))
        context.dataStore.edit {
            it[Keys.SECRET_BLOB] = Base64.encodeToString(iv + ct, Base64.NO_WRAP)
        }
    }

    suspend fun loadPassword(): String? {
        val blob = context.dataStore.data.first()[Keys.SECRET_BLOB] ?: return null
        val bytes = try {
            Base64.decode(blob, Base64.NO_WRAP)
        } catch (_: IllegalArgumentException) {
            return null
        }
        if (bytes.size < 13) return null
        val plain = SecretStore.decrypt(bytes.copyOfRange(0, 12), bytes.copyOfRange(12, bytes.size)) ?: return null
        return String(plain, Charsets.UTF_8)
    }

    suspend fun clearAllForDatasetSwitch() {
        context.dataStore.edit { p ->
            p.remove(Keys.ROOT_URL)
            p.remove(Keys.ACCOUNT)
            p.remove(Keys.SECRET_BLOB)
        }
    }
}

/**
 * Android Keystore 随机 AES-256-GCM 密钥；密文与 IV 放私有 DataStore。
 * 密钥不可用或认证标签失败返回 null（要求用户重新输入密码）。
 * 不使用已弃用的 EncryptedSharedPreferences。
 */
object SecretStore {
    private const val KEY_ALIAS = "minidrop_webdav"
    private const val ANDROID_KEYSTORE = "AndroidKeyStore"

    fun encrypt(plain: ByteArray): Pair<ByteArray, ByteArray> {
        val key = getOrCreateKey()
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key)
        return cipher.iv to cipher.doFinal(plain)
    }

    fun decrypt(iv: ByteArray, cipherText: ByteArray): ByteArray? = try {
        val key = getKey() ?: return null
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, iv))
        cipher.doFinal(cipherText)
    } catch (_: Exception) {
        null
    }

    private fun getKey(): SecretKey? = try {
        val ks = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (ks.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.secretKey
    } catch (_: Exception) {
        null
    }

    private fun getOrCreateKey(): SecretKey {
        getKey()?.let { return it }
        val generator = KeyGenerator.getInstance("AES", ANDROID_KEYSTORE)
        generator.init(
            android.security.keystore.KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                android.security.keystore.KeyProperties.PURPOSE_ENCRYPT or
                    android.security.keystore.KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(android.security.keystore.KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(android.security.keystore.KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .setRandomizedEncryptionRequired(true)
                .build(),
        )
        return generator.generateKey()
    }
}
