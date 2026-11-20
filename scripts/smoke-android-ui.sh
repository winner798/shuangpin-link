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
"$ANDROID_HOME/emulator/emulator" -avd shuangpin-ui -no-window -no-audio -no-boot-anim -no-snapshot \
    -gpu swiftshader_indirect -memory 2048 > "$output/emulator.log" 2>&1 &
emulator_pid=$!
timeout 180 adb wait-for-device
timeout 180 bash -c 'until [[ $(adb shell getprop sys.boot_completed | tr -d "\r") == 1 ]]; do sleep 3; done'
adb install "$work/ime.apk"
adb install "$work/editor.apk"
adb shell am start -W -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p "$package"
adb shell settings put secure show_ime_with_hard_keyboard 1
adb shell ime enable "$ime"
adb shell ime set "$ime"
adb shell am start -W -n "$preview/.PreviewActivity"
timeout 120 bash -c 'until adb shell dumpsys input_method | grep -E "(mInputShown|mShowRequested)=true" >/dev/null; do sleep 2; done'
test "$(adb shell settings get secure default_input_method | tr -d '\r')" = "$ime"
sleep 8
adb exec-out screencap -p > "$output/android-keyboard.png"
adb shell input keyevent KEYCODE_N KEYCODE_I KEYCODE_H KEYCODE_K
sleep 2
adb exec-out screencap -p > "$output/android-candidates.png"
adb logcat -d -s AndroidRuntime:E InputMethodManagerService:W > "$output/android-runtime.log"
if grep -F "Process: $package," "$output/android-runtime.log"; then
    exit 1
fi
