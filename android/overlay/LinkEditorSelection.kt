package com.osfans.trime.link

/** Tracks offsets only. Editors may combine or delay acknowledgements of IME edits. */
internal class LinkEditorSelection {
    private var caret = -1
    private var compositionStart = -1
    private val pendingCommits = ArrayDeque<Int>()

    fun start(first: Int, last: Int) {
        caret = minOf(first, last)
        compositionStart = -1
        pendingCommits.clear()
    }

    fun composing(length: Int) {
        if (compositionStart < 0 && length > 0) compositionStart = caret
        if (compositionStart >= 0) caret = compositionStart + length
        if (length == 0) compositionStart = -1
    }

    fun commit(length: Int) {
        val start = if (compositionStart >= 0) compositionStart else caret
        compositionStart = -1
        if (start < 0) return
        caret = start + length
        if (pendingCommits.size == 32) pendingCommits.removeFirst()
        pendingCommits.addLast(caret)
    }

    fun externallyMoved(first: Int, last: Int, region: Int, hasPreedit: Boolean, changed: Boolean): Boolean {
        if (region >= 0) {
            if (hasPreedit) compositionStart = region
            return false
        }
        val ack = if (first == last) pendingCommits.lastIndexOf(first) else -1
        if (ack >= 0) {
            repeat(ack + 1) { pendingCommits.removeFirst() }
            return false
        }
        if (!changed) return false
        val moved = caret >= 0 && (first != caret || last != caret)
        start(first, last)
        return moved
    }
}
