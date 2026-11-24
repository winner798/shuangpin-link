package com.osfans.trime.link

import java.io.File
import java.util.Base64

// Link the actual clipboard codecs; the text size constant normally comes from LinkTransport.
internal const val CLIP_LIMIT = 64 * 1024

fun main(args: Array<String>) {
    val source = File(args[1]).readBytes()
    val info = ImageFormats.inspect(source)
    if (args[0] == "strip") {
        val output = ImageFormats.withoutMetadata(source)
        check(info == ImageFormats.inspect(output))
        File(args[2]).writeBytes(output)
        return
    }
    val event = ImageEvent(args[3], args[4].toLong(), source.size, args[2])
    val key = ImageWire.key(Base64.getEncoder().encodeToString(ByteArray(32) { it.toByte() }), event.sender)
    val result = ByteArray(source.size)
    for (index in 0 until event.count) {
        val file = File(args[5], "$index.txt")
        when (args[0]) {
            "seal" -> file.writeText(ImageWire.seal(key, event, index, source))
            "open" -> ImageWire.open(key, event, index, file.readText(), result)
            else -> error("Unknown fixture command.")
        }
    }
    if (args[0] == "open") check(result.contentEquals(source))
}
