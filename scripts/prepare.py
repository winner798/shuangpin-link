"""Prepare pinned upstream code/assets and apply the Shuangpin Link overlay."""

import argparse
import hashlib
import json
import re
import runpy
import shutil
import subprocess
import time
import urllib.request
from pathlib import Path, PurePosixPath
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / ".build"
VENDOR = BUILD / "vendor"
TRIME_TAG = "v3.3.13"
TRIME_REV = "b5cad1a49095a3652e9188982de400ca3a653f37"
ICE_REV = "927e1cf1d3eeb9bf4be85e3f684dba4dd485b6f6"
ARCHES = ("arm64-v8a", "x86_64")
INPUT_SCHEMES = ("double_pinyin_mspy", "rime_ice")
SCHEMA_DEPENDENCIES = ("melt_eng", "radical_pinyin")
PACKAGED_SCHEMAS = {f"{name}.schema.yaml" for name in (*INPUT_SCHEMES, *SCHEMA_DEPENDENCIES)}
CHECKSUMS = {
    "predict.db": "2a5a2b7c77f8f3d7c0836dfc8fd8b791ac2574d8bd93a3a2baaae1ee4861f5be",
    "trime-arm64.apk": "e7535ea421a25962eb2ea37c46ff867838e69d33ba6d935876ab84d108de5020",
    "trime-x86_64.apk": "d3bfbc84a99d77e150e9918ddb9d1a348a730bdbbbea5c0c1ee4375356a6f221",
}


def run(*args, cwd=ROOT):
    return subprocess.check_output(args, cwd=cwd, text=True).strip()


def checkout(name, url, ref, commit):
    path = BUILD / name
    cmd = ["git", "-c", "http.proxy=", "-c", f"safe.directory={path.as_posix()}"]
    if not (path / ".git").exists():
        subprocess.run([*cmd, "init", str(path)], check=True, cwd=ROOT)
        subprocess.run([*cmd, "-C", str(path), "fetch", "--depth", "1", url, ref], check=True)
        subprocess.run([*cmd, "-C", str(path), "checkout", "--detach", "FETCH_HEAD"], check=True)
    actual = run(*cmd, "-C", str(path), "rev-parse", "HEAD")
    if actual != commit:
        raise RuntimeError(f"Unexpected {name} revision: {actual}")
    return path


def download(name, url):
    VENDOR.mkdir(parents=True, exist_ok=True)
    target = VENDOR / name
    if target.is_file() and target.stat().st_size:
        if hashlib.sha256(target.read_bytes()).hexdigest() != CHECKSUMS[name]:
            raise RuntimeError(f"Cached asset checksum mismatch: {target}")
        return target
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "ShuangpinLink-build/1"})
            with urllib.request.urlopen(request, timeout=60) as response, target.with_suffix(".part").open("wb") as output:
                shutil.copyfileobj(response, output)
            if hashlib.sha256(target.with_suffix(".part").read_bytes()).hexdigest() != CHECKSUMS[name]:
                raise RuntimeError(f"Downloaded asset checksum mismatch: {name}")
            target.with_suffix(".part").replace(target)
            return target
        except OSError:
            if attempt == 2:
                raise
            time.sleep(2 ** attempt)


def copy_rime(destination, windows=False):
    src = checkout("rime-ice", "https://github.com/iDvel/rime-ice.git", ICE_REV, ICE_REV)
    destination.mkdir(parents=True, exist_ok=True)
    included = ("lua", "cn_dicts", "en_dicts", "opencc")
    for name in included:
        shutil.copytree(src / name, destination / name, dirs_exist_ok=True)
    # APK extraction and an older preparation can leave starter schemas in this generated tree.
    for path in destination.glob("*.schema.yaml"):
        if path.name not in PACKAGED_SCHEMAS:
            path.unlink()
    for path in src.glob("*.yaml"):
        if path.name.endswith(".schema.yaml") and path.name not in PACKAGED_SCHEMAS:
            continue
        shutil.copyfile(path, destination / path.name)
    (destination / "NOTICE-rime-ice.txt").write_text(
        f"Rime Ice, GPL-3.0-only\nhttps://github.com/iDvel/rime-ice/tree/{ICE_REV}\n", encoding="utf-8"
    )
    shutil.copyfile(src / "LICENSE", destination / "LICENSE-rime-ice.txt")
    if (src / "AUTHORS").exists():
        shutil.copyfile(src / "AUTHORS", destination / "AUTHORS-rime-ice.txt")
    default_yaml = destination / "default.yaml"
    text = default_yaml.read_text(encoding="utf-8")
    scheme_list = "schema_list:\n" + "".join(f"  - schema: {item}\n" for item in INPUT_SCHEMES)
    text, count = re.subn(r"(?ms)^schema_list:\n.*?(?=^[a-z]\w*:)", scheme_list + "\n", text, count=1)
    if count != 1:
        raise RuntimeError("Rime Ice schema list moved")
    text = re.sub(r"(?m)^    - traditionalization[ \t]*(?:#.*)?\n", "", text)
    text = re.sub(r"(?m)^(  page_size: )5\b", r"\g<1>7", text, count=1)
    default_yaml.write_text(text, encoding="utf-8")
    spelling_sets = []
    for schema_id in INPUT_SCHEMES:
        schema = destination / f"{schema_id}.schema.yaml"
        text = schema.read_text(encoding="utf-8")
        if schema_id == "rime_ice":
            text = text.replace("  name: 雾凇拼音\n", "  name: 普通拼音\n", 1)
        text = re.sub(r"(?m)^  - name: traditionalization[^\n]*\n    states:[^\n]*\n", "", text)
        text = text.replace("switches:\n", "switches:\n  - name: simplification\n    reset: 1\n"
                            "  - name: prediction\n    states: [联想关, 联想开]\n    reset: 1\n", 1)
        text = text.replace("  processors:\n", "  processors:\n    - predictor\n", 1)
        text = text.replace("  translators:\n", "  translators:\n    - predict_translator\n", 1)
        text = re.sub(r"(?m)^    - simplifier@traditionalize[^\n]*\n", "", text)
        text = re.sub(r"(?m)^    - uniquifier",
                      "    - simplifier@link_simplified\n    - lua_filter@*link_words\n    - uniquifier", text, count=1)
        algebra = re.search(r"(?m)^  algebra:\n(?P<rules>(?:[ \t]+[^\n]*\n|#[^\n]*\n|\n)*)", text)
        if not algebra:
            raise RuntimeError(f"Missing spelling rules in {schema_id}")
        exact = [line.strip().removeprefix("- ") for line in algebra["rules"].splitlines()
                 if line.lstrip().startswith("- ")]
        # Preserve order and native Rime penalties. The new alternatives are evaluated on full syllables.
        fuzzy = ["fuzz/eng$/en/", "fuzz/en$/eng/", "fuzz/ing$/in/", "fuzz/in$/ing/"]
        for name, rules in (("exact", exact), ("fuzzy", fuzzy + exact)):
            values = [rule.split(" #", 1)[0].strip() for rule in rules]
            spelling_sets.append(f"{schema_id}_{name}:\n" + "".join(f"  - {json.dumps(rule, ensure_ascii=False)}\n" for rule in values))
        text = text[:algebra.start()] + f"  algebra:\n    __include: link_profile:/{schema_id}\n\n" + text[algebra.end():]
        text += ("\n\npredictor:\n  db: predict.db\n  max_candidates: 12\n  max_iterations: 2\n"
                 "\nlink_simplified:\n  option_name: simplification\n  opencc_config: t2s.json\n  tips: none\n")
        schema.write_text(text, encoding="utf-8")
    (destination / "link_spellings.yaml").write_text("\n".join(spelling_sets), encoding="utf-8")
    shutil.copyfile(ROOT / "rime/link_profile.yaml", destination / "link_profile.yaml")
    shutil.copyfile(ROOT / "rime/link_words.lua", destination / "lua/link_words.lua")
    if not windows:
        shutil.copyfile(ROOT / "rime/english_corrections.tsv", destination / "english_corrections.tsv")
    shutil.copyfile(download("predict.db", "https://github.com/rime/librime-predict/releases/download/data-1.0/predict.db"),
                    destination / "predict.db")
    if windows:
        (destination / "weasel.custom.yaml").unlink(missing_ok=True)
        for name in ("default.custom.yaml", "shuangpin_link.custom.yaml"):
            shutil.copyfile(ROOT / "rime" / name, destination / name)


def extract_apk(file, native, assets, arch):
    with ZipFile(file) as apk:
        files = apk.namelist()
        for name in files:
            if name.startswith(f"lib/{arch}/") and name.endswith(".so"):
                base = PurePosixPath(name).name
                target = native / arch / base
            elif name.startswith("assets/shared/") and not name.endswith("/"):
                parts = PurePosixPath(name).parts[2:]
                if any(part in ("..", ".") for part in parts):
                    raise ValueError("Invalid asset path")
                target = assets.joinpath(*parts)
            else:
                continue
            if target.is_symlink():
                target.unlink()
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(apk.read(name))
        if not list((native / arch).glob("*rime*.so")):
            raise RuntimeError(f"Missing Rime JNI binaries: {arch}")


def change(file, edits):
    text = file.read_text(encoding="utf-8")
    for before, after in edits:
        if after in text:
            continue
        if text.count(before) != 1:
            raise RuntimeError(f"Upstream layout changed: {file.name}, {before[:75]}")
        text = text.replace(before, after, 1)
    file.write_text(text, encoding="utf-8")


def restrict_android_schemes(java):
    manager = java / "core/data/UserDataManager.kt"
    text = manager.read_text(encoding="utf-8")
    if "val inputSchemeIds" not in text:
        ids = ", ".join(f'"{item}"' for item in INPUT_SCHEMES)
        text, count = re.subn(
            r'(?ms)^    private const val SCHEMA_LIST_CUSTOM_PATCH = """.*?^    """\n',
            f"    val inputSchemeIds = listOf({ids})\n", text, count=1,
        )
        if count != 1:
            raise RuntimeError("Trime's first-run schema configuration moved")
        manager.write_text(text, encoding="utf-8")
    change(manager, [
        ('''    fun ensureDefaultCustomConfig() {
        val custom = File(userDataDir, DEFAULT_CUSTOM_FILE_NAME)
        if (!custom.exists() && custom.createNewFile()) {
            custom.writeText(SCHEMA_LIST_CUSTOM_PATCH.trimIndent())
        }
    }''', r'''    fun ensureDefaultCustomConfig(): Boolean {
        val custom = File(userDataDir, DEFAULT_CUSTOM_FILE_NAME)
        val original = if (custom.isFile) custom.readText().replace("\r\n", "\n") else ""
        val entry = Regex("""(?m)^([ \t]+)schema_list[ \t]*:[^\n]*(?:\n(?:\1[ \t]+|\1(?=-))[^\n]*)*""").find(original)
        val patchHeader = Regex("""(?m)^patch[ \t]*:[ \t]*(?:\{[ \t]*\})?[ \t]*(?:#[^\n]*)?$""").find(original)
        fun schemaList(indent: String): String = buildString {
            append(indent).append("schema_list:")
            for (id in inputSchemeIds) append('\n').append(indent).append("  - schema: ").append(id)
        }
        val updated = when {
            entry != null -> original.replaceRange(entry.range, schemaList(entry.groupValues[1]))
            patchHeader != null -> {
                val indent = Regex("""(?m)^([ \t]+)\S""").find(original, patchHeader.range.last + 1)
                    ?.groupValues?.get(1) ?: "  "
                original.replaceRange(patchHeader.range, "patch:\n${schemaList(indent)}")
            }
            // Existing flow-style patch maps are left intact. The keyboard picker still uses the supported IDs.
            Regex("""(?m)^patch[ \t]*:""").containsMatchIn(original) -> return false
            original.isBlank() -> "patch:\n${schemaList("  ")}\n"
            else -> "${original.trimEnd()}\n\npatch:\n${schemaList("  ")}\n"
        }
        if (updated == original) return false
        custom.parentFile?.mkdirs()
        val atomic = AtomicFile(custom)
        val out = atomic.startWrite()
        try {
            out.write(updated.toByteArray(Charsets.UTF_8))
            atomic.finishWrite(out)
        } catch (error: IOException) {
            atomic.failWrite(out)
            throw error
        }
        return true
    }'''),
    ])
    rime = java / "core/Rime.kt"
    change(rime, [
        ('    override suspend fun availableSchemata(): Array<SchemaItem> = withRimeContext { getAvailableRimeSchemaList() }',
         '''    private fun Array<SchemaItem>.supportedSchemata(): Array<SchemaItem> = UserDataManager.inputSchemeIds
        .mapNotNull { id -> firstOrNull { it.id == id } }
        .toTypedArray()

    override suspend fun availableSchemata(): Array<SchemaItem> = withRimeContext { getAvailableRimeSchemaList().supportedSchemata() }'''),
        ('    override suspend fun enabledSchemata(): Array<SchemaItem> = withRimeContext { getSelectedRimeSchemaList() }',
         '    override suspend fun enabledSchemata(): Array<SchemaItem> = withRimeContext { getSelectedRimeSchemaList().supportedSchemata() }'),
        ('    override suspend fun setEnabledSchemata(schemaIds: Array<String>) = withRimeContext { selectRimeSchemas(schemaIds) }',
         '    override suspend fun setEnabledSchemata(schemaIds: Array<String>) = withRimeContext { selectRimeSchemas(UserDataManager.inputSchemeIds.toTypedArray()) }'),
        ('    override suspend fun selectedSchemata(): Array<SchemaItem> = withRimeContext { getRimeSchemaList() }',
         '    override suspend fun selectedSchemata(): Array<SchemaItem> = withRimeContext { getRimeSchemaList().supportedSchemata() }'),
        ('    override suspend fun selectSchema(schemaId: String) = withRimeContext { selectRimeSchema(schemaId) }',
         '    override suspend fun selectSchema(schemaId: String) = withRimeContext { schemaId in UserDataManager.inputSchemeIds && selectRimeSchema(schemaId) }'),
        ('        UserDataManager.ensureDefaultCustomConfig()',
         '        val schemaConfigChanged = UserDataManager.ensureDefaultCustomConfig()'),
        ('            fullCheck,\n        )', '            fullCheck || schemaConfigChanged,\n        )'),
    ])
    picker = java / "ime/dialog/EnabledSchemaPickerDialog.kt"
    change(picker, [
        ('                setTitle(R.string.select_current_schema)', '                setTitle("输入方案")'),
    ])
    for filename, api in (("ime/keyboard/CommonKeyboardActionListener.kt", "api"),
                          ("ime/switches/SwitchOptionWindow.kt", "r")):
        path = java / filename
        text = path.read_text(encoding="utf-8")
        call = f"EnabledSchemaPickerDialog.build({api}, service.lifecycleScope, context)"
        text = re.sub(
            re.escape(call) + r" \{\n +setNegativeButton\(R\.string\.enable_schemata\) \{ _, _ ->\n +AppUtils\.launchMainToSchemaList\(context\)\n +\}\n +\}",
            call, text, count=1,
        )
        path.write_text(text, encoding="utf-8")


def prepare_android():
    src = checkout("trime", "https://github.com/osfans/trime.git", f"refs/tags/{TRIME_TAG}", TRIME_REV)
    app = src / "app"
    assets = app / "src/main/assets/shared"
    java = app / "src/main/java/com/osfans/trime"
    for arch, name in zip(ARCHES, ("trime-arm64.apk", "trime-x86_64.apk"), strict=True):
        apk = download(name, f"https://github.com/osfans/trime/releases/download/{TRIME_TAG}/com.osfans.trime-v3.3.13-0-gb5cad1a4-{arch}-release.apk")
        extract_apk(apk, app / "prebuilt", assets, arch)
    copy_rime(assets)
    shutil.copytree(ROOT / "android/overlay", java / "link", dirs_exist_ok=True)
    shutil.copyfile(ROOT / "android/res/xml/link_update_paths.xml", app / "src/main/res/xml/link_update_paths.xml")
    release = ROOT / "updates"
    update_assets = assets.parent / "ime-update"
    update_assets.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(release / "public-key.spki.b64", update_assets / "public-key.spki.b64")
    shutil.copyfile(release / "version.json", update_assets / "version.json")
    version = json.loads((release / "version.json").read_text(encoding="utf-8"))
    version_code, version_name = version["versionCode"], version["versionName"]
    if type(version_code) is not int or not 1 <= version_code <= 2_100_000_000:
        raise ValueError("Invalid Android update version code")
    if not isinstance(version_name, str) or not re.fullmatch(r"[0-9A-Za-z][0-9A-Za-z.+-]{0,63}", version_name):
        raise ValueError("Invalid Android update version name")
    gradle = app / "build.gradle.kts"
    old_plugin = '    id("com.osfans.trime.opencc-data")\n'
    text = gradle.read_text(encoding="utf-8")
    text = text.replace(old_plugin, "", 1)
    for field, before, after in (("versionCode", r"[0-9]+", str(version_code)),
                                 ("versionName", r'"[^"\r\n]*"', f'"{version_name}"')):
        text, count = re.subn(rf"(?m)^        {field} = {before}$", f"        {field} = {after}", text)
        if count != 1:
            raise RuntimeError(f"Upstream layout changed: {gradle.name}, {field}")
    gradle.write_text(text, encoding="utf-8")
    change(gradle, [
        ('applicationId = "com.osfans.trime"', 'applicationId = "io.github.winner798.shuangpinlink"'),
        ("minSdk = 21", "minSdk = 29"),
        ("dependencies {\n    ksp", 'dependencies {\n    implementation("com.journeyapps:zxing-android-embedded:4.3.0")\n    ksp'),
        ('resValue("string", "trime_app_name", "@string/app_name_release")', 'resValue("string", "trime_app_name", "双拼互联")'),
        ('resValue("string", "trime_app_name", "@string/app_name_debug")', 'resValue("string", "trime_app_name", "双拼互联（测试）")'),
    ])
    change(app / "src/main/AndroidManifest.xml", [
        ('    <uses-permission android:name="android.permission.VIBRATE" />',
         '    <uses-permission android:name="android.permission.INTERNET" />\n    <uses-permission android:name="android.permission.VIBRATE" />'),
        ('    <uses-permission android:name="android.permission.POST_NOTIFICATIONS" />',
         '    <uses-permission android:name="android.permission.POST_NOTIFICATIONS" />\n'
         '    <uses-permission android:name="android.permission.REQUEST_INSTALL_PACKAGES" />'),
        ('android:allowBackup="true"', 'android:allowBackup="false"\n        android:usesCleartextTraffic="false"'),
        ('        <!-- Settings activity -->', '        <activity android:name=".link.LinkActivity" android:exported="false" />\n\n        <!-- Settings activity -->'),
        ('        <activity android:name=".link.LinkActivity" android:exported="false" />',
         '        <provider\n'
         '            android:name=".link.LinkUpdateFileProvider"\n'
         '            android:authorities="${applicationId}.link-updates"\n'
         '            android:exported="false"\n'
         '            android:grantUriPermissions="true">\n'
         '            <meta-data\n'
         '                android:name="android.support.FILE_PROVIDER_PATHS"\n'
         '                android:resource="@xml/link_update_paths" />\n'
         '        </provider>\n\n'
         '        <activity android:name=".link.LinkActivity" android:exported="false" />'),
    ])
    restrict_android_schemes(java)
    change(java / "core/RimeApi.kt", [
        ("    suspend fun syncUserData(): Boolean", "    suspend fun clearLinkedDictionary()\n    suspend fun syncUserData(): Boolean"),
    ])
    change(java / "core/Rime.kt", [
        ("    override suspend fun syncUserData(): Boolean = lock.withLock {",
         """    override suspend fun clearLinkedDictionary(): Unit = lock.withLock {
        withRimeContext {
            exitRime()
            try {
                val user = UserDataManager.userDataDir
                val database = java.io.File(user, "rime_ice.userdb")
                check(!database.exists() || database.deleteRecursively())
                val sync = java.io.File(user, "sync")
                for (folder in sync.listFiles().orEmpty().filter { it.isDirectory }) {
                    val snapshot = java.io.File(folder, "rime_ice.userdb.txt")
                    check(!snapshot.exists() || snapshot.delete())
                }
            } finally {
                startRime(false)
            }
        }
    }

    override suspend fun syncUserData(): Boolean = lock.withLock {"""),
    ])
    service = java / "ime/core/TrimeInputMethodService.kt"
    change(service, [
        ("    private lateinit var rime: RimeSession", "    private lateinit var rime: RimeSession\n    private lateinit var clipboardLink: com.osfans.trime.link.ClipboardLink"),
        ("        super.onCreate()", "        super.onCreate()\n        clipboardLink = com.osfans.trime.link.ClipboardLink(this, lifecycleScope, rime)\n        clipboardLink.start()"),
        ("        super.onDestroy()", "        clipboardLink.close()\n        super.onDestroy()"),
        ('        Timber.d("onStartInput: restarting=$restarting")',
         '        Timber.d("onStartInput: restarting=$restarting")\n        clipboardLink.editor(attribute)'),
        ('        Timber.d("onStartInputView: restarting=$restarting")',
         '        Timber.d("onStartInputView: restarting=$restarting")\n        clipboardLink.resume(attribute)'),
    ])
    change(java / "ui/main/settings/ProfileSettingsFragment.kt", [
        ("            addCategory(R.string.storage)",
         "            addPreference(Preference(ctx).apply {\n"
         '                title = "双拼互联 · 剪贴板和词库"\n'
         "                isIconSpaceReserved = false\n"
         "                intent = android.content.Intent(ctx, com.osfans.trime.link.LinkActivity::class.java)\n"
         "            })\n"
         "            addCategory(R.string.storage)"),
    ])
    change(java / "ime/keyboard/KeyboardWindow.kt", [
        ("        switchKeyboard(targetKeyboard)\n        val isAsciiMode", 
         "        val privateField = com.osfans.trime.link.sensitiveEditor(info)\n"
         '        switchKeyboard(if (privateField) ".ascii" else targetKeyboard)\n'
         "        val isAsciiMode"),
        ('        if (targetKeyboard == ".ascii" || targetKeyboard == "number") {',
         '        if (privateField || targetKeyboard == ".ascii" || targetKeyboard == "number") {'),
    ])
    runpy.run_path(str(ROOT / "android/apply_design.py"))["apply_design"](src, assets)


def prepare_windows():
    copy_rime(BUILD / "windows-data", windows=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("target", choices=("android", "windows"))
    BUILD.mkdir(parents=True, exist_ok=True)
    args = parser.parse_args()
    {"android": prepare_android, "windows": prepare_windows}[args.target]()
