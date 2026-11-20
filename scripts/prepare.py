"""Prepare pinned upstream code/assets and apply the Shuangpin Link overlay."""

import argparse
import hashlib
import re
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
WEASEL_TAG = "0.17.4"
ARCHES = ("arm64-v8a", "x86_64")
CHECKSUMS = {
    "weasel.exe": "cf509534a8f5f8af9c98ed7cbb8f135439f145a8cbe7e50ede42bb5b5ab45c29",
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
    for path in src.glob("*.yaml"):
        if path.name.endswith(".schema.yaml") and path.name not in (
            "double_pinyin_mspy.schema.yaml", "melt_eng.schema.yaml", "radical_pinyin.schema.yaml",
        ):
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
    text, count = re.subn(r"(?ms)^schema_list:\n.*?(?=^[a-z]\w*:)",
                          "schema_list:\n  - schema: double_pinyin_mspy\n\n", text, count=1)
    if count != 1:
        raise RuntimeError("Rime Ice schema list moved")
    default_yaml.write_text(text, encoding="utf-8")
    schema = destination / "double_pinyin_mspy.schema.yaml"
    text = schema.read_text(encoding="utf-8")
    text = text.replace("switches:\n", "switches:\n  - name: prediction\n    states: [联想关, 联想开]\n    reset: 1\n", 1)
    text = text.replace("  processors:\n", "  processors:\n    - predictor\n", 1)
    text = text.replace("  translators:\n", "  translators:\n    - predict_translator\n", 1)
    text += "\n\npredictor:\n  db: predict.db\n  max_candidates: 5\n  max_iterations: 1\n"
    schema.write_text(text, encoding="utf-8")
    shutil.copyfile(download("predict.db", "https://github.com/rime/librime-predict/releases/download/data-1.0/predict.db"),
                    destination / "predict.db")
    if windows:
        for name in ("default.custom.yaml", "weasel.custom.yaml"):
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
    gradle = app / "build.gradle.kts"
    old_plugin = '    id("com.osfans.trime.opencc-data")\n'
    text = gradle.read_text(encoding="utf-8")
    if old_plugin in text:
        gradle.write_text(text.replace(old_plugin, "", 1), encoding="utf-8")
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
        ('android:allowBackup="true"', 'android:allowBackup="false"\n        android:usesCleartextTraffic="false"'),
        ('        <!-- Settings activity -->', '        <activity android:name=".link.LinkActivity" android:exported="false" />\n\n        <!-- Settings activity -->'),
    ])
    change(java / "core/data/UserDataManager.kt", [
        ("          - schema: luna_pinyin_simp", "          - schema: double_pinyin_mspy"),
    ])
    user_data_manager = java / "core/data/UserDataManager.kt"
    text = user_data_manager.read_text(encoding="utf-8")
    user_data_manager.write_text(text.replace("          - schema: luna_pinyin\n", "", 1), encoding="utf-8")
    keyboard = assets / "trime.yaml"
    text = keyboard.read_text(encoding="utf-8")
    qwerty = re.search(r"(?ms)^  qwerty:\n.*?(?=^  \w+:)", text)
    if not qwerty:
        raise RuntimeError("Trime QWERTY layout missing")
    compact = re.sub(r"(?m)^    - \{width: 5\}\n", "", qwerty[0])
    compact = compact.replace("  qwerty:", "  default:", 1).replace("預設26鍵", "微软双拼 · 26键", 1)
    compact = compact.replace("    - {click: 'l', long_click: ';'}\n", "    - {click: 'l'}\n    - {click: ';', long_click: ':'}\n", 1)
    text, count = re.subn(r"(?ms)(^preset_keyboards:\n)  default:\n.*?(?=^  \w+:)",
                          lambda match: match[1] + compact, text, count=1)
    if count != 1:
        raise RuntimeError("Trime default layout missing")
    keyboard.write_text(text, encoding="utf-8")
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


def prepare_windows():
    download("weasel.exe", f"https://github.com/rime/weasel/releases/download/{WEASEL_TAG}/weasel-0.17.4.0-installer.exe")
    copy_rime(BUILD / "windows-data", windows=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("target", choices=("android", "windows"))
    BUILD.mkdir(parents=True, exist_ok=True)
    args = parser.parse_args()
    {"android": prepare_android, "windows": prepare_windows}[args.target]()
