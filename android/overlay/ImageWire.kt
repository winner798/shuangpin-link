package com.osfans.trime.link

import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

internal const val IMAGE_LIMIT = 8 * 1024 * 1024
internal const val IMAGE_CHUNK = 262144
private const val DOMAIN = "ime-sync/v3/image"

internal data class ImageEvent(val id: String, val at: Long, val size: Int, val sender: String) {
    init {
        require(ClipboardCodec.isValidId(id) && size in 1..IMAGE_LIMIT && at > 0)
        require(sender == ClipboardCodec.PEER || sender == ClipboardCodec.OWNER)
    }
    val count = (size + IMAGE_CHUNK - 1) / IMAGE_CHUNK
    fun length(i: Int): Int {
        require(i in 0 until count)
        return minOf(IMAGE_CHUNK, size - i * IMAGE_CHUNK)
    }
}

internal object ImageWire {
    private val random = SecureRandom()

    fun key(pairing: String, sender: String): SecretKey {
        val direction = when (sender) {
            ClipboardCodec.PEER -> "peer-to-owner"
            ClipboardCodec.OWNER -> "owner-to-peer"
            else -> error("Invalid direction")
        }
        val raw = java.util.Base64.getDecoder().decode(pairing)
        require(raw.size == 32)
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(DOMAIN.toByteArray(), "HmacSHA256"))
        mac.init(SecretKeySpec(mac.doFinal(raw), "HmacSHA256"))
        return SecretKeySpec(mac.doFinal(direction.toByteArray() + byteArrayOf(1)), "AES")
    }

    private fun cipher(mode: Int, key: SecretKey, event: ImageEvent, i: Int, nonce: ByteArray) =
        Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(mode, key, GCMParameterSpec(128, nonce))
            updateAAD("$DOMAIN\n${event.sender}\n${event.id}\n${event.at}\n${event.size}\n$i\n${event.count}".toByteArray())
        }

    fun seal(key: SecretKey, event: ImageEvent, i: Int, data: ByteArray): String {
        require(data.size == event.size)
        val nonce = ByteArray(12).also(random::nextBytes)
        val encrypted = cipher(Cipher.ENCRYPT_MODE, key, event, i, nonce).doFinal(data, i * IMAGE_CHUNK, event.length(i))
        return java.util.Base64.getEncoder().encodeToString(nonce + encrypted)
    }

    fun open(key: SecretKey, event: ImageEvent, i: Int, envelope: String, dest: ByteArray) {
        require(dest.size == event.size)
        val encrypted = java.util.Base64.getDecoder().decode(envelope)
        require(encrypted.size == event.length(i) + 28)
        cipher(Cipher.DECRYPT_MODE, key, event, i, encrypted.copyOf(12)).doFinal(encrypted, 12, encrypted.size - 12, dest, i * IMAGE_CHUNK)
    }
}
