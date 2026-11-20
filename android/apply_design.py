"""Wire the Android frontend to the embedded keyboard engine and app settings."""

import shutil
from pathlib import Path


HERE = Path(__file__).resolve().parent
THEME_FILE = HERE / "ime/shuangpin_link.trime.yaml"
THEME_ID = THEME_FILE.stem


def apply_edit(path, before, after):
    text = path.read_text(encoding="utf-8")
    if after in text:
        return
    if text.count(before) != 1:
        raise RuntimeError(f"Upstream layout changed: {path.name}, {before[:72]}")
    path.write_text(text.replace(before, after, 1), encoding="utf-8")


def apply_design(src, assets):
    app = src / "app/src/main"
    java = app / "java/com/osfans/trime"
    shutil.copyfile(THEME_FILE, assets / THEME_FILE.name)
    for resource in ("drawable/link_mark.xml", "values/link_appearance.xml", "mipmap-anydpi-v26/link_icon.xml"):
        destination = app / "res" / resource
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(HERE / "res" / resource, destination)

    manifest = app / "AndroidManifest.xml"
    apply_edit(manifest, 'android:targetActivity=".ui.main.MainActivity"', 'android:targetActivity=".link.LinkActivity"')
    apply_edit(manifest, 'android:name=".ui.main.MainActivity"\n            android:exported="true"',
               'android:name=".ui.main.MainActivity"\n            android:exported="false"')
    apply_edit(manifest, 'android:icon="@mipmap/ic_app_icon"', 'android:icon="@mipmap/link_icon"')
    apply_edit(manifest, 'android:roundIcon="@mipmap/ic_app_icon_round"', 'android:roundIcon="@mipmap/link_icon"')
    method = app / "res/xml/method.xml"
    apply_edit(method, 'android:settingsActivity="com.osfans.trime.ui.main.MainActivity"',
               'android:settingsActivity="com.osfans.trime.MainLauncherAlias"')
    apply_edit(method, 'android:label="@string/trime_app_slogan"', 'android:label="@string/trime_app_name"')
    apply_edit(method, 'android:icon="@mipmap/ic_app_icon"', 'android:icon="@mipmap/link_icon"')

    app_utils = java / "util/AppUtils.kt"
    launch_entry = "    fun launchMainActivity(context: Context) {\n"
    apply_edit(app_utils, launch_entry + "        context.startActivity<MainActivity> {",
               launch_entry + "        context.startActivity<com.osfans.trime.link.LinkActivity> {")

    application = java / "TrimeApplication.kt"
    crash_handler = "            Thread.setDefaultUncaughtExceptionHandler { _, e ->"
    apply_edit(application, crash_handler, crash_handler + """
                for (failure in generateSequence(e) { it.cause }.take(6)) {
                    val diagnostic = Throwable(failure.javaClass.name).apply { stackTrace = failure.stackTrace }
                    android.util.Log.e("ShuangpinCrash", "Uncaught Android error", diagnostic)
                }""")
    old_manual = "            // Clipboard sharing is manual and does not start the local history collector."
    history_start = old_manual if old_manual in application.read_text(encoding="utf-8") else "            ClipboardHelper.init(ctx)"
    apply_edit(application, history_start,
               "            // Clipboard synchronization does not start the local history collector.")

    actions = java / "ime/keyboard/CommonKeyboardActionListener.kt"
    apply_edit(actions, '"clipboard_window" -> handleClipboardWindow(arg)',
               '"clipboard_window" -> openLinkClipboard()')
    apply_edit(actions, "                    windowManager.attachWindow(ClipboardWindow(di))",
               "                    openLinkClipboard()")
    apply_edit(actions, """            private fun handleClipboardWindow(arg: String) {
                val tabIndex = arg.toIntOrNull()?.coerceIn(0, 1) ?: 0
                windowManager.attachWindow(ClipboardWindow(di, tabIndex))
            }""", """            private fun openLinkClipboard() {
                if (com.osfans.trime.link.sensitiveEditor(service.currentInputEditorInfo)) return
                com.osfans.trime.link.LinkActivity.openClipboard(service)
            }""")

    input_bar = java / "ime/bar/InputBarDelegate.kt"
    apply_edit(input_bar, "            ClipboardHelper.addOnUpdateListener(onClipboardUpdateListener)",
               "            // Copied content never appears as an automatic keyboard suggestion.")

    theme_manager = java / "data/theme/ThemeManager.kt"
    previous_selection = 'val selectedThemeId = "shuangpin_link"'
    original_selection = 'val selectedThemeId = prefs.selectedTheme.getValue()'
    selection = previous_selection if previous_selection in theme_manager.read_text(encoding="utf-8") else original_selection
    apply_edit(theme_manager, selection, f'val selectedThemeId = "{THEME_ID}"')
    apply_edit(theme_manager, "if (resolvedTheme.configId != selectedThemeId) {",
               "if (resolvedTheme.configId != prefs.selectedTheme.getValue()) {")
    theme_prefs = java / "data/theme/ThemePrefs.kt"
    previous_preference = 'SELECTED_THEME,\n            "shuangpin_link",'
    original_preference = 'SELECTED_THEME,\n            "trime",'
    preference = previous_preference if previous_preference in theme_prefs.read_text(encoding="utf-8") else original_preference
    apply_edit(theme_prefs, preference, f'SELECTED_THEME,\n            "{THEME_ID}",')
    color_manager = java / "data/theme/ColorManager.kt"
    apply_edit(color_manager, "selectedSchemeId = prefs.normalModeColor.getValue(),", 'selectedSchemeId = "default",')
    apply_edit(color_manager, "followSystemDayNight = prefs.followSystemDayNight.getValue(),", "followSystemDayNight = true,")

    keyboard_view = java / "ime/core/InputView.kt"
    refresh = """    fun refreshColors() {
        keyboardBackground.imageDrawable = """
    after_window = """        windowManager.attachWindow(KeyboardWindow)

        keyboardBackground.imageDrawable = """
    for location in (refresh, after_window):
        apply_edit(keyboard_view, location + 'scope.drawable("keyboard_background")',
                   location + "com.osfans.trime.link.keyboardGlass(scope)")

    keyboard = java / "ime/keyboard/Keyboard.kt"
    apply_edit(keyboard, "    /** 按鍵默認水平間距  */",
               '''    // The portrait reference has 1248 px of usable width and four 200 px rows.
    private val referenceGeometry = theme.name == "双拼互联" && !context.isLandscapeMode()
    private fun referencePx(value: Int) = kotlin.math.round(allowedWidth * value / 1248f).toInt()

    /** 按鍵默認水平間距  */''')
    for dimension, measured in (("horizontalGap", 18), ("verticalGap", 34)):
        anchor = f"    internal val {dimension}: Int =\n        resolvePositive"
        apply_edit(keyboard, anchor, f"    internal val {dimension}: Int =\n"
                   f"        if (referenceGeometry) referencePx({measured})\n        else resolvePositive")
    apply_edit(keyboard, "    val keyboardHeight: Int =\n        resolvePositive(",
               "    val keyboardHeight: Int =\n        if (referenceGeometry) referencePx(800)\n        else resolvePositive(")

    key_view = java / "ime/keyboard/KeyView.kt"
    draw_label = "            drawLabel(canvas, label)"
    apply_edit(key_view, draw_label, '''            val centerX = (width - paddingLeft - paddingRight) / 2f + paddingLeft
            val centerY = (height - paddingTop - paddingBottom) / 2f + paddingTop
            if (theme.name != "双拼互联" ||
                !com.osfans.trime.link.drawLinkKeyIcon(canvas, label, centerX, centerY, dp(24).toFloat(), key.getTextColor())
            ) {
                drawLabel(canvas, label)
            }''')

    ime = java / "ime/core/TrimeInputMethodService.kt"
    commit = "                    commitText(it.data.text)"
    apply_edit(ime, commit, '''                    if (it.data.text != " " || !correctEnglishSpace()) {
                        commitText(it.data.text)
                    }''')
    forwarded_key = "                                val keyCode = it.value.keyCode"
    apply_edit(ime, forwarded_key, forwarded_key + '''
                                if (keyCode == KeyEvent.KEYCODE_SPACE && it.modifiers.metaState == 0 &&
                                    correctEnglishSpace()
                                ) return@msg''')
    messages = "    private fun handleRimeMessage(it: RimeMessage<*>) {"
    apply_edit(ime, messages, '''    private fun correctEnglishSpace(): Boolean =
        rime.run { statusCached.isAsciiMode } && com.osfans.trime.link.LinkInputOptions.correctEnglish(this)

''' + messages)
    finish_view = "    override fun onFinishInputView(finishingInput: Boolean) {"
    apply_edit(ime, finish_view, """    override fun onWindowShown() {
        super.onWindowShown()
        if (::clipboardLink.isInitialized) clipboardLink.windowShown()
    }

    override fun onWindowHidden() {
        if (::clipboardLink.isInitialized) clipboardLink.suspendClipboard()
        super.onWindowHidden()
    }

    override fun onFinishInputView(finishingInput: Boolean) {
        clipboardLink.suspendClipboard()""")
    input_view = "    private var inputView: InputView? = null"
    apply_edit(ime, input_view, input_view + "\n    private var inputViewAwaitingTheme = false")
    theme_ready = "                ColorManager.addOnChangedListener(onColorChangeListener)"
    apply_edit(ime, theme_ready, theme_ready + """
                if (inputViewAwaitingTheme && ::contentView.isInitialized) {
                    themeScope?.let { scope ->
                        inputViewAwaitingTheme = false
                        replaceInputViews(scope)
                        if (isInputViewShown && inputDeviceManager.useVirtualKeyboard) {
                            currentInputEditorInfo?.let { editor -> inputView?.startInput(editor, restarting = false) }
                        }
                    }
                }""")
    view_requested = """            Timber.w("ThemeScope is not ready yet; deferring input view creation")
            return null
        }
        replaceInputViews(scope)"""
    apply_edit(ime, view_requested, """            Timber.w("ThemeScope is not ready yet; deferring input view creation")
            inputViewAwaitingTheme = true
            return null
        }
        inputViewAwaitingTheme = false
        replaceInputViews(scope)""")

    prefs = java / "data/prefs/AppPrefs.kt"
    apply_edit(prefs, "DATA_STORAGE_MODE, DataStorageMode.EXTERNAL_SYNC)",
               "DATA_STORAGE_MODE, DataStorageMode.APP_STORAGE)")
    user_data = java / "core/data/UserDataManager.kt"
    apply_edit(user_data, "fun useExternalSync(): Boolean = dataStorageMode == DataStorageMode.EXTERNAL_SYNC",
               "fun useExternalSync(): Boolean = dataStorageMode == DataStorageMode.EXTERNAL_SYNC && hasExternalAccess()")

    rime = java / "core/Rime.kt"
    apply_edit(rime, "private fun startRime(fullCheck: Boolean) {", "private fun startRime(requestedFullCheck: Boolean) {")
    startup_anchor = "        val sharedDataDir = DataManager.sharedDataDir.absolutePath"
    apply_edit(rime, startup_anchor,
               "        val fullCheck = com.osfans.trime.link.LinkLearning.restorePreference() || requestedFullCheck\n"
               + startup_anchor)
