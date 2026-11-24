package com.osfans.trime.link

import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.inputmethodservice.InputMethodService
import android.net.Uri
import android.os.PersistableBundle
import android.os.Process
import android.view.inputmethod.InputConnection
import android.view.inputmethod.InputContentInfo
import android.widget.Toast
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.util.UUID

internal data class ImageStatus(val label: String = "", val thumbnail: Bitmap? = null)
private data class ReadyImage(val uri: Uri, val mime: String, val expires: Long, val thumb: Bitmap?)

internal class LinkImages(
    private val ime: InputMethodService,
    private val settings: LinkSettings,
    private val scope: CoroutineScope,
    private val safe: () -> Boolean,
) {
    private val clipboard = requireNotNull(ime.getSystemService(ClipboardManager::class.java))
    private val state = MutableStateFlow(ImageStatus())
    val view = state.asStateFlow()
    private var pairing: ClipboardPairing? = null
    private var watcher: Job? = null
    private var transfer: Job? = null
    private var expiry: Job? = null
    private var retry: (() -> Unit)? = null
    private var ready: ReadyImage? = null
    private var incoming: ImageEvent? = null
    private var lastLocal = 0L
    private var observed = 0L
    private var generation = 0L
    private val enabled get() = settings.unlocked && settings.prefs.getBoolean("images", false)
    private fun allowed(p: ClipboardPairing) = pairing === p && enabled && safe() && settings.allowsClipboard(p.record)

    fun start(p: ClipboardPairing) {
        if (!enabled && settings.prefs.getBoolean("imageDisablePending", false)) {
            scope.launch { runCatching { settings.announceImages(p) } }
        }
        if (!enabled || !safe() || pairing === p) return
        stop()
        pairing = p
        watcher = scope.launch { watch(p) }
        val desc = clipboard.primaryClipDescription ?: return
        if (desc.label?.toString() == REMOTE_LABEL) {
            restorePreview(p, desc)
        } else if (desc.timestamp > maxOf(settings.prefs.getLong("imagesSince", Long.MAX_VALUE),
                settings.prefs.getLong("imageSent", 0), settings.prefs.getLong("historyAfter", 0))
            && ClipboardCodec.isFresh(desc.timestamp)) {
            changed(desc)
        } else {
            lastLocal = desc.timestamp
        }
    }

    fun stop() {
        pairing = null
        watcher?.cancel()
        reset()
        lastLocal = 0
        observed = 0
    }

    private fun reset() {
        generation++
        transfer?.cancel()
        transfer = null
        expiry?.cancel()
        ready = null
        retry = null
        incoming = null
        state.value = ImageStatus()
    }

    fun changed(desc: ClipDescription?) {
        val p = pairing ?: return
        if (!allowed(p)) return
        if (desc?.label?.toString() == REMOTE_LABEL) return
        if (desc != null && observed == desc.timestamp) return
        reset()
        observed = desc?.timestamp ?: 0L
        lastLocal = desc?.timestamp ?: System.currentTimeMillis()
        if (desc == null || desc.extras?.getBoolean(ClipDescription.EXTRA_IS_SENSITIVE) == true ||
            !desc.hasMimeType("image/*") || !ClipboardCodec.isFresh(desc.timestamp)) return
        val clip = clipboard.primaryClip ?: return
        if (clip.itemCount != 1 || clip.description.timestamp != desc.timestamp) return
        val uri = clip.getItemAt(0).uri ?: return
        if (uri.scheme != "content" || uri.authority == "${ime.packageName}.link-images") return
        upload(p, uri, desc.timestamp)
    }

    fun textReceived(at: Long) {
        if (at >= (incoming?.at ?: 0L)) reset()
    }

    fun acceptsText(at: Long): Boolean = pairing == null || at > lastLocal

    private fun upload(p: ClipboardPairing, uri: Uri, stamp: Long, at: Long = stamp) {
        val current = generation
        val client = LinkHttp(p.device.server, p.device.token)
        val id = UUID.randomUUID().toString().replace("-", "")
        retry = null
        state.value = ImageStatus("正在准备图片…")
        transfer = scope.launch {
            var started = false
            try {
                if (!settings.announceImages(p)) throw LinkHttpError(409)
                val keep = settings.prefs.getBoolean("imageMetadata", false)
                val data = runInterruptible(Dispatchers.IO) {
                    readImage(uri).let { original ->
                        ImageFormats.inspect(original)
                        if (keep) original else ImageFormats.withoutMetadata(original)
                    }
                }
                if (!allowed(p) || current != generation || !ClipboardCodec.isFresh(stamp)) return@launch
                val event = ImageEvent(id, at, data.size, ClipboardCodec.PEER)
                val key = ImageWire.key(p.device.key, event.sender)
                started = true
                client.call("PUT", "image/$id?sentAt=$at&size=${data.size}", responseLimit = 256, apiVersion = 3)
                for (i in 0 until event.count) {
                    if (!allowed(p) || generation != current || !ClipboardCodec.isFresh(stamp)) return@launch
                    val encrypted = withContext(Dispatchers.Default) { ImageWire.seal(key, event, i, data) }
                    state.value = ImageStatus("发送图片 ${i * 100 / event.count}% · 点此取消")
                    try { client.putImageChunk(event, i, encrypted) }
                    catch (e: java.io.IOException) {
                        if (e is LinkHttpError && e.code < 500) throw e
                        delay(500)
                        if (!allowed(p) || generation != current || !ClipboardCodec.isFresh(stamp)) return@launch
                        client.putImageChunk(event, i, encrypted)
                    }
                }
                started = false
                settings.prefs.edit().putLong("imageSent", stamp).apply()
                state.value = ImageStatus("图片已发送")
                scheduleHide(2500)
            } catch (c: CancellationException) { throw c }
            catch (error: Exception) {
                if (allowed(p) && current == generation) {
                    state.value = ImageStatus(errorMessage(error) + " · 点此重试")
                    retry = {
                        if (clipboard.primaryClipDescription?.timestamp == stamp && ClipboardCodec.isFresh(stamp)) {
                            lastLocal = maxOf(System.currentTimeMillis(), at + 1)
                            upload(p, uri, stamp, lastLocal)
                        } else reset()
                    }
                }
            } finally {
                if (started) scope.launch { runCatching { client.call("DELETE", "image/$id", responseLimit = 256, apiVersion = 3) } }
            }
        }
    }

    private fun readImage(uri: Uri): ByteArray {
        val own = uri.authority == "${ime.packageName}.link-images"
        if (!own) {
            require(uri.scheme == "content" && uri.authority != null)
            // A clip can name another provider. Read only through the URI grant supplied by Android.
            require(ime.checkUriPermission(uri, Process.myPid(), Process.myUid(), Intent.FLAG_GRANT_READ_URI_PERMISSION)
                == PackageManager.PERMISSION_GRANTED)
            require(ime.packageManager.resolveContentProvider(uri.authority!!, 0)?.applicationInfo?.uid != Process.myUid())
        }
        val source = requireNotNull(ime.contentResolver.openInputStream(uri))
        return source.use { stream ->
            val output = ByteArrayOutputStream()
            val buffer = ByteArray(32768)
            while (true) {
                val n = stream.read(buffer)
                if (n < 0) break
                require(n > 0 && output.size() + n <= IMAGE_LIMIT)
                output.write(buffer, 0, n)
            }
            output.toByteArray()
        }
    }

    private suspend fun watch(p: ClipboardPairing) {
        val client = LinkHttp(p.device.server, p.device.token)
        var revision = 0L
        var announced = false
        while (currentCoroutineContext().isActive && allowed(p)) {
            try {
                if (!announced) {
                    if (!settings.announceImages(p)) { delay(10_000); continue }
                    announced = true
                }
                val metadata = client.call("GET", "image?after=$revision&wait=20", responseLimit = 512, apiVersion = 3) ?: continue
                val next = metadata.getLong("revision")
                require(next > revision)
                revision = next
                val event = ImageEvent(metadata.getString("id"), metadata.getLong("sentAt"), metadata.getInt("size"), metadata.getString("sender"))
                if (event.sender != ClipboardCodec.OWNER || !canReceive(p, event)) continue
                reset()
                download(p, event)
            } catch (c: CancellationException) { throw c }
            catch (e: Exception) {
                announced = false
                delay(if (e is LinkHttpError && e.code == 404) 60_000 else 3500)
            }
        }
    }

    private fun canReceive(p: ClipboardPairing, e: ImageEvent): Boolean {
        if (!allowed(p) || !ClipboardCodec.isFresh(e.at) || e.at <= maxOf(lastLocal,
                settings.prefs.getLong("clipboardReceivedAtV2", 0))) return false
        val description = clipboard.primaryClipDescription ?: return true
        if (description.label?.toString() == REMOTE_LABEL && description.extras?.getString(RECEIPT) ==
            settings.prefs.getString(RECEIPT, "") && description.timestamp == settings.prefs.getLong("historyReceiptTime", -1)) return true
        return description.timestamp < e.at && description.extras?.getBoolean(ClipDescription.EXTRA_IS_SENSITIVE) != true
    }

    private fun download(p: ClipboardPairing, event: ImageEvent) {
        val current = generation
        val client = LinkHttp(p.device.server, p.device.token)
        retry = null
        incoming = event
        transfer = scope.launch {
            try {
                val bytes = ByteArray(event.size)
                val key = ImageWire.key(p.device.key, event.sender)
                for (i in 0 until event.count) {
                    if (current != generation || !canReceive(p, event)) { reset(); return@launch }
                    state.value = ImageStatus("接收图片 ${i * 100 / event.count}% · 点此取消")
                    val body = requireNotNull(client.call("GET", "image/${event.id}/$i", responseLimit = (IMAGE_CHUNK + 30) * 4 / 3 + 96, apiVersion = 3))
                    withContext(Dispatchers.Default) { ImageWire.open(key, event, i, body.getString("envelope"), bytes) }
                }
                val image = withContext(Dispatchers.IO) {
                    val info = ImageFormats.inspect(bytes)
                    val uri = LinkImageProvider.store(ime, event, info.mime, bytes)
                    ReadyImage(uri, info.mime, minOf(System.currentTimeMillis(), event.at) + ClipboardCodec.MAX_AGE_MS,
                        thumbnail(bytes))
                }
                if (current != generation || !canReceive(p, event)) { reset(); return@launch }
                if (!settings.recordClipboardReceipt(p.record, event.at)) { reset(); return@launch }
                val clip = ClipData(REMOTE_LABEL, arrayOf(image.mime), ClipData.Item(image.uri))
                clip.description.extras = PersistableBundle().apply {
                    putBoolean(ClipDescription.EXTRA_IS_SENSITIVE, true)
                    putString(RECEIPT, event.id)
                }
                clipboard.setPrimaryClip(clip)
                val desc = clipboard.primaryClipDescription
                if (desc?.extras?.getString(RECEIPT) == event.id) settings.prefs.edit().putString(RECEIPT, event.id)
                    .putLong("historyReceiptTime", desc.timestamp).apply()
                preview(image)
            } catch (c: CancellationException) { throw c }
            catch (error: Exception) {
                if (allowed(p) && current == generation) {
                    state.value = ImageStatus(errorMessage(error) + " · 点此重试")
                    retry = { if (canReceive(p, event)) download(p, event) else reset() }
                }
            }
        }
    }

    private fun restorePreview(p: ClipboardPairing, desc: ClipDescription) {
        if (!desc.hasMimeType("image/*") || desc.extras?.getString(RECEIPT) != settings.prefs.getString(RECEIPT, "") ||
            desc.timestamp != settings.prefs.getLong("historyReceiptTime", -1)) return
        val uri = clipboard.primaryClip?.getItemAt(0)?.uri ?: return
        if (uri.authority != "${ime.packageName}.link-images") return
        transfer = scope.launch {
            val image = withContext(Dispatchers.IO) {
                runCatching {
                    val bytes = readImage(uri)
                    ReadyImage(uri, ImageFormats.inspect(bytes).mime, settings.prefs.getLong("clipboardReceivedAtV2", 0) + ClipboardCodec.MAX_AGE_MS, thumbnail(bytes))
                }.getOrNull()
            }
            if (image != null && allowed(p) && clipboard.primaryClipDescription?.timestamp == desc.timestamp) preview(image)
        }
    }

    private fun thumbnail(bytes: ByteArray): Bitmap? {
        val opts = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, opts)
        var sample = 1
        while (maxOf(opts.outWidth, opts.outHeight) / sample > 128) sample *= 2
        return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample })
    }

    private fun preview(image: ReadyImage) {
        incoming = null
        ready = image
        state.value = ImageStatus("图片 · 点此粘贴", image.thumb)
        scheduleHide(image.expires - System.currentTimeMillis())
    }

    private fun scheduleHide(ms: Long) {
        expiry?.cancel()
        expiry = scope.launch {
            delay(ms.coerceAtLeast(0))
            state.value = ImageStatus()
            ready = null
            withContext(Dispatchers.IO) { LinkImageProvider.prune(ime) }
        }
    }

    fun dismiss(): Boolean {
        if (state.value.label.isBlank()) return false
        reset()
        return true
    }

    fun tap(): Boolean {
        if (state.value.label.isBlank()) return false
        val action = retry
        val image = ready
        when {
            !safe() || !enabled -> reset()
            action != null -> action()
            image != null && System.currentTimeMillis() < image.expires -> {
                val accepts = ime.currentInputEditorInfo?.contentMimeTypes.orEmpty().any { ClipDescription.compareMimeTypes(image.mime, it) }
                val success = accepts && runCatching { ime.currentInputConnection?.commitContent(
                    InputContentInfo(image.uri, ClipDescription("图片", arrayOf(image.mime)), null),
                    InputConnection.INPUT_CONTENT_GRANT_READ_URI_PERMISSION, null) == true }.getOrDefault(false)
                if (!success) Toast.makeText(ime, "此输入框不支持粘贴该图片，可到支持图片的应用粘贴。", Toast.LENGTH_SHORT).show()
            }
            else -> dismiss()
        }
        return true
    }

    private fun errorMessage(error: Exception) = when {
        error is LinkHttpError && error.code == 404 -> "请更新中继和电脑端"
        error is LinkHttpError && error.code == 409 -> "请在电脑端开启图片互传"
        error is IllegalArgumentException -> "图片格式或大小不支持"
        else -> "图片传输失败"
    }

    companion object {
        const val RECEIPT = "io.github.winner798.shuangpinlink.RECEIPT"
    }
}
