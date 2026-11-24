package io.github.winner798.keyboardpreview;

import android.content.*;
import android.database.Cursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import java.io.*;

public class ImageSource extends ContentProvider {
    static final byte[] PNG = android.util.Base64.decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGOo4JFuAAACvwEg/If2gQAAAABJRU5ErkJggg==", 0);
    @Override public boolean onCreate() { return true; }
    @Override public String getType(Uri uri) { return "image/png"; }
    @Override public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        if (!"r".equals(mode) || !"/test.png".equals(uri.getPath())) throw new FileNotFoundException();
        File file = new File(getContext().getCacheDir(), "test.png");
        try (FileOutputStream stream = new FileOutputStream(file)) { stream.write(PNG); }
        catch(IOException failure) { throw new FileNotFoundException(); }
        return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY);
    }
    @Override public Cursor query(Uri u,String[] p,String s,String[] a,String order){ return null; }
    @Override public Uri insert(Uri u, ContentValues v){ throw new UnsupportedOperationException(); }
    @Override public int delete(Uri u,String s,String[] a){ throw new UnsupportedOperationException(); }
    @Override public int update(Uri u,ContentValues v,String s,String[] a){ throw new UnsupportedOperationException(); }
}
