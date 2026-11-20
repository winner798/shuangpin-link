package com.osfans.trime.link

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.FileProvider
import com.osfans.trime.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.Signature
import java.security.spec.X509EncodedKeySpec
import java.util.UUID

private const val MANIFEST_LIMIT = 16 * 1024
private const val APK_LIMIT = 100L * 1024 * 1024
private const val CACHE_LIFETIME = 24 * 60 * 60 * 1000L
private val APK_NAME = Regex("[0-9a-f-]{36}\\.apk")
private val SHA256 = Regex("[0-9a-f]{64}")
private val VERSION_NAME = Regex("[0-9A-Za-z][0-9A-Za-z.+-]{0,63}")

class LinkUpdateFileProvider : FileProvider()

internal data class LinkRelease(
    val versionCode: Int,
    val versionName: String,
    val platform: String,
    val size: Long,
    val sha256: ByteArray,
)

internal class InvalidLinkUpdate : IOException("Invalid Shuangpin Link update")
internal class UnsupportedLinkUpdate : IOException("No APK for this Android device")

internal class LinkUpdates(private val context: Context) {
    private val directory = File(context.cacheDir, "link-updates")

    suspend fun check(client: LinkHttp): LinkRelease? {
        val response = client.call("GET", "update", responseLimit = MANIFEST_LIMIT) ?: throw InvalidLinkUpdate()
        return withContext(Dispatchers.IO) {
            val raw = decode(response.getString("manifest"))
            val signature = decode(response.getString("signature"))
            val keyBytes = context.assets.open("ime-update/public-key.spki.b64").bufferedReader().use {
                decode(it.readText().trim())
            }
            val key = KeyFactory.getInstance("RSA").generatePublic(X509EncodedKeySpec(keyBytes))
            val verified = Signature.getInstance("SHA256withRSA").run {
                initVerify(key)
                update(raw)
                verify(signature)
            }
            if (!verified) throw InvalidLinkUpdate()

            val manifest = JSONObject(String(raw, Charsets.UTF_8))
            val versionCode = manifest.opt("versionCode") as? Int ?: throw InvalidLinkUpdate()
            val versionName = manifest.opt("versionName") as? String ?: throw InvalidLinkUpdate()
            if (versionCode !in 1..2_100_000_000 || !VERSION_NAME.matches(versionName)) throw InvalidLinkUpdate()
            if (versionCode <= BuildConfig.VERSION_CODE) return@withContext null

            val files = manifest.getJSONObject("files")
            val platform = Build.SUPPORTED_ABIS.asSequence()
                .filter { it == "arm64-v8a" || it == "x86_64" }
                .map { "android-$it" }
                .firstOrNull { files.has(it) } ?: throw UnsupportedLinkUpdate()
            val entry = files.getJSONObject(platform)
            val size = (entry.opt("size") as? Int)?.toLong() ?: throw InvalidLinkUpdate()
            val hash = entry.opt("sha256") as? String ?: throw InvalidLinkUpdate()
            if (size !in 1..APK_LIMIT || !SHA256.matches(hash)) throw InvalidLinkUpdate()
            LinkRelease(versionCode, versionName, platform, size,
                ByteArray(32) { index -> hash.substring(2 * index, 2 * index + 2).toInt(16).toByte() })
        }
    }

    suspend fun download(client: LinkHttp, release: LinkRelease): File {
        val file = File(directory, "${UUID.randomUUID()}.apk")
        var complete = false
        try {
            withContext(Dispatchers.IO) {
                if (!directory.mkdirs() && !directory.isDirectory) throw IOException("Cannot create update directory")
                directory.listFiles()?.filter { APK_NAME.matches(it.name) && expired(it) }?.forEach { it.delete() }
                val connection = client.connection("GET", "update/files/${release.versionCode}/${release.platform}")
                connection.setRequestProperty("Accept-Encoding", "identity")
                try {
                    val code = connection.responseCode
                    if (code != 200) throw LinkHttpError(code)
                    val length = connection.contentLengthLong
                    if (length >= 0 && length != release.size) throw InvalidLinkUpdate()
                    val digest = MessageDigest.getInstance("SHA-256")
                    var received = 0L
                    connection.inputStream.use { input ->
                        file.outputStream().buffered().use { output ->
                            val buffer = ByteArray(64 * 1024)
                            while (true) {
                                ensureActive()
                                val count = input.read(buffer)
                                if (count < 0) break
                                received += count
                                if (received > release.size) throw InvalidLinkUpdate()
                                digest.update(buffer, 0, count)
                                output.write(buffer, 0, count)
                            }
                        }
                    }
                    if (received != release.size || !MessageDigest.isEqual(digest.digest(), release.sha256)) {
                        throw InvalidLinkUpdate()
                    }
                    verifyPackage(file, release)
                } finally {
                    connection.disconnect()
                }
            }
            complete = true
            return file
        } finally {
            if (!complete) file.delete()
        }
    }

    fun cachedApk(name: String): File? = if (APK_NAME.matches(name)) {
        File(directory, name).takeIf { it.isFile && !expired(it) }
    } else null

    @Suppress("DEPRECATION")
    private fun verifyPackage(file: File, release: LinkRelease) {
        val manager = context.packageManager
        val flags = PackageManager.GET_SIGNING_CERTIFICATES
        val downloaded = manager.getPackageArchiveInfo(file.absolutePath, flags) ?: throw InvalidLinkUpdate()
        val installed = manager.getPackageInfo(context.packageName, flags)
        val downloadSigners = downloaded.signingInfo?.apkContentsSigners?.toSet().orEmpty()
        val installedSigners = installed.signingInfo?.apkContentsSigners?.toSet().orEmpty()
        if (downloaded.packageName != installed.packageName || downloaded.longVersionCode != release.versionCode.toLong() ||
            downloadSigners.isEmpty() || downloadSigners != installedSigners
        ) throw InvalidLinkUpdate()
    }

    private fun expired(file: File) = System.currentTimeMillis() - file.lastModified() > CACHE_LIFETIME
}
