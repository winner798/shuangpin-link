package com.osfans.trime.link

import android.app.AlertDialog
import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.res.ColorStateList
import android.content.res.Configuration
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.view.WindowManager
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.widget.SwitchCompat
import androidx.core.content.FileProvider
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.updatePadding
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.withResumed
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.osfans.trime.BuildConfig
import com.osfans.trime.daemon.RimeDaemon
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import kotlin.math.roundToInt

class LinkActivity : ComponentActivity() {
    private lateinit var settings: LinkSettings
    private lateinit var status: TextView
    private lateinit var clipboardStatus: TextView
    private lateinit var clearHistory: Button
    private lateinit var clipboardSection: LinearLayout
    private lateinit var scroll: ScrollView
    private lateinit var keyboardStatus: TextView
    private lateinit var dictionaryStatus: TextView
    private lateinit var keyboardAction: Button
    private lateinit var scanAction: Button
    private lateinit var removePairAction: Button
    private lateinit var syncAction: Button
    private lateinit var pause: SwitchCompat
    private lateinit var share: SwitchCompat
    private lateinit var learning: SwitchCompat
    private lateinit var fuzzy: SwitchCompat
    private lateinit var updates: LinkUpdates
    private lateinit var updateStatus: TextView
    private lateinit var checkUpdate: Button
    private lateinit var installUpdate: Button
    private var verifiedApk: File? = null
    private var bindingPreferences = false

    private data class Palette(
        val surface: Int,
        val surfaceHighlight: Int,
        val panel: Int,
        val panelHighlight: Int,
        val text: Int,
        val muted: Int,
        val accentText: Int,
        val outline: Int,
        val disabled: Int,
    ) {
        val accent = 0xFF82CFFF.toInt()
        val onAccent = 0xFF163048.toInt()
    }

    private val dark get() = (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES

    private val colors by lazy {
        if (dark) {
            Palette(
                surface = 0xFF0C1826.toInt(),
                surfaceHighlight = 0xFF1B3954.toInt(),
                panel = 0xE6232033.toInt(),
                panelHighlight = 0xED322B49.toInt(),
                text = 0xFFEAF7FF.toInt(),
                muted = 0xFFAAC6DA.toInt(),
                accentText = 0xFFA9DFFF.toInt(),
                outline = 0xFF345877.toInt(),
                disabled = 0xFF7794AA.toInt(),
            )
        } else {
            Palette(
                surface = 0xFFEDF7FF.toInt(),
                surfaceHighlight = 0xFFFCFEFF.toInt(),
                panel = 0xD9F8F4FF.toInt(),
                panelHighlight = 0xF2FFFFFF.toInt(),
                text = 0xFF163048.toInt(),
                muted = 0xFF4B6479.toInt(),
                accentText = 0xFF155A8A.toInt(),
                outline = 0xFFCDE3F1.toInt(),
                disabled = 0xFF57748C.toInt(),
            )
        }
    }

    private val inputMethodManager get() = getSystemService(InputMethodManager::class.java)
    private val scanner = registerForActivityResult(ScanContract()) { result ->
        result.contents?.let(::pair)
    }
    private val installPermission = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        updateStatus.text = if (packageManager.canRequestPackageInstalls()) {
            "已允许安装，点击「安装更新」继续。"
        } else {
            "请在系统设置中允许双拼互联安装更新。"
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        settings = LinkSettings(this)
        if (!settings.unlocked) { finish(); return }
        updates = LinkUpdates(applicationContext)

        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowCompat.getInsetsController(window, window.decorView).apply {
            isAppearanceLightStatusBars = !dark
            isAppearanceLightNavigationBars = !dark
        }
        val pane = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }
        pane.addView(TextView(this).apply {
            text = "双拼互联"
            textSize = 32f
            setTextColor(colors.text)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            isAccessibilityHeading = true
            setPadding(dp(4), dp(6), dp(4), dp(18))
        }, row())

        section(pane, "键盘") {
            keyboardStatus = addStatus(this, "")
            keyboardAction = addAction(this, "启用双拼互联", primary = true) { openKeyboardSetup() }
            addStatus(this, "打字时点「切换方案」，选择微软双拼或普通拼音。", top = 10)
        }

        clipboardSection = section(pane, "设备互通") {
            status = addStatus(this, "")
            scanAction = addAction(this, "扫描电脑配对码", primary = true) {
                scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                    .setPrompt("扫描电脑上的配对二维码").setBeepEnabled(false).setOrientationLocked(false))
            }
            pause = addSwitch(this, "暂停剪贴板同步", "paused", false)
            clipboardStatus = addStatus(this, "", top = 8)
            clearHistory = addAction(this, "清除旧剪贴板历史") { confirmClearClipboardHistory() }
            removePairAction = addAction(this, "移除手机配对") {
                AlertDialog.Builder(this@LinkActivity).setMessage("移除后，请同时在电脑上解除手机配对。")
                    .setNegativeButton("取消", null).setPositiveButton("移除") { _, _ ->
                        settings.forget()
                        refresh()
                    }.show()
            }
        }

        section(pane, "个人词库") {
            learning = addSwitch(this, "学习选词习惯", "learning", true)
            fuzzy = addSwitch(this, "模糊拼音（en/eng、in/ing）", "fuzzy", false)
            addSwitch(this, "英文自动纠错", "englishAutocorrect", false)
            addStatus(this, "英文模式下，按空格时修正常见拼写错误。")
            share = addSwitch(this, "跨设备共享词库", "share", true)
            syncAction = addAction(this, "立即同步词库") {
                settings.prefs.edit().putLong("syncNow", System.currentTimeMillis()).apply()
                showDictionaryStatus("已请求同步。输入法在线时会处理。")
            }
            addAction(this, "清空本机词库") {
                AlertDialog.Builder(this@LinkActivity)
                    .setTitle("清空本机词库？")
                    .setMessage("这会清除手机学到的词条和词频，并关闭手机上的词库共享。")
                    .setNegativeButton("取消", null)
                    .setPositiveButton("清空") { _, _ -> clearLearning() }.show()
            }
            dictionaryStatus = addStatus(this, "", top = 10).apply { visibility = View.GONE }
        }

        section(pane, "软件更新") {
            updateStatus = addStatus(this, "当前版本 ${BuildConfig.VERSION_NAME}")
            checkUpdate = addAction(this, "检查更新") { checkForUpdate() }
            installUpdate = addAction(this, "安装更新", primary = true) { installVerifiedUpdate() }
            installUpdate.visibility = View.GONE
        }
        savedInstanceState?.getString("verifiedUpdateApk")?.let(updates::cachedApk)?.let(::showVerifiedApk)

        val wrap = FrameLayout(this)
        val wideWidth = if (resources.configuration.screenWidthDp >= 640) dp(584) else MATCH_PARENT
        wrap.addView(pane, FrameLayout.LayoutParams(wideWidth, WRAP_CONTENT, Gravity.TOP or Gravity.CENTER_HORIZONTAL))
        scroll = ScrollView(this).apply {
            background = rounded(colors.surface, 0, highlight = colors.surfaceHighlight)
            clipToPadding = false
            isFillViewport = true
            addView(wrap)
        }
        ViewCompat.setOnApplyWindowInsetsListener(scroll) { view, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
            view.updatePadding(left = bars.left + dp(20), top = bars.top + dp(16),
                right = bars.right + dp(20), bottom = bars.bottom + dp(16))
            insets
        }
        setContentView(scroll)
        refresh()
        refreshKeyboard()
        showClipboardSection(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        if (::scroll.isInitialized) showClipboardSection(intent)
    }

    override fun onResume() {
        super.onResume()
        if (::keyboardStatus.isInitialized) refreshKeyboard()
    }

    private fun showClipboardSection(intent: Intent?) {
        if (intent?.action == ACTION_CLIPBOARD) {
            scroll.post { scroll.scrollTo(0, clipboardSection.top) }
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus && ::keyboardStatus.isInitialized) refreshKeyboard()
    }

    private fun keyboardIsEnabled() = inputMethodManager?.enabledInputMethodList?.any { it.packageName == packageName } == true

    private fun refreshKeyboard() {
        val enabled = keyboardIsEnabled()
        val selected = Settings.Secure.getString(contentResolver, Settings.Secure.DEFAULT_INPUT_METHOD)
            ?.let(ComponentName::unflattenFromString)?.packageName == packageName
        keyboardStatus.text = when {
            selected -> "已设为当前键盘"
            enabled -> "已启用，请在系统列表里选择双拼互联。"
            else -> "在系统键盘列表中启用双拼互联。"
        }
        keyboardAction.text = when {
            selected -> "更换当前键盘"
            enabled -> "选择双拼互联键盘"
            else -> "打开系统键盘设置"
        }
    }

    private fun openKeyboardSetup() {
        if (keyboardIsEnabled()) {
            inputMethodManager?.showInputMethodPicker()
        } else {
            try {
                startActivity(Intent(Settings.ACTION_INPUT_METHOD_SETTINGS))
            } catch (_: ActivityNotFoundException) {
                keyboardStatus.text = "请从系统设置打开键盘管理。"
            }
        }
    }

    private fun dp(value: Int) = (value * resources.displayMetrics.density).roundToInt()

    private fun row(top: Int = 0) = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply {
        topMargin = dp(top)
    }

    private fun rounded(fill: Int, radius: Int, stroke: Int? = null, highlight: Int? = null): GradientDrawable {
        val shape = if (highlight != null) {
            GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(highlight, fill))
        } else {
            GradientDrawable().apply { setColor(fill) }
        }
        shape.cornerRadius = dp(radius).toFloat()
        stroke?.let { shape.setStroke(dp(1), it) }
        return shape
    }

    private fun section(pane: LinearLayout, title: String, content: LinearLayout.() -> Unit): LinearLayout {
        val panel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            val padding = dp(16)
            setPadding(padding, padding, padding, padding)
            background = rounded(colors.panel, 22, colors.outline, colors.panelHighlight)
            addView(TextView(this@LinkActivity).apply {
                text = title
                textSize = 18f
                setTextColor(colors.text)
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                isAccessibilityHeading = true
                setPadding(0, 0, 0, dp(8))
            }, row())
            content()
        }
        pane.addView(panel, row().apply { bottomMargin = dp(12) })
        return panel
    }

    private fun addStatus(pane: LinearLayout, value: String, top: Int = 0) = TextView(this).apply {
        text = value
        textSize = 14f
        setTextColor(colors.muted)
        setLineSpacing(dp(2).toFloat(), 1f)
        accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
        pane.addView(this, row(top))
    }

    private fun showDictionaryStatus(message: String) {
        dictionaryStatus.text = message
        dictionaryStatus.visibility = View.VISIBLE
    }

    private fun addAction(pane: LinearLayout, label: String, primary: Boolean = false, action: () -> Unit) = Button(this).apply {
        text = label
        textSize = 14f
        isAllCaps = false
        minHeight = dp(48)
        stateListAnimator = null
        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        val textColor = if (primary) colors.onAccent else colors.accentText
        setTextColor(ColorStateList(arrayOf(intArrayOf(-android.R.attr.state_enabled), intArrayOf()),
            intArrayOf(colors.disabled, textColor)))
        val ripple = (textColor and 0xFFFFFF) or 0x24000000
        background = RippleDrawable(ColorStateList.valueOf(ripple),
            if (primary) rounded(colors.accent, 16) else rounded(colors.panel, 16, colors.outline), null)
        backgroundTintList = null
        setPadding(dp(12), dp(6), dp(12), dp(6))
        setOnClickListener { action() }
        pane.addView(this, row(8))
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        verifiedApk?.let { outState.putString("verifiedUpdateApk", it.name) }
    }

    private fun checkForUpdate() {
        lifecycleScope.launch {
            checkUpdate.isEnabled = false
            updateStatus.text = "正在检查更新…"
            try {
                val release = updates.check()
                updateStatus.text = if (release == null) {
                    "已是最新版本 ${BuildConfig.VERSION_NAME}。"
                } else {
                    "发现新版本 ${release.versionName}。"
                }
                if (release != null) lifecycle.withResumed {
                    AlertDialog.Builder(this@LinkActivity)
                        .setTitle("更新到 ${release.versionName}")
                        .setMessage("下载完成后，可在系统安装界面确认更新。")
                        .setNegativeButton("取消", null)
                        .setPositiveButton("下载") { _, _ -> downloadUpdate(release) }
                        .show()
                }
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (error: LinkHttpError) {
                updateStatus.text = when (error.code) {
                    404 -> "暂未发布更新。"
                    else -> "检查更新失败，请稍后重试。"
                }
            } catch (_: UnsupportedLinkUpdate) {
                updateStatus.text = "当前设备没有可用的安卓更新包。"
            } catch (_: InvalidLinkUpdate) {
                updateStatus.text = "更新信息校验失败，请稍后重试。"
            } catch (_: Exception) {
                updateStatus.text = "检查更新失败，请检查网络后重试。"
            } finally {
                checkUpdate.isEnabled = true
            }
        }
    }

    private fun downloadUpdate(release: LinkRelease) {
        lifecycleScope.launch {
            checkUpdate.isEnabled = false
            installUpdate.isEnabled = false
            updateStatus.text = "正在下载 ${release.versionName}…"
            try {
                val file = updates.download(release)
                showVerifiedApk(file)
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (_: InvalidLinkUpdate) {
                updateStatus.text = "更新包校验失败，请重新下载。"
            } catch (_: Exception) {
                updateStatus.text = "下载失败，请重新检查更新。"
            } finally {
                checkUpdate.isEnabled = true
                installUpdate.isEnabled = true
            }
        }
    }

    private fun showVerifiedApk(file: File) {
        verifiedApk = file
        installUpdate.visibility = View.VISIBLE
        updateStatus.text = "更新包已校验，可安装。"
    }

    private fun installVerifiedUpdate() {
        val file = verifiedApk?.let { updates.cachedApk(it.name) }
        if (file == null) {
            verifiedApk = null
            installUpdate.visibility = View.GONE
            updateStatus.text = "更新包已失效，请重新检查更新。"
            return
        }
        try {
            if (!packageManager.canRequestPackageInstalls()) {
                installPermission.launch(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")))
                return
            }
            val uri = FileProvider.getUriForFile(this, "$packageName.link-updates", file)
            val install = Intent(Intent.ACTION_VIEW)
                .setDataAndType(uri, "application/vnd.android.package-archive")
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                .apply { clipData = ClipData.newRawUri("双拼互联更新包", uri) }
            startActivity(install)
        } catch (_: ActivityNotFoundException) {
            updateStatus.text = "系统安装界面不可用。"
        } catch (_: IllegalArgumentException) {
            updateStatus.text = "更新文件无法打开，请重新下载。"
        } catch (_: SecurityException) {
            updateStatus.text = "系统限制了安装，请检查应用安装权限。"
        }
    }

    private fun addSwitch(pane: LinearLayout, label: String, key: String, default: Boolean) =
        SwitchCompat(this).apply {
            text = label
            textSize = 15f
            minHeight = dp(48)
            setTextColor(colors.text)
            val states = arrayOf(intArrayOf(-android.R.attr.state_enabled), intArrayOf(android.R.attr.state_checked), intArrayOf())
            thumbTintList = ColorStateList(states, intArrayOf(colors.disabled, colors.onAccent, colors.panel))
            trackTintList = ColorStateList(states, intArrayOf(colors.outline, colors.accent, colors.outline))
            isChecked = settings.prefs.getBoolean(key, default)
            setOnCheckedChangeListener { _, checked ->
                if (bindingPreferences) return@setOnCheckedChangeListener
                if (key == "learning" || key == "fuzzy") {
                    applyInputOption(key, checked)
                } else {
                    settings.prefs.edit().putBoolean(key, checked).apply()
                    if (key == "share") syncAction.isEnabled = checked && settings.device() != null
                    if (key == "paused") showClipboardStatus(checked)
                }
            }
            pane.addView(this, row(2))
        }

    private fun applyInputOption(key: String, enabled: Boolean) {
        val isLearning = key == "learning"
        val description = if (isLearning) "选词学习" else "模糊拼音"
        val configure: (Boolean) -> Unit =
            if (isLearning) { value -> LinkLearning.configure(value) } else LinkInputOptions::configureFuzzy
        learning.isEnabled = false
        fuzzy.isEnabled = false
        showDictionaryStatus("正在更新${description}设置…")
        lifecycleScope.launch {
            val previous = settings.prefs.getBoolean(key, isLearning)
            val name = "ShuangpinLinkSettings"
            try {
                withContext(Dispatchers.IO) { configure(enabled) }
                settings.prefs.edit().putBoolean(key, enabled).apply()
                RimeDaemon.createSession(name).runOnReady { deploy() }
                showDictionaryStatus("两种输入方案均已${if (enabled) "开启" else "关闭"}$description。")
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (_: Exception) {
                settings.prefs.edit().putBoolean(key, previous).apply()
                withContext(Dispatchers.IO) { runCatching { configure(previous) } }
                bindingPreferences = true
                (if (isLearning) learning else fuzzy).isChecked = previous
                bindingPreferences = false
                showDictionaryStatus("${description}设置更新失败，请稍后重试。")
            } finally {
                RimeDaemon.destroySession(name)
                learning.isEnabled = true
                fuzzy.isEnabled = true
            }
        }
    }

    private fun refresh() {
        val device = settings.device()
        status.text = if (device == null) "在电脑上生成配对码，即可同步剪贴板和词库。" else "已配对 · ${java.net.URI(device.server).host}"
        scanAction.text = if (device == null) "扫描电脑配对码" else "重新扫码配对"
        removePairAction.visibility = if (device == null) View.GONE else View.VISIBLE
        bindingPreferences = true
        pause.isChecked = settings.prefs.getBoolean("paused", false)
        share.isChecked = settings.prefs.getBoolean("share", true)
        bindingPreferences = false
        pause.isEnabled = device != null
        share.isEnabled = device != null
        syncAction.isEnabled = device != null && share.isChecked
        showClipboardStatus(pause.isChecked)
        showLegacyClipboardHistory()
    }

    private fun showClipboardStatus(paused: Boolean) {
        clipboardStatus.text = if (paused) {
            "剪贴板同步已暂停。"
        } else {
            "打开键盘时自动同步。系统标记为敏感的内容会跳过。"
        }
    }

    private fun showLegacyClipboardHistory() {
        clearHistory.visibility = if (LinkClipboard.hasLegacyHistory(this)) View.VISIBLE else View.GONE
    }

    private fun confirmClearClipboardHistory() {
        AlertDialog.Builder(this)
            .setTitle("清除旧剪贴板历史？")
            .setMessage("将从本机删除旧版本记录的所有剪贴板条目，包括置顶条目。")
            .setNegativeButton("取消", null)
            .setPositiveButton("清除") { _, _ ->
                lifecycleScope.launch {
                    val cleared = withContext(Dispatchers.IO) { LinkClipboard.clearLegacyHistory(this@LinkActivity) }
                    clipboardStatus.text = if (cleared) "本机旧剪贴板历史已清除。" else "旧剪贴板历史清除失败。"
                    showLegacyClipboardHistory()
                }
            }.show()
    }

    private fun clearLearning() {
        lifecycleScope.launch {
            val name = "ShuangpinLinkDictionaryReset"
            settings.prefs.edit().putBoolean("share", false).remove("dictionaryReceived").remove("dictionarySent").apply()
            val session = RimeDaemon.createSession(name)
            try {
                session.runOnReady { clearLinkedDictionary() }
                refresh()
                showDictionaryStatus("本机个人词库已清空。")
            } catch (cancel: CancellationException) { throw cancel }
            catch (_: Exception) { showDictionaryStatus("清空词库未完成，请稍后重试。") }
            finally { RimeDaemon.destroySession(name) }
        }
    }

    private fun pair(payload: String) {
        lifecycleScope.launch {
            status.text = "正在完成配对…"
            try {
                require(payload.length <= 2048)
                val qr = JSONObject(payload)
                val server = qr.getString("server")
                val invitation = qr.getString("invite")
                val key = qr.getString("key")
                require(qr.getInt("v") == 1 && invitation.matches(Regex("[A-Za-z0-9_-]{43}")) && decode(key).size == 32)
                val device = LinkHttp(server, invitation).call("POST", "join") ?: error("No device")
                settings.save(LinkDevice(server, device.getString("token"), key))
                refresh()
            } catch (cancel: CancellationException) { throw cancel }
            catch (_: Exception) { status.text = "配对未完成。请在电脑端重新生成配对码。" }
        }
    }

    internal companion object {
        private const val ACTION_CLIPBOARD = "com.osfans.trime.link.OPEN_CLIPBOARD"

        fun openClipboard(context: Context) {
            context.startActivity(Intent(context, LinkActivity::class.java).apply {
                action = ACTION_CLIPBOARD
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP
            })
        }
    }
}
