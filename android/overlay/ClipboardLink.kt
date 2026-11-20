package com.osfans.trime.link

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.SharedPreferences
import android.text.InputType
import android.view.inputmethod.EditorInfo
import com.osfans.trime.core.data.UserDataManager
import com.osfans.trime.daemon.RimeSession
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest
import java.util.UUID

internal fun sensitiveEditor(info: EditorInfo?): Boolean {
    val input = info ?: return false
    if (input.imeOptions and EditorInfo.IME_FLAG_NO_PERSONALIZED_LEARNING != 0) return true
    return when (input.inputType and InputType.TYPE_MASK_CLASS) {
        InputType.TYPE_CLASS_TEXT -> (input.inputType and InputType.TYPE_MASK_VARIATION) in setOf(
            InputType.TYPE_TEXT_VARIATION_PASSWORD, InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD,
            InputType.TYPE_TEXT_VARIATION_WEB_PASSWORD,
        )
        InputType.TYPE_CLASS_NUMBER -> input.inputType and InputType.TYPE_MASK_VARIATION == InputType.TYPE_NUMBER_VARIATION_PASSWORD
        else -> false
    }
}

internal class ClipboardLink(context: Context, private val scope: CoroutineScope, private val rime: RimeSession) {
    private val settings = LinkSettings(context)
    private val clipboard = requireNotNull(context.getSystemService(ClipboardManager::class.java))
    private val signal = Channel<Unit>(Channel.CONFLATED)
    private val dictionaryLock = Mutex()
    private var pending: Outgoing? = null
    private var run: Job? = null
    private var registered = false
    private var sensitive = false
    private var localChanges = 0L
    private var revision = 0L
    private var lastObserved: String? = null
    private val listener = ClipboardManager.OnPrimaryClipChangedListener { capture(force = true) }
    private val preferenceListener = SharedPreferences.OnSharedPreferenceChangeListener { _, key ->
        if (key in setOf("device", "paused", "share")) {
            if (key == "device") revision = 0
            refresh()
        }
        if (key == "syncNow") scope.launch { syncDictionarySafely() }
    }

    private data class Outgoing(val id: String, val text: String, val timestamp: Long)

    fun start() {
        if (registered || !settings.unlocked) return
        registered = true
        settings.prefs.registerOnSharedPreferenceChangeListener(preferenceListener)
        clipboard.addPrimaryClipChangedListener(listener)
        // Establish a baseline without sending clipboard contents from before pairing.
        lastObserved = currentText()
        refresh()
    }

    fun editor(info: EditorInfo?) {
        start()
        val wasSensitive = sensitive
        sensitive = sensitiveEditor(info)
        if (sensitive) pending = null
        if (registered && wasSensitive != sensitive) refresh()
    }

    fun resume(info: EditorInfo?) {
        editor(info)
        if (!sensitive) capture(force = false)
    }

    fun close() {
        run?.cancel()
        if (registered) {
            settings.prefs.unregisterOnSharedPreferenceChangeListener(preferenceListener)
            clipboard.removePrimaryClipChangedListener(listener)
        }
        signal.close()
    }

    private fun currentText(): String? = runCatching {
        val clip = clipboard.primaryClip ?: return null
        val description = clip.description
        if (description.label?.toString() == REMOTE_LABEL || clip.itemCount != 1 ||
            description.extras?.getBoolean("android.content.extra.IS_SENSITIVE", false) == true
        ) return null
        val item = clip.getItemAt(0)
        val text = item.text?.toString() ?: item.uri?.takeIf { it.scheme == "https" || it.scheme == "http" }?.toString()
        text?.takeIf { it.isNotEmpty() && it.toByteArray().size <= CLIP_LIMIT }
    }.getOrNull()

    private fun capture(force: Boolean) {
        if (sensitive || !registered) return
        val text = currentText()
        if (text == null) {
            if (runCatching { clipboard.primaryClipDescription?.label?.toString() }.getOrNull() != REMOTE_LABEL) {
                localChanges++
                pending = null
            }
            return
        }
        if (!force && text == lastObserved) return
        lastObserved = text
        localChanges++
        if (settings.prefs.getBoolean("paused", false) || settings.device() == null) return
        pending = Outgoing(UUID.randomUUID().toString().replace("-", ""), text, System.currentTimeMillis())
        signal.trySend(Unit)
    }

    private fun refresh() {
        run?.cancel()
        pending = null
        if (sensitive) return
        val device = settings.device() ?: return
        val client = LinkHttp(device.server, device.token)
        run = scope.launch {
            if (!settings.prefs.getBoolean("paused", false)) {
                launch { receive(client, device) }
                launch { upload(client, device) }
            }
            if (settings.prefs.getBoolean("share", true)) launch {
                delay(10000)
                while (isActive) {
                    syncDictionarySafely()
                    delay(10 * 60 * 1000L)
                }
            }
        }
    }

    private suspend fun receive(client: LinkHttp, device: LinkDevice) {
        while (true) {
            val started = localChanges
            try {
                val data = client.call("GET", "clip?after=$revision&wait=20") ?: continue
                if (!sensitive && pending == null && started == localChanges && !data.isNull("envelope")) {
                    val text = String(Sealing.open(device.key, "clip", data.getString("envelope"), CLIP_LIMIT), Charsets.UTF_8)
                    if (text.isNotEmpty()) clipboard.setPrimaryClip(ClipData.newPlainText(REMOTE_LABEL, text))
                }
                revision = data.getLong("revision")
            } catch (cancel: CancellationException) { throw cancel }
            catch (_: Exception) { delay(1500) }
        }
    }

    private suspend fun upload(client: LinkHttp, device: LinkDevice) {
        while (true) {
            val item = pending
            if (item == null) {
                signal.receive()
                continue
            }
            if (System.currentTimeMillis() - item.timestamp >= 24 * 60 * 60 * 1000L) {
                pending = null
                continue
            }
            try {
                val envelope = Sealing.seal(device.key, "clip", item.text.toByteArray())
                client.call("PUT", "clip", JSONObject().put("id", item.id).put("envelope", envelope))
                if (pending == item) pending = null
            } catch (cancel: CancellationException) { throw cancel }
            catch (_: Exception) { delay(1500) }
        }
    }

    private suspend fun syncDictionarySafely() {
        try { syncDictionaryOnce() }
        catch (cancel: CancellationException) { throw cancel }
        catch (_: Exception) { }
    }

    private suspend fun syncDictionaryOnce(): Unit = dictionaryLock.withLock {
        if (sensitive || !settings.prefs.getBoolean("share", true)) return@withLock
        val device = settings.device() ?: return@withLock
        val client = LinkHttp(device.server, device.token)
        val local = UserDataManager.userDataDir
        if (!rime.runOnReady { syncUserData() }) return@withLock
        val fileName = "rime_ice.userdb.txt"
        val snapshot = withContext(Dispatchers.IO) {
            val line = File(local, "installation.yaml").useLines { lines ->
                lines.firstOrNull { it.startsWith("installation_id:") }
            }
            val id = line?.substringAfter(':')?.trim()?.trim('"', '\'') ?: return@withContext null
            if (!id.matches(Regex("[a-zA-Z0-9_-]+"))) return@withContext null
            val file = File(local, "sync/$id/$fileName")
            if (file.isFile && file.length() in 1..DICT_LIMIT.toLong()) file.readBytes() else null
        }
        if (snapshot != null) {
            val hash = encode(MessageDigest.getInstance("SHA-256").digest(snapshot))
            if (hash != settings.prefs.getString("dictionarySent", null)) {
                val envelope = Sealing.seal(device.key, "dictionary", snapshot)
                client.call("PUT", "dictionary", JSONObject().put("version", System.currentTimeMillis()).put("envelope", envelope))
                settings.prefs.edit().putString("dictionarySent", hash).apply()
            }
        }
        val after = settings.prefs.getLong("dictionaryReceived", 0)
        val remote = client.call("GET", "dictionary?after=$after") ?: return@withLock
        val bytes = Sealing.open(device.key, "dictionary", remote.getString("envelope"), DICT_LIMIT)
        val destination = File(local, "sync/shuangpin-link-peer/$fileName")
        withContext(Dispatchers.IO) {
            destination.parentFile?.mkdirs()
            val temp = File(destination.parentFile, "$fileName.tmp")
            temp.writeBytes(bytes)
            check(temp.renameTo(destination))
        }
        if (rime.runOnReady { syncUserData() }) {
            withContext(Dispatchers.IO) { destination.delete() }
            settings.prefs.edit().putLong("dictionaryReceived", remote.getLong("version")).apply()
        }
    }
}
