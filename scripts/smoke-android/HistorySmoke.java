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
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.accessibility.AccessibilityNodeInfo;
import android.view.accessibility.AccessibilityWindowInfo;
import android.view.inputmethod.BaseInputConnection;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.LinearLayout;

/** Runs only against the disposable test editor and a fresh emulator. */
public class HistorySmoke extends Instrumentation {
    private UiAutomation ui;
    private EditText editor;
    private Activity activity;
    private boolean testArrows;
    private static final String SAMPLE = "Pearl clipboard sample " +
        "01234567890123456789012345678901234567890123456789012345678901234567890123456789";
    private static final String PRIVATE = "Excluded test fixture";

    @Override public void onCreate(Bundle args) {
        super.onCreate(args);
        testArrows = args != null && "arrows".equals(args.getString("scenario"));
        start();
    }

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
            if (testArrows) {
                verifyPassiveSuggestions();
                result.putString("status", "Passive predictions yielded to editor actions and explicit selection worked");
                finish(Activity.RESULT_OK, result);
                return;
            }
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

    private void focusField(EditText target) {
        editor = target;
        runOnMainSync(() -> {
            editor.requestFocus();
            editor.setSelection(editor.length());
        });
        keyboard(true);
        boolean[] active = {false};
        for (int i = 0; i < 30; i++) {
            runOnMainSync(() -> active[0] = editor.hasFocus() && activity.getSystemService(InputMethodManager.class).isActive(editor));
            if (active[0]) { SystemClock.sleep(350); return; }
            SystemClock.sleep(100);
        }
        throw new AssertionError("Target editor did not acquire IME focus");
    }

    private void typeNihaoAndSpace() {
        for (int[] key : new int[][]{{754,1937},{807,1594},{644,1766},{114,1766},{914,1594},{530,2108}}) {
            touch(key[0], key[1], 40);
            SystemClock.sleep(85);
        }
    }

    private void openPrediction() {
        runOnMainSync(() -> {
            editor.setText("ABCD");
            editor.setSelection(2);
            activity.getSystemService(InputMethodManager.class).restartInput(editor);
        });
        keyboard(true);
        typeNihaoAndSpace();
        waitForEditor("AB你好CD");
        // A fresh dictionary suggests 吗 after 你好. Check the candidate before sending
        // the key so an empty/disabled prediction menu cannot make the test pass.
        awaitText("吗");
    }

    private void verifyArrow(int key, int expectedCursor) {
        openPrediction();
        String committed = "AB你好CD";
        sendKeyDownUpSync(key);
        boolean[] moved = {false};
        for (int i = 0; i < 40; i++) {
            runOnMainSync(() -> moved[0] = editor.getSelectionStart() == expectedCursor
                && editor.getSelectionEnd() == expectedCursor && committed.contentEquals(editor.getText()));
            if (moved[0] && find("吗") == null) {
                typeNihaoAndSpace();
                waitForEditor(committed.substring(0, expectedCursor) + "你好" + committed.substring(expectedCursor));
                return;
            }
            SystemClock.sleep(100);
        }
        throw new AssertionError(KeyEvent.keyCodeToString(key) + " did not dismiss predictions and move exactly once");
    }

    private void waitForPredictionToClose() {
        long end = SystemClock.uptimeMillis() + 5000;
        while (find("吗") != null) {
            if (SystemClock.uptimeMillis() >= end) throw new AssertionError("Prediction remained on screen");
            SystemClock.sleep(100);
        }
    }

    private void verifyPassiveSuggestions() {
        verifyArrow(KeyEvent.KEYCODE_DPAD_LEFT, 3);
        verifyArrow(KeyEvent.KEYCODE_DPAD_RIGHT, 5);
        openPrediction();
        touch(530, 2108, 40); // On-screen Space
        waitForEditor("AB你好 CD");
        waitForPredictionToClose();
        openPrediction();
        sendKeyDownUpSync(KeyEvent.KEYCODE_DEL);
        waitForEditor("AB你CD");
        waitForPredictionToClose();
        openPrediction();
        typeNihaoAndSpace();
        waitForEditor("AB你好你好CD");
        openPrediction();
        tap(awaitText("吗"));
        waitForEditor("AB你好吗CD");
        openPrediction();
        runOnMainSync(() -> editor.setSelection(0, 2));
        waitForPredictionToClose();
        waitForEditor("AB你好CD");
        openPrediction();
        int[] location = new int[2];
        runOnMainSync(() -> {
            editor.getLocationOnScreen(location);
            location[0] += editor.getTotalPaddingLeft() + 1;
            location[1] += editor.getHeight() / 2;
        });
        touch(location[0], location[1], 60);
        waitForPredictionToClose();
        waitForEditor("AB你好CD");
        openPrediction();
        keyboard(false);
        keyboard(true);
        waitForPredictionToClose();
        waitForEditor("AB你好CD");
    }

    private boolean typeModeSample() {
        // Full Pinyin was selected earlier in the test. The exact committed result
        // distinguishes Chinese from ASCII, including the trailing ASCII space.
        String[] prefix = {""};
        runOnMainSync(() -> prefix[0] = editor.getText().toString());
        typeNihaoAndSpace();
        String[] committed = {""};
        boolean[] composing = {false};
        for (int i = 0; i < 30; i++) {
            runOnMainSync(() -> {
                committed[0] = editor.getText().toString();
                composing[0] = BaseInputConnection.getComposingSpanStart(editor.getText()) >= 0;
            });
            if (!composing[0] && (prefix[0] + "nihao ").equals(committed[0])) return true;
            if (!composing[0] && (prefix[0] + "你好").equals(committed[0])) return false;
            SystemClock.sleep(100);
        }
        throw new AssertionError("Language probe did not commit literal English or the expected Chinese phrase");
    }

    private void verifyPrivateFieldModeRestoration() {
        EditText normal = editor;
        EditText[] password = new EditText[1];
        runOnMainSync(() -> {
            password[0] = new EditText(activity);
            password[0].setSingleLine(true);
            password[0].setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
            ((LinearLayout) normal.getParent()).addView(password[0], new LinearLayout.LayoutParams(-1, -2));
        });
        focusField(normal);
        boolean initialEnglish = typeModeSample();
        for (boolean english : new boolean[]{initialEnglish, !initialEnglish}) {
            if (english != initialEnglish) {
                touch(789, 2108, 45);
                SystemClock.sleep(300);
                if (typeModeSample() != english) throw new AssertionError("Normal editor did not switch to " + (english ? "English" : "Chinese"));
            }
            focusField(password[0]);
            touch(789, 2108, 45);
            if (!typeModeSample()) throw new AssertionError("Password field accepted Chinese composition");
            copy("Unmarked private test fixture", false);
            focusField(normal);
            if (typeModeSample() != english) throw new AssertionError("Password field did not restore " + (english ? "English" : "Chinese"));
        }
        runOnMainSync(() -> {
            ((LinearLayout) normal.getParent()).removeView(password[0]);
            normal.setText("");
            activity.getSystemService(InputMethodManager.class).restartInput(normal);
        });
        keyboard(true);
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
