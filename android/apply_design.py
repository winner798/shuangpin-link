"""Wire the Android frontend to the embedded keyboard engine and app settings."""

import shutil
from pathlib import Path


HERE = Path(__file__).resolve().parent


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
    shutil.copyfile(HERE / "ime/shuangpin_link.trime.yaml", assets / "shuangpin_link.trime.yaml")
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

    theme_manager = java / "data/theme/ThemeManager.kt"
    apply_edit(theme_manager, "val selectedThemeId = prefs.selectedTheme.getValue()",
               'val selectedThemeId = "shuangpin_link"')
    apply_edit(theme_manager, "if (resolvedTheme.configId != selectedThemeId) {",
               "if (resolvedTheme.configId != prefs.selectedTheme.getValue()) {")
    theme_prefs = java / "data/theme/ThemePrefs.kt"
    apply_edit(theme_prefs, 'SELECTED_THEME,\n            "trime",',
               'SELECTED_THEME,\n            "shuangpin_link",')
    color_manager = java / "data/theme/ColorManager.kt"
    apply_edit(color_manager, "selectedSchemeId = prefs.normalModeColor.getValue(),", 'selectedSchemeId = "default",')
    apply_edit(color_manager, "followSystemDayNight = prefs.followSystemDayNight.getValue(),", "followSystemDayNight = true,")

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
