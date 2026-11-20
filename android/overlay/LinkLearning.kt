package com.osfans.trime.link

import com.osfans.trime.TrimeApplication
import com.osfans.trime.core.data.UserDataManager
import timber.log.Timber
import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption

internal object LinkLearning {
    private val schemas = listOf("double_pinyin_mspy", "rime_ice")
    private val patchHeader = Regex("(?m)^patch:[ \\t]*(\\{\\}[ \\t]*)?(?:#.*)?$")
    private val nextSection = Regex("(?m)^[A-Za-z_][A-Za-z0-9_/-]*:")
    private val learningValue = Regex("(?m)^( +translator/enable_user_dict:[ \\t]*)(true|false)(?=[ \\t]*(?:#.*)?$)")
    private val indent = Regex("(?m)^( +)(?=[A-Za-z_\"'])")

    fun restorePreference(): Boolean = runCatching {
        val settings = LinkSettings(TrimeApplication.getInstance())
        settings.unlocked && settings.prefs.contains("learning") && configure(settings.prefs.getBoolean("learning", true))
    }.onFailure { Timber.w(it, "Unable to restore the personal dictionary setting") }.getOrDefault(false)

    fun configure(enabled: Boolean): Boolean {
        val directory = UserDataManager.userDataDir
        var changed = false
        for (schema in schemas) {
            val target = File(directory, "$schema.custom.yaml")
            if (enabled && !target.exists()) continue // Rime enables the user dictionary by default.
            val previous = if (target.isFile) target.readText() else ""
            val next = withLearning(previous, enabled)
            if (next == previous) continue
            directory.mkdirs()
            val scratch = Files.createTempFile(directory.toPath(), ".learning-", ".tmp")
            try {
                scratch.toFile().writeText(next)
                Files.move(scratch, target.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
            } finally {
                Files.deleteIfExists(scratch)
            }
            changed = true
        }
        return changed
    }

    private fun withLearning(source: String, enabled: Boolean): String {
        val header = patchHeader.find(source) ?: run {
            require(nextSection.findAll(source).none { it.value == "patch:" }) { "Unsupported schema patch format" }
            val start = if (source.isBlank()) "" else source.trimEnd() + "\n\n"
            return start + "patch:\n  translator/enable_user_dict: $enabled\n"
        }
        if (header.groups[1] != null) {
            val expanded = source.replaceRange(header.groups[1]!!.range, "")
            return withLearning(expanded, enabled)
        }
        val endOfHeader = source.indexOf('\n', header.range.last + 1)
        val bodyStart = if (endOfHeader >= 0) endOfHeader + 1 else source.length
        val bodyEnd = nextSection.find(source, bodyStart)?.range?.first ?: source.length
        val body = source.substring(bodyStart, bodyEnd)
        val matching = learningValue.find(body)
        if (matching != null) {
            val range = matching.groups[2]!!.range
            return source.replaceRange(bodyStart + range.first, bodyStart + range.last + 1, enabled.toString())
        }
        val spaces = indent.find(body)?.groups?.get(1)?.value ?: "  "
        val prefix = if (bodyEnd == 0 || source[bodyEnd - 1] == '\n') "" else "\n"
        return source.replaceRange(bodyEnd, bodyEnd, "$prefix${spaces}translator/enable_user_dict: $enabled\n")
    }
}
