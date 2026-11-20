package com.osfans.trime.link

import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Context
import android.os.PersistableBundle
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.util.UUID
import javax.crypto.SecretKey

internal class LinkClipboard(
    context: Context,
    private val settings: LinkSettings,
    private val scope: CoroutineScope,
    private val hasInputFocus: () -> Boolean,
) {
    private val clipboard = requireNotNull(context.getSystemService(ClipboardManager::class.java))
    private val changeListener = ClipboardManager.OnPrimaryClipChangedListener { capture() }
    private var session: Session? = null
    private var sentAt = 0L

    private class Session(val pairing: ClipboardPairing, openedAt: Long) {
        val client = LinkHttp(pairing.device.server, pairing.device.token)
        val outbound: SecretKey = ClipboardCodec.deriveKey(pairing.device.key, ClipboardCodec.PEER)
        val inbound: SecretKey = ClipboardCodec.deriveKey(pairing.device.key, ClipboardCodec.OWNER)
        val changes = Channel<Unit>(Channel.CONFLATED)
        var localChange = 0L
        var lastClipTimestamp = openedAt
        var privateClip = false
        var pending: ClipboardCodec.Packet? = null
        var sendJob: Job? = null
        var receiveJob: Job? = null
    }

    fun start() {
        if (!hasInputFocus()) {
            stop()
            return
        }
        if (session?.let(::isAuthorized) == true) return
        stop()
        val pairing = settings.clipboardPairing() ?: return
        if (!settings.allowsClipboard(pairing.record)) return
        val active = runCatching { Session(pairing, System.currentTimeMillis()) }.getOrNull() ?: return
        session = active
        // Observe future system copy events. Opening or pairing never reads the current clip.
        clipboard.addPrimaryClipChangedListener(changeListener)
        active.sendJob = scope.launch { upload(active) }
        active.receiveJob = scope.launch { receive(active) }
    }

    fun stop() {
        val previous = session ?: return
        session = null
        clipboard.removePrimaryClipChangedListener(changeListener)
        previous.pending = null
        previous.sendJob?.cancel()
        previous.receiveJob?.cancel()
        previous.changes.close()
    }

    private fun isAuthorized(active: Session): Boolean =
        session === active && hasInputFocus() && settings.allowsClipboard(active.pairing.record)

    private fun capture() {
        val active = session ?: return
        if (!isAuthorized(active)) return
        val description = runCatching { clipboard.primaryClipDescription }.getOrNull()
        if (description != null) {
            if (description.isRemote() || description.timestamp <= active.lastClipTimestamp) return
            active.lastClipTimestamp = description.timestamp
        }
        active.localChange++
        active.pending = null
        active.privateClip = description?.isSensitive() == true
        if (description == null || active.privateClip) return
        val clip = runCatching { clipboard.primaryClip }.getOrNull() ?: return
        val current = clip.description
        if (current.isSensitive()) active.privateClip = !current.isRemote()
        if (current.timestamp != description.timestamp || current.isSensitive() || current.isRemote() || clip.itemCount != 1) return
        val item = clip.getItemAt(0)
        val text = item.text?.toString() ?: item.uri?.takeIf { it.scheme == "https" || it.scheme == "http" }?.toString()
        if (text.isNullOrEmpty() || text.length > CLIP_LIMIT) return
        val bytes = text.toByteArray(Charsets.UTF_8)
        if (bytes.size > CLIP_LIMIT) return
        sentAt = maxOf(System.currentTimeMillis(), sentAt + 1)
        if (!ClipboardCodec.isFresh(sentAt)) return
        val id = UUID.randomUUID().toString().replace("-", "")
        active.pending = runCatching { ClipboardCodec.seal(active.outbound, ClipboardCodec.PEER, id, sentAt, bytes) }.getOrNull()
        active.changes.trySend(Unit)
    }

    private suspend fun upload(active: Session) {
        while (currentCoroutineContext().isActive && isAuthorized(active)) {
            val packet = active.pending
            if (packet == null) {
                if (active.changes.receiveCatching().isClosed) return
                continue
            }
            if (!ClipboardCodec.isFresh(packet.sentAt)) {
                active.pending = null
                continue
            }
            try {
                val body = JSONObject().put("id", packet.id).put("sentAt", packet.sentAt).put("envelope", packet.envelope)
                active.client.call("PUT", "clip", body, responseLimit = RESPONSE_LIMIT, apiVersion = 2)
                if (active.pending == packet) active.pending = null
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (error: Exception) {
                if (credentialsRejected(active, error)) return
                delay(1_500)
            }
        }
        if (session === active) stop()
    }

    private suspend fun receive(active: Session) {
        var revision = 0L
        while (currentCoroutineContext().isActive && isAuthorized(active)) {
            val localChange = active.localChange
            try {
                val data = active.client.call("GET", "clip?after=$revision&wait=20", responseLimit = RESPONSE_LIMIT, apiVersion = 2)
                if (data == null) {
                    delay(250)
                    continue
                }
                val nextRevision = data.integer("revision")
                if (nextRevision == null || nextRevision <= revision) {
                    delay(250)
                    continue
                }
                revision = nextRevision
                if (localChange != active.localChange || active.pending != null || active.privateClip || !isAuthorized(active)) continue
                receivePacket(active, data)
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (error: Exception) {
                if (credentialsRejected(active, error)) return
                delay(1_500)
            }
        }
        if (session === active) stop()
    }

    private fun receivePacket(active: Session, data: JSONObject) {
        if (data.opt("sender") != ClipboardCodec.OWNER) return
        val at = data.integer("sentAt") ?: return
        if (!ClipboardCodec.isFresh(at)) return
        val id = data.opt("id") as? String ?: return
        val envelope = data.opt("envelope") as? String ?: return
        val packet = ClipboardCodec.Packet(id, at, envelope)
        val text = runCatching { ClipboardCodec.open(active.inbound, ClipboardCodec.OWNER, packet) }.getOrNull() ?: return
        if (text.isEmpty() || !isAuthorized(active) || !ClipboardCodec.isFresh(at)) return
        // Persist the authenticated high-water mark before exposing the data to the OS.
        if (!settings.recordClipboardReceipt(active.pairing.record, at)) return
        val clip = ClipData.newPlainText(REMOTE_LABEL, text)
        clip.description.extras = PersistableBundle().apply { putBoolean(ClipDescription.EXTRA_IS_SENSITIVE, true) }
        clipboard.setPrimaryClip(clip)
    }

    private fun credentialsRejected(active: Session, error: Exception): Boolean {
        if (error !is LinkHttpError || (error.code != 401 && error.code != 409)) return false
        if (session === active) stop()
        return true
    }

    private fun ClipDescription.isRemote() = label?.toString() == REMOTE_LABEL

    private fun ClipDescription.isSensitive() = extras?.getBoolean(ClipDescription.EXTRA_IS_SENSITIVE, false) == true

    private fun JSONObject.integer(property: String): Long? {
        val raw = opt(property)
        return (raw as? Long) ?: (raw as? Int)?.toLong()
    }

    companion object {
        private const val RESPONSE_LIMIT = (CLIP_LIMIT + 128) * 4 / 3 + 256
        private const val LEGACY_DB = "clipboard.db"

        fun hasLegacyHistory(context: Context): Boolean = storageContexts(context).any { storage ->
            val db = storage.getDatabasePath(LEGACY_DB)
            listOf("", "-journal", "-shm", "-wal").any { suffix -> db.resolveSibling(db.name + suffix).exists() }
        }

        fun clearLegacyHistory(context: Context): Boolean {
            storageContexts(context).forEach { it.deleteDatabase(LEGACY_DB) }
            return !hasLegacyHistory(context)
        }

        private fun storageContexts(context: Context) = listOf(context, context.createDeviceProtectedStorageContext())
    }
}
