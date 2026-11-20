package com.osfans.trime.link

import android.content.Context
import android.text.InputType
import android.util.AtomicFile
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import com.osfans.trime.core.data.UserDataManager
import com.osfans.trime.ime.core.TrimeInputMethodService
import java.io.File
import java.util.Locale

internal object LinkInputOptions {
    private const val FUZZY_PATCH = """patch:
  double_pinyin_mspy:
    __include: link_spellings:/double_pinyin_mspy_fuzzy
  rime_ice:
    __include: link_spellings:/rime_ice_fuzzy
"""
    private const val WINDOW = 32
    private val lastWord = Regex("(^|[ \\t\\r\\n])([A-Za-z]{3,24})$")
    private var corrections: Map<String, String>? = null

    fun configureFuzzy(enabled: Boolean) {
        val path = File(UserDataManager.userDataDir, "link_profile.custom.yaml")
        path.parentFile?.mkdirs()
        val file = AtomicFile(path)
        if (!enabled) {
            file.delete()
            check(!path.exists()) { "Unable to turn off fuzzy spelling" }
            return
        }
        val output = file.startWrite()
        try {
            output.write(FUZZY_PATCH.toByteArray(Charsets.UTF_8))
            file.finishWrite(output)
        } catch (error: Exception) {
            file.failWrite(output)
            throw error
        }
    }

    private fun dictionary(context: Context): Map<String, String> {
        corrections?.let { return it }
        val loaded =
            context.assets.open("shared/english_corrections.tsv").bufferedReader().useLines { lines ->
                lines.associate { it.substringBefore('\t') to it.substringAfter('\t') }
            }
        corrections = loaded
        return loaded
    }

    private fun allowedConnection(service: TrimeInputMethodService): InputConnection? {
        val settings = LinkSettings(service)
        if (!settings.unlocked || !settings.prefs.getBoolean("englishAutocorrect", false)) return null
        val editor = service.currentInputEditorInfo ?: return null
        val type = editor.inputType
        val variation = type and InputType.TYPE_MASK_VARIATION
        if ((type and InputType.TYPE_MASK_CLASS) != InputType.TYPE_CLASS_TEXT ||
            (variation != InputType.TYPE_TEXT_VARIATION_NORMAL &&
                variation != InputType.TYPE_TEXT_VARIATION_SHORT_MESSAGE &&
                variation != InputType.TYPE_TEXT_VARIATION_LONG_MESSAGE) ||
            (type and InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS) != 0 ||
            (editor.imeOptions and EditorInfo.IME_FLAG_NO_PERSONALIZED_LEARNING) != 0
        ) return null
        val connection = service.currentInputConnection ?: return null
        return connection.takeIf { it.getSelectedText(0).isNullOrEmpty() }
    }

    private fun correctedWord(context: Context, word: String): String? {
        val lowered = word.lowercase(Locale.ROOT)
        val replacement = runCatching { dictionary(context)[lowered] }.getOrNull() ?: return null
        return when {
            word == lowered -> replacement
            word == word.uppercase(Locale.ROOT) -> replacement.uppercase(Locale.ROOT)
            word.drop(1) == lowered.drop(1) -> replacement.replaceFirstChar { it.uppercaseChar() }
            else -> null
        }
    }

    fun correctedComposition(service: TrimeInputMethodService, raw: String): String? {
        val connection = allowedConnection(service) ?: return null
        val before = connection.getTextBeforeCursor(WINDOW, 0)?.toString()?.removeSuffix(raw) ?: return null
        if (before.lastOrNull()?.isWhitespace() == false) return null
        return correctedWord(service, raw)
    }

    fun correctEnglish(service: TrimeInputMethodService): Boolean {
        val connection = allowedConnection(service) ?: return false
        val before = connection.getTextBeforeCursor(WINDOW, 0)?.toString() ?: return false
        val match = lastWord.find(before) ?: return false
        val word = match.groupValues[2]
        val corrected = correctedWord(service, word) ?: return false
        connection.beginBatchEdit()
        try {
            if (!connection.deleteSurroundingText(word.length, 0)) return false
            if (connection.commitText("$corrected ", 1)) return true
            connection.commitText(word, 1)
            return false
        } finally {
            connection.endBatchEdit()
        }
    }
}
