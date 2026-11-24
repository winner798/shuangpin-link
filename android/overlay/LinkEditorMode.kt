package com.osfans.trime.link

/** All calls run on the engine dispatcher. Editor overrides never change the user's language. */
internal class LinkEditorMode {
    private var userMode: Boolean? = null
    private var beforeField: Boolean? = null
    private var layoutMode: Boolean? = null
    var privateField = false

    fun enter(asciiField: Boolean, current: Boolean): Boolean? {
        layoutMode = null
        if (asciiField) {
            if (beforeField == null) beforeField = userMode ?: current
            return true
        }
        val previous = userMode ?: beforeField
        beforeField = null
        return previous
    }

    fun select(value: Boolean): Boolean {
        if (privateField) return true
        layoutMode = null
        if (beforeField == null) userMode = value
        return value
    }

    fun layout(value: Boolean): Boolean {
        if (privateField) return true
        layoutMode = value
        return value
    }

    fun required(): Boolean? = when {
        privateField -> true
        layoutMode != null -> layoutMode
        beforeField != null -> null
        else -> userMode
    }
}
