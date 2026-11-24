package com.osfans.trime.link

import android.content.Context
import android.os.UserManager
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONObject
import java.io.IOException
import java.net.URI
import java.security.KeyStore
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import javax.net.ssl.HttpsURLConnection

internal const val CLIP_LIMIT = 64 * 1024
internal const val DICT_LIMIT = 8 * 1024 * 1024
internal const val REMOTE_LABEL = "双拼互联 · 远端"
private const val KEY_ALIAS = "ime-sync-device-v1"

internal fun encode(data: ByteArray): String = Base64.encodeToString(data, Base64.NO_WRAP)
internal fun decode(data: String): ByteArray = Base64.decode(data, Base64.NO_WRAP)

internal object Sealing {
    private fun cipher(mode: Int, key: SecretKey, nonce: ByteArray, kind: String) =
        Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(mode, key, GCMParameterSpec(128, nonce))
            updateAAD("ime-sync/v1/$kind".toByteArray())
        }

    fun seal(key: String, kind: String, body: ByteArray): String {
        val nonce = ByteArray(12).also { SecureRandom().nextBytes(it) }
        return encode(nonce + cipher(Cipher.ENCRYPT_MODE, SecretKeySpec(decode(key), "AES"), nonce, kind).doFinal(body))
    }

    fun open(key: String, kind: String, envelope: String, limit: Int): ByteArray {
        val data = decode(envelope)
        require(data.size in 28..limit + 28)
        val nonce = data.copyOfRange(0, 12)
        return cipher(Cipher.DECRYPT_MODE, SecretKeySpec(decode(key), "AES"), nonce, kind).doFinal(data, 12, data.size - 12)
    }
}

internal class LinkHttp(server: String, private val token: String) {
    private val base = URI(server.trimEnd('/') + "/").also {
        require(it.scheme == "https" && it.host != null && it.userInfo == null && it.query == null && it.fragment == null)
    }

    fun connection(method: String, route: String, apiVersion: Int = 1): HttpsURLConnection {
        val conn = base.resolve("v$apiVersion/$route").toURL().openConnection() as HttpsURLConnection
        conn.requestMethod = method
        conn.connectTimeout = 7000
        conn.readTimeout = 26000
        conn.instanceFollowRedirects = false
        conn.setRequestProperty("Authorization", "Bearer $token")
        if (apiVersion >= 2) {
            conn.useCaches = false
            conn.setRequestProperty("Cache-Control", "no-store")
        }
        return conn
    }

    suspend fun call(
        method: String,
        route: String,
        body: JSONObject? = null,
        responseLimit: Int = (DICT_LIMIT + 128) * 4 / 3 + 256,
        apiVersion: Int = 1,
    ): JSONObject? = exchange(method, route, responseLimit, apiVersion, body?.let { { it.toString().toByteArray() } })

    suspend fun putImageChunk(event: ImageEvent, index: Int, envelope: String) {
        val length = event.length(index)
        require(envelope.length == ((length + 28 + 2) / 3) * 4)
        exchange("PUT", "image/${event.id}/$index", 256, 3) {
            // Base64 produced by ImageWire uses only this alphabet. Android's JSONStringer
            // can escape '/' and grow a full block beyond the protocol request limit.
            require(envelope.all { it in 'A'..'Z' || it in 'a'..'z' || it in '0'..'9' || it in "+/=" })
            "{\"envelope\":\"$envelope\"}".toByteArray(Charsets.US_ASCII)
        }
    }

    private suspend fun exchange(method: String, route: String, responseLimit: Int, apiVersion: Int,
                                 body: (() -> ByteArray)?): JSONObject? = withContext(Dispatchers.IO) {
        val conn = connection(method, route, apiVersion)
        val cancelConnection = if (apiVersion >= 3) launch(start = CoroutineStart.UNDISPATCHED) {
            try { awaitCancellation() } finally { conn.disconnect() }
        } else null
        try {
            if (body != null) {
                val bytes = body()
                conn.setRequestProperty("Content-Type", "application/json")
                conn.setFixedLengthStreamingMode(bytes.size)
                conn.doOutput = true
                conn.outputStream.use { it.write(bytes) }
            }
            when (val code = conn.responseCode) {
                204 -> null
                200, 201 -> conn.inputStream.use { input ->
                    val output = java.io.ByteArrayOutputStream()
                    val buffer = ByteArray(8192)
                    while (true) {
                        val size = input.read(buffer)
                        if (size < 0) break
                        if (output.size() + size > responseLimit) throw IOException("Response too large")
                        output.write(buffer, 0, size)
                    }
                    JSONObject(output.toString("UTF-8"))
                }
                else -> throw LinkHttpError(code)
            }
        } finally {
            cancelConnection?.cancel()
            conn.disconnect()
        }
    }
}

internal class LinkHttpError(val code: Int) : IOException("HTTP $code")
internal data class LinkDevice(val server: String, val token: String, val key: String)
internal data class ClipboardPairing(val record: String, val device: LinkDevice)

@Synchronized
internal fun localAesKey(alias: String): SecretKey {
    val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
    (store.getKey(alias, null) as? SecretKey)?.let { return it }
    return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
        init(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256).build())
        generateKey()
    }
}

internal class LinkSettings(context: Context) {
    private val app = context.applicationContext
    val unlocked: Boolean get() = app.getSystemService(UserManager::class.java)?.isUserUnlocked == true
    val prefs get() = app.getSharedPreferences("ime_sync", Context.MODE_PRIVATE)

    private fun storedDevice() = if (unlocked) prefs.getString("device", null) else null

    fun device(): LinkDevice? = storedDevice()?.let(::openDevice)

    fun clipboardPairing(): ClipboardPairing? = storedDevice()?.let { record ->
        openDevice(record)?.let { ClipboardPairing(record, it) }
    }

    fun allowsClipboard(record: String) = storedDevice() == record && !prefs.getBoolean("paused", false)

    // The settings screen and the keyboard share a process. Serialize announcements
    // and read the preference inside the lock so a late request cannot undo a toggle.
    suspend fun announceImages(pairing: ClipboardPairing): Boolean = imageAnnouncements.withLock {
        if (storedDevice() != pairing.record) return@withLock false
        val enabled = allowsClipboard(pairing.record) && prefs.getBoolean("images", false)
        val result = LinkHttp(pairing.device.server, pairing.device.token).call("PUT",
            "image/capability?enabled=${if (enabled) 1 else 0}", responseLimit = 256, apiVersion = 3)
        if (storedDevice() == pairing.record && !enabled && !prefs.getBoolean("images", false))
            prefs.edit().remove("imageDisablePending").apply()
        enabled && result?.optBoolean("peerEnabled") == true
    }

    private companion object { val imageAnnouncements = Mutex() }

    fun recordClipboardReceipt(record: String, sentAt: Long): Boolean =
        allowsClipboard(record) && sentAt > prefs.getLong("clipboardReceivedAtV2", 0) &&
            prefs.edit().putLong("clipboardReceivedAtV2", sentAt).commit()

    private fun openDevice(record: String): LinkDevice? = runCatching {
        val data = decode(record)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, localAesKey(KEY_ALIAS), GCMParameterSpec(128, data, 0, 12))
        val json = JSONObject(String(cipher.doFinal(data, 12, data.size - 12), Charsets.UTF_8))
        LinkDevice(json.getString("server"), json.getString("token"), json.getString("key"))
    }.getOrNull()

    fun save(device: LinkDevice) {
        val json = JSONObject().put("server", device.server).put("token", device.token).put("key", device.key)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, localAesKey(KEY_ALIAS)) }
        val record = encode(cipher.iv + cipher.doFinal(json.toString().toByteArray()))
        check(prefs.edit().remove("dictionaryReceived").remove("dictionarySent").remove("clipboardReceivedAtV2")
            .remove("images").remove("imageSent").remove("imagesSince")
            .putBoolean("share", true).putString("device", record).commit())
    }

    fun forget() {
        prefs.edit().remove("dictionaryReceived").remove("dictionarySent").remove("clipboardReceivedAtV2")
            .remove("images").remove("imageSent").remove("imagesSince").remove("device").apply()
    }
}
