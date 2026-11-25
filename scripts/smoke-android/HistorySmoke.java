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
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.LinearLayout;

/** Runs only against the disposable test editor and a fresh emulator. */
public class HistorySmoke extends Instrumentation {
    private UiAutomation ui;
    private EditText editor;
    private Activity activity;
    private String scenario;
    private static final String SAMPLE = "Pearl clipboard sample " +
        "01234567890123456789012345678901234567890123456789012345678901234567890123456789";
    private static final String PRIVATE = "Excluded test fixture";

    @Override public void onCreate(Bundle args) {
        super.onCreate(args);
        scenario = args == null ? null : args.getString("scenario");
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
            if ("double-pinyin-order".equals(scenario)) {
                composeSample("ah", "昂");
                AccessibilityNodeInfo phrase = awaitText("安徽");
                Rect word = new Rect();
                phrase.getBoundsInScreen(word);
                for (String character : new String[]{"昂", "肮", "盎", "卬"}) {
                    Rect letter = new Rect();
                    awaitText(character).getBoundsInScreen(letter);
                    if (letter.centerX() >= word.left || !word.contains(word.centerX(), letter.centerY()))
                        throw new AssertionError(character + " should precede 安徽 on the same candidate row");
                }
                tap(phrase);
                waitForEditor("安徽");
                result.putString("status", "MS Shuangpin single characters precede a selectable phrase");
                finish(Activity.RESULT_OK, result);
                return;
            }
            if ("arrows".equals(scenario)) {
                verifyFullPinyinSingleSyllable();
                verifyPassiveSuggestions();
                verifyBundledWords();
                verifyLearnedContinuation();
                verifyUppercaseCode();
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
            runOnMainSync(() -> { editor.setText("左右"); editor.setSelection(1); });
            keyboard(false);
            copy(SAMPLE, false);
            keyboard(true);
            AccessibilityNodeInfo preview = awaitText("Pearl clipboard sample");
            if (!preview.getText().toString().endsWith("…")) throw new AssertionError("Expected a shortened preview");
            tap(preview);
            waitForEditor("左" + SAMPLE + "右");
            copy(PRIVATE, true);
            SystemClock.sleep(500);
            if (find(PRIVATE) != null) throw new AssertionError("A marked clip appeared in the bar");
            runOnMainSync(() -> editor.setSelection(1, 1 + SAMPLE.length()));
            copy("Second ordinary test clip", false);
            tap(awaitText("Second ordinary test clip"));
            waitForEditor("左Second ordinary test clip右");
            copy("Second ordinary test clip", false);
            Rect bounds = new Rect();
            awaitText("Second ordinary test clip").getBoundsInScreen(bounds);
            touch(bounds.centerX(), bounds.centerY(), 850);
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

    private void composeSample(String spelling, String first) {
        runOnMainSync(() -> {
            editor.setText("");
            activity.getSystemService(InputMethodManager.class).restartInput(editor);
        });
        keyboard(true);
        for (char ch : spelling.toCharArray()) {
            switch (ch) { // Positions on the 1080 px emulator keyboard.
                case 'a': touch(114, 1766, 40); break;
                case 'h': touch(644, 1766, 40); break;
                case 'x': touch(324, 1937, 40); break;
                case 'i': touch(807, 1594, 40); break;
                case 'n': touch(754, 1937, 40); break;
                case '\'': sendKeyDownUpSync(KeyEvent.KEYCODE_APOSTROPHE); break;
                default: throw new IllegalArgumentException(spelling);
            }
            SystemClock.sleep(85);
        }
        awaitText(first);
    }

    private void verifyFullPinyinSingleSyllable() {
        // xian also matches xi'an. The old fourth and fifth choices were 西安 and 西岸.
        for (int key : new int[]{KeyEvent.KEYCODE_4, KeyEvent.KEYCODE_5}) {
            composeSample("xian", "先");
            sendKeyDownUpSync(key);
            String[] selected = {""};
            for (int attempt = 0; attempt < 40; attempt++) {
                runOnMainSync(() -> {
                    if (BaseInputConnection.getComposingSpanStart(editor.getText()) < 0)
                        selected[0] = editor.getText().toString();
                });
                if (!selected[0].isEmpty()) break;
                SystemClock.sleep(100);
            }
            String text = selected[0];
            if (text.codePointCount(0, text.length()) != 1 || !Character.isIdeographic(text.codePointAt(0)))
                throw new AssertionError("Expected a single character for xian at " +
                    KeyEvent.keyCodeToString(key) + ", got [" + text + "]");
        }
        composeSample("xi'an", "西安");
        sendKeyDownUpSync(KeyEvent.KEYCODE_SPACE);
        waitForEditor("西安");
    }

    private void verifyBundledWords() {
        runOnMainSync(() -> {
            editor.setText("");
            activity.getSystemService(InputMethodManager.class).restartInput(editor);
        });
        keyboard(true);
        // Enter hylj using the visible keys. The old release lacks 会议链接 on this page.
        for (int[] key : new int[][]{{644,1766},{595,1594},{966,1766},{750,1766}}) {
            touch(key[0], key[1], 40);
            SystemClock.sleep(85);
        }
        tap(awaitText("会议链接"));
        waitForEditor("会议链接");
        // Type mingtianxiawu. This context has never been used by the disposable editor.
        for (int[] key : new int[][]{{860,1937},{807,1594},{754,1937},{538,1766},
                {487,1594},{807,1594},{114,1766},{754,1937},{324,1937},{807,1594},
                {114,1766},{165,1594},{700,1594}}) {
            touch(key[0], key[1], 40);
            SystemClock.sleep(85);
        }
        tap(awaitText("明天下午"));
        waitForEditor("会议链接明天下午");
        tap(awaitText("有空吗"));
        waitForEditor("会议链接明天下午有空吗");
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

    private void verifyLearnedContinuation() {
        // Use real keycaps and select each word independently, as in ordinary typing.
        for (int pass = 0; pass < 3; pass++) {
            runOnMainSync(() -> {
                editor.setText("");
                activity.getSystemService(InputMethodManager.class).restartInput(editor);
            });
            keyboard(true);
            selectWord("mingtian", "明天");
            waitForEditor("明天");
            if (pass < 2) {
                selectWord("xiawu", "下午");
                selectWord("kaihui", "开会");
            } else {
                tap(awaitText("下午开会"));
            }
            waitForEditor("明天下午开会");
        }
    }

    private void verifyUppercaseCode() {
        runOnMainSync(() -> {
            editor.setText("");
            activity.getSystemService(InputMethodManager.class).restartInput(editor);
        });
        keyboard(true);
        touch(75, 1937, 40);   // Shift
        touch(537, 1937, 40);  // V
        touch(165, 1594, 750); // W's printed digit 2
        touch(530, 2108, 40);
        waitForEditor("V2");
        touch(75, 1937, 40);   // Begin another identifier.
        touch(537, 1937, 40);
        touch(90, 2108, 40);   // 123
        touch(165, 1594, 40);  // 2
        touch(90, 2108, 40);   // ABC
        touch(90, 2108, 40);   // Reopen 123 while V2 is still being edited.
        touch(270, 1594, 40);  // 3
        touch(530, 2108, 40);  // Confirm the whole identifier without a trailing space.
        waitForEditor("V2V23");
        touch(57, 1594, 40);   // Start an independent number on the same keypad.
        waitForEditor("V2V231");
        touch(830, 2108, 40);  // Standalone period uses the keypad's restored ASCII mode.
        waitForEditor("V2V231.");
        touch(90, 2108, 40);   // ABC
        touch(754, 1937, 40);  // n
        touch(807, 1594, 40);  // i
        touch(90, 2108, 40);   // An ordinary Chinese composition still commits on switching.
        touch(165, 1594, 40);
        waitForEditor("V2V231.你2");
        touch(90, 2108, 40);   // ABC
        touch(75, 1937, 750);  // Hold Shift to lock uppercase across multiple letters.
        touch(430, 1937, 40);  // C
        touch(1020, 1594, 40); // P
        touch(700, 1594, 40);  // U
        touch(75, 1937, 40);   // Release caps lock.
        touch(90, 2108, 40);   // 123
        touch(57, 1594, 40);
        touch(165, 1594, 40);
        touch(530, 2108, 40);
        waitForEditor("V2V231.你2CPU12");
        touch(90, 2108, 40);   // ABC
    }

    private void selectWord(String spelling, String word) {
        String[] rows = {"qwertyuiop", "asdfghjkl", "zxcvbnm"};
        int[] left = {57, 111, 216};
        int[] y = {1594, 1766, 1937};
        for (char ch : spelling.toCharArray()) {
            for (int row = 0; row < rows.length; row++) {
                int col = rows[row].indexOf(ch);
                if (col < 0) continue;
                touch(left[row] + 107 * col, y[row], 40);
                break;
            }
        }
        tap(awaitText(word));
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
        verifyPredictionDigits();
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
        java.util.concurrent.atomic.AtomicInteger searches = new java.util.concurrent.atomic.AtomicInteger();
        runOnMainSync(() -> {
            editor.setImeOptions(EditorInfo.IME_ACTION_SEARCH);
            editor.setOnEditorActionListener((view, action, event) -> {
                if (action != EditorInfo.IME_ACTION_SEARCH) return false;
                searches.incrementAndGet();
                return true;
            });
        });
        openPrediction();
        touch(968, 2108, 40); // The editor's Search action
        for (int i = 0; i < 40 && searches.get() == 0; i++) SystemClock.sleep(100);
        if (searches.get() != 1) throw new AssertionError("Enter did not perform the requested editor action once");
        waitForPredictionToClose();
        waitForEditor("AB你好CD");
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

    private void verifyPredictionDigits() {
        openPrediction();
        String first = awaitText("1 ").getText().toString().substring(2);
        sendKeyDownUpSync(KeyEvent.KEYCODE_1);
        waitForEditor("AB你好" + first + "CD");

        openPrediction();
        String second = awaitText("2 ").getText().toString().substring(2);
        // W carries the printed 2 on the portrait keyboard.
        touch(165, 1594, 750);
        waitForEditor("AB你好" + second + "CD");

        openPrediction();
        second = awaitText("2 ").getText().toString().substring(2);
        sendDigitChord(KeyEvent.KEYCODE_NUMPAD_2, KeyEvent.META_NUM_LOCK_ON);
        waitForEditor("AB你好" + second + "CD");

        openPrediction();
        // Both schemes have seven page slots. An unmatched 0 should close this menu.
        sendKeyDownUpSync(KeyEvent.KEYCODE_0);
        waitForEditor("AB你好0CD");
        waitForPredictionToClose();
        sendKeyDownUpSync(KeyEvent.KEYCODE_1);
        waitForEditor("AB你好01CD");

        openPrediction();
        java.util.concurrent.atomic.AtomicInteger shortcut = new java.util.concurrent.atomic.AtomicInteger();
        runOnMainSync(() -> editor.setOnKeyListener((v, keyCode, event) -> {
            if (keyCode != KeyEvent.KEYCODE_1 || !event.isCtrlPressed()) return false;
            if (event.getAction() == KeyEvent.ACTION_DOWN) shortcut.incrementAndGet();
            return true;
        }));
        try {
            sendDigitChord(KeyEvent.KEYCODE_1, KeyEvent.META_CTRL_ON);
            waitForPredictionToClose();
            waitForEditor("AB你好CD");
            if (shortcut.get() != 1) throw new AssertionError("Ctrl+1 did not reach the editor exactly once");
        } finally {
            runOnMainSync(() -> editor.setOnKeyListener(null));
        }
    }

    private void sendDigitChord(int code, int meta) {
        long down = SystemClock.uptimeMillis();
        for (int action : new int[]{KeyEvent.ACTION_DOWN, KeyEvent.ACTION_UP}) {
            sendKeySync(new KeyEvent(down, SystemClock.uptimeMillis(), action, code, 0, meta));
        }
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
        throw new AssertionError("Expected test editor text [" + expected + "], found [" + current[0] + "]");
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
                if (!node.isVisibleToUser() || node.getText() == null) continue;
                String label = node.getText().toString();
                if (label.startsWith(prefix) || (label.length() > 2 && label.charAt(1) == ' '
                    && Character.isDigit(label.charAt(0)) && label.startsWith(prefix, 2))) return node;
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
