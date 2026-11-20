package com.osfans.trime.link

import java.nio.ByteBuffer
import java.security.SecureRandom
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

internal object ClipboardCodec {
    const val PROTOCOL = "ime-sync/v2/clip"
    const val OWNER = "owner"
    const val PEER = "peer"
    const val MAX_AGE_MS = 600_000L
    private const val MAX_FUTURE_MS = 120_000L
    private const val NONCE_BYTES = 12
    private const val TAG_BYTES = 16
    private val lowerHexId = Regex("[a-f0-9]{32}")
    private val random = SecureRandom()

    data class Packet(val id: String, val sentAt: Long, val envelope: String)

    fun isValidId(id: String) = lowerHexId.matches(id)

    fun isFresh(sentAt: Long, now: Long = System.currentTimeMillis()): Boolean =
        sentAt > 0 && sentAt > now - MAX_AGE_MS && sentAt <= now + MAX_FUTURE_MS

    fun deriveKey(pairingKey: String, sender: String): SecretKey {
        val direction = when (sender) {
            OWNER -> "owner-to-peer"
            PEER -> "peer-to-owner"
            else -> error("Invalid clipboard sender")
        }
        val input = Base64.getDecoder().decode(pairingKey)
        require(input.size == 32)
        val hmac = Mac.getInstance("HmacSHA256")
        hmac.init(SecretKeySpec(PROTOCOL.toByteArray(Charsets.UTF_8), "HmacSHA256"))
        val prk = hmac.doFinal(input)
        hmac.init(SecretKeySpec(prk, "HmacSHA256"))
        return SecretKeySpec(hmac.doFinal(direction.toByteArray(Charsets.UTF_8) + byteArrayOf(1)), "AES")
    }

    fun seal(key: SecretKey, sender: String, id: String, sentAt: Long, bytes: ByteArray): Packet {
        require(isValidId(id) && bytes.size in 1..CLIP_LIMIT)
        val nonce = ByteArray(NONCE_BYTES).also(random::nextBytes)
        val data = cipher(Cipher.ENCRYPT_MODE, key, nonce, sender, id, sentAt).doFinal(bytes)
        return Packet(id, sentAt, Base64.getEncoder().encodeToString(nonce + data))
    }

    fun open(key: SecretKey, sender: String, packet: Packet): String {
        require(isValidId(packet.id))
        val data = Base64.getDecoder().decode(packet.envelope)
        require(data.size in (NONCE_BYTES + TAG_BYTES + 1)..(CLIP_LIMIT + NONCE_BYTES + TAG_BYTES))
        val nonce = data.copyOfRange(0, NONCE_BYTES)
        val plain = cipher(Cipher.DECRYPT_MODE, key, nonce, sender, packet.id, packet.sentAt)
            .doFinal(data, NONCE_BYTES, data.size - NONCE_BYTES)
        return Charsets.UTF_8.newDecoder().decode(ByteBuffer.wrap(plain)).toString()
    }

    private fun cipher(mode: Int, key: SecretKey, nonce: ByteArray, sender: String, id: String, sentAt: Long) =
        Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(mode, key, GCMParameterSpec(TAG_BYTES * 8, nonce))
            updateAAD("$PROTOCOL\n$sender\n$id\n$sentAt".toByteArray(Charsets.UTF_8))
        }
}
