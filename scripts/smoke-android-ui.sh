#!/usr/bin/env bash
set -euo pipefail

apk=$1
output=$2
image='system-images;android-35;google_apis;x86_64'
tools="$ANDROID_HOME/build-tools/37.0.0"
platform="$ANDROID_HOME/platforms/android-37.0/android.jar"
package='io.github.winner798.shuangpinlink'
ime="$package/com.osfans.trime.ime.core.TrimeInputMethodService"
preview='io.github.winner798.keyboardpreview'
work=$(mktemp -d)
emulator_pid=

cleanup() {
    if [[ -n $emulator_pid ]]; then
        timeout 10 adb logcat -b main -b system -b crash -d -v threadtime > "$output/android-runtime.log" 2>&1 || true
        timeout 10 adb shell dumpsys input_method > "$output/android-input-method.txt" 2>&1 || true
        timeout 10 adb shell dumpsys activity exit-info "$package" > "$output/android-exit-info.txt" 2>&1 || true
        adb emu kill >/dev/null 2>&1 || kill "$emulator_pid" 2>/dev/null || true
    fi
    rm -rf -- "$work"
}
trap cleanup EXIT
mkdir -p "$output" "$work/classes" "$work/dex"

# A separate editor exercises the app through the Android input-method API.
cat > "$work/AndroidManifest.xml" <<'XML'
<manifest xmlns:android="http://schemas.android.com/apk/res/android"
    package="io.github.winner798.keyboardpreview">
    <application android:label="输入预览" android:theme="@android:style/Theme.Material.Light.NoActionBar">
        <activity android:name=".PreviewActivity" android:exported="true" />
    </application>
</manifest>
XML
cat > "$work/PreviewActivity.java" <<'JAVA'
package io.github.winner798.keyboardpreview;

import android.app.Activity;
import android.os.Bundle;
import android.text.InputType;
import android.view.WindowManager;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.LinearLayout;

public class PreviewActivity extends Activity {
    @Override public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        LinearLayout content = new LinearLayout(this);
        content.setPadding(48, 96, 48, 24);
        content.setOrientation(LinearLayout.VERTICAL);
        EditText editor = new EditText(this);
        editor.setHint("在这里试打");
        editor.setInputType(InputType.TYPE_CLASS_TEXT);
        content.addView(editor, new LinearLayout.LayoutParams(-1, -2));
        setContentView(content);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE);
        editor.requestFocus();
        editor.postDelayed(() -> getSystemService(InputMethodManager.class)
                .showSoftInput(editor, InputMethodManager.SHOW_IMPLICIT), 1000);
    }
}
JAVA

"$tools/aapt2" link -I "$platform" --manifest "$work/AndroidManifest.xml" \
    --min-sdk-version 29 --target-sdk-version 34 -o "$work/editor-unsigned.apk"
javac --release 8 -cp "$platform" -d "$work/classes" "$work/PreviewActivity.java"
"$tools/d8" --min-api 29 --lib "$platform" --output "$work/dex" \
    "$work/classes/io/github/winner798/keyboardpreview/PreviewActivity.class"
zip -jq "$work/editor-unsigned.apk" "$work/dex/classes.dex"
keytool -genkeypair -keystore "$work/test.p12" -storepass android -alias smoke -keyalg RSA \
    -dname 'CN=Android UI regression test' -validity 7
"$tools/apksigner" sign --ks "$work/test.p12" --ks-pass pass:android --out "$work/editor.apk" "$work/editor-unsigned.apk"
"$tools/apksigner" sign --ks "$work/test.p12" --ks-pass pass:android --out "$work/ime.apk" "$apk"

test -e /dev/kvm
sudo chmod a+rw /dev/kvm
sdkmanager 'emulator' "$image"
echo no | avdmanager create avd --force --name shuangpin-ui --package "$image" --device pixel_5
avd_config="${ANDROID_AVD_HOME:-$HOME/.android/avd}/shuangpin-ui.avd/config.ini"
sed -i '/^hw.keyboard[[:space:]]*=/d' "$avd_config"
printf '\nhw.keyboard=no\n' >> "$avd_config"
"$ANDROID_HOME/emulator/emulator" -avd shuangpin-ui -no-window -no-audio -no-boot-anim -no-snapshot \
    -gpu swiftshader_indirect -memory 2048 > "$output/emulator.log" 2>&1 &
emulator_pid=$!
timeout 180 adb wait-for-device
timeout 180 bash -c 'until [[ $(adb shell getprop sys.boot_completed | tr -d "\r") == 1 ]]; do sleep 3; done'
adb logcat -c
adb install "$work/ime.apk"
adb install "$work/editor.apk"
adb shell wm size 1080x2340
adb shell cmd uimode night no
adb shell am start -W -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p "$package"
adb shell settings put secure show_ime_with_hard_keyboard 1
adb shell ime enable "$ime"
adb shell ime set "$ime"
adb shell am start -W -n "$preview/.PreviewActivity"
adb shell input tap 200 300
if ! timeout 100 bash -c 'until adb shell dumpsys input_method | grep -F "mInputShown=true" >/dev/null; do
    if adb logcat -d -s ShuangpinCrash:E | grep -F "Uncaught Android error" >/dev/null; then exit 1; fi
    sleep 2
done'; then
    adb exec-out screencap -p > "$output/android-keyboard.png"
    exit 1
fi
test "$(adb shell settings get secure default_input_method | tr -d '\r')" = "$ime"
adb exec-out screencap -p > "$output/android-keyboard.png"
adb shell pidof "$package"
# On a fresh install the keyboard can be visible while librime is still building its dictionary.
timeout 180 bash -c 'until adb logcat -d -s rime.trime:I | grep -E "finished updating schemas: [1-9][0-9]* success, 0 failure" >/dev/null; do sleep 3; done'
sleep 2
adb exec-out screencap -p > "$output/android-keyboard.png"
adb shell 'input tap 754 1937; input tap 807 1594; input tap 644 1766; input tap 861 1766'
sleep 2
adb exec-out screencap -p > "$output/android-candidates.png"
adb shell input tap 530 2108
adb shell uiautomator dump --compressed /sdcard/shuangpin-editor.xml
adb exec-out cat /sdcard/shuangpin-editor.xml > "$output/android-editor.xml"
grep -F 'text="你好"' "$output/android-editor.xml" >/dev/null

# Give the second scheme a fresh session while keeping the compiled dictionaries.
adb shell am force-stop "$package"
adb shell am force-stop "$preview"
adb shell am start -W -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p "$package"
adb shell ime set "$ime"
adb shell am start -W -n "$preview/.PreviewActivity"
adb shell input tap 200 300
timeout 30 bash -c 'until adb shell dumpsys input_method | grep -F "mInputShown=true" >/dev/null; do sleep 1; done'
sleep 4
adb shell input tap 134 1435
sleep 1
adb exec-out screencap -p > "$output/android-scheme-picker.png"
adb shell uiautomator dump /sdcard/shuangpin-schemes.xml
adb exec-out cat /sdcard/shuangpin-schemes.xml > "$output/android-schemes.xml"
read -r scheme_x scheme_y < <(python3 - "$output/android-schemes.xml" <<'PY'
import re
import sys
import xml.etree.ElementTree as ET

rows = ET.parse(sys.argv[1]).findall('.//node[@class="android.widget.CheckedTextView"]')
assert [row.get("text") for row in rows] == ["微软双拼", "普通拼音"]
left, top, right, bottom = map(int, re.findall(r"\d+", rows[1].get("bounds")))
print((left + right) // 2, (top + bottom) // 2)
PY
)
adb shell input tap "$scheme_x" "$scheme_y"
# Wait for the new scheme's keyboard and its candidate view to attach.
sleep 4
adb shell 'input tap 754 1937; sleep 0.2; input tap 807 1594; sleep 0.2; input tap 644 1766; sleep 0.2; input tap 114 1766; sleep 0.2; input tap 914 1594'
sleep 1
adb exec-out screencap -p > "$output/android-full-pinyin-candidates.png"
adb shell input tap 530 2108
sleep 1
adb shell uiautomator dump --compressed /sdcard/shuangpin-editor.xml
adb exec-out cat /sdcard/shuangpin-editor.xml > "$output/android-editor-full-pinyin.xml"
grep -F 'text="你好"' "$output/android-editor-full-pinyin.xml" >/dev/null

# Enable the actual opt-in switch before exercising the on-screen English keys.
adb shell am force-stop "$preview"
adb shell am start -W -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p "$package"
correction=''
for _ in {1..5}; do
    adb shell uiautomator dump --compressed /sdcard/shuangpin-options.xml >/dev/null
    adb exec-out cat /sdcard/shuangpin-options.xml > "$output/android-options.xml"
    correction=$(python3 - "$output/android-options.xml" <<'PY'
import re
import sys
import xml.etree.ElementTree as ET

for node in ET.parse(sys.argv[1]).findall('.//node[@text="英文自动纠错"]'):
    left, top, right, bottom = map(int, re.findall(r"\d+", node.get("bounds")))
    if bottom - top >= 75:
        assert node.get("checked") == "false"
        print((left + right) // 2, (top + bottom) // 2)
        break
PY
)
    if [[ -n $correction ]]; then break; fi
    adb shell input swipe 540 1810 540 980 330
    sleep 1
done
test -n "$correction"
read -r correction_x correction_y <<< "$correction"
adb shell input tap "$correction_x" "$correction_y"
adb shell uiautomator dump --compressed /sdcard/shuangpin-options.xml >/dev/null
adb exec-out cat /sdcard/shuangpin-options.xml > "$output/android-options.xml"
python3 - "$output/android-options.xml" <<'PY'
import sys
import xml.etree.ElementTree as ET

node = ET.parse(sys.argv[1]).find('.//node[@text="英文自动纠错"]')
assert node is not None and node.get("checked") == "true"
PY

adb shell am start -W -n "$preview/.PreviewActivity"
adb shell input tap 200 300
timeout 30 bash -c 'until adb shell dumpsys input_method | grep -F "mInputShown=true" >/dev/null; do sleep 1; done'
sleep 3
adb shell 'input tap 789 2108; sleep 0.5; input tap 486 1594; input tap 270 1594; input tap 644 1766; input tap 530 2108'
sleep 1
adb shell uiautomator dump --compressed /sdcard/shuangpin-editor.xml >/dev/null
adb exec-out cat /sdcard/shuangpin-editor.xml > "$output/android-editor-english.xml"
grep -F 'text="the "' "$output/android-editor-english.xml" >/dev/null
