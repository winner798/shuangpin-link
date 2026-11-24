package com.osfans.trime.link

import android.app.KeyguardManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.SharedPreferences
import android.inputmethodservice.InputMethodService
import android.os.PowerManager
import android.text.InputType
import android.view.inputmethod.EditorInfo
import com.osfans.trime.core.data.UserDataManager
import com.osfans.trime.daemon.RimeSession
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest

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

internal class ClipboardLink(private val ime: InputMethodService, private val scope: CoroutineScope, private val rime: RimeSession) {
    private val settings = LinkSettings(ime)
    private val dictionaryLock = Mutex()
    private var dictionaryJob: Job? = null
    private var registered = false
    private var sensitive = false
    private var inputViewActive = false
    private val power = ime.getSystemService(PowerManager::class.java)
    private val keyguard = ime.getSystemService(KeyguardManager::class.java)
    private val safeFocus = {
        val editor = ime.currentInputEditorInfo
        inputViewActive && ime.isInputViewShown && ime.currentInputConnection != null &&
            editor != null && editor.inputType != InputType.TYPE_NULL && !sensitiveEditor(editor) &&
            power?.isInteractive == true && keyguard?.isDeviceLocked == false
    }
    val history = LinkHistory(ime, settings, scope, safeFocus)
    private val clipboard = LinkClipboard(ime, settings, scope, history, safeFocus)
    private val screenLock = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            when (intent?.action) {
                Intent.ACTION_SCREEN_OFF -> {
                    protectHistory("historyLock", true)
                    suspendClipboard()
                }
                Intent.ACTION_USER_PRESENT -> protectHistory("historyLock", false)
            }
        }
    }
    private val preferenceListener = SharedPreferences.OnSharedPreferenceChangeListener { _, key ->
        if (key == "device" || key == "paused" || key == "history") {
            clipboard.stop()
            clipboard.start()
        }
        if (key == "device" || key == "share") refreshDictionarySync()
        if (key == "syncNow") scope.launch { syncDictionarySafely() }
    }

    fun start() {
        if (registered || !settings.unlocked) return
        registered = true
        ime.registerReceiver(screenLock, IntentFilter(Intent.ACTION_SCREEN_OFF).apply { addAction(Intent.ACTION_USER_PRESENT) })
        if (keyguard?.isDeviceLocked != false || power?.isInteractive != true) protectHistory("historyLock", true)
        settings.prefs.registerOnSharedPreferenceChangeListener(preferenceListener)
        refreshDictionarySync()
    }

    fun editor(info: EditorInfo?) {
        suspendClipboard()
        updateEditorPrivacy(info)
    }

    private fun updateEditorPrivacy(info: EditorInfo?) {
        val wasSensitive = sensitive
        sensitive = sensitiveEditor(info)
        if (info != null) protectHistory("historyPrivateEditor", sensitive)
        if (!registered) start()
        else if (wasSensitive != sensitive) refreshDictionarySync()
    }

    fun resume(info: EditorInfo?) {
        updateEditorPrivacy(info)
        inputViewActive = true
        windowShown()
    }

    private fun protectHistory(key: String, protected: Boolean) {
        if (!settings.unlocked || settings.prefs.getBoolean(key, false) == protected) return
        // Persist both ends of a private interval so process death cannot resurrect a clip from it.
        settings.prefs.edit().putBoolean(key, protected).putLong("historyAfter", System.currentTimeMillis()).commit()
    }

    fun windowShown() {
        if (keyguard?.isDeviceLocked == false && power?.isInteractive == true) protectHistory("historyLock", false)
        clipboard.start()
    }

    fun suspendClipboard() {
        inputViewActive = false
        clipboard.stop()
    }

    fun close() {
        suspendClipboard()
        dictionaryJob?.cancel()
        if (registered) {
            ime.unregisterReceiver(screenLock)
            settings.prefs.unregisterOnSharedPreferenceChangeListener(preferenceListener)
            registered = false
        }
    }

    private fun refreshDictionarySync() {
        dictionaryJob?.cancel()
        if (sensitive || !settings.prefs.getBoolean("share", true) || settings.device() == null) return
        dictionaryJob = scope.launch {
            delay(10_000)
            while (isActive) {
                syncDictionarySafely()
                delay(10 * 60 * 1000L)
            }
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
