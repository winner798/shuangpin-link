package com.osfans.trime.link

import android.graphics.Typeface
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.lifecycle.lifecycleScope
import com.osfans.trime.data.theme.ThemeScope
import com.osfans.trime.ime.core.TrimeInputMethodService
import com.osfans.trime.ime.keyboard.KeyboardWindow
import com.osfans.trime.ime.window.BoardWindow
import com.osfans.trime.ime.window.BoardWindowManager
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import org.kodein.di.DI
import org.kodein.di.instance
import kotlin.math.roundToInt

internal class HistoryWindow(di: DI) : BoardWindow.BarBoardWindow(di) {
    override val title = "剪贴板历史"
    private val service: TrimeInputMethodService by instance()
    private val manager: BoardWindowManager by instance()
    private val theme: ThemeScope by instance()
    private val history get() = service.linkHistory
    private lateinit var panel: LinearLayout
    private var updates: Job? = null
    private var confirming = false
    private var expanded: Long? = null

    override fun onCreateView(): View = LinearLayout(context).also {
        panel = it
        it.orientation = LinearLayout.VERTICAL
        it.setPadding(dp(8), 0, dp(8), 0)
    }

    override fun onAttached() {
        history.refresh()
        updates = service.lifecycleScope.launch { history.view.collect(::render) }
    }

    override fun onDetached() {
        updates?.cancel()
        panel.removeAllViews()
    }

    override fun refreshColors() = render(history.view.value)

    private fun render(state: HistoryView) {
        panel.removeAllViews()
        panel.background = keyboardGlass(theme)
        if (!history.enabled) {
            panel.addView(label("历史已暂停。在设置中开启本机剪贴板历史后即可查看。"))
            return
        }
        if (confirming) {
            panel.addView(label("清空这台手机上的剪贴板历史？"))
            val actions = LinearLayout(context)
            actions.addView(button("取消") { confirming = false; render(history.view.value) })
            actions.addView(button("确认清空") { confirming = false; expanded = null; history.clear() })
            panel.addView(actions)
            return
        }
        val entry = state.items.find { it.time == expanded }
        if (entry != null) {
            val actions = LinearLayout(context)
            actions.addView(button("返回列表") { expanded = null; render(history.view.value) })
            actions.addView(button("粘贴") { paste(entry) })
            actions.addView(button("删除") { history.remove(entry) })
            panel.addView(actions)
            panel.addView(ScrollView(context).apply { addView(label(entry.text)) })
            return
        }
        expanded = null
        val header = LinearLayout(context)
        val info = when {
            state.error -> "历史读取失败"
            state.items.isEmpty() -> "暂无剪贴板历史"
            else -> "最近 ${state.items.size} 条"
        }
        header.addView(label(info), LinearLayout.LayoutParams(0, -2, 1f))
        if (state.error) header.addView(button("重试") { history.refresh() })
        if (state.items.isNotEmpty() || state.error) header.addView(button("清空") {
            confirming = true
            render(history.view.value)
        })
        panel.addView(header)
        val rows = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
        for (item in state.items) {
            val row = LinearLayout(context)
            val excerpt = button(item.text.take(160)) { paste(item) }.apply {
                maxLines = 2
                ellipsize = TextUtils.TruncateAt.END
            }
            row.addView(excerpt, LinearLayout.LayoutParams(0, -2, 1f))
            row.addView(button("查看") { expanded = item.time; render(history.view.value) })
            row.addView(button("删除") { history.remove(item) })
            rows.addView(row)
        }
        panel.addView(ScrollView(context).apply { addView(rows) })
    }

    private fun paste(item: HistoryItem) {
        if (!history.enabled || history.view.value.items.none { it.time == item.time }) return
        service.commitText(item.text)
        manager.attachWindow(KeyboardWindow)
    }

    private fun label(value: String) = TextView(context).apply {
        text = value
        textSize = 15f
        minHeight = dp(48)
        setTextColor(theme.colors.candidateTextColor)
        gravity = Gravity.CENTER_VERTICAL
        setPadding(dp(8), dp(4), dp(8), dp(4))
    }

    private fun button(value: String, action: () -> Unit) = label(value).apply {
        isFocusable = true
        minWidth = dp(48)
        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        val attr = android.util.TypedValue()
        if (context.theme.resolveAttribute(android.R.attr.selectableItemBackground, attr, true)) setBackgroundResource(attr.resourceId)
        setOnClickListener { if (history.enabled) action() }
    }

    private fun dp(value: Int) = (context.resources.displayMetrics.density * value).roundToInt()
}
