package io.github.winner798.keyboardpreview;

import android.app.Activity;
import android.content.*;
import android.net.Uri;
import android.os.Bundle;
import android.view.View;
import android.view.inputmethod.*;
import android.widget.*;
import java.io.InputStream;
import java.util.Arrays;

/** An actual rich-content target. Runs only in the disposable test editor. */
public class ImageEditor extends Activity {
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        TextView result = new TextView(this);
        EditText input = new EditText(this) {
            @Override public InputConnection onCreateInputConnection(EditorInfo info) {
                InputConnection base = super.onCreateInputConnection(info);
                info.contentMimeTypes = new String[]{"image/png"};
                return new InputConnectionWrapper(base, false) {
                    @Override public boolean commitContent(InputContentInfo content, int flags, Bundle opts) {
                        try {
                            content.requestPermission();
                            try (InputStream file = getContentResolver().openInputStream(content.getContentUri())) {
                                byte[] actual = new byte[ImageSource.PNG.length];
                                new java.io.DataInputStream(file).readFully(actual);
                                if (!Arrays.equals(ImageSource.PNG, actual) || file.read() != -1) throw new AssertionError();
                            } finally { content.releasePermission(); }
                            result.setText("Verified image pasted");
                            return true;
                        } catch (Exception failure) {
                            result.setText("Paste error " + failure.getClass().getSimpleName());
                            return false;
                        }
                    }
                };
            }
        };
        root.addView(input);
        root.addView(result);
        setContentView(root);
        input.requestFocus();
        input.postDelayed(() -> getSystemService(InputMethodManager.class).showSoftInput(input, InputMethodManager.SHOW_IMPLICIT), 700);
        input.postDelayed(() -> {
            Uri uri = Uri.parse("content://io.github.winner798.keyboardpreview.images/test.png");
            getSystemService(ClipboardManager.class).setPrimaryClip(
                new ClipData("synthetic test image", new String[]{"image/png"}, new ClipData.Item(uri)));
        }, 3500);
    }
}
