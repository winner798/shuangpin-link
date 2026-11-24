package io.github.winner798.keyboardpreview;

import android.accessibilityservice.AccessibilityServiceInfo;
import android.app.Activity;
import android.app.Instrumentation;
import android.app.UiAutomation;
import android.content.ClipData;
import android.content.ClipDescription;
import android.content.ClipboardManager;
import android.content.Intent;
import android.graphics.Rect;
import android.os.Bundle;
import android.os.PersistableBundle;
import android.os.SystemClock;
import android.text.InputType;
import android.view.MotionEvent;
import android.view.accessibility.AccessibilityNodeInfo;
import android.view.accessibility.AccessibilityWindowInfo;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;

/** Runs only against the disposable test editor and a fresh emulator. */
public class HistorySmoke extends Instrumentation {
    private UiAutomation ui;
    private EditText editor;
    private Activity activity;
    private static final String SAMPLE = "Pearl clipboard sample " +
        "01234567890123456789012345678901234567890123456789012345678901234567890123456789";
    private static final String PRIVATE = "Excluded test fixture";

    @Override public void onCreate(Bundle args) { super.onCreate(args); start(); }

    @Override public void onStart() {
        Bundle result = new Bundle();
        try {
            ui = getUiAutomation();
            AccessibilityServiceInfo access = ui.getServiceInfo();
            access.flags |= AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS;
            ui.setServiceInfo(access);
            Intent open = new Intent(getTargetContext(), PreviewActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            activity = startActivitySync(open);
            editor = activity.findViewById(android.R.id.content).findViewWithTag("sampleEditor");
            keyboard(true);
            verifyPrivateFieldModeRestoration();
            // Use the symbol printed over Z on the real 1080-pixel portrait keyboard.
            touch(216, 1937, 750);
            touch(540, 1766, 35);
            tap(awaitText("@gmail.com"));
            waitForEditor("@gmail.com");
            runOnMainSync(() -> editor.setText(""));
            keyboard(false);
            copy(SAMPLE, false);
            keyboard(true);
            tap(awaitText("Pearl clipboard sample"));
            awaitText("最近 1 条");
            tap(awaitText("查看"));
            awaitText(SAMPLE);
            tap(awaitText("粘贴"));
            waitForEditor(SAMPLE);
            copy(PRIVATE, true);
            SystemClock.sleep(500);
            copy("Second ordinary test clip", false);
            tap(awaitText("Second ordinary test clip"));
            awaitText("最近 2 条");
            if (find(PRIVATE) != null) throw new AssertionError("A marked clip entered history");
            tap(awaitText("删除"));
            awaitText("最近 1 条");
            tap(awaitText("清空"));
            tap(awaitText("确认清空"));
            awaitText("暂无剪贴板历史");
            result.putString("status", "Clipboard preview, paste, deletion and sensitive exclusion verified");
            finish(Activity.RESULT_OK, result);
        } catch (Throwable failure) {
            result.putString("error", failure.toString());
            finish(Activity.RESULT_CANCELED, result);
        }
    }

    private void resetField(int type) {
        runOnMainSync(() -> {
            editor.setInputType(type);
            editor.setText("");
            activity.getSystemService(InputMethodManager.class).restartInput(editor);
        });
        keyboard(true);
        SystemClock.sleep(400);
    }

    private boolean typeModeSample() {
        // Full Pinyin was selected earlier in the test. The exact committed result
        // distinguishes Chinese from ASCII, including the trailing ASCII space.
        for (int[] key : new int[][]{{754,1937},{807,1594},{644,1766},{114,1766},{914,1594},{530,2108}}) {
            touch(key[0], key[1], 40);
            SystemClock.sleep(85);
        }
        String[] committed = {""};
        for (int i = 0; i < 30; i++) {
            runOnMainSync(() -> committed[0] = editor.getText().toString());
            if ("nihao ".equals(committed[0])) return true;
            if ("你好".equals(committed[0])) return false;
            SystemClock.sleep(100);
        }
        throw new AssertionError("Language probe did not commit literal English or the expected Chinese phrase");
    }

    private void verifyPrivateFieldModeRestoration() {
        int normal = InputType.TYPE_CLASS_TEXT;
        int password = normal | InputType.TYPE_TEXT_VARIATION_PASSWORD;
        for (boolean english : new boolean[]{false, true}) {
            resetField(normal);
            if (typeModeSample() != english) {
                resetField(normal);
                touch(789, 2108, 45);
                SystemClock.sleep(200);
                if (typeModeSample() != english) throw new AssertionError("Could not select the requested language");
            }
            resetField(password);
            touch(789, 2108, 45);
            if (!typeModeSample()) throw new AssertionError("Password field accepted Chinese composition");
            copy("Unmarked private test fixture", false);
            resetField(normal);
            if (typeModeSample() != english) throw new AssertionError("Password field changed the previous language");
        }
        resetField(normal);
    }

    private void waitForEditor(String expected) {
        final String[] current = {""};
        for (int i = 0; i < 40; i++) {
            runOnMainSync(() -> current[0] = editor.getText().toString());
            if (expected.equals(current[0])) return;
            SystemClock.sleep(100);
        }
        throw new AssertionError("The selected text did not reach the editor");
    }

    private void copy(String text, boolean sensitive) {
        runOnMainSync(() -> {
            ClipData clip = ClipData.newPlainText("Test", text);
            if (sensitive) {
                PersistableBundle metadata = new PersistableBundle();
                metadata.putBoolean(ClipDescription.EXTRA_IS_SENSITIVE, true);
                clip.getDescription().setExtras(metadata);
            }
            activity.getSystemService(ClipboardManager.class).setPrimaryClip(clip);
        });
    }

    private void keyboard(boolean show) {
        long end = SystemClock.uptimeMillis() + 10000;
        while (SystemClock.uptimeMillis() < end) {
            runOnMainSync(() -> {
                InputMethodManager manager = activity.getSystemService(InputMethodManager.class);
                if (show) { editor.requestFocus(); manager.showSoftInput(editor, InputMethodManager.SHOW_IMPLICIT); }
                else manager.hideSoftInputFromWindow(editor.getWindowToken(), 0);
            });
            SystemClock.sleep(250);
            boolean visible = ui.getWindows().stream().anyMatch(w ->
                w.getType() == AccessibilityWindowInfo.TYPE_INPUT_METHOD && w.getRoot() != null);
            if (visible == show) return;
        }
        throw new AssertionError("Keyboard visibility did not change to " + show);
    }

    private AccessibilityNodeInfo find(String prefix) {
        for (AccessibilityWindowInfo win : ui.getWindows()) {
            if (win.getType() != AccessibilityWindowInfo.TYPE_INPUT_METHOD) continue;
            AccessibilityNodeInfo root = win.getRoot();
            if (root == null) continue;
            for (AccessibilityNodeInfo node : root.findAccessibilityNodeInfosByText(prefix)) {
                if (node.isVisibleToUser() && node.getText() != null &&
                    node.getText().toString().startsWith(prefix)) return node;
            }
        }
        return null;
    }

    private AccessibilityNodeInfo awaitText(String prefix) {
        long end = SystemClock.uptimeMillis() + 10000;
        do {
            AccessibilityNodeInfo found = find(prefix);
            if (found != null) return found;
            SystemClock.sleep(150);
        } while (SystemClock.uptimeMillis() < end);
        throw new AssertionError("Missing IME view: " + prefix);
    }

    private void tap(AccessibilityNodeInfo node) {
        Rect bounds = new Rect();
        node.getBoundsInScreen(bounds);
        touch(bounds.centerX(), bounds.centerY(), 30);
    }

    private void touch(int x, int y, long duration) {
        long at = SystemClock.uptimeMillis();
        for (int action : new int[]{MotionEvent.ACTION_DOWN, MotionEvent.ACTION_UP}) {
            if (action == MotionEvent.ACTION_UP) SystemClock.sleep(duration);
            MotionEvent event = MotionEvent.obtain(at, SystemClock.uptimeMillis(), action, x, y, 0);
            event.setSource(android.view.InputDevice.SOURCE_TOUCHSCREEN);
            ui.injectInputEvent(event, true);
            event.recycle();
        }
    }
}
