package io.github.winner798.keyboardpreview;

import android.content.*;
import android.database.Cursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import java.io.*;
import java.util.zip.CRC32;
import java.util.zip.DeflaterOutputStream;

public class ImageSource extends ContentProvider {
    static final byte[] PNG = syntheticPng();
    private static void chunk(DataOutputStream out, String kind, byte[] data) throws IOException {
        byte[] tag = kind.getBytes("US-ASCII");
        CRC32 crc = new CRC32(); crc.update(tag); crc.update(data);
        out.writeInt(data.length); out.write(tag); out.write(data); out.writeInt((int)crc.getValue());
    }
    private static byte[] syntheticPng() {
        try {
            ByteArrayOutputStream png = new ByteArrayOutputStream();
            DataOutputStream out = new DataOutputStream(png);
            out.writeLong(0x89504e470d0a1a0aL);
            ByteArrayOutputStream header = new ByteArrayOutputStream();
            DataOutputStream dims = new DataOutputStream(header);
            dims.writeInt(320); dims.writeInt(320); dims.write(new byte[]{8,6,0,0,0});
            chunk(out, "IHDR", header.toByteArray());
            ByteArrayOutputStream data = new ByteArrayOutputStream();
            int state = 13;
            try (DeflaterOutputStream deflate = new DeflaterOutputStream(data)) {
                byte[] row = new byte[1 + 320 * 4];
                for (int y=0; y<320; y++) {
                    for (int x=1; x<row.length; x++) {
                        state = state * 1664525 + 1013904223;
                        row[x] = (byte)(state >>> 24);
                    }
                    deflate.write(row);
                }
            }
            chunk(out, "IDAT", data.toByteArray());
            chunk(out, "IEND", new byte[0]);
            if (png.size() <= 262144) throw new AssertionError("Expected more than one authenticated image chunk");
            return png.toByteArray();
        } catch(IOException error) { throw new AssertionError(error); }
    }
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
