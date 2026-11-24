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
    apply_edit(manifest, '        <!-- Settings activity -->',
               '        <provider android:name=".link.LinkImageProvider" android:authorities="${applicationId}.link-images"\n'
               '            android:exported="false" android:grantUriPermissions="true" />\n\n        <!-- Settings activity -->')
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
    old_clipboard = """            private fun handleClipboardWindow(arg: String) {
                val tabIndex = arg.toIntOrNull()?.coerceIn(0, 1) ?: 0
                windowManager.attachWindow(ClipboardWindow(di, tabIndex))
            }"""
    if "LinkActivity.openClipboard(service)" in actions.read_text(encoding="utf-8"):
        old_clipboard = """            private fun openLinkClipboard() {
                if (com.osfans.trime.link.sensitiveEditor(service.currentInputEditorInfo)) return
                com.osfans.trime.link.LinkActivity.openClipboard(service)
            }"""
    apply_edit(actions, old_clipboard, """            private fun openLinkClipboard() {
                if (com.osfans.trime.link.sensitiveEditor(service.currentInputEditorInfo)) return
                windowManager.attachWindow(com.osfans.trime.link.HistoryWindow(di))
            }""")

    input_bar = java / "ime/bar/InputBarDelegate.kt"
    old_preview = "            ClipboardHelper.addOnUpdateListener(onClipboardUpdateListener)"
    if "// Copied content never appears" in input_bar.read_text(encoding="utf-8"):
        old_preview = "            // Copied content never appears as an automatic keyboard suggestion."
    old_binding = """            com.osfans.trime.link.bindHistoryPreview(this, service) { preview ->
                alwaysUi.clipboardUi.text.text = preview.orEmpty()
                isClipboardFresh = !preview.isNullOrEmpty()
                evalAlwaysUiState()
            }"""
    if old_binding in input_bar.read_text(encoding="utf-8"):
        old_preview = old_binding
    apply_edit(input_bar, old_preview,
               """            com.osfans.trime.link.bindHistoryPreview(this, service) { preview, thumbnail ->
                alwaysUi.clipboardUi.text.text = preview.orEmpty()
                alwaysUi.clipboardUi.setPreview(thumbnail)
                isClipboardFresh = !preview.isNullOrEmpty()
                evalAlwaysUiState()
            }""")
    old_click = """                    val content = ClipboardHelper.lastBean?.text
                    content?.let { service.commitText(it) }
                    dismissClipboardSuggestion()"""
    history_click = "                    if (service.linkHistory.enabled) windowManager.attachWindow(com.osfans.trime.link.HistoryWindow(di))"
    if history_click in input_bar.read_text(encoding="utf-8"):
        old_click = history_click
    apply_edit(input_bar, old_click, history_click.replace("if (service.", "if (!service.linkImages.tap() && service."))
    apply_edit(input_bar, """            clipboardUi.dismiss.setOnClickListener {
                dismissClipboardSuggestion()""", """            clipboardUi.dismiss.setOnClickListener {
                if (!service.linkImages.dismiss()) dismissClipboardSuggestion()""")
    suggestion = java / "ime/bar/ui/ClipboardSuggestionUi.kt"
    apply_edit(suggestion, "    val text =", """    fun setPreview(image: android.graphics.Bitmap?) {
        if (image != null) icon.setImageBitmap(image)
        else icon.setImageDrawable(ctx.getDrawable(R.drawable.ic_clipboard_24)?.mutate()?.apply { setTint(scope.colors.candidateTextColor) })
    }

    val text =""")
    apply_edit(input_bar, """                    ClipboardHelper.lastBean?.let {
                        AppUtils.launchClipEdit(context, it.id, ClipEditActivity.FROM_CLIPBOARD)
                    }""", """                    if (service.linkHistory.enabled) {
                        windowManager.attachWindow(com.osfans.trime.link.HistoryWindow(di))
                    }""")

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

    queued_key = "                service.postRimeJob {\n                    if (service.hookKeyboard(keyEventCode, m)) {"
    apply_edit(actions, queued_key, '''                service.postRimeJob {
                    if (keyEventCode == KeyEvent.KEYCODE_SPACE && m == 0 && getRuntimeOption("ascii_mode")) {
                        val raw = getRawInput()
                        val correction = com.osfans.trime.link.LinkInputOptions
                        if (raw.isEmpty()) {
                            if (correction.correctEnglish(service)) return@postRimeJob
                        } else {
                            correction.correctedComposition(service, raw)?.let {
                                service.commitText("$it ")
                                clearComposition()
                                return@postRimeJob
                            }
                        }
                    }
                    if (service.hookKeyboard(keyEventCode, m)) {''')

    ime = java / "ime/core/TrimeInputMethodService.kt"
    apply_edit(ime, "    private lateinit var clipboardLink: com.osfans.trime.link.ClipboardLink",
               "    private lateinit var clipboardLink: com.osfans.trime.link.ClipboardLink\n"
               "    internal val linkHistory get() = clipboardLink.history")
    apply_edit(ime, "    internal val linkHistory get() = clipboardLink.history",
               "    internal val linkHistory get() = clipboardLink.history\n    internal val linkImages get() = clipboardLink.images")
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
    # Regenerate the call sites in-place on pristine and previously prepared trees.
    source = ime.read_text(encoding="utf-8")
    for before, after, body in [
        ("    private var cursorUpdateIndex = 0\n", "    private val recreateInputViewPrefs", """
    private val linkSelection = com.osfans.trime.link.LinkEditorSelection()

    internal fun dismissLinkPrediction() {
        postRimeJob { dismissPrediction() }
    }

"""),
        ("        clipboardLink.editor(attribute)\n", "        val isNullType", """        linkSelection.start(attribute.initialSelStart, attribute.initialSelEnd)
        dismissLinkPrediction()
"""),
        ("            candidatesEnd,\n        )\n", "        cursorUpdateIndex += 1", """        if (linkSelection.externallyMoved(newSelStart, newSelEnd, candidatesStart, composingText.isNotEmpty(),
                oldSelStart != newSelStart || oldSelEnd != newSelEnd)) {
            dismissLinkPrediction()
        }
"""),
        ("    fun commitText(text: String) {\n        val ic = currentInputConnection ?: return\n",
         "        // when composing text equals commit content", "        linkSelection.commit(text.length)\n\n"),
        ("                ic.deleteSurroundingText(1, 0)\n            }\n", "            ic.setComposingText(text, 1)",
         "            linkSelection.composing(text.length)\n"),
    ]:
        if source.count(before) != 1:
            raise RuntimeError(f"Unexpected editor selection anchor: {before}")
        start = source.index(before) + len(before)
        end = source.index(after, start)
        source = source[:start] + body + source[end:]
    ime.write_text(source, encoding="utf-8")
    apply_edit(ime, "        super.onViewClicked(focusChanged)", """        super.onViewClicked(focusChanged)
        dismissLinkPrediction()""")
    apply_edit(ime, "        super.onUpdateEditorToolType(toolType)", """        super.onUpdateEditorToolType(toolType)
        if (toolType == android.view.MotionEvent.TOOL_TYPE_FINGER || toolType == android.view.MotionEvent.TOOL_TYPE_STYLUS) {
            dismissLinkPrediction()
        }""")
    apply_edit(ime, """            clearComposition()
        }
        InputFeedbackManager.finishInput()""", """            dismissPrediction()
            clearComposition()
        }
        InputFeedbackManager.finishInput()""")
    apply_edit(actions, "                        service.commitText(action.commit)", """                        service.dismissLinkPrediction()
                        service.commitText(action.commit)""")
    apply_edit(actions, "                if (shouldHandle) {", """                if (shouldHandle) {
                    when (action.code) {
                        KeyEvent.KEYCODE_FUNCTION -> if (action.command != "select_candidate") service.dismissLinkPrediction()
                        KeyEvent.KEYCODE_SWITCH_CHARSET, KeyEvent.KEYCODE_EISU, KeyEvent.KEYCODE_LANGUAGE_SWITCH,
                        KeyEvent.KEYCODE_SETTINGS, KeyEvent.KEYCODE_PROG_RED,
                        KeyEvent.KEYCODE_MENU, KeyEvent.KEYCODE_VOICE_ASSIST -> service.dismissLinkPrediction()
                    }""")
    apply_edit(actions, '                        Timber.d("handleKey: hook")',
               '                        dismissPrediction()\n                        Timber.d("handleKey: hook")')
    api = java / "core/RimeApi.kt"
    apply_edit(api, "    suspend fun clearComposition()", "    suspend fun dismissPrediction()\n\n    suspend fun clearComposition()")
    engine = java / "core/Rime.kt"
    apply_edit(engine, "    override suspend fun clearComposition()", """    override suspend fun dismissPrediction(): Unit = withRimeContext {
        if (hasMenu && getRimeRawInput().isEmpty()) {
            val option = "link_dismiss_prediction"
            setRimeOption(option, !getRimeOption(option))
            emitResponse()
        }
    }

    override suspend fun clearComposition()""")
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
