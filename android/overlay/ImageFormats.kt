package com.osfans.trime.link

import java.io.ByteArrayOutputStream
import java.io.IOException
import java.nio.ByteBuffer
import java.util.zip.CRC32

/** Inspect containers without allocating a bitmap. The metadata filter preserves compressed frames. */
internal object ImageFormats {
    const val MAX_BYTES = 8 * 1024 * 1024
    data class Info(val mime: String, val width: Int, val height: Int)

    fun inspect(bytes: ByteArray): Info = Reader(bytes, null).parse()

    fun withoutMetadata(bytes: ByteArray): ByteArray {
        val out = ByteArrayOutputStream()
        val reader = Reader(bytes, out)
        val info = reader.parse()
        val result = out.toByteArray()
        if (info.mime == "image/webp") {
            val size = result.size - 8
            for (i in 0..3) result[4+i] = (size ushr (8*i)).toByte()
            if (reader.keepWebpOrientation) result[20] = (result[20].toInt() or 8).toByte()
        }
        return result
    }

    private class Reader(val data: ByteArray, val out: ByteArrayOutputStream?) {
        var width = 0
        var height = 0
        var frames = 0
        var framePixels = 0L
        var keepWebpOrientation = false
        fun valid(ok: Boolean) {
            if (!ok) throw IOException("图片格式损坏、尺寸过大或包含不支持的结构。")
        }
        fun bounds(p: Int, n: Int) = valid(p >= 0 && n >= 0 && p <= data.size - n)
        fun u(p: Int) = data[p].toInt() and 255
        fun u16(p: Int, little: Boolean = false) = if (little) u(p) + (u(p+1) shl 8) else (u(p) shl 8) + u(p+1)
        fun u24(p: Int) = u16(p, true) + (u(p+2) shl 16)
        fun u32(p: Int, little: Boolean = false): Int {
            val n = if (little) u24(p) or (u(p+3) shl 24) else (u16(p) shl 16) or u16(p+2)
            valid(n >= 0)
            return n
        }
        fun at(p: Int, text: String) = p >= 0 && p <= data.size-text.length && text.indices.all { u(p+it) == text[it].code }
        fun copy(p: Int, n: Int) { out?.write(data,p,n) }
        // Rebuild only display orientation; no GPS/thumbnail/vendor offsets are followed.
        fun orientation(offset: Int, length: Int): ByteArray? {
            val header = if (length >= 6 && at(offset, "Exif\u0000\u0000")) 6 else 0
            val start = offset + header
            val size = length - header
            val little = at(start, "II")
            if (size < 8 || (!little && !at(start, "MM")) || u16(start+2,little) != 42) return null
            val directoryOffset = if (little) u24(start+4) or (u(start+7) shl 24)
                else (u16(start+4) shl 16) or u16(start+6)
            if (directoryOffset !in 8..size-2) return null
            val directory = start + directoryOffset
            val entries = u16(directory,little)
            if (entries > (size-directoryOffset-2)/12) return null
            for (i in 0 until entries) {
                val field = directory+2+12*i
                if (u16(field,little)!=274 || u16(field+2,little)!=3) continue
                val count = if (little) u24(field+4) or (u(field+7) shl 24) else (u16(field+4) shl 16) or u16(field+6)
                if (count != 1) continue
                val value = u16(field+8,little)
                if (value !in 2..8) return null
                return ByteBuffer.allocate(26).putShort(0x4d4d).putShort(42).putInt(8).putShort(1)
                    .putShort(274).putShort(3).putInt(1).putShort(value.toShort()).putShort(0).putInt(0).array()
            }
            return null
        }
        fun canvas(w: Int, h: Int) {
            valid(w in 1..16384 && h in 1..16384 && w.toLong()*h <= 32_000_000)
            if (width != 0) valid(w == width && h == height)
            width = w
            height = h
        }
        fun frame(x: Int, y: Int, w: Int, h: Int) {
            valid(w > 0 && h > 0 && x in 0..width-w && y in 0..height-h)
            framePixels += w.toLong()*h
            valid(++frames <= 512 && framePixels <= 256_000_000)
        }
        fun parse(): Info {
            valid(data.size in 12..MAX_BYTES)
            val mime = when {
                u(0)==137 && at(1,"PNG\r\n\u001a\n") -> { png(); "image/png" }
                u(0)==255 && u(1)==216 -> { jpeg(); "image/jpeg" }
                at(0,"GIF87a") || at(0,"GIF89a") -> { gif(); "image/gif" }
                at(0,"RIFF") && at(8,"WEBP") -> { webp(); "image/webp" }
                else -> throw IOException("图片互传支持 PNG、JPEG、WebP 和 GIF。")
            }
            return Info(mime,width,height)
        }
        fun png() {
            copy(0,8)
            var p = 8
            var chunks = 0
            var declared = 0
            var pixels = false
            val checksum = CRC32()
            while (true) {
                bounds(p,12)
                valid(++chunks <= 65536)
                val n = u32(p)
                val b = p+8
                bounds(b,n)
                bounds(b+n,4)
                val type = String(data,p+4,4,Charsets.US_ASCII)
                checksum.reset()
                checksum.update(data,p+4,n+4)
                val crc = (u16(b+n).toLong() shl 16) or u16(b+n+2).toLong()
                valid(checksum.value == crc)
                if (type=="IHDR") { valid(p==8 && n==13); canvas(u32(b),u32(b+4)) }
                valid(width>0)
                when (type) {
                    "IDAT" -> if (n>0) pixels=true
                    "acTL" -> { valid(n==8 && declared==0); declared=u32(b); valid(declared in 1..512) }
                    "fcTL" -> { valid(n==26 && declared>0); frame(u32(b+12),u32(b+16),u32(b+4),u32(b+8)) }
                }
                when (type) {
                    "IHDR","PLTE","IDAT","IEND","tRNS","sRGB","gAMA","cHRM","acTL","fcTL","fdAT" -> copy(p,n+12)
                    "eXIf" -> if (out != null) orientation(b,n)?.let { tiff ->
                        val chunk = ByteBuffer.allocate(12+tiff.size).putInt(tiff.size)
                            .put("eXIf".toByteArray(Charsets.US_ASCII)).put(tiff)
                        checksum.reset()
                        checksum.update(chunk.array(),4,4+tiff.size)
                        out.write(chunk.putInt(checksum.value.toInt()).array())
                    }
                    else -> valid(u(p+4) and 32 != 0)
                }
                p = b+n+4
                if (type=="IEND") { valid(n==0 && pixels && p==data.size && frames==declared); return }
            }
        }
        fun jpeg() {
            copy(0,2)
            var p = 2
            var scan = false
            while (true) {
                val marker = p
                bounds(p,2)
                valid(u(p++)==255)
                while (p<data.size && u(p)==255) p++
                bounds(p,1)
                val code = u(p++)
                if (code==217) { valid(scan && width>0 && p==data.size); copy(marker,p-marker); return }
                valid(code!=0 && code!=216 && code !in 208..215)
                if (code==1) { copy(marker,p-marker); continue }
                bounds(p,2)
                val n = u16(p)
                valid(n>=2)
                bounds(p,n)
                if (code in 192..207 && code!=196 && code!=200 && code!=204) {
                    valid(n>=8)
                    canvas(u16(p+5),u16(p+3))
                }
                if ((code !in 224..239 && code!=254) || (code==238 && n==14 && at(p+2,"Adobe")))
                    copy(marker,p+n-marker)
                else if (code==225 && out!=null && n>=8 && at(p+2,"Exif\u0000\u0000")) orientation(p+2,n-2)?.let { tiff ->
                    out.write(byteArrayOf(255.toByte(),225.toByte(),0,(tiff.size+8).toByte()))
                    out.write("Exif\u0000\u0000".toByteArray(Charsets.US_ASCII))
                    out.write(tiff)
                }
                p += n
                if (code!=218) continue
                valid(width>0)
                scan=true
                val start=p
                while (true) {
                    while (p<data.size && u(p)!=255) p++
                    val next=p
                    while (p<data.size && u(p)==255) p++
                    bounds(p,1)
                    if (u(p)==0 || u(p) in 208..215) { p++; continue }
                    copy(start,next-start)
                    p=next
                    break
                }
            }
        }
        fun gifBlocks(offset: Int): Int {
            var p=offset
            while (true) {
                bounds(p,1)
                val n=u(p++)
                if (n==0) return p
                bounds(p,n)
                p+=n
            }
        }
        fun gif() {
            bounds(0,13)
            canvas(u16(6,true),u16(8,true))
            var p=13
            if (u(10) and 128 != 0) p+=3*(2 shl (u(10) and 7))
            bounds(0,p)
            copy(0,p)
            while (true) {
                val start=p
                bounds(p,1)
                when (u(p++)) {
                    59 -> { valid(frames>0 && p==data.size); copy(start,1); return }
                    44 -> {
                        bounds(p,9)
                        frame(u16(p,true),u16(p+2,true),u16(p+4,true),u16(p+6,true))
                        val flags=u(p+8)
                        p+=9
                        if (flags and 128 != 0) p+=3*(2 shl (flags and 7))
                        bounds(p,2)
                        valid(u(p) in 2..8 && u(p+1)>0)
                        p=gifBlocks(p+1)
                        copy(start,p-start)
                    }
                    33 -> {
                        bounds(p,2)
                        val ext=u(p++)
                        val end=gifBlocks(p)
                        val control=ext==249 && u(p)==4 && end-p==6
                        val loop=ext==255 && end-p==17 && u(p)==11 && (at(p+1,"NETSCAPE2.0") || at(p+1,"ANIMEXTS1.0"))
                            && u(p+12)==3 && u(p+13)==1
                        if (control || loop) copy(start,end-start)
                        p=end
                    }
                    else -> valid(false)
                }
            }
        }
        fun webpSize(t: String,p: Int,n: Int): Pair<Int,Int> {
            if (t=="VP8L") {
                valid(n>=5 && u(p)==47)
                return Pair(1+(u16(p+1,true) and 16383),1+((u24(p+2) ushr 6) and 16383))
            }
            valid(n>=10 && u(p+3)==157 && u(p+4)==1 && u(p+5)==42)
            return Pair(u16(p+6,true) and 16383,u16(p+8,true) and 16383)
        }
        fun webp() {
            valid(u32(4,true)==data.size-8)
            copy(0,12)
            var pixels=false
            var chunks=0
            var p=12
            while (p<data.size) {
                bounds(p,8)
                valid(++chunks<=65536)
                val type=String(data,p,4,Charsets.US_ASCII)
                val n=u32(p+4,true)
                val b=p+8
                bounds(b,n)
                val end=b+n+(n and 1)
                bounds(p,end-p)
                when (type) {
                    "VP8X" -> {
                        valid(p==12 && n==10)
                        canvas(1+u24(b+4),1+u24(b+7))
                        copy(p,8)
                        out?.write(u(b) and 44.inv())
                        copy(b+1,end-b-1)
                    }
                    "VP8 ","VP8L" -> {
                        val (w,h)=webpSize(type,b,n)
                        canvas(w,h)
                        valid(!pixels && frames==0)
                        pixels=true
                        copy(p,end-p)
                    }
                    "ANMF" -> {
                        valid(n>=24 && !pixels)
                        val w=1+u24(b+6)
                        val h=1+u24(b+9)
                        frame(2*u24(b),2*u24(b+3),w,h)
                        var payload=false
                        var s=b+16
                        while (s<b+n) {
                            valid(b+n-s>=8)
                            val len=u32(s+4,true)
                            valid(len<=b+n-s-8 && (len and 1)<=b+n-s-8-len)
                            val t=String(data,s,4,Charsets.US_ASCII)
                            if (t=="VP8 " || t=="VP8L") {
                                valid(!payload && webpSize(t,s+8,len)==Pair(w,h))
                                payload=true
                            } else valid(t=="ALPH" && !payload)
                            s+=8+len+(len and 1)
                        }
                        valid(payload)
                        copy(p,end-p)
                    }
                    "ALPH","ANIM" -> copy(p,end-p)
                    "EXIF" -> if (out!=null && at(12,"VP8X")) orientation(b,n)?.let { tiff ->
                        keepWebpOrientation=true
                        out.write("EXIF".toByteArray(Charsets.US_ASCII))
                        out.write(byteArrayOf(tiff.size.toByte(),0,0,0))
                        out.write(tiff)
                    }
                }
                p=end
            }
            valid(width>0 && (pixels || frames>0))
        }
    }
}
