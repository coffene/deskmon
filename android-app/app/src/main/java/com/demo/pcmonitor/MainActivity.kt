package com.demo.pcmonitor

import android.Manifest
import android.annotation.SuppressLint
import android.app.admin.DevicePolicyManager
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Typeface
import android.graphics.drawable.ColorDrawable
import android.media.Ringtone
import android.media.RingtoneManager
import android.os.Build
import android.os.Bundle
import android.os.CountDownTimer
import android.text.InputType
import android.text.Spannable
import android.text.SpannableStringBuilder
import android.text.TextUtils
import android.text.style.StrikethroughSpan
import android.text.style.StyleSpan
import android.util.Base64
import android.util.TypedValue
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.view.animation.AlphaAnimation
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import androidx.core.content.res.ResourcesCompat
import com.demo.pcmonitor.databinding.ActivityMainBinding
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import kotlin.math.roundToInt

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private lateinit var client: MonitorClient

    private var currentMode = ConnectionMode.NETWORK
    private var isConnected = false
    private var playingOnLeft = false
    private var showPlayingPanel = true
    private var mediaTimeMode = 0 // 0=elapsed, 1=remaining+total, 2=remaining only, 3=both+total
    private var fontChoice = 0 // 0=Fira Sans, 1=IBM Plex Sans, 2=Barlow, 3=Roboto
    private var lastThumbSeq = -1
    private var cachedThumbBitmap: Bitmap? = null
    private val cachedTypefaces = arrayOfNulls<Pair<Typeface?, Typeface?>>(4)
    private var lastSnapshot: ResourceSnapshot? = null
    private var lastAppliedCfgSeq = 0
    private var lastAppliedTimerSeq = 0
    private var lastAppliedPowerSeq = 0

    // Layout 3 customizable components & proportions
    private var l3ShowTask = true
    private var l3ShowTimer = true
    private var l3ShowRings = true
    private var l3ShowNotif = true
    private var l3SplitPct = 54
    private var l3RightSplitPct = 55
    private var l3TextScale = 100
    private var l3MetricScale = 100
    private var showTopBar = true
    private var showLogo = true
    private var lastRenderedNotifSeq = -1
    private var cachedNotifications: List<OsNotification> = emptyList()
    private var cachedNotifAccess = true

    // Layout 3: Compact Focus Timer & Authentic Pomodoro state
    private var timerTotalMs = 25 * 60 * 1000L
    private var timerRemainingMs = 25 * 60 * 1000L
    private var timerRunning = false
    private var isPomodoroMode = false
    private var pomodoroTotalCycles = 4
    private var pomodoroCurrentCycle = 1
    private var pomodoroIsBreak = false
    private var countDownTimer: CountDownTimer? = null
    private var activeRingtone: Ringtone? = null
    private var pendingLockCloseApp = false
    private var isShuttingDown = false

    private val deviceAdminLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
            val dpm = getSystemService(Context.DEVICE_POLICY_SERVICE) as? DevicePolicyManager
            val admin = ComponentName(this, MonitorDeviceAdminReceiver::class.java)
            if (dpm != null && dpm.isAdminActive(admin)) {
                lockAndMaybeClose(dpm, closeApp = pendingLockCloseApp)
            } else {
                Toast.makeText(this, getString(R.string.device_admin_denied), Toast.LENGTH_SHORT).show()
            }
        }

    private val prefs by lazy {
        getSharedPreferences("pc_monitor_prefs", Context.MODE_PRIVATE)
    }

    private val bluetoothAdapter: BluetoothAdapter? by lazy {
        val manager = getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager
        manager?.adapter
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        fontChoice = getSharedPreferences("pc_monitor_prefs", Context.MODE_PRIVATE)
            .getInt("font_choice", 0)
            .coerceIn(0, 3)
        setTheme(
            when (fontChoice) {
                1 -> R.style.Theme_PCResourceMonitor_Plex
                2 -> R.style.Theme_PCResourceMonitor_Barlow
                3 -> R.style.Theme_PCResourceMonitor_Roboto
                else -> R.style.Theme_PCResourceMonitor
            }
        )
        super.onCreate(savedInstanceState)

        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        binding.layoutFlipper.inAnimation = AlphaAnimation(0f, 1f).apply { duration = 180 }
        binding.layoutFlipper.outAnimation = AlphaAnimation(1f, 0f).apply { duration = 140 }

        client = MonitorClient(
            onStateChanged = { connected, status ->
                runOnUiThread {
                    isConnected = connected
                    updateConnectionUi(connected, status)
                }
            },
            onSnapshot = { snapshot ->
                runOnUiThread { renderSnapshot(snapshot) }
            },
            onTargetDetected = { mode, host, port ->
                runOnUiThread {
                    currentMode = mode
                    prefs.edit()
                        .putString("mode", mode.name)
                        .putString("host", host)
                        .putInt("port", port)
                        .apply()
                    syncModeToggleUi()
                }
            }
        )

        setupModeToggle()
        setupButtons()
        restoreSavedPreferences()
        updateConnectionUi(false, getString(R.string.status_disconnected))
        autoDetectAndConnectOnStartup()
    }

    override fun onDestroy() {
        cleanupAppResources()
        super.onDestroy()
    }

    private fun cleanupAppResources() {
        if (isShuttingDown) return
        isShuttingDown = true
        window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        countDownTimer?.cancel()
        stopAlarmRingtone()
        clearCachedThumb()
        lastSnapshot = null
        cachedNotifications = emptyList()
        if (::client.isInitialized) {
            client.shutdown()
        }
    }

    override fun onTrimMemory(level: Int) {
        super.onTrimMemory(level)
        if (level >= TRIM_MEMORY_UI_HIDDEN) {
            clearCachedThumb()
            lastThumbSeq = -1
        }
    }

    private fun clearCachedThumb() {
        if (::binding.isInitialized) {
            binding.ivMediaThumb3.setImageDrawable(null)
            binding.ivMediaThumb4.setImageDrawable(null)
        }
        cachedThumbBitmap?.recycle()
        cachedThumbBitmap = null
    }

    private fun decodeSampledThumb(bytes: ByteArray): Bitmap? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
        var sample = 1
        while (bounds.outWidth / (sample * 2) >= 192 || bounds.outHeight / (sample * 2) >= 192) {
            sample *= 2
        }
        val opts = BitmapFactory.Options().apply {
            inSampleSize = sample
            inPreferredConfig = Bitmap.Config.RGB_565
        }
        return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, opts)
    }

    private fun setHeaderBarAnimated(bar: View, visible: Boolean) {
        bar.animate().cancel()
        if (!visible) {
            if (bar.visibility == View.VISIBLE) {
                bar.animate()
                    .alpha(0f)
                    .setDuration(150)
                    .withEndAction {
                        bar.visibility = View.GONE
                        bar.alpha = 1f
                    }
                    .start()
            }
        } else {
            if (bar.visibility != View.VISIBLE) {
                bar.alpha = 0f
                bar.visibility = View.VISIBLE
                bar.animate()
                    .alpha(1f)
                    .setDuration(160)
                    .start()
            } else {
                bar.alpha = 1f
            }
        }
    }

    private fun applyTopBarVisibility(animated: Boolean = false) {
        val visible = !isConnected || showTopBar
        if (animated) {
            setHeaderBarAnimated(binding.headerBar3, visible)
            setHeaderBarAnimated(binding.headerBar4, visible)
        } else {
            val vis = if (visible) View.VISIBLE else View.GONE
            binding.headerBar3.animate().cancel()
            binding.headerBar3.visibility = vis
            binding.headerBar3.alpha = 1f
            binding.headerBar4.animate().cancel()
            binding.headerBar4.visibility = vis
            binding.headerBar4.alpha = 1f
        }
    }

    private fun applyLogoVisibility() {
        val vis = if (showLogo) View.VISIBLE else View.GONE
        binding.ivLogo1.visibility = vis
        binding.ivLogo3.visibility = vis
        binding.ivLogo4.visibility = vis
    }

    private fun animateSoftFade(view: View) {
        if (view.visibility != View.VISIBLE) return
        view.animate().cancel()
        view.alpha = 0.35f
        view.animate().alpha(1f).setDuration(160).start()
    }

    private fun setProgressSmooth(bar: ProgressBar, value: Int) {
        if (bar.progress != value) {
            bar.setProgress(value, true)
        }
    }

    private fun setupModeToggle() {
        binding.toggleMode.addOnButtonCheckedListener { _, checkedId, isChecked ->
            if (!isChecked) return@addOnButtonCheckedListener
            val newMode = when (checkedId) {
                R.id.btnModeUsb -> ConnectionMode.USB
                R.id.btnModeBluetooth -> ConnectionMode.BLUETOOTH
                else -> ConnectionMode.NETWORK
            }
            if (newMode != currentMode) {
                currentMode = newMode
                prefs.edit().putString("mode", currentMode.name).apply()
                if (isConnected) {
                    client.disconnect()
                }
            }
        }
    }

    private fun setupButtons() {
        val showLayoutPicker = View.OnClickListener { showLayoutSelectorDialog() }
        binding.btnLayout1.setOnClickListener(showLayoutPicker)
        binding.btnLayout3.setOnClickListener(showLayoutPicker)
        binding.btnLayout4.setOnClickListener(showLayoutPicker)

        val showFontPicker = View.OnClickListener { showFontSelectorDialog() }
        binding.btnFont1.setOnClickListener(showFontPicker)
        binding.btnFont3.setOnClickListener(showFontPicker)
        binding.btnFont4.setOnClickListener(showFontPicker)

        // Layout 1 direct controls
        binding.btnConfig.setOnClickListener {
            when (currentMode) {
                ConnectionMode.NETWORK -> showNetworkConfigDialog()
                ConnectionMode.USB -> showUsbHelpDialog()
                ConnectionMode.BLUETOOTH -> showBluetoothPickerDialog()
            }
        }

        binding.btnConnect.setOnClickListener {
            if (isConnected) {
                client.disconnect()
            } else {
                startConnection()
            }
        }

        // Layout 2 & 3 unified connection dialog
        val openConnDialog = View.OnClickListener { showConnectionDialog() }
        binding.btnConnectionMenu3.setOnClickListener(openConnDialog)
        binding.btnConnectionMenu4.setOnClickListener(openConnDialog)

        // Power menu across all 3 layouts (close app & lock screen without hardware power button)
        val openPowerMenu = View.OnClickListener { showPowerDialog() }
        val instantCloseAndLock = View.OnLongClickListener {
            executePowerAction(closeApp = true, lockScreen = true)
            true
        }
        binding.btnPower1.setOnClickListener(openPowerMenu)
        binding.btnPower3.setOnClickListener(openPowerMenu)
        binding.btnPower4.setOnClickListener(openPowerMenu)
        binding.btnPower1.setOnLongClickListener(instantCloseAndLock)
        binding.btnPower3.setOnLongClickListener(instantCloseAndLock)
        binding.btnPower4.setOnLongClickListener(instantCloseAndLock)

        // Toggle logo visibility by tapping logo (or tapping title text next to it to restore)
        val toggleLogo = View.OnClickListener {
            showLogo = !showLogo
            prefs.edit().putBoolean("show_logo", showLogo).apply()
            applyLogoVisibility()
            syncOptionsToServer()
        }
        binding.ivLogo1.setOnClickListener(toggleLogo)
        binding.ivLogo3.setOnClickListener(toggleLogo)
        binding.ivLogo4.setOnClickListener(toggleLogo)
        binding.tvHostTitle1.setOnClickListener(toggleLogo)
        binding.tvHostTitle3.setOnClickListener(toggleLogo)
        binding.tvHostTitle4.setOnClickListener(toggleLogo)

        // Layout 2 & 3: Tap metrics/headers to toggle top bar (never hide when not connected)
        val toggleTopBar = View.OnClickListener {
            if (!isConnected) {
                applyTopBarVisibility(animated = false)
                return@OnClickListener
            }
            showTopBar = !showTopBar
            prefs.edit().putBoolean("show_top_bar", showTopBar).apply()
            applyTopBarVisibility(animated = true)
            syncOptionsToServer()
        }
        binding.paneMetrics3.setOnClickListener(toggleTopBar)
        binding.currentTaskHeader4.setOnClickListener(toggleTopBar)
        binding.ringsBar4.setOnClickListener(toggleTopBar)
        binding.tvNotifHeader4.setOnClickListener(toggleTopBar)

        binding.btnComponents4.setOnClickListener { showLayout3ComponentsDialog() }
        binding.btnResize4.setOnClickListener { showLayout3ResizeDialog() }
        binding.btnClearNotifs4.setOnClickListener { client.sendClearNotifications() }

        setupLayout3DragHandles()

        // Toggle Now Playing visibility (shared between Layout 2 & 3)
        val togglePlaying = View.OnClickListener {
            if (binding.layoutFlipper.displayedChild == 2 && showPlayingPanel && visibleL3Count() <= 1) {
                Toast.makeText(this, "Keep at least 1 component visible", Toast.LENGTH_SHORT).show()
                return@OnClickListener
            }
            showPlayingPanel = !showPlayingPanel
            prefs.edit().putBoolean("show_playing", showPlayingPanel).apply()
            applyPlayingVisibility()
            syncOptionsToServer()
        }
        binding.btnTogglePlaying3.setOnClickListener(togglePlaying)
        binding.btnTogglePlaying4.setOnClickListener(togglePlaying)

        // Swap Playing side Left <-> Right (shared between Layout 2 & 3)
        val swapSide = View.OnClickListener {
            playingOnLeft = !playingOnLeft
            prefs.edit().putBoolean("playing_on_left", playingOnLeft).apply()
            applyPlayingSideOrder()
            animateSoftFade(binding.splitContainer3)
            animateSoftFade(binding.splitContainer4)
            syncOptionsToServer()
        }
        binding.btnSwapSide3.setOnClickListener(swapSide)
        binding.btnSwapSide4.setOnClickListener(swapSide)

        // Cycle Now Playing time mode on tap (shared between Layout 2 & 3)
        val cycleTimeMode = View.OnClickListener {
            mediaTimeMode = (mediaTimeMode + 1) % 4
            prefs.edit().putInt("media_time_mode", mediaTimeMode).apply()
            renderMediaTime(lastSnapshot)
            syncOptionsToServer()
        }
        binding.tvMediaTimeLeft3.setOnClickListener(cycleTimeMode)
        binding.tvMediaTimeLeft4.setOnClickListener(cycleTimeMode)

        // Media transport buttons in Layout 2 & Layout 3
        binding.btnMediaPrev.setOnClickListener { client.sendCommand("prev") }
        binding.btnMediaPlayPause.setOnClickListener { client.sendCommand("play_pause") }
        binding.btnMediaStop.setOnClickListener { client.sendCommand("stop") }
        binding.btnMediaNext.setOnClickListener { client.sendCommand("next") }

        binding.btnMediaPrev4.setOnClickListener { client.sendCommand("prev") }
        binding.btnMediaPlayPause4.setOnClickListener { client.sendCommand("play_pause") }
        binding.btnMediaStop4.setOnClickListener { client.sendCommand("stop") }
        binding.btnMediaNext4.setOnClickListener { client.sendCommand("next") }

        // Layout 3: Tap compact timer progress strip to open timer options or stop alarm
        binding.timerBar4.setOnClickListener {
            if (activeRingtone != null) {
                stopAlarmRingtone()
            } else {
                showTimerDialog()
            }
        }
    }

    private fun showStyledDialog(builder: MaterialAlertDialogBuilder): AlertDialog {
        builder.setBackground(ColorDrawable(ContextCompat.getColor(this, R.color.bg_card)))
        val dlg = builder.show()
        val highCol = ContextCompat.getColor(this, R.color.m2_on_surface_high)
        val medCol = ContextCompat.getColor(this, R.color.m2_on_surface_medium)
        val accentCol = ContextCompat.getColor(this, R.color.m2_secondary)
        val tint = android.content.res.ColorStateList.valueOf(accentCol)
        dlg.window?.decorView?.let {
            styleDialogColorsRecursively(it, highCol, medCol, accentCol, tint)
            applySelectedFontToView(it)
        }
        return dlg
    }

    private fun styleDialogColorsRecursively(
        view: View,
        highCol: Int,
        medCol: Int,
        accentCol: Int,
        tint: android.content.res.ColorStateList
    ) {
        when (view) {
            is CheckBox -> {
                view.setTextColor(highCol)
                view.buttonTintList = tint
            }
            is RadioButton -> {
                view.setTextColor(highCol)
                view.buttonTintList = tint
            }
            is EditText -> {
                view.setTextColor(highCol)
                view.setHintTextColor(medCol)
            }
            is android.widget.Button -> {
                view.setTextColor(accentCol)
            }
            is TextView -> {
                if (view.currentTextColor != accentCol) {
                    view.setTextColor(highCol)
                }
            }
        }
        if (view is ViewGroup) {
            for (i in 0 until view.childCount) {
                styleDialogColorsRecursively(view.getChildAt(i), highCol, medCol, accentCol, tint)
            }
        }
    }

    private fun showPowerDialog() {
        val items = arrayOf(
            getString(R.string.power_close_and_lock),
            getString(R.string.power_lock_only),
            getString(R.string.power_close_only)
        )
        showStyledDialog(
            MaterialAlertDialogBuilder(this)
                .setTitle(getString(R.string.power_dialog_title))
                .setItems(items) { _, which ->
                    when (which) {
                        0 -> executePowerAction(closeApp = true, lockScreen = true)
                        1 -> executePowerAction(closeApp = false, lockScreen = true)
                        2 -> executePowerAction(closeApp = true, lockScreen = false)
                    }
                }
                .setNegativeButton("Cancel", null)
        )
    }

    private fun executePowerAction(closeApp: Boolean, lockScreen: Boolean) {
        if (!lockScreen) {
            if (closeApp) {
                cleanupAppResources()
                finishAndRemoveTask()
            }
            return
        }

        val dpm = getSystemService(Context.DEVICE_POLICY_SERVICE) as? DevicePolicyManager
        if (dpm == null) {
            Toast.makeText(this, "Device Policy service unavailable", Toast.LENGTH_SHORT).show()
            return
        }
        val admin = ComponentName(this, MonitorDeviceAdminReceiver::class.java)
        if (!dpm.isAdminActive(admin)) {
            pendingLockCloseApp = closeApp
            val intent = Intent(DevicePolicyManager.ACTION_ADD_DEVICE_ADMIN).apply {
                putExtra(DevicePolicyManager.EXTRA_DEVICE_ADMIN, admin)
                putExtra(
                    DevicePolicyManager.EXTRA_ADD_EXPLANATION,
                    getString(R.string.device_admin_explanation)
                )
            }
            deviceAdminLauncher.launch(intent)
            return
        }

        lockAndMaybeClose(dpm, closeApp)
    }

    private fun lockAndMaybeClose(dpm: DevicePolicyManager, closeApp: Boolean) {
        dpm.lockNow()
        if (closeApp) {
            cleanupAppResources()
            finishAndRemoveTask()
        }
    }

    private fun showTimerDialog() {
        val items = arrayOf(
            if (timerRunning) "Pause timer" else "Start timer",
            "Reset timer",
            "10 minutes",
            "15 minutes",
            "20 minutes",
            "25 minutes",
            "45 minutes",
            "Pomodoro",
            "Custom duration"
        )
        showStyledDialog(
            MaterialAlertDialogBuilder(this)
                .setTitle("Timer")
                .setItems(items) { _, which ->
                    when (which) {
                        0 -> if (timerRunning) pauseTimer() else startTimer()
                        1 -> {
                            stopAlarmRingtone()
                            resetTimer()
                        }
                        2 -> {
                            isPomodoroMode = false
                            selectTimerMinutes(10, "10 min")
                            startTimer()
                        }
                        3 -> {
                            isPomodoroMode = false
                            selectTimerMinutes(15, "15 min")
                            startTimer()
                        }
                        4 -> {
                            isPomodoroMode = false
                            selectTimerMinutes(20, "20 min")
                            startTimer()
                        }
                        5 -> {
                            isPomodoroMode = false
                            selectTimerMinutes(25, "25 min")
                            startTimer()
                        }
                        6 -> {
                            isPomodoroMode = false
                            selectTimerMinutes(45, "45 min")
                            startTimer()
                        }
                        7 -> showPomodoroCycleDialog()
                        8 -> showCustomTimerDialog()
                    }
                }
                .setNegativeButton("Close", null)
        )
    }

    private fun showPomodoroCycleDialog() {
        val items = arrayOf(
            "2 cycles",
            "4 cycles",
            "6 cycles",
            "8 cycles",
            "Custom cycles"
        )
        showStyledDialog(
            MaterialAlertDialogBuilder(this)
                .setTitle("Pomodoro cycles")
                .setItems(items) { _, which ->
                    when (which) {
                        0 -> startPomodoroSession(2)
                        1 -> startPomodoroSession(4)
                        2 -> startPomodoroSession(6)
                        3 -> startPomodoroSession(8)
                        4 -> showCustomPomodoroCyclesDialog()
                    }
                }
                .setNegativeButton("Cancel", null)
        )
    }

    private fun showCustomPomodoroCyclesDialog() {
        val layout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(48, 16, 48, 8)
        }
        val etCycles = EditText(this).apply {
            hint = "Cycles"
            inputType = InputType.TYPE_CLASS_NUMBER
            setText(pomodoroTotalCycles.toString())
        }
        layout.addView(etCycles)

        showStyledDialog(
            MaterialAlertDialogBuilder(this)
                .setTitle("Custom cycles")
                .setView(layout)
                .setPositiveButton("Start") { _, _ ->
                    val count = etCycles.text.toString().toIntOrNull()
                    if (count != null && count > 0) {
                        startPomodoroSession(count.coerceIn(1, 24))
                    }
                }
                .setNegativeButton("Cancel", null)
        )
    }

    private fun startPomodoroSession(cycles: Int) {
        isPomodoroMode = true
        pomodoroTotalCycles = cycles.coerceIn(1, 24)
        pomodoroCurrentCycle = 1
        pomodoroIsBreak = false
        selectTimerMinutes(25, "Pomodoro 1/$pomodoroTotalCycles Focus")
        startTimer()
    }

    private fun selectTimerMinutes(minutes: Int, label: String) {
        stopAlarmRingtone()
        countDownTimer?.cancel()
        timerRunning = false
        val safeMin = minutes.coerceIn(1, 360)
        timerTotalMs = safeMin * 60 * 1000L
        timerRemainingMs = timerTotalMs
        binding.tvTimerPreset4.text = label
        updateTimerUi()
    }

    private fun startTimer() {
        stopAlarmRingtone()
        if (timerRemainingMs <= 0L) {
            timerRemainingMs = timerTotalMs
        }
        timerRunning = true
        countDownTimer?.cancel()
        countDownTimer = object : CountDownTimer(timerRemainingMs, 250L) {
            override fun onTick(millisUntilFinished: Long) {
                timerRemainingMs = millisUntilFinished
                updateTimerUi()
            }

            override fun onFinish() {
                timerRunning = false
                timerRemainingMs = 0L
                updateTimerUi()
                onTimerCompleted()
            }
        }.start()
    }

    private fun pauseTimer() {
        countDownTimer?.cancel()
        timerRunning = false
        updateTimerUi()
    }

    private fun resetTimer() {
        countDownTimer?.cancel()
        timerRunning = false
        if (isPomodoroMode) {
            pomodoroCurrentCycle = 1
            pomodoroIsBreak = false
            timerTotalMs = 25 * 60 * 1000L
            binding.tvTimerPreset4.text = "Pomodoro 1/$pomodoroTotalCycles Focus"
        }
        timerRemainingMs = timerTotalMs
        updateTimerUi()
    }

    private fun updateTimerUi() {
        val totalSec = ((timerRemainingMs + 999L) / 1000L).toInt().coerceAtLeast(0)
        val hrs = totalSec / 3600
        val mins = (totalSec % 3600) / 60
        val secs = totalSec % 60
        binding.tvTimerDisplay4.text = if (hrs > 0) {
            String.format("%02d:%02d:%02d", hrs, mins, secs)
        } else {
            String.format("%02d:%02d", mins, secs)
        }
        val progress = if (timerTotalMs > 0L) {
            ((timerRemainingMs * 1000L) / timerTotalMs).toInt().coerceIn(0, 1000)
        } else {
            0
        }
        binding.progressTimer4.progress = progress
    }

    private fun onTimerCompleted() {
        playSystemAlarmRingtone()
        if (isPomodoroMode) {
            if (!pomodoroIsBreak) {
                if (pomodoroCurrentCycle >= pomodoroTotalCycles) {
                    showStyledDialog(
                        MaterialAlertDialogBuilder(this)
                            .setTitle("Pomodoro complete")
                            .setMessage("Completed all $pomodoroTotalCycles cycles.")
                            .setPositiveButton("Stop ring") { _, _ ->
                                stopAlarmRingtone()
                                resetTimer()
                            }
                            .setOnCancelListener { stopAlarmRingtone() }
                    )
                } else {
                    val isLongBreak = (pomodoroCurrentCycle % 4 == 0)
                    val breakMin = if (isLongBreak) 15 else 5
                    val breakLabel = if (isLongBreak) "Long break" else "Short break"
                    showStyledDialog(
                        MaterialAlertDialogBuilder(this)
                            .setTitle("Focus $pomodoroCurrentCycle/$pomodoroTotalCycles complete")
                            .setMessage("Next: $breakLabel for $breakMin minutes.")
                            .setPositiveButton("Start break") { _, _ ->
                                stopAlarmRingtone()
                                pomodoroIsBreak = true
                                selectTimerMinutes(
                                    breakMin,
                                    "Pomodoro $pomodoroCurrentCycle/$pomodoroTotalCycles Break"
                                )
                                startTimer()
                            }
                            .setNegativeButton("Stop") { _, _ ->
                                stopAlarmRingtone()
                                resetTimer()
                            }
                            .setOnCancelListener { stopAlarmRingtone() }
                    )
                }
            } else {
                val nextCycle = pomodoroCurrentCycle + 1
                showStyledDialog(
                    MaterialAlertDialogBuilder(this)
                        .setTitle("Break complete")
                        .setMessage("Ready for Focus $nextCycle/$pomodoroTotalCycles.")
                        .setPositiveButton("Start focus") { _, _ ->
                            stopAlarmRingtone()
                            pomodoroCurrentCycle = nextCycle
                            pomodoroIsBreak = false
                            selectTimerMinutes(
                                25,
                                "Pomodoro $pomodoroCurrentCycle/$pomodoroTotalCycles Focus"
                            )
                            startTimer()
                        }
                        .setNegativeButton("Stop") { _, _ ->
                            stopAlarmRingtone()
                            resetTimer()
                        }
                        .setOnCancelListener { stopAlarmRingtone() }
                )
            }
            return
        }

        val taskTitle = lastSnapshot?.currentTask.orEmpty()
        val msg = if (taskTitle.isNotBlank()) {
            "Finished: $taskTitle"
        } else {
            "Timer finished."
        }
        showStyledDialog(
            MaterialAlertDialogBuilder(this)
                .setTitle("Timer finished")
                .setMessage(msg)
                .setPositiveButton("Stop ring") { _, _ ->
                    stopAlarmRingtone()
                    resetTimer()
                }
                .setOnCancelListener {
                    stopAlarmRingtone()
                }
        )
    }

    private fun playSystemAlarmRingtone() {
        stopAlarmRingtone()
        try {
            val uri = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM)
                ?: RingtoneManager.getDefaultUri(RingtoneManager.TYPE_NOTIFICATION)
                ?: RingtoneManager.getDefaultUri(RingtoneManager.TYPE_RINGTONE)
            if (uri != null) {
                activeRingtone = RingtoneManager.getRingtone(applicationContext, uri)
                activeRingtone?.play()
            }
        } catch (_: Exception) {
        }
    }

    private fun stopAlarmRingtone() {
        try {
            activeRingtone?.stop()
        } catch (_: Exception) {
        }
        activeRingtone = null
    }

    private fun showCustomTimerDialog() {
        val layout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(48, 16, 48, 8)
        }
        val etMin = EditText(this).apply {
            hint = "Minutes"
            inputType = InputType.TYPE_CLASS_NUMBER
            setText((timerTotalMs / 60000L).toString())
        }
        layout.addView(etMin)

        showStyledDialog(
            MaterialAlertDialogBuilder(this)
                .setTitle("Custom duration")
                .setView(layout)
                .setPositiveButton("Start") { _, _ ->
                    val mins = etMin.text.toString().toIntOrNull()
                    if (mins != null && mins > 0) {
                        isPomodoroMode = false
                        selectTimerMinutes(mins, "$mins min")
                        startTimer()
                    }
                }
                .setNegativeButton("Cancel", null)
        )
    }

    private fun visibleL3Count(): Int {
        var count = 0
        if (l3ShowTask) count++
        if (l3ShowTimer) count++
        if (l3ShowRings) count++
        if (showPlayingPanel) count++
        if (l3ShowNotif) count++
        return count
    }

    private fun applyPlayingVisibility() {
        val vis = if (showPlayingPanel) View.VISIBLE else View.GONE
        val iconColor = ContextCompat.getColor(
            this,
            if (showPlayingPanel) R.color.m2_secondary else R.color.m2_on_surface_medium
        )

        // Layout 2
        binding.panePlaying3.visibility = vis
        binding.dividerVertical3.visibility = vis
        binding.btnSwapSide3.visibility = vis
        binding.btnTogglePlaying3.setColorFilter(iconColor)

        // Layout 3 (delegated to full Layout 3 component/size controller)
        binding.btnTogglePlaying4.setColorFilter(iconColor)
        applyLayout3Config()
    }

    private fun getL3HorizontalRange(): Pair<Int, Int> {
        val density = resources.displayMetrics.density
        val netPx = (binding.splitContainer4.width -
            binding.splitContainer4.paddingLeft -
            binding.splitContainer4.paddingRight -
            binding.dividerVerticalContainer4.width)
        if (netPx <= 0) return 37 to 66
        val netDp = netPx / density
        val minLeftDp = if (l3ShowRings) 215f else 165f
        val minRightDp = 195f
        var lo = kotlin.math.ceil((minLeftDp / netDp) * 100f).toInt().coerceIn(37, 52)
        var hi = kotlin.math.floor(((netDp - minRightDp) / netDp) * 100f).toInt().coerceIn(54, 66)
        if (lo > hi) {
            lo = 50
            hi = 54
        }
        return lo to hi
    }

    private fun applyLayout3Config() {
        val (minSplit, maxSplit) = getL3HorizontalRange()
        l3SplitPct = l3SplitPct.coerceIn(minSplit, maxSplit)
        l3RightSplitPct = l3RightSplitPct.coerceIn(38, 65)
        l3TextScale = l3TextScale.coerceIn(85, 130)
        l3MetricScale = l3MetricScale.coerceIn(80, 125)

        val hasLeft = l3ShowTask || l3ShowTimer || l3ShowRings
        val hasRight = showPlayingPanel || l3ShowNotif
        val hasBothSides = hasLeft && hasRight

        // Left column components & smart dividers
        binding.paneFocus4.visibility = if (hasLeft) View.VISIBLE else View.GONE
        binding.taskSection4.visibility = if (l3ShowTask) View.VISIBLE else View.GONE
        binding.timerBar4.visibility = if (l3ShowTimer) View.VISIBLE else View.GONE
        binding.ringsBar4.visibility = if (l3ShowRings) View.VISIBLE else View.GONE
        binding.dividerTaskTimer4.visibility =
            if (l3ShowTask && (l3ShowTimer || l3ShowRings)) View.VISIBLE else View.GONE
        binding.dividerTimerRings4.visibility =
            if (l3ShowRings && l3ShowTimer) View.VISIBLE else View.GONE
        binding.paneFocus4.gravity =
            if (l3ShowTask) Gravity.TOP else Gravity.CENTER_VERTICAL

        // Right column components & vertical stack split
        binding.paneRight4.visibility = if (hasRight) View.VISIBLE else View.GONE
        binding.playingSection4.visibility = if (showPlayingPanel) View.VISIBLE else View.GONE
        binding.notifSection4.visibility = if (l3ShowNotif) View.VISIBLE else View.GONE
        binding.dividerRightContainer4.visibility =
            if (showPlayingPanel && l3ShowNotif) View.VISIBLE else View.GONE
        binding.dividerVerticalContainer4.visibility =
            if (hasBothSides) View.VISIBLE else View.GONE
        binding.btnSwapSide4.visibility = if (hasBothSides) View.VISIBLE else View.GONE

        // Column horizontal split weights
        val leftWeight = if (hasBothSides) l3SplitPct.toFloat() else 1f
        val rightWeight = if (hasBothSides) (100 - l3SplitPct).toFloat() else 1f
        (binding.paneFocus4.layoutParams as LinearLayout.LayoutParams).weight = leftWeight
        (binding.paneRight4.layoutParams as LinearLayout.LayoutParams).weight = rightWeight

        // Right stack vertical weights
        val playingWeight = if (showPlayingPanel && l3ShowNotif) l3RightSplitPct.toFloat() else 1f
        val notifWeight = if (showPlayingPanel && l3ShowNotif) (100 - l3RightSplitPct).toFloat() else 1f
        (binding.playingSection4.layoutParams as LinearLayout.LayoutParams).weight = playingWeight
        (binding.notifSection4.layoutParams as LinearLayout.LayoutParams).weight = notifWeight

        // Adaptive Now Playing proportions so controls never clip when stacked with Notifications
        val density = resources.displayMetrics.density
        val compactBoth = showPlayingPanel && l3ShowNotif
        val thumbMaxDp = when {
            !compactBoth -> 124
            l3RightSplitPct >= 62 -> 64
            l3RightSplitPct >= 45 -> 42
            else -> 0
        }
        val thumbLp = binding.ivMediaThumb4.layoutParams
        if (thumbMaxDp > 0) {
            thumbLp.height = (thumbMaxDp * density).roundToInt()
            binding.ivMediaThumb4.layoutParams = thumbLp
        } else {
            binding.ivMediaThumb4.visibility = View.GONE
        }
        val ctrlHeight = when {
            !compactBoth -> 40
            l3RightSplitPct >= 50 -> 34
            else -> 30
        }
        val ctrlPx = (ctrlHeight * density).roundToInt()
        for (b in listOf(binding.btnMediaPrev4, binding.btnMediaPlayPause4, binding.btnMediaStop4, binding.btnMediaNext4)) {
            b.layoutParams.height = ctrlPx
        }
        val transportLp = binding.mediaTransportRow4.layoutParams as LinearLayout.LayoutParams
        transportLp.topMargin = ((if (compactBoth) 3 else 10) * density).roundToInt()
        binding.mediaTransportRow4.layoutParams = transportLp

        // Text scale across Task & Notifications
        val tScale = l3TextScale / 100f
        binding.tvActiveTaskTitle4.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18f * tScale)
        binding.tvActiveTaskDue4.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f * tScale)
        binding.tvActiveTaskNote4.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f * tScale)
        binding.tvNotifHeader4.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f * tScale)

        // Metric scale across Rings & Timer
        val mScale = l3MetricScale / 100f
        val ringPx = (36f * mScale * density).roundToInt()
        for (ring in listOf(binding.ringCpu4, binding.ringRam4, binding.ringStorage4)) {
            ring.indicatorSize = ringPx
            ring.layoutParams.width = ringPx
            ring.layoutParams.height = ringPx
            ring.requestLayout()
        }
        for (lbl in listOf(binding.tvRingCpuLabel4, binding.tvRingRamLabel4, binding.tvRingStorageLabel4)) {
            lbl.setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f * mScale)
        }
        for (valTv in listOf(binding.tvRingCpu4, binding.tvRingRam4, binding.tvRingStorage4)) {
            valTv.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f * mScale)
        }
        binding.tvTimerPreset4.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f * mScale)
        binding.tvTimerDisplay4.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f * mScale)

        binding.splitContainer4.requestLayout()
        if (l3ShowNotif) {
            renderNotificationItems4()
        }
    }

    @SuppressLint("ClickableViewAccessibility")
    private fun setupLayout3DragHandles() {
        var hDownRawX = 0f
        var hStartSplit = 54
        var hLastTapUp = 0L

        binding.dividerVerticalContainer4.setOnTouchListener { _, event ->
            val totalW = (binding.splitContainer4.width -
                binding.splitContainer4.paddingLeft -
                binding.splitContainer4.paddingRight).coerceAtLeast(1)
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    hDownRawX = event.rawX
                    hStartSplit = l3SplitPct
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    val dx = event.rawX - hDownRawX
                    val sign = if (playingOnLeft) -1f else 1f
                    val deltaPct = ((dx / totalW) * 100f * sign).roundToInt()
                    val (minSplit, maxSplit) = getL3HorizontalRange()
                    val next = (hStartSplit + deltaPct).coerceIn(minSplit, maxSplit)
                    if (next != l3SplitPct) {
                        l3SplitPct = next
                        applyLayout3Config()
                    }
                    true
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    val now = android.os.SystemClock.uptimeMillis()
                    if (kotlin.math.abs(event.rawX - hDownRawX) < 10f && now - hLastTapUp < 320L) {
                        l3SplitPct = 54
                        applyLayout3Config()
                        hLastTapUp = 0L
                    } else if (kotlin.math.abs(event.rawX - hDownRawX) < 10f) {
                        hLastTapUp = now
                    }
                    saveL3Prefs()
                    syncOptionsToServer()
                    true
                }
                else -> false
            }
        }

        var vDownRawY = 0f
        var vStartSplit = 55
        var vLastTapUp = 0L

        binding.dividerRightContainer4.setOnTouchListener { _, event ->
            val totalH = binding.paneRight4.height.coerceAtLeast(1)
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    vDownRawY = event.rawY
                    vStartSplit = l3RightSplitPct
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    val dy = event.rawY - vDownRawY
                    val deltaPct = ((dy / totalH) * 100f).roundToInt()
                    val next = (vStartSplit + deltaPct).coerceIn(38, 65)
                    if (next != l3RightSplitPct) {
                        l3RightSplitPct = next
                        applyLayout3Config()
                    }
                    true
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    val now = android.os.SystemClock.uptimeMillis()
                    if (kotlin.math.abs(event.rawY - vDownRawY) < 10f && now - vLastTapUp < 320L) {
                        l3RightSplitPct = 55
                        applyLayout3Config()
                        vLastTapUp = 0L
                    } else if (kotlin.math.abs(event.rawY - vDownRawY) < 10f) {
                        vLastTapUp = now
                    }
                    saveL3Prefs()
                    syncOptionsToServer()
                    true
                }
                else -> false
            }
        }
    }

    private fun saveL3Prefs() {
        prefs.edit()
            .putBoolean("show_playing", showPlayingPanel)
            .putBoolean("l3_show_task", l3ShowTask)
            .putBoolean("l3_show_timer", l3ShowTimer)
            .putBoolean("l3_show_rings", l3ShowRings)
            .putBoolean("l3_show_notif", l3ShowNotif)
            .putInt("l3_split_pct", l3SplitPct)
            .putInt("l3_right_split_pct", l3RightSplitPct)
            .putInt("l3_text_scale", l3TextScale)
            .putInt("l3_metric_scale", l3MetricScale)
            .apply()
    }

    private fun applyPlayingSideOrder() {
        // Layout 2
        val c3 = binding.splitContainer3
        val m3 = binding.paneMetrics3
        val d3 = binding.dividerVertical3
        val p3 = binding.panePlaying3
        c3.removeAllViews()
        if (playingOnLeft) {
            c3.addView(p3)
            c3.addView(d3)
            c3.addView(m3)
        } else {
            c3.addView(m3)
            c3.addView(d3)
            c3.addView(p3)
        }

        // Layout 3
        val c4 = binding.splitContainer4
        val m4 = binding.paneFocus4
        val d4 = binding.dividerVerticalContainer4
        val p4 = binding.paneRight4
        c4.removeAllViews()
        if (playingOnLeft) {
            c4.addView(p4)
            c4.addView(d4)
            c4.addView(m4)
        } else {
            c4.addView(m4)
            c4.addView(d4)
            c4.addView(p4)
        }
    }

    private fun restoreSavedPreferences() {
        val savedLayout = prefs.getInt("ui_layout", 0).coerceIn(0, 2)
        binding.layoutFlipper.displayedChild = savedLayout

        showPlayingPanel = prefs.getBoolean("show_playing", true)
        l3ShowTask = prefs.getBoolean("l3_show_task", true)
        l3ShowTimer = prefs.getBoolean("l3_show_timer", true)
        l3ShowRings = prefs.getBoolean("l3_show_rings", true)
        l3ShowNotif = prefs.getBoolean("l3_show_notif", true)
        if (visibleL3Count() == 0) {
            l3ShowTask = true
        }
        l3SplitPct = prefs.getInt("l3_split_pct", 54).coerceIn(37, 66)
        l3RightSplitPct = prefs.getInt("l3_right_split_pct", 55).coerceIn(38, 65)
        l3TextScale = prefs.getInt("l3_text_scale", 100).coerceIn(85, 130)
        l3MetricScale = prefs.getInt("l3_metric_scale", 100).coerceIn(80, 125)
        showTopBar = prefs.getBoolean("show_top_bar", true)
        showLogo = prefs.getBoolean("show_logo", true)

        applyPlayingVisibility()
        applyLogoVisibility()
        applyTopBarVisibility(animated = false)

        playingOnLeft = prefs.getBoolean("playing_on_left", false)
        applyPlayingSideOrder()

        mediaTimeMode = prefs.getInt("media_time_mode", 0).coerceIn(0, 3)
        renderMediaTime(null)
        updateTimerUi()

        fontChoice = prefs.getInt("font_choice", 0).coerceIn(0, 3)
        applySelectedFont()

        val savedMode = prefs.getString("mode", ConnectionMode.NETWORK.name)
        currentMode = runCatching { ConnectionMode.valueOf(savedMode ?: "") }
            .getOrDefault(ConnectionMode.NETWORK)
        syncModeToggleUi()
    }

    private fun syncModeToggleUi() {
        val btnId = when (currentMode) {
            ConnectionMode.USB -> R.id.btnModeUsb
            ConnectionMode.BLUETOOTH -> R.id.btnModeBluetooth
            ConnectionMode.NETWORK -> R.id.btnModeNetwork
        }
        binding.toggleMode.check(btnId)
    }

    private fun showFontSelectorDialog() {
        val options = arrayOf(
            "Fira Sans",
            "IBM Plex Sans",
            "Barlow",
            "Roboto"
        )
        showStyledDialog(
            MaterialAlertDialogBuilder(this)
                .setTitle("Select font")
                .setSingleChoiceItems(options, fontChoice) { dialog, which ->
                    fontChoice = which
                    prefs.edit().putInt("font_choice", which).apply()
                    applySelectedFont()
                    syncOptionsToServer()
                    dialog.dismiss()
                }
                .setNegativeButton("Close", null)
        )
    }

    private fun getSelectedTypefacePair(): Pair<Typeface?, Typeface?> {
        val idx = fontChoice.coerceIn(0, 3)
        cachedTypefaces[idx]?.let { return it }
        val pair = when (idx) {
            1 -> Pair(
                ResourcesCompat.getFont(this, R.font.ibm_plex_sans_regular),
                ResourcesCompat.getFont(this, R.font.ibm_plex_sans_bold)
            )
            2 -> Pair(
                ResourcesCompat.getFont(this, R.font.barlow_regular),
                ResourcesCompat.getFont(this, R.font.barlow_bold)
            )
            3 -> Pair(Typeface.SANS_SERIF, Typeface.create(Typeface.SANS_SERIF, Typeface.BOLD))
            else -> Pair(
                ResourcesCompat.getFont(this, R.font.fira_sans_regular),
                ResourcesCompat.getFont(this, R.font.fira_sans_bold)
            )
        }
        cachedTypefaces[idx] = pair
        return pair
    }

    private fun applySelectedFont() {
        applySelectedFontToView(binding.root)
    }

    private fun applySelectedFontToView(root: View) {
        val (regTf, boldTf) = getSelectedTypefacePair()
        applyFontRecursively(root, regTf, boldTf)
    }

    private fun applyFontRecursively(view: View, regTf: Typeface?, boldTf: Typeface?) {
        if (view is TextView) {
            // Preserve original XML bold vs normal across multiple font switches
            val origBold = (view.getTag(R.id.layoutFlipper) as? Boolean)
                ?: (view.typeface?.isBold == true).also {
                    view.setTag(R.id.layoutFlipper, it)
                }
            val baseTf = if (origBold) boldTf else regTf
            if (baseTf != null) {
                view.setTypeface(baseTf, if (origBold) Typeface.BOLD else Typeface.NORMAL)
            }
        } else if (view is ViewGroup) {
            for (i in 0 until view.childCount) {
                applyFontRecursively(view.getChildAt(i), regTf, boldTf)
            }
        }
    }

    private fun showLayoutSelectorDialog() {
        val options = arrayOf(
            "Layout 1",
            "Layout 2",
            "Layout 3"
        )
        val current = binding.layoutFlipper.displayedChild.coerceIn(0, 2)
        showStyledDialog(
            MaterialAlertDialogBuilder(this)
                .setTitle("Select layout")
                .setSingleChoiceItems(options, current) { dialog, which ->
                    binding.layoutFlipper.displayedChild = which
                    prefs.edit().putInt("ui_layout", which).apply()
                    lastSnapshot?.let { renderSnapshot(it) }
                    syncOptionsToServer()
                    dialog.dismiss()
                }
                .setNegativeButton("Close", null)
        )
    }

    private fun syncOptionsToServer() {
        client.sendClientConfig(
            layout = binding.layoutFlipper.displayedChild.coerceIn(0, 2),
            font = fontChoice.coerceIn(0, 3),
            showPlaying = showPlayingPanel,
            playingLeft = playingOnLeft,
            timeMode = mediaTimeMode.coerceIn(0, 3),
            l3ShowTask = l3ShowTask,
            l3ShowTimer = l3ShowTimer,
            l3ShowRings = l3ShowRings,
            l3ShowNotif = l3ShowNotif,
            l3SplitPct = l3SplitPct,
            l3RightSplitPct = l3RightSplitPct,
            l3TextScale = l3TextScale,
            l3MetricScale = l3MetricScale,
            showTopBar = showTopBar,
            showLogo = showLogo
        )
    }

    private fun showLayout3ComponentsDialog() {
        val density = resources.displayMetrics.density
        val padH = (20 * density).toInt()
        val padV = (8 * density).toInt()
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(padH, padV, padH, (4 * density).toInt())
        }
        val colLeft = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        val colRight = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
                marginStart = (12 * density).toInt()
            }
        }
        row.addView(colLeft)
        row.addView(colRight)

        val textCol = ContextCompat.getColor(this, R.color.m2_on_surface_high)
        val tintList = android.content.res.ColorStateList.valueOf(
            ContextCompat.getColor(this, R.color.m2_secondary)
        )

        fun addToggle(parent: LinearLayout, title: String, initial: Boolean, onChange: (Boolean) -> Unit) {
            val cb = CheckBox(this).apply {
                text = title
                textSize = 13.5f
                isChecked = initial
                setTextColor(textCol)
                buttonTintList = tintList
            }
            cb.setOnCheckedChangeListener { buttonView, isChecked ->
                if (!isChecked && visibleL3Count() <= 1) {
                    buttonView.isChecked = true
                    Toast.makeText(this, "Keep at least 1 component visible", Toast.LENGTH_SHORT).show()
                    return@setOnCheckedChangeListener
                }
                onChange(isChecked)
                saveL3Prefs()
                applyPlayingVisibility()
                syncOptionsToServer()
            }
            parent.addView(cb)
        }

        addToggle(colLeft, "Task & Note", l3ShowTask) { l3ShowTask = it }
        addToggle(colLeft, "Timer & Pomodoro", l3ShowTimer) { l3ShowTimer = it }
        addToggle(colLeft, "Resource Rings", l3ShowRings) { l3ShowRings = it }
        addToggle(colRight, "Now Playing", showPlayingPanel) { showPlayingPanel = it }
        addToggle(colRight, "OS Notifications", l3ShowNotif) { l3ShowNotif = it }

        showStyledDialog(
            MaterialAlertDialogBuilder(this)
                .setTitle("Visible components")
                .setView(row)
                .setPositiveButton("Close", null)
        )
    }

    private fun showLayout3ResizeDialog() {
        val density = resources.displayMetrics.density
        val padH = (18 * density).toInt()
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padH, (6 * density).toInt(), padH, (2 * density).toInt())
        }

        val (minSplit, maxSplit) = getL3HorizontalRange()
        val hasLeft = l3ShowTask || l3ShowTimer || l3ShowRings
        val hasRight = showPlayingPanel || l3ShowNotif

        val previewBox = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setBackgroundColor(ContextCompat.getColor(this@MainActivity, R.color.bg_screen))
            setPadding((5 * density).toInt(), (5 * density).toInt(), (5 * density).toInt(), (5 * density).toInt())
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                (56 * density).toInt()
            ).apply { bottomMargin = (6 * density).toInt() }
        }
        fun makeCell(labelColor: Int): TextView = TextView(this).apply {
            gravity = Gravity.CENTER
            textSize = 10.5f
            setTextColor(labelColor)
            setBackgroundColor(ContextCompat.getColor(this@MainActivity, R.color.m2_track))
        }
        val highCol = ContextCompat.getColor(this, R.color.m2_on_surface_high)
        val accentCol = ContextCompat.getColor(this, R.color.m2_secondary)
        val pvLeft = makeCell(highCol).apply {
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, l3SplitPct.toFloat())
        }
        val pvRightCol = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, (100 - l3SplitPct).toFloat()).apply {
                marginStart = (4 * density).toInt()
            }
        }
        val pvPlaying = makeCell(accentCol).apply {
            layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, l3RightSplitPct.toFloat())
        }
        val pvNotif = makeCell(highCol).apply {
            layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, (100 - l3RightSplitPct).toFloat()).apply {
                topMargin = (3 * density).toInt()
            }
        }
        pvRightCol.addView(pvPlaying)
        pvRightCol.addView(pvNotif)
        if (playingOnLeft) {
            pvRightCol.layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, (100 - l3SplitPct).toFloat())
            pvLeft.layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, l3SplitPct.toFloat()).apply {
                marginStart = (4 * density).toInt()
            }
            previewBox.addView(pvRightCol)
            previewBox.addView(pvLeft)
        } else {
            previewBox.addView(pvLeft)
            previewBox.addView(pvRightCol)
        }
        root.addView(previewBox)

        fun updatePreview() {
            pvLeft.visibility = if (hasLeft) View.VISIBLE else View.GONE
            pvRightCol.visibility = if (hasRight) View.VISIBLE else View.GONE
            (pvLeft.layoutParams as LinearLayout.LayoutParams).weight =
                if (hasLeft && hasRight) l3SplitPct.toFloat() else 1f
            (pvRightCol.layoutParams as LinearLayout.LayoutParams).weight =
                if (hasLeft && hasRight) (100 - l3SplitPct).toFloat() else 1f
            pvPlaying.visibility = if (showPlayingPanel) View.VISIBLE else View.GONE
            pvNotif.visibility = if (l3ShowNotif) View.VISIBLE else View.GONE
            (pvPlaying.layoutParams as LinearLayout.LayoutParams).weight =
                if (showPlayingPanel && l3ShowNotif) l3RightSplitPct.toFloat() else 1f
            (pvNotif.layoutParams as LinearLayout.LayoutParams).weight =
                if (showPlayingPanel && l3ShowNotif) (100 - l3RightSplitPct).toFloat() else 1f
            pvLeft.text = if (hasLeft) "Task / Metrics ${l3SplitPct}%" else ""
            pvPlaying.text = if (showPlayingPanel) {
                if (l3ShowNotif) "Playing ${l3RightSplitPct}%" else "Playing"
            } else ""
            pvNotif.text = if (l3ShowNotif) {
                if (showPlayingPanel) "Notif ${100 - l3RightSplitPct}%" else "OS Notif"
            } else ""
            previewBox.requestLayout()
        }
        updatePreview()

        fun makeSliderCell(
            parentRow: LinearLayout,
            label: String,
            minVal: Int,
            maxVal: Int,
            initial: Int,
            enabled: Boolean,
            isRightCol: Boolean,
            onApply: (Int) -> Unit
        ): Pair<SeekBar, TextView> {
            val cell = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
                    if (isRightCol) marginStart = (12 * density).toInt()
                }
            }
            val header = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(0, (4 * density).toInt(), 0, 0)
            }
            val tvLabel = TextView(this).apply {
                text = label
                textSize = 12f
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.END
                setTextColor(highCol)
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            }
            val tvVal = TextView(this).apply {
                text = "$initial%"
                textSize = 12f
                setTextColor(accentCol)
            }
            header.addView(tvLabel)
            header.addView(tvVal)
            cell.addView(header)

            val sb = SeekBar(this).apply {
                max = (maxVal - minVal).coerceAtLeast(1)
                progress = (initial - minVal).coerceIn(0, max)
                isEnabled = enabled
                alpha = if (enabled) 1f else 0.4f
            }
            sb.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(seekBar: SeekBar?, progress: Int, fromUser: Boolean) {
                    val v = minVal + progress
                    tvVal.text = "$v%"
                    if (fromUser) {
                        onApply(v)
                        updatePreview()
                        applyLayout3Config()
                    }
                }
                override fun onStartTrackingTouch(seekBar: SeekBar?) {}
                override fun onStopTrackingTouch(seekBar: SeekBar?) {
                    saveL3Prefs()
                    syncOptionsToServer()
                }
            })
            cell.addView(sb)
            parentRow.addView(cell)
            return sb to tvVal
        }

        // 2x2 grid of sliders so all 4 fit cleanly on a 360dp landscape screen without scrolling
        val row1 = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        val row2 = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        root.addView(row1)
        root.addView(row2)

        val (splitSlider, splitValTv) = makeSliderCell(row1, "Task <-> Right", minSplit, maxSplit, l3SplitPct, hasLeft && hasRight, false) { l3SplitPct = it }
        val (stackSlider, stackValTv) = makeSliderCell(row1, "Playing <-> Notif", 38, 65, l3RightSplitPct, showPlayingPanel && l3ShowNotif, true) { l3RightSplitPct = it }
        val (textSlider, textValTv) = makeSliderCell(row2, "Text size", 85, 130, l3TextScale, true, false) { l3TextScale = it }
        val (metricSlider, metricValTv) = makeSliderCell(row2, "Rings & Timer", 80, 125, l3MetricScale, true, true) { l3MetricScale = it }

        val dlg = showStyledDialog(
            MaterialAlertDialogBuilder(this)
                .setTitle("Resize Layout 3")
                .setView(root)
                .setNeutralButton("Reset", null)
                .setPositiveButton("Close", null)
        )
        dlg.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener {
            l3SplitPct = 54.coerceIn(minSplit, maxSplit)
            l3RightSplitPct = 55
            l3TextScale = 100
            l3MetricScale = 100
            splitSlider.progress = (l3SplitPct - minSplit).coerceIn(0, splitSlider.max)
            stackSlider.progress = 55 - 38
            textSlider.progress = 100 - 85
            metricSlider.progress = 100 - 80
            splitValTv.text = "${l3SplitPct}%"
            stackValTv.text = "55%"
            textValTv.text = "100%"
            metricValTv.text = "100%"
            updatePreview()
            saveL3Prefs()
            applyLayout3Config()
            syncOptionsToServer()
        }
    }

    private fun autoDetectAndConnectOnStartup() {
        val host = prefs.getString("host", "192.168.1.100") ?: "192.168.1.100"
        val port = prefs.getInt("port", MonitorClient.DEFAULT_PORT)
        val btAddress = prefs.getString("bt_address", null)
        val btAdapterIfPermitted = if (hasBluetoothPermission()) bluetoothAdapter else null

        client.autoDetectAndConnect(
            preferredMode = currentMode,
            savedHost = host,
            savedPort = port,
            bluetoothAddress = btAddress,
            bluetoothAdapter = btAdapterIfPermitted
        )
    }

    private fun startConnection() {
        if (currentMode == ConnectionMode.BLUETOOTH && !hasBluetoothPermission()) {
            requestBluetoothPermission()
            return
        }

        val host = prefs.getString("host", "192.168.1.100") ?: "192.168.1.100"
        val port = prefs.getInt("port", MonitorClient.DEFAULT_PORT)
        val btAddress = prefs.getString("bt_address", null)

        client.connect(
            mode = currentMode,
            host = host,
            port = port,
            bluetoothAddress = btAddress,
            bluetoothAdapter = bluetoothAdapter
        )
    }

    private fun updateConnectionUi(connected: Boolean, status: String) {
        val dotRes = if (connected) {
            android.R.drawable.presence_online
        } else {
            android.R.drawable.presence_offline
        }

        // Layout 1
        binding.tvStatus1.text = status
        binding.btnConnect.setColorFilter(
            ContextCompat.getColor(
                this,
                if (connected) R.color.accent_ram else R.color.m2_on_surface_high
            )
        )
        binding.statusDot1.setBackgroundResource(dotRes)

        // Layout 2: Never hide option bar until connected
        binding.tvStatus3.text = status
        binding.statusDot3.setBackgroundResource(dotRes)

        // Layout 3: Never hide option bar until connected
        binding.tvStatus4.text = status
        binding.statusDot4.setBackgroundResource(dotRes)

        applyTopBarVisibility(animated = false)

        if (!connected) {
            // Reset so server-side options always take priority upon next connect
            lastAppliedCfgSeq = 0
            lastAppliedPowerSeq = 0
        }
    }

    private fun applyRemoteCommandsIfNeeded(s: ResourceSnapshot) {
        if (s.cfgSeq > 0 && s.cfgSeq != lastAppliedCfgSeq) {
            lastAppliedCfgSeq = s.cfgSeq
            val editor = prefs.edit()

            if (s.cfgLayout in 0..2 && binding.layoutFlipper.displayedChild != s.cfgLayout) {
                binding.layoutFlipper.displayedChild = s.cfgLayout
                editor.putInt("ui_layout", s.cfgLayout)
            }
            if (s.cfgFont in 0..3 && fontChoice != s.cfgFont) {
                fontChoice = s.cfgFont
                editor.putInt("font_choice", fontChoice)
                applySelectedFont()
            }
            if (showPlayingPanel != s.cfgShowPlaying) {
                showPlayingPanel = s.cfgShowPlaying
                editor.putBoolean("show_playing", showPlayingPanel)
                applyPlayingVisibility()
            }
            if (playingOnLeft != s.cfgPlayingLeft) {
                playingOnLeft = s.cfgPlayingLeft
                editor.putBoolean("playing_on_left", playingOnLeft)
                applyPlayingSideOrder()
            }
            val newTimeMode = s.cfgTimeMode.coerceIn(0, 3)
            if (mediaTimeMode != newTimeMode) {
                mediaTimeMode = newTimeMode
                editor.putInt("media_time_mode", mediaTimeMode)
            }
            var l3Changed = false
            if (l3ShowTask != s.cfgL3ShowTask) {
                l3ShowTask = s.cfgL3ShowTask
                editor.putBoolean("l3_show_task", l3ShowTask)
                l3Changed = true
            }
            if (l3ShowTimer != s.cfgL3ShowTimer) {
                l3ShowTimer = s.cfgL3ShowTimer
                editor.putBoolean("l3_show_timer", l3ShowTimer)
                l3Changed = true
            }
            if (l3ShowRings != s.cfgL3ShowRings) {
                l3ShowRings = s.cfgL3ShowRings
                editor.putBoolean("l3_show_rings", l3ShowRings)
                l3Changed = true
            }
            if (l3ShowNotif != s.cfgL3ShowNotif) {
                l3ShowNotif = s.cfgL3ShowNotif
                editor.putBoolean("l3_show_notif", l3ShowNotif)
                l3Changed = true
            }
            val nextSplit = s.cfgL3SplitPct.coerceIn(37, 66)
            if (l3SplitPct != nextSplit) {
                l3SplitPct = nextSplit
                editor.putInt("l3_split_pct", l3SplitPct)
                l3Changed = true
            }
            val nextRightSplit = s.cfgL3RightSplitPct.coerceIn(38, 65)
            if (l3RightSplitPct != nextRightSplit) {
                l3RightSplitPct = nextRightSplit
                editor.putInt("l3_right_split_pct", l3RightSplitPct)
                l3Changed = true
            }
            val nextTextScale = s.cfgL3TextScale.coerceIn(85, 130)
            if (l3TextScale != nextTextScale) {
                l3TextScale = nextTextScale
                editor.putInt("l3_text_scale", l3TextScale)
                l3Changed = true
            }
            val nextMetricScale = s.cfgL3MetricScale.coerceIn(80, 125)
            if (l3MetricScale != nextMetricScale) {
                l3MetricScale = nextMetricScale
                editor.putInt("l3_metric_scale", l3MetricScale)
                l3Changed = true
            }
            if (showTopBar != s.cfgShowTopBar) {
                showTopBar = s.cfgShowTopBar
                editor.putBoolean("show_top_bar", showTopBar)
                applyTopBarVisibility(animated = true)
            }
            if (showLogo != s.cfgShowLogo) {
                showLogo = s.cfgShowLogo
                editor.putBoolean("show_logo", showLogo)
                applyLogoVisibility()
            }
            editor.apply()
            if (l3Changed) {
                applyLayout3Config()
            }
        }

        if (s.timerSeq > 0 && s.timerSeq != lastAppliedTimerSeq) {
            lastAppliedTimerSeq = s.timerSeq
            when (s.timerCmd) {
                "set_min" -> {
                    isPomodoroMode = false
                    val mins = s.timerVal.coerceIn(1, 360)
                    selectTimerMinutes(mins, "$mins min")
                    startTimer()
                }
                "pomodoro" -> {
                    startPomodoroSession(s.timerVal.coerceIn(1, 24))
                }
                "toggle" -> {
                    if (timerRunning) pauseTimer() else startTimer()
                }
                "reset" -> {
                    stopAlarmRingtone()
                    resetTimer()
                }
                "stop_ring" -> {
                    stopAlarmRingtone()
                }
            }
        }

        if (s.powerSeq > 0 && s.powerSeq != lastAppliedPowerSeq) {
            lastAppliedPowerSeq = s.powerSeq
            when (s.powerCmd) {
                "close_and_lock" -> executePowerAction(closeApp = true, lockScreen = true)
                "lock_only" -> executePowerAction(closeApp = false, lockScreen = true)
                "close_only" -> executePowerAction(closeApp = true, lockScreen = false)
            }
        }
    }

    private fun renderSnapshot(s: ResourceSnapshot) {
        lastSnapshot = s
        applyRemoteCommandsIfNeeded(s)

        val cpuInt = s.cpuPercent.roundToInt().coerceIn(0, 100)
        val ramInt = s.ramPercent.roundToInt().coerceIn(0, 100)
        val diskInt = s.diskPercent.roundToInt().coerceIn(0, 100)
        val taskText = s.currentTask.ifBlank { "No active task" }
        val taskWithDue = if (s.currentDue.isNotBlank()) {
            "$taskText   Due ${s.currentDue}"
        } else {
            taskText
        }

        // Decode new thumbnail bitmap only when server reports a new thumbSeq (RGB_565 downsampled)
        val isPlaying = s.mediaStatus.equals("Playing", ignoreCase = true)
        val isActiveMedia = isPlaying || s.mediaStatus.equals("Paused", ignoreCase = true)
        if (!isActiveMedia) {
            clearCachedThumb()
            lastThumbSeq = s.thumbSeq
        } else if (s.thumbSeq != lastThumbSeq && s.mediaThumbBase64.isNotEmpty()) {
            lastThumbSeq = s.thumbSeq
            val newBmp = try {
                val bytes = Base64.decode(s.mediaThumbBase64, Base64.DEFAULT)
                decodeSampledThumb(bytes)
            } catch (_: Exception) {
                null
            }
            clearCachedThumb()
            cachedThumbBitmap = newBmp
        }

        // Always cache notification payload regardless of active layout
        var notifNeedsRedraw = false
        if (s.notifSync) {
            cachedNotifAccess = s.notifAccess
            cachedNotifications = s.notifications
            if (s.notifSeq != lastRenderedNotifSeq) {
                notifNeedsRedraw = true
            }
        } else if (cachedNotifAccess != s.notifAccess) {
            cachedNotifAccess = s.notifAccess
            notifNeedsRedraw = true
        }

        // Only update views belonging to the currently active layout to save CPU & UI passes
        when (binding.layoutFlipper.displayedChild) {
            0 -> {
                binding.tvHostTitle1.text = s.hostname
                binding.tvCpuPercent1.text = getString(R.string.percent_fmt, cpuInt)
                setProgressSmooth(binding.progressCpu1, cpuInt)
                binding.tvCpuDetail1.text = if (cpuInt >= 85) "High load" else "Normal"

                binding.tvRamPercent1.text = getString(R.string.percent_fmt, ramInt)
                setProgressSmooth(binding.progressRam1, ramInt)
                binding.tvRamDetail1.text = String.format("%.1f / %.1f GB", s.ramUsedGb, s.ramTotalGb)

                binding.tvStoragePercent1.text = getString(R.string.percent_fmt, diskInt)
                setProgressSmooth(binding.progressStorage1, diskInt)
                binding.tvStorageDetail1.text = String.format("%.0f / %.0f GB", s.diskUsedGb, s.diskTotalGb)

                binding.tvCurrentTask1.text = if (s.currentNote.isNotBlank()) {
                    SpannableStringBuilder(taskWithDue).append("\n").append(formatSimpleNote(s.currentNote))
                } else {
                    taskWithDue
                }
            }
            1 -> {
                binding.tvHostTitle3.text = s.hostname
                binding.tvCpuValue3.text = getString(R.string.percent_fmt, cpuInt)
                setProgressSmooth(binding.progressCpu3, cpuInt)

                binding.tvRamValue3.text = getString(R.string.ram_value_fmt, ramInt, s.ramUsedGb, s.ramTotalGb)
                setProgressSmooth(binding.progressRam3, ramInt)

                binding.tvStorageValue3.text = getString(R.string.storage_value_fmt, diskInt, s.diskUsedGb, s.diskTotalGb)
                setProgressSmooth(binding.progressStorage3, diskInt)

                binding.tvCurrentTask3.text = getString(R.string.current_task_fmt, taskWithDue)

                if (showPlayingPanel) {
                    renderPlayingViews(
                        s,
                        isPlaying,
                        isActiveMedia,
                        binding.ivMediaThumb3,
                        binding.tvMediaArtist3,
                        binding.tvMediaTitle3,
                        binding.progressMedia3,
                        binding.btnMediaPlayPause
                    )
                    renderMediaTime(s)
                }
            }
            2 -> {
                binding.tvHostTitle4.text = s.hostname
                binding.tvActiveTaskTitle4.text = taskText
                if (s.currentDue.isNotBlank()) {
                    binding.tvActiveTaskDue4.visibility = View.VISIBLE
                    binding.tvActiveTaskDue4.text = "Due ${s.currentDue}"
                } else {
                    binding.tvActiveTaskDue4.visibility = View.GONE
                    binding.tvActiveTaskDue4.text = ""
                }
                binding.tvActiveTaskNote4.text = formatSimpleNote(s.currentNote)

                setProgressSmooth(binding.ringCpu4, cpuInt)
                binding.tvRingCpu4.text = "$cpuInt%"

                setProgressSmooth(binding.ringRam4, ramInt)
                binding.tvRingRam4.text = "$ramInt%"

                setProgressSmooth(binding.ringStorage4, diskInt)
                binding.tvRingStorage4.text = "$diskInt%"

                if (showPlayingPanel) {
                    renderPlayingViews(
                        s,
                        isPlaying,
                        isActiveMedia,
                        binding.ivMediaThumb4,
                        binding.tvMediaArtist4,
                        binding.tvMediaTitle4,
                        binding.progressMedia4,
                        binding.btnMediaPlayPause4
                    )
                    if (l3ShowNotif && l3RightSplitPct < 45) {
                        binding.ivMediaThumb4.visibility = View.GONE
                    }
                    renderMediaTime(s)
                }

                if (notifNeedsRedraw || s.notifSeq != lastRenderedNotifSeq) {
                    lastRenderedNotifSeq = s.notifSeq
                    renderNotificationItems4()
                }
            }
        }
    }

    private fun formatSimpleNote(raw: String): CharSequence {
        if (raw.isEmpty()) return ""
        val sb = SpannableStringBuilder()
        val lines = raw.split('\n')
        for ((idx, line) in lines.withIndex()) {
            if (idx > 0) sb.append('\n')
            val normalized = when {
                line.startsWith("- ") -> "\u2022 " + line.substring(2)
                line.startsWith("[ ] ") -> "\u2610 " + line.substring(4)
                line.startsWith("[x] ", ignoreCase = true) -> "\u2611 " + line.substring(4)
                else -> line
            }
            var i = 0
            val n = normalized.length
            while (i < n) {
                if (i + 1 < n && normalized[i] == '*' && normalized[i + 1] == '*') {
                    val end = normalized.indexOf("**", i + 2)
                    if (end > i + 2) {
                        val sStart = sb.length
                        sb.append(normalized.substring(i + 2, end))
                        sb.setSpan(StyleSpan(Typeface.BOLD), sStart, sb.length, Spannable.SPAN_EXCLUSIVE_EXCLUSIVE)
                        i = end + 2
                        continue
                    }
                } else if (i + 1 < n && normalized[i] == '~' && normalized[i + 1] == '~') {
                    val end = normalized.indexOf("~~", i + 2)
                    if (end > i + 2) {
                        val sStart = sb.length
                        sb.append(normalized.substring(i + 2, end))
                        sb.setSpan(StrikethroughSpan(), sStart, sb.length, Spannable.SPAN_EXCLUSIVE_EXCLUSIVE)
                        i = end + 2
                        continue
                    }
                } else if (normalized[i] == '*') {
                    val end = normalized.indexOf('*', i + 1)
                    if (end > i + 1) {
                        val sStart = sb.length
                        sb.append(normalized.substring(i + 1, end))
                        sb.setSpan(StyleSpan(Typeface.ITALIC), sStart, sb.length, Spannable.SPAN_EXCLUSIVE_EXCLUSIVE)
                        i = end + 1
                        continue
                    }
                }
                sb.append(normalized[i])
                i++
            }
        }
        return sb
    }

    private fun renderNotificationItems4() {
        val density = resources.displayMetrics.density
        val tScale = l3TextScale / 100f
        val count = cachedNotifications.size
        binding.tvNotifCount4.text = if (count > 0) "$count" else ""
        binding.btnClearNotifs4.visibility = if (count > 0) View.VISIBLE else View.GONE

        if (!cachedNotifAccess) {
            binding.tvNotifEmpty4.text = getString(R.string.notif_access_denied)
            binding.tvNotifEmpty4.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f * tScale)
            binding.tvNotifEmpty4.visibility = View.VISIBLE
            binding.notifContainer4.removeAllViews()
            return
        }

        if (cachedNotifications.isEmpty()) {
            binding.tvNotifEmpty4.text = getString(R.string.notif_empty)
            binding.tvNotifEmpty4.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f * tScale)
            binding.tvNotifEmpty4.visibility = View.VISIBLE
            binding.notifContainer4.removeAllViews()
            return
        }

        binding.tvNotifEmpty4.visibility = View.GONE
        binding.notifContainer4.removeAllViews()
        val highColor = ContextCompat.getColor(this, R.color.m2_on_surface_high)
        val medColor = ContextCompat.getColor(this, R.color.m2_on_surface_medium)
        val accentColor = ContextCompat.getColor(this, R.color.m2_secondary)
        val dividerColor = ContextCompat.getColor(this, R.color.m2_divider)

        for (item in cachedNotifications) {
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(0, (5 * density).toInt(), 0, (5 * density).toInt())
            }

            val topMeta = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            val tvMeta = TextView(this).apply {
                text = if (item.time.isNotBlank()) "${item.app}  •  ${item.time}" else item.app
                setTextColor(accentColor)
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f * tScale)
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.END
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            }
            val tvDismiss = TextView(this).apply {
                text = "×"
                setTextColor(medColor)
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
                setPadding((8 * density).toInt(), 0, (2 * density).toInt(), 0)
                setOnClickListener { client.sendDismissNotification(item.id) }
            }
            topMeta.addView(tvMeta)
            topMeta.addView(tvDismiss)
            row.addView(topMeta)

            val tvTitle = TextView(this).apply {
                text = item.title
                setTextColor(highColor)
                setTypeface(typeface, Typeface.BOLD)
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f * tScale)
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.END
            }
            row.addView(tvTitle)

            if (item.body.isNotBlank()) {
                val tvBody = TextView(this).apply {
                    text = item.body
                    setTextColor(medColor)
                    setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f * tScale)
                    maxLines = 2
                    ellipsize = TextUtils.TruncateAt.END
                }
                row.addView(tvBody)
                row.setOnClickListener {
                    tvBody.maxLines = if (tvBody.maxLines == 2) Int.MAX_VALUE else 2
                }
            }

            val sep = View(this).apply {
                layoutParams = LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT,
                    (1 * density).toInt().coerceAtLeast(1)
                ).apply { topMargin = (5 * density).toInt() }
                setBackgroundColor(dividerColor)
            }
            row.addView(sep)
            binding.notifContainer4.addView(row)
        }
        applySelectedFontToView(binding.notifContainer4)
    }

    private fun renderPlayingViews(
        s: ResourceSnapshot,
        isPlaying: Boolean,
        isActiveMedia: Boolean,
        ivThumb: android.widget.ImageView,
        tvArtist: TextView,
        tvTitle: TextView,
        progress: com.google.android.material.progressindicator.LinearProgressIndicator,
        btnPlayPause: android.widget.ImageButton
    ) {
        if (isActiveMedia && s.mediaArtist.isNotBlank()) {
            tvArtist.visibility = View.VISIBLE
            tvArtist.text = s.mediaArtist
        } else {
            tvArtist.visibility = View.GONE
            tvArtist.text = ""
        }

        tvTitle.text = if (isActiveMedia && s.mediaTitle.isNotBlank()) {
            s.mediaTitle
        } else {
            getString(R.string.media_not_playing)
        }

        val mediaPct = if (isActiveMedia && s.mediaDurSec > 0) {
            ((s.mediaPosSec.toFloat() / s.mediaDurSec.toFloat()) * 100f).roundToInt().coerceIn(0, 100)
        } else {
            0
        }
        progress.progress = mediaPct
        btnPlayPause.setImageResource(if (isPlaying) R.drawable.ic_fa_pause else R.drawable.ic_fa_play)

        val bmp = cachedThumbBitmap
        if (isActiveMedia && bmp != null) {
            ivThumb.visibility = View.VISIBLE
            ivThumb.setImageBitmap(bmp)
        } else {
            ivThumb.visibility = View.GONE
        }
    }

    private fun renderMediaTime(s: ResourceSnapshot?) {
        val isActive = s != null && (
            s.mediaStatus.equals("Playing", ignoreCase = true) ||
                s.mediaStatus.equals("Paused", ignoreCase = true)
            )
        val pos = if (isActive) s!!.mediaPosSec else 0
        val dur = if (isActive) s!!.mediaDurSec else 0
        val rem = (dur - pos).coerceAtLeast(0)

        val elapsedStr = formatTime(pos)
        val remainingStr = "-${formatTime(rem)}"
        val totalStr = formatTime(dur)

        val leftText: String
        val rightVisible: Int
        when (mediaTimeMode) {
            1 -> {
                leftText = remainingStr
                rightVisible = View.VISIBLE
            }
            2 -> {
                leftText = remainingStr
                rightVisible = View.GONE
            }
            3 -> {
                leftText = "$elapsedStr / $remainingStr"
                rightVisible = View.VISIBLE
            }
            else -> {
                leftText = elapsedStr
                rightVisible = View.VISIBLE
            }
        }

        binding.tvMediaTimeLeft3.text = leftText
        binding.tvMediaTimeRight3.visibility = rightVisible
        binding.tvMediaTimeRight3.text = totalStr

        binding.tvMediaTimeLeft4.text = leftText
        binding.tvMediaTimeRight4.visibility = rightVisible
        binding.tvMediaTimeRight4.text = totalStr
    }

    private fun formatTime(totalSeconds: Int): String {
        val safe = totalSeconds.coerceAtLeast(0)
        val minutes = safe / 60
        val seconds = safe % 60
        return String.format("%02d:%02d", minutes, seconds)
    }

    private fun showNetworkConfigDialog() {
        val currentHost = prefs.getString("host", "192.168.1.100") ?: "192.168.1.100"
        val currentPort = prefs.getInt("port", MonitorClient.DEFAULT_PORT)

        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(36, 12, 36, 4)
        }

        val etHost = EditText(this).apply {
            hint = "IP address"
            setText(currentHost)
            inputType = InputType.TYPE_CLASS_TEXT
            maxLines = 1
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        val etPort = EditText(this).apply {
            hint = "Port"
            setText(currentPort.toString())
            inputType = InputType.TYPE_CLASS_NUMBER
            maxLines = 1
            layoutParams = LinearLayout.LayoutParams(
                (88 * resources.displayMetrics.density).toInt(),
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply {
                marginStart = (10 * resources.displayMetrics.density).toInt()
            }
        }
        row.addView(etHost)
        row.addView(etPort)

        showStyledDialog(
            MaterialAlertDialogBuilder(this)
                .setTitle("Local network")
                .setView(row)
                .setPositiveButton("Save") { _, _ ->
                    val host = etHost.text.toString().trim()
                    val port = etPort.text.toString().toIntOrNull() ?: MonitorClient.DEFAULT_PORT
                    if (host.isNotEmpty()) {
                        prefs.edit().putString("host", host).putInt("port", port).apply()
                        Toast.makeText(this, "Saved $host:$port", Toast.LENGTH_SHORT).show()
                    }
                }
                .setNegativeButton("Cancel", null)
        )
    }

    private fun showUsbHelpDialog() {
        showStyledDialog(
            MaterialAlertDialogBuilder(this)
                .setTitle("USB")
                .setMessage(
                    "1. Connect phone to PC via USB cable with USB Debugging enabled.\n" +
                        "2. The Windows server automatically forwards USB port 8765.\n" +
                        "3. Tap Connect on the phone."
                )
                .setPositiveButton("OK", null)
        )
    }

    @SuppressLint("MissingPermission")
    private fun showConnectionDialog() {
        val density = resources.displayMetrics.density
        val padH = (24 * density).toInt()
        val padV = (8 * density).toInt()

        val container = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padH, padV, padH, 0)
        }

        val radioGroup = RadioGroup(this).apply {
            orientation = RadioGroup.HORIZONTAL
        }
        val rbWifi = RadioButton(this).apply { text = "Local network"; id = View.generateViewId() }
        val rbUsb = RadioButton(this).apply { text = "USB"; id = View.generateViewId() }
        val rbBt = RadioButton(this).apply { text = "Bluetooth"; id = View.generateViewId() }
        radioGroup.addView(rbWifi)
        radioGroup.addView(rbUsb)
        radioGroup.addView(rbBt)
        container.addView(radioGroup)

        // Single compact horizontal row for Host + Port (no vertical scrolling needed on 360dp screen)
        val inputRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(0, (4 * density).toInt(), 0, 0)
        }
        val etHost = EditText(this).apply {
            hint = "IP address"
            setText(prefs.getString("host", "192.168.1.100"))
            inputType = InputType.TYPE_CLASS_TEXT
            maxLines = 1
            textSize = 14f
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        val etPort = EditText(this).apply {
            hint = "Port"
            setText(prefs.getInt("port", MonitorClient.DEFAULT_PORT).toString())
            inputType = InputType.TYPE_CLASS_NUMBER
            maxLines = 1
            textSize = 14f
            layoutParams = LinearLayout.LayoutParams(
                (84 * density).toInt(),
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply {
                marginStart = (8 * density).toInt()
            }
        }
        inputRow.addView(etHost)
        inputRow.addView(etPort)
        container.addView(inputRow)

        val tvHelper = TextView(this).apply {
            setPadding(4, (6 * density).toInt(), 4, (4 * density).toInt())
            textSize = 13f
            setTextColor(ContextCompat.getColor(context, R.color.m2_on_surface_medium))
        }
        container.addView(tvHelper)

        // Inline Bluetooth device list (shrinks tightly to content)
        val btScroll = android.widget.ScrollView(this).apply {
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            )
            visibility = View.GONE
        }
        val btDeviceGroup = RadioGroup(this).apply {
            orientation = RadioGroup.VERTICAL
        }
        btScroll.addView(btDeviceGroup)
        container.addView(btScroll)

        val verName = runCatching {
            packageManager.getPackageInfo(packageName, 0).versionName
        }.getOrDefault("26.1119")
        val tvVersion = TextView(this).apply {
            text = "Deskmon $verName"
            textSize = 11f
            setTextColor(ContextCompat.getColor(context, R.color.m2_on_surface_medium))
            gravity = Gravity.END
            setPadding(0, (6 * density).toInt(), 0, 0)
        }
        container.addView(tvVersion)

        var activeDialog: AlertDialog? = null

        fun populateInlineBluetoothDevices() {
            btDeviceGroup.removeAllViews()
            if (!hasBluetoothPermission()) {
                requestBluetoothPermission()
                tvHelper.visibility = View.VISIBLE
                tvHelper.text = "Grant Bluetooth permission to list paired devices."
                btScroll.visibility = View.GONE
                return
            }
            val adapter = bluetoothAdapter
            if (adapter == null || !adapter.isEnabled) {
                tvHelper.visibility = View.VISIBLE
                tvHelper.text = "Enable Bluetooth on phone to see paired PCs."
                btScroll.visibility = View.GONE
                return
            }
            val paired = adapter.bondedDevices?.toList().orEmpty()
            if (paired.isEmpty()) {
                tvHelper.visibility = View.VISIBLE
                tvHelper.text = "No paired devices found."
                btScroll.visibility = View.GONE
                return
            }
            tvHelper.visibility = View.GONE
            btScroll.visibility = View.VISIBLE
            btScroll.layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                if (paired.size > 2) (96 * density).toInt() else LinearLayout.LayoutParams.WRAP_CONTENT
            )
            val savedAddr = prefs.getString("bt_address", null)
            for (dev in paired) {
                val rb = RadioButton(this).apply {
                    id = View.generateViewId()
                    text = "${dev.name ?: "PC"}   ${dev.address}"
                    textSize = 13f
                    setOnClickListener {
                        prefs.edit()
                            .putString("bt_address", dev.address)
                            .putString("bt_name", dev.name ?: dev.address)
                            .apply()
                    }
                }
                btDeviceGroup.addView(rb)
                if (dev.address == savedAddr || (savedAddr == null && btDeviceGroup.checkedRadioButtonId == -1)) {
                    btDeviceGroup.check(rb.id)
                    prefs.edit()
                        .putString("bt_address", dev.address)
                        .putString("bt_name", dev.name ?: dev.address)
                        .apply()
                }
            }
            applySelectedFontToView(btDeviceGroup)
        }

        fun updateFieldsForMode(mode: ConnectionMode) {
            when (mode) {
                ConnectionMode.NETWORK -> {
                    inputRow.visibility = View.VISIBLE
                    etHost.visibility = View.VISIBLE
                    etPort.visibility = View.VISIBLE
                    etPort.layoutParams = LinearLayout.LayoutParams(
                        (84 * density).toInt(),
                        LinearLayout.LayoutParams.WRAP_CONTENT
                    ).apply {
                        marginStart = (8 * density).toInt()
                    }
                    tvHelper.visibility = View.GONE
                    btScroll.visibility = View.GONE
                }
                ConnectionMode.USB -> {
                    inputRow.visibility = View.VISIBLE
                    etHost.visibility = View.GONE
                    etPort.visibility = View.VISIBLE
                    etPort.layoutParams = LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.MATCH_PARENT,
                        LinearLayout.LayoutParams.WRAP_CONTENT
                    )
                    tvHelper.visibility = View.GONE
                    btScroll.visibility = View.GONE
                }
                ConnectionMode.BLUETOOTH -> {
                    inputRow.visibility = View.GONE
                    populateInlineBluetoothDevices()
                }
            }
            container.requestLayout()
            activeDialog?.window?.setLayout(
                WindowManager.LayoutParams.WRAP_CONTENT,
                WindowManager.LayoutParams.WRAP_CONTENT
            )
        }

        when (currentMode) {
            ConnectionMode.NETWORK -> radioGroup.check(rbWifi.id)
            ConnectionMode.USB -> radioGroup.check(rbUsb.id)
            ConnectionMode.BLUETOOTH -> radioGroup.check(rbBt.id)
        }
        updateFieldsForMode(currentMode)

        radioGroup.setOnCheckedChangeListener { _, checkedId ->
            currentMode = when (checkedId) {
                rbUsb.id -> ConnectionMode.USB
                rbBt.id -> ConnectionMode.BLUETOOTH
                else -> ConnectionMode.NETWORK
            }
            prefs.edit().putString("mode", currentMode.name).apply()
            syncModeToggleUi()
            updateFieldsForMode(currentMode)
        }

        val builder = MaterialAlertDialogBuilder(this)
            .setTitle("Connection")
            .setView(container)
            .setPositiveButton(if (isConnected) "Reconnect" else "Connect") { _, _ ->
                val host = etHost.text.toString().trim().ifEmpty { "192.168.1.100" }
                val port = etPort.text.toString().toIntOrNull() ?: MonitorClient.DEFAULT_PORT
                prefs.edit()
                    .putString("mode", currentMode.name)
                    .putString("host", host)
                    .putInt("port", port)
                    .apply()
                syncModeToggleUi()
                startConnection()
            }

        if (isConnected || client.isAutoReconnecting()) {
            builder.setNegativeButton("Disconnect") { _, _ ->
                client.disconnect()
            }
        } else {
            builder.setNegativeButton("Close", null)
        }

        activeDialog = showStyledDialog(builder)
    }

    @SuppressLint("MissingPermission")
    private fun showBluetoothPickerDialog() {
        if (!hasBluetoothPermission()) {
            requestBluetoothPermission()
            return
        }
        val adapter = bluetoothAdapter
        if (adapter == null || !adapter.isEnabled) {
            Toast.makeText(this, "Please enable Bluetooth first", Toast.LENGTH_SHORT).show()
            return
        }

        val paired = adapter.bondedDevices?.toList().orEmpty()
        if (paired.isEmpty()) {
            Toast.makeText(this, "No paired devices. Pair your PC in Settings first.", Toast.LENGTH_LONG).show()
            return
        }

        val labels = paired.map { "${it.name ?: "Unknown"}\n${it.address}" }.toTypedArray()
        showStyledDialog(
            MaterialAlertDialogBuilder(this)
                .setTitle("Select Paired PC")
                .setItems(labels) { _, which ->
                    val device = paired[which]
                    prefs.edit()
                        .putString("bt_address", device.address)
                        .putString("bt_name", device.name ?: device.address)
                        .apply()
                    Toast.makeText(this, "Selected ${device.name ?: device.address}", Toast.LENGTH_SHORT).show()
                }
                .setNegativeButton("Cancel", null)
        )
    }

    private fun hasBluetoothPermission(): Boolean {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_CONNECT) ==
                PackageManager.PERMISSION_GRANTED
        } else {
            true
        }
    }

    private fun requestBluetoothPermission() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            ActivityCompat.requestPermissions(
                this,
                arrayOf(Manifest.permission.BLUETOOTH_CONNECT),
                1001
            )
        }
    }
}
