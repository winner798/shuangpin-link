package com.osfans.trime.link

import android.app.KeyguardManager
import android.content.ContentProvider
import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import java.io.File
import java.io.FileNotFoundException

/** Only URIs explicitly granted by the clipboard service or commitContent can be opened by another app. */
class LinkImageProvider : ContentProvider() {
    override fun onCreate(): Boolean {
        context?.let { prune(it) }
        return true
    }

    private fun resolve(uri: Uri): File {
        val app = context ?: throw FileNotFoundException()
        val settings = LinkSettings(app)
        val name = uri.lastPathSegment.orEmpty()
        if (uri.pathSegments.size != 1 || !fileName.matches(name) || !settings.unlocked ||
            !settings.prefs.getBoolean("images", false) || settings.prefs.getBoolean("paused", false) ||
            app.getSystemService(KeyguardManager::class.java)?.isDeviceLocked != false) throw FileNotFoundException()
        val file = File(directory(app), name)
        if (!valid(file)) throw FileNotFoundException()
        return file
    }

    override fun getType(uri: Uri): String? = when (resolve(uri).extension) {
        "jpg" -> "image/jpeg"
        "png" -> "image/png"
        "webp" -> "image/webp"
        "gif" -> "image/gif"
        else -> null
    }

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, selectionArgs: Array<out String>?, sortOrder: String?): Cursor {
        val file = resolve(uri)
        val columns = (projection?.toList() ?: listOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE))
            .filter { it == OpenableColumns.DISPLAY_NAME || it == OpenableColumns.SIZE }.toTypedArray()
        return MatrixCursor(columns, 1).apply {
            addRow(columns.map { if (it == OpenableColumns.DISPLAY_NAME) "图片.${file.extension}" else file.length() })
        }
    }

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        if (mode != "r") throw FileNotFoundException()
        return ParcelFileDescriptor.open(resolve(uri), ParcelFileDescriptor.MODE_READ_ONLY)
    }
    override fun insert(uri: Uri, values: ContentValues?): Uri? = throw UnsupportedOperationException()
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = throw UnsupportedOperationException()
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int = throw UnsupportedOperationException()

    companion object {
        private val fileName = Regex("[a-f0-9]{32}\\.(png|jpg|webp|gif)")
        private fun directory(app: Context) = File(app.cacheDir, "link-image-clips")
        private fun valid(file: File) = file.isFile && System.currentTimeMillis() - file.lastModified() in 0 until ClipboardCodec.MAX_AGE_MS
        @Synchronized internal fun prune(app: Context, all: Boolean = false) {
            directory(app).listFiles()?.forEach { if (all || !valid(it)) it.delete() }
        }
        @Synchronized internal fun store(app: Context, event: ImageEvent, mime: String, bytes: ByteArray): Uri {
            require(ClipboardCodec.isFresh(event.at))
            val ext = when (mime) {
                "image/jpeg" -> "jpg"
                "image/png" -> "png"
                "image/gif" -> "gif"
                "image/webp" -> "webp"
                else -> error("Unsupported image")
            }
            val folder = directory(app)
            folder.mkdirs()
            prune(app)
            val file = File(folder, "${event.id}.$ext")
            val temp = File.createTempFile("clip", ".part", folder)
            try {
                temp.writeBytes(bytes)
                check(temp.setLastModified(minOf(System.currentTimeMillis(), event.at)) && temp.renameTo(file))
            } finally { temp.delete() }
            folder.listFiles().orEmpty().filter { it != file }.sortedByDescending { it.lastModified() }.drop(1).forEach { it.delete() }
            return Uri.Builder().scheme("content").authority("${app.packageName}.link-images").appendPath(file.name).build()
        }
    }
}
