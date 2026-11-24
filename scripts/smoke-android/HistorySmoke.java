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
