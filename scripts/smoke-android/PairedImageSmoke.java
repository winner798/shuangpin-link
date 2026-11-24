package io.github.winner798.keyboardpreview;

import android.app.*;
import android.content.*;
import android.graphics.Rect;
import android.os.*;
import android.security.keystore.*;
import android.util.Base64;
import android.view.accessibility.*;
import android.accessibilityservice.AccessibilityServiceInfo;
import android.view.MotionEvent;
import java.io.*;
import java.security.KeyStore;
import java.security.cert.CertificateFactory;
import javax.crypto.*;
import javax.net.ssl.*;
import org.json.JSONObject;

/** The disposable emulator signs this instrumentation and the IME with the same temporary key. */
public class PairedImageSmoke extends Instrumentation {
    private Bundle args;
    private void phase(String message) {
        Bundle update=new Bundle(); update.putString("imagePhase",message); sendStatus(1,update);
    }
    @Override public void onCreate(Bundle args) { super.onCreate(args); this.args=args; start(); }
    @Override public void onStart() {
        Bundle output = new Bundle();
        try {
            // Trust the one ephemeral localhost certificate solely in this instrumented process.
            KeyStore trust = KeyStore.getInstance(KeyStore.getDefaultType());
            trust.load(null);
            try(InputStream der = new ByteArrayInputStream(Base64.decode(args.getString("cert"), 0))) {
                trust.setCertificateEntry("relay", CertificateFactory.getInstance("X.509").generateCertificate(der));
            }
            TrustManagerFactory tm = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm());
            tm.init(trust);
            SSLContext tls = SSLContext.getInstance("TLS");
            tls.init(null, tm.getTrustManagers(), null);
            HttpsURLConnection.setDefaultSSLSocketFactory(tls.getSocketFactory());
            KeyStore keys = KeyStore.getInstance("AndroidKeyStore");
            keys.load(null);
            String alias = "ime-sync-device-v1";
            SecretKey key = (SecretKey)keys.getKey(alias, null);
            if (key == null) {
                KeyGenerator gen = KeyGenerator.getInstance("AES","AndroidKeyStore");
                gen.init(new KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes("GCM").setEncryptionPaddings("NoPadding").setKeySize(256).build());
                key=gen.generateKey();
            }
            String device = new JSONObject().put("server","https://10.0.2.2:18766").put("token",args.getString("token"))
                .put("key",args.getString("key")).toString();
            Cipher gcm=Cipher.getInstance("AES/GCM/NoPadding");
            gcm.init(Cipher.ENCRYPT_MODE,key);
            ByteArrayOutputStream record=new ByteArrayOutputStream();
            record.write(gcm.getIV()); record.write(gcm.doFinal(device.getBytes("UTF-8")));
            getTargetContext().getSharedPreferences("ime_sync", Context.MODE_PRIVATE).edit()
                .putString("device",Base64.encodeToString(record.toByteArray(),Base64.NO_WRAP))
                .putLong("imagesSince",System.currentTimeMillis()).putBoolean("images",true).commit();
            UiAutomation ui=getUiAutomation();
            AccessibilityServiceInfo access=ui.getServiceInfo();
            access.flags |= AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS;
            ui.setServiceInfo(access);
            try (InputStream started = new ParcelFileDescriptor.AutoCloseInputStream(ui.executeShellCommand(
                    "am start -W -n io.github.winner798.keyboardpreview/.ImageEditor"))) {
                byte[] discard = new byte[512];
                while (started.read(discard) >= 0) { }
            }
            boolean tapped=false, copied=false;
            String last="";
            long end=SystemClock.uptimeMillis()+65000;
            while(SystemClock.uptimeMillis()<end) {
                for (AccessibilityWindowInfo window : ui.getWindows()) {
                    AccessibilityNodeInfo root=window.getRoot();
                    if(root==null) continue;
                    if (!copied && !root.findAccessibilityNodeInfosByText("Synthetic image copied").isEmpty()) {
                        copied=true; phase("Source editor copied the synthetic PNG");
                    }
                    if (!root.findAccessibilityNodeInfosByText("Verified image pasted").isEmpty()) {
                        output.putString("status","Paired Android image upload, receive and rich paste verified");
                        finish(Activity.RESULT_OK,output);return;
                    }
                    for (AccessibilityNodeInfo label: root.findAccessibilityNodeInfosByText("图片")) {
                        last=String.valueOf(label.getText());
                        if(tapped || !last.contains("点此粘贴")) continue;
                        Rect r=new Rect();label.getBoundsInScreen(r);
                        long now=SystemClock.uptimeMillis();
                        for(int action:new int[]{MotionEvent.ACTION_DOWN, MotionEvent.ACTION_UP}) {
                            MotionEvent tap=MotionEvent.obtain(now,SystemClock.uptimeMillis(),action,r.centerX(),r.centerY(),0);
                            tap.setSource(android.view.InputDevice.SOURCE_TOUCHSCREEN);
                            ui.injectInputEvent(tap,true);tap.recycle();
                        }
                        tapped=true; phase("Received image thumbnail tapped");
                    }
                }
                SystemClock.sleep(200);
            }
            throw new AssertionError("Missing round-trip image or rich paste, last UI: " + last);
        } catch(Throwable failure) {
            output.putString("error",failure.toString());finish(Activity.RESULT_CANCELED,output);
        }
    }
}
