package com.osfans.trime.link

import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Path

private val shift = Path().apply {
    moveTo(3f, 11f)
    lineTo(12f, 3f)
    lineTo(21f, 11f)
    lineTo(16f, 11f)
    lineTo(16f, 20f)
    lineTo(8f, 20f)
    lineTo(8f, 11f)
    close()
}
private val backspace = Path().apply {
    moveTo(2.5f, 12f)
    lineTo(8f, 5.5f)
    lineTo(19f, 5.5f)
    quadTo(21.5f, 5.5f, 21.5f, 8f)
    lineTo(21.5f, 16f)
    quadTo(21.5f, 18.5f, 19f, 18.5f)
    lineTo(8f, 18.5f)
    close()
    moveTo(11f, 9f)
    lineTo(17f, 15f)
    moveTo(17f, 9f)
    lineTo(11f, 15f)
}
private val lineBreak = Path().apply {
    moveTo(20f, 5f)
    lineTo(20f, 10f)
    quadTo(20f, 14f, 16f, 14f)
    lineTo(4f, 14f)
    moveTo(9f, 9f)
    lineTo(4f, 14f)
    lineTo(9f, 19f)
}
private val stroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
    style = Paint.Style.STROKE
    strokeWidth = 1.65f
    strokeCap = Paint.Cap.ROUND
    strokeJoin = Paint.Join.ROUND
}

internal fun drawLinkKeyIcon(canvas: Canvas, label: String, x: Float, y: Float, size: Float, color: Int): Boolean {
    val path = when (label) {
        "⇧" -> shift
        "⌫" -> backspace
        "↵" -> lineBreak
        else -> return false
    }
    stroke.color = color
    val previous = canvas.save()
    canvas.translate(x - size / 2, y - size / 2)
    canvas.scale(size / 24f, size / 24f)
    canvas.drawPath(path, stroke)
    canvas.restoreToCount(previous)
    return true
}
