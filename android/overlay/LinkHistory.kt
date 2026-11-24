package com.osfans.trime.link

import android.content.Context
import android.util.AtomicFile
import android.view.View
import androidx.lifecycle.lifecycleScope
import com.osfans.trime.ime.core.TrimeInputMethodService
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileNotFoundException
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec

internal data class HistoryItem(val time: Long, val text: String)
internal data class HistorySnapshot(val through: Long = 0, val items: List<HistoryItem> = emptyList()) {
    fun record(item: HistoryItem, after: Long): HistorySnapshot {
        if (item.time <= maxOf(through, after)) return this
        return HistorySnapshot(item.time, (listOf(item) + items.filter { it.text != item.text }).take(50))
    }

    companion object {
        const val HOUR = 60 * 60 * 1000L
        fun canImport(time: Long, after: Long, now: Long) = time > after && now - time in 0L..HOUR
    }
}

internal data class HistoryView(
    val items: List<HistoryItem> = emptyList(),
    val preview: String? = null,
    val error: Boolean = false,
)

internal fun bindHistoryPreview(view: View, service: TrimeInputMethodService, update: (String?) -> Unit) {
    view.addOnAttachStateChangeListener(object : View.OnAttachStateChangeListener {
        private var updates: Job? = null
        override fun onViewAttachedToWindow(v: View) {
            updates = service.lifecycleScope.launch {
                service.linkHistory.view.map { it.preview }.distinctUntilChanged().collect { value ->
                    val excerpt = value?.take(80)?.replace('\n', ' ')?.replace('\r', ' ')
                    update(if (value != null && value.length > 80) "${excerpt}…" else excerpt)
                }
            }
        }
        override fun onViewDetachedFromWindow(v: View) {
            updates?.cancel()
            update(null)
        }
    })
}

/** A credential-protected, atomic snapshot. The timestamp also prevents resurrecting deleted clips. */
internal class LinkHistory(
    private val context: Context,
    private val settings: LinkSettings,
    private val scope: CoroutineScope,
    private val eligible: () -> Boolean,
) {
    private val file by lazy { historyFile(context) }
    private val mutableView = MutableStateFlow(HistoryView())
    val view = mutableView.asStateFlow()
    private var generation = 0L
    private var previewTime = 0L
    val enabled: Boolean get() = eligible() && settings.unlocked &&
        settings.prefs.getBoolean("history", true) && !settings.prefs.getBoolean("paused", false) &&
        !settings.prefs.getBoolean("historyPrivateEditor", false) && !settings.prefs.getBoolean("historyLock", false)

    fun hide() {
        generation++
        previewTime = 0
        mutableView.value = HistoryView()
    }

    fun refresh() = change { it }

    fun refreshIfExpired() {
        val oldest = view.value.items.lastOrNull() ?: return
        if (oldest.time <= System.currentTimeMillis() - RETENTION) refresh()
    }

    fun refreshOnOpen() {
        if (previewTime == 0L) refresh()
    }

    fun hidePreview() {
        previewTime = 0
        mutableView.value = mutableView.value.copy(preview = null)
    }

    fun showRecordedPreview(time: Long) {
        if (!canImport(time)) return
        previewTime = time
        refresh()
    }

    fun canImport(time: Long): Boolean = enabled &&
        HistorySnapshot.canImport(time, settings.prefs.getLong("historyAfter", 0), System.currentTimeMillis())

    fun accept(text: String, time: Long) {
        if (!canImport(time)) return
        previewTime = time
        change { snapshot ->
            snapshot.record(HistoryItem(time, text), settings.prefs.getLong("historyAfter", 0))
        }
    }

    fun remove(item: HistoryItem) = change { old -> old.copy(items = old.items.filter { it.time != item.time }) }

    fun clear() = change(erase = true) { HistorySnapshot(System.currentTimeMillis()) }

    private fun change(erase: Boolean = false, transform: (HistorySnapshot) -> HistorySnapshot) {
        if (!enabled) return
        val at = generation
        scope.launch {
            try {
                lock.withLock {
                    val updated = withContext(Dispatchers.IO) {
                        val previous = if (erase) HistorySnapshot() else read()
                        val cutoff = System.currentTimeMillis() - RETENTION
                        val next = transform(previous.copy(items = previous.items.filter { it.time > cutoff }))
                        if (erase || next != previous) write(next)
                        next
                    }
                    if (enabled && at == generation) {
                        val preview = updated.items.firstOrNull()?.takeIf { it.time == previewTime }?.text
                        mutableView.value = HistoryView(updated.items, preview)
                    }
                }
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (_: Exception) {
                if (at == generation) mutableView.value = HistoryView(error = true)
            }
        }
    }

    private fun read(): HistorySnapshot {
        val bytes = try { file.readFully() } catch (_: FileNotFoundException) { return HistorySnapshot() }
        require(bytes.size >= 28)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, localAesKey(KEY), GCMParameterSpec(128, bytes, 0, 12))
        val json = JSONObject(String(cipher.doFinal(bytes, 12, bytes.size - 12), Charsets.UTF_8))
        val array = json.getJSONArray("items")
        val items = List(array.length()) { index ->
            val item = array.getJSONArray(index)
            HistoryItem(item.getLong(0), item.getString(1))
        }
        return HistorySnapshot(json.getLong("through"), items)
    }

    private fun write(next: HistorySnapshot) {
        val rows = JSONArray()
        for (entry in next.items) rows.put(JSONArray().put(entry.time).put(entry.text))
        val payload = JSONObject().put("through", next.through).put("items", rows)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, localAesKey(KEY))
        val sealed = cipher.doFinal(payload.toString().toByteArray(Charsets.UTF_8))
        val stream = file.startWrite()
        try {
            stream.write(cipher.iv)
            stream.write(sealed)
            file.finishWrite(stream)
        } catch (error: Exception) {
            file.failWrite(stream)
            throw error
        }
    }

    companion object {
        private const val KEY = "shuangpin-local-clipboard-v1"
        private const val RETENTION = 7 * 24 * HistorySnapshot.HOUR
        private val lock = Mutex()
        private fun historyFile(context: Context) = AtomicFile(File(context.noBackupFilesDir, "clipboard-history-v1.bin"))

        suspend fun clearLocal(context: Context) = lock.withLock {
            withContext(Dispatchers.IO) {
                val settings = LinkSettings(context)
                check(settings.unlocked)
                check(settings.prefs.edit().putLong("historyAfter", System.currentTimeMillis()).commit())
                val history = historyFile(context)
                history.delete()
                check(!history.baseFile.exists())
            }
        }
    }
}
