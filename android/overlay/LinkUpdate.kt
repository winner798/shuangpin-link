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
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.net.URI
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.Signature
import java.security.spec.X509EncodedKeySpec
import java.util.UUID
import javax.net.ssl.HttpsURLConnection

private const val MANIFEST_LIMIT = 16 * 1024
private const val APK_LIMIT = 100L * 1024 * 1024
private const val CACHE_LIFETIME = 24 * 60 * 60 * 1000L
private const val RELEASES_BASE = "https://github.com/winner798/shuangpin-link/releases/"
private const val MAX_REDIRECTS = 5
private val RELEASE_HOSTS = setOf("github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com")
private val REDIRECTS = setOf(301, 302, 303, 307, 308)
private val APK_NAME = Regex("[0-9a-f-]{36}\\.apk")
private val SHA256 = Regex("[0-9a-f]{64}")
private val VERSION_NAME = Regex("(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)")

private fun validVersionName(name: String) = name.length <= 40 && VERSION_NAME.matches(name)

private fun openReleaseFile(path: String): HttpsURLConnection {
    var address = URI(RELEASES_BASE + path)
    repeat(MAX_REDIRECTS + 1) { redirects ->
        val host = address.host?.lowercase() ?: throw InvalidLinkUpdate()
        if (!address.scheme.equals("https", ignoreCase = true) || address.userInfo != null ||
            (address.port != -1 && address.port != 443) || address.fragment != null || host !in RELEASE_HOSTS
        ) throw InvalidLinkUpdate()

        val connection = address.toURL().openConnection() as HttpsURLConnection
        var keepOpen = false
        try {
            connection.requestMethod = "GET"
            connection.connectTimeout = 7000
            connection.readTimeout = 26000
            connection.instanceFollowRedirects = false
            connection.useCaches = false
            connection.setRequestProperty("Accept-Encoding", "identity")
            val status = connection.responseCode
            if (status == 200) {
                keepOpen = true
                return connection
            }
            if (status !in REDIRECTS) throw LinkHttpError(status)
            val location = connection.getHeaderField("Location")
            if (redirects == MAX_REDIRECTS || location.isNullOrBlank()) throw InvalidLinkUpdate()
            address = try {
                address.resolve(location)
            } catch (_: IllegalArgumentException) {
                throw InvalidLinkUpdate()
            }
        } finally {
            if (!keepOpen) connection.disconnect()
        }
    }
    throw InvalidLinkUpdate()
}

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

    suspend fun check(): LinkRelease? = withContext(Dispatchers.IO) {
        val connection = openReleaseFile("latest/download/update-manifest.json")
        val response = try {
            if (connection.contentLengthLong > MANIFEST_LIMIT) throw InvalidLinkUpdate()
            connection.inputStream.use { input ->
                val body = ByteArrayOutputStream()
                val buffer = ByteArray(4096)
                while (true) {
                    ensureActive()
                    val received = input.read(buffer)
                    if (received < 0) break
                    if (body.size() + received > MANIFEST_LIMIT) throw InvalidLinkUpdate()
                    body.write(buffer, 0, received)
                }
                JSONObject(body.toString("UTF-8"))
            }
        } finally {
            connection.disconnect()
        }
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
        if (versionCode !in 1..2_100_000_000 || !validVersionName(versionName)) throw InvalidLinkUpdate()
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

    suspend fun download(release: LinkRelease): File {
        val file = File(directory, "${UUID.randomUUID()}.apk")
        var complete = false
        try {
            withContext(Dispatchers.IO) {
                if (!directory.mkdirs() && !directory.isDirectory) throw IOException("Cannot create update directory")
                directory.listFiles()?.filter { APK_NAME.matches(it.name) && expired(it) }?.forEach { it.delete() }
                if (!validVersionName(release.versionName)) throw InvalidLinkUpdate()
                val fileName = when (release.platform) {
                    "android-arm64-v8a" -> "ShuangpinLink-Android-arm64-v8a.apk"
                    "android-x86_64" -> "ShuangpinLink-Android-x86_64.apk"
                    else -> throw UnsupportedLinkUpdate()
                }
                val connection = openReleaseFile("download/v${release.versionName}/$fileName")
                try {
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
