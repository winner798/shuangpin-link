package com.osfans.trime.link

import android.app.AlertDialog
import android.os.Bundle
import android.view.WindowManager
import android.widget.Button
import android.widget.CheckBox
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.lifecycle.lifecycleScope
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.osfans.trime.core.data.UserDataManager
import com.osfans.trime.daemon.RimeDaemon
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File

class LinkActivity : ComponentActivity() {
    private lateinit var settings: LinkSettings
    private lateinit var status: TextView
    private lateinit var pause: CheckBox
    private lateinit var share: CheckBox
    private lateinit var learning: CheckBox
    private val scanner = registerForActivityResult(ScanContract()) { result ->
        result.contents?.let(::pair)
    }

    override fun onCreate(state: Bundle?) {
        super.onCreate(state)
        window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        settings = LinkSettings(this)
        if (!settings.unlocked) { finish(); return }
        val pane = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            val pad = (24 * resources.displayMetrics.density).toInt()
            setPadding(pad, pad, pad, pad)
        }
        val title = TextView(this).apply { text = "双拼互联"; textSize = 26f }
        status = TextView(this).apply { textSize = 16f }
        pane.addView(title)
        pane.addView(status)
        pane.addView(Button(this).apply {
            text = "扫描电脑配对码"
            setOnClickListener {
                scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                    .setPrompt("扫描 Windows 端的配对二维码").setBeepEnabled(false).setOrientationLocked(false))
            }
        })
        pause = addSwitch(pane, "暂停剪贴板同步", "paused", false)
        share = addSwitch(pane, "共享个人词库", "share", true)
        learning = addSwitch(pane, "学习选词习惯", "learning", true)
        pane.addView(Button(this).apply {
            text = "立即同步个人词库"
            setOnClickListener {
                if (settings.device() != null && settings.prefs.getBoolean("share", true)) {
                    settings.prefs.edit().putLong("syncNow", System.currentTimeMillis()).apply()
                    status.text = "已请求同步。输入法在线时会处理。"
                }
            }
        })
        pane.addView(Button(this).apply {
            text = "移除此手机的配对信息"
            setOnClickListener {
                AlertDialog.Builder(this@LinkActivity).setMessage("移除后，请同时在电脑上解除手机配对。")
                    .setNegativeButton("取消", null).setPositiveButton("移除") { _, _ ->
                        settings.forget()
                        refresh()
                    }.show()
            }
        })
        pane.addView(Button(this).apply {
            text = "关闭"
            setOnClickListener { finish() }
        })
        setContentView(ScrollView(this).apply { addView(pane) })
        refresh()
    }

    private fun addSwitch(pane: LinearLayout, label: String, key: String, default: Boolean) =
        CheckBox(this).apply {
            text = label
            isChecked = settings.prefs.getBoolean(key, default)
            setOnCheckedChangeListener { _, checked ->
                if (key == "learning") applyLearning(checked)
                else settings.prefs.edit().putBoolean(key, checked).apply()
            }
            pane.addView(this)
        }

    private fun applyLearning(enabled: Boolean) {
        learning.isEnabled = false
        lifecycleScope.launch {
            val session = RimeDaemon.createSession("ShuangpinLinkSettings")
            try {
                withContext(Dispatchers.IO) {
                    val target = File(UserDataManager.userDataDir, "double_pinyin_mspy.custom.yaml")
                    target.parentFile?.mkdirs()
                    target.writeText("patch:\n  translator/enable_user_dict: $enabled\n")
                }
                session.runOnReady { deploy() }
                settings.prefs.edit().putBoolean("learning", enabled).apply()
            } catch (cancel: CancellationException) { throw cancel }
            catch (_: Exception) { status.text = "词库设置更新失败，请重新部署输入法。" }
            finally {
                RimeDaemon.destroySession("ShuangpinLinkSettings")
                learning.isEnabled = true
            }
        }
    }

    private fun refresh() {
        val device = settings.device()
        status.text = if (device == null) "尚未配对。请在电脑端生成配对码。" else "已配对 · ${java.net.URI(device.server).host}"
        pause.isChecked = settings.prefs.getBoolean("paused", false)
        share.isChecked = settings.prefs.getBoolean("share", true)
        pause.isEnabled = device != null
        share.isEnabled = device != null
    }

    private fun pair(payload: String) {
        lifecycleScope.launch {
            status.text = "正在完成配对…"
            try {
                require(payload.length <= 2048)
                val qr = JSONObject(payload)
                val server = qr.getString("server")
                val invitation = qr.getString("invite")
                val key = qr.getString("key")
                require(qr.getInt("v") == 1 && invitation.matches(Regex("[A-Za-z0-9_-]{43}")) && decode(key).size == 32)
                val device = LinkHttp(server, invitation).call("POST", "join") ?: error("No device")
                settings.save(LinkDevice(server, device.getString("token"), key))
                refresh()
            } catch (cancel: CancellationException) { throw cancel }
            catch (_: Exception) { status.text = "配对未完成。请在电脑端重新生成配对码。" }
        }
    }
}
