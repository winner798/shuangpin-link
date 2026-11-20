package com.osfans.trime.link

import android.app.AlertDialog
import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.CheckBox
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.FileProvider
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.withResumed
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.osfans.trime.BuildConfig
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
    private lateinit var updates: LinkUpdates
    private lateinit var updateStatus: TextView
    private lateinit var checkUpdate: Button
    private lateinit var installUpdate: Button
    private var verifiedApk: File? = null
    private val scanner = registerForActivityResult(ScanContract()) { result ->
        result.contents?.let(::pair)
    }
    private val installPermission = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        updateStatus.text = if (packageManager.canRequestPackageInstalls()) {
            "已允许安装，点击「安装更新」继续。"
        } else {
            "请在系统设置中允许双拼互联安装更新。"
        }
    }

    override fun onCreate(state: Bundle?) {
        super.onCreate(state)
        window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        settings = LinkSettings(this)
        if (!settings.unlocked) { finish(); return }
        updates = LinkUpdates(applicationContext)
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
            text = "清空本机个人词库"
            setOnClickListener {
                AlertDialog.Builder(this@LinkActivity)
                    .setMessage("清空手机学到的词条和词频，并关闭手机的共享词库？电脑上的词库仍可继续使用。")
                    .setNegativeButton("取消", null)
                    .setPositiveButton("清空") { _, _ -> clearLearning() }.show()
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
        updateStatus = TextView(this).apply { text = "当前版本 ${BuildConfig.VERSION_NAME}" }
        checkUpdate = Button(this).apply {
            text = "检查更新"
            setOnClickListener { checkForUpdate() }
        }
        installUpdate = Button(this).apply {
            text = "安装更新"
            visibility = View.GONE
            setOnClickListener { installVerifiedUpdate() }
        }
        pane.addView(updateStatus)
        pane.addView(checkUpdate)
        pane.addView(installUpdate)
        state?.getString("verifiedUpdateApk")?.let(updates::cachedApk)?.let(::showVerifiedApk)
        pane.addView(Button(this).apply {
            text = "关闭"
            setOnClickListener { finish() }
        })
        setContentView(ScrollView(this).apply { addView(pane) })
        refresh()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        verifiedApk?.let { outState.putString("verifiedUpdateApk", it.name) }
    }

    private fun checkForUpdate() {
        val device = settings.device()
        if (device == null) {
            updateStatus.text = "请先和电脑配对，再检查更新。"
            return
        }
        lifecycleScope.launch {
            checkUpdate.isEnabled = false
            updateStatus.text = "正在检查更新…"
            try {
                val release = updates.check(LinkHttp(device.server, device.token))
                updateStatus.text = if (release == null) {
                    "已是最新版本 ${BuildConfig.VERSION_NAME}。"
                } else {
                    "发现新版本 ${release.versionName}。"
                }
                if (release != null) lifecycle.withResumed {
                    AlertDialog.Builder(this@LinkActivity)
                        .setTitle("更新到 ${release.versionName}")
                        .setMessage("下载完成后，可在系统安装界面确认更新。")
                        .setNegativeButton("取消", null)
                        .setPositiveButton("下载") { _, _ -> downloadUpdate(device, release) }
                        .show()
                }
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (error: LinkHttpError) {
                updateStatus.text = when (error.code) {
                    401 -> "配对已失效，请重新配对。"
                    404 -> "服务器暂未发布更新。"
                    else -> "检查更新失败，请稍后重试。"
                }
            } catch (_: UnsupportedLinkUpdate) {
                updateStatus.text = "当前设备没有可用的安卓更新包。"
            } catch (_: InvalidLinkUpdate) {
                updateStatus.text = "更新信息校验失败，请稍后重试。"
            } catch (_: Exception) {
                updateStatus.text = "检查更新失败，请检查网络后重试。"
            } finally {
                checkUpdate.isEnabled = true
            }
        }
    }

    private fun downloadUpdate(device: LinkDevice, release: LinkRelease) {
        if (settings.device() != device) {
            updateStatus.text = "配对信息已变更，请重新检查更新。"
            return
        }
        lifecycleScope.launch {
            checkUpdate.isEnabled = false
            installUpdate.isEnabled = false
            updateStatus.text = "正在下载 ${release.versionName}…"
            try {
                val file = updates.download(LinkHttp(device.server, device.token), release)
                showVerifiedApk(file)
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (_: InvalidLinkUpdate) {
                updateStatus.text = "更新包校验失败，请重新下载。"
            } catch (_: Exception) {
                updateStatus.text = "下载失败，请重新检查更新。"
            } finally {
                checkUpdate.isEnabled = true
                installUpdate.isEnabled = true
            }
        }
    }

    private fun showVerifiedApk(file: File) {
        verifiedApk = file
        installUpdate.visibility = View.VISIBLE
        updateStatus.text = "更新包已校验，可安装。"
    }

    private fun installVerifiedUpdate() {
        val file = verifiedApk?.let { updates.cachedApk(it.name) }
        if (file == null) {
            verifiedApk = null
            installUpdate.visibility = View.GONE
            updateStatus.text = "更新包已失效，请重新检查更新。"
            return
        }
        try {
            if (!packageManager.canRequestPackageInstalls()) {
                installPermission.launch(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")))
                return
            }
            val uri = FileProvider.getUriForFile(this, "$packageName.link-updates", file)
            val install = Intent(Intent.ACTION_VIEW)
                .setDataAndType(uri, "application/vnd.android.package-archive")
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                .apply { clipData = ClipData.newRawUri("双拼互联更新包", uri) }
            startActivity(install)
        } catch (_: ActivityNotFoundException) {
            updateStatus.text = "系统安装界面不可用。"
        } catch (_: IllegalArgumentException) {
            updateStatus.text = "更新文件无法打开，请重新下载。"
        } catch (_: SecurityException) {
            updateStatus.text = "系统限制了安装，请检查应用安装权限。"
        }
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

    private fun clearLearning() {
        lifecycleScope.launch {
            val name = "ShuangpinLinkDictionaryReset"
            settings.prefs.edit().putBoolean("share", false).remove("dictionaryReceived").remove("dictionarySent").apply()
            val session = RimeDaemon.createSession(name)
            try {
                session.runOnReady { clearLinkedDictionary() }
                refresh()
                status.text = "本机个人词库已清空。"
            } catch (cancel: CancellationException) { throw cancel }
            catch (_: Exception) { status.text = "清空词库未完成，请稍后重试。" }
            finally { RimeDaemon.destroySession(name) }
        }
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
