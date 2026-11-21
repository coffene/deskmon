package com.demo.pcmonitor

import org.json.JSONObject

data class OsNotification(
    val id: Int,
    val app: String,
    val title: String,
    val body: String,
    val time: String
)

data class ResourceSnapshot(
    val cpuPercent: Float,
    val ramPercent: Float,
    val ramUsedGb: Float,
    val ramTotalGb: Float,
    val diskPercent: Float,
    val diskUsedGb: Float,
    val diskTotalGb: Float,
    val currentTask: String,
    val currentDue: String,
    val currentNote: String,
    val hostname: String,
    val mediaTitle: String,
    val mediaArtist: String,
    val mediaStatus: String,
    val mediaPosSec: Int,
    val mediaDurSec: Int,
    val thumbSeq: Int,
    val mediaThumbBase64: String,
    val cfgSeq: Int,
    val cfgLayout: Int,
    val cfgFont: Int,
    val cfgShowPlaying: Boolean,
    val cfgPlayingLeft: Boolean,
    val cfgTimeMode: Int,
    val cfgL3ShowTask: Boolean,
    val cfgL3ShowTimer: Boolean,
    val cfgL3ShowRings: Boolean,
    val cfgL3ShowNotif: Boolean,
    val cfgL3SplitPct: Int,
    val cfgL3RightSplitPct: Int,
    val cfgL3TextScale: Int,
    val cfgL3MetricScale: Int,
    val cfgShowTopBar: Boolean,
    val cfgShowLogo: Boolean,
    val cfgFullscreen: Boolean,
    val cfgBgEnabled: Boolean,
    val cfgBgMode: Int,
    val cfgBgOpacity: Int,
    val cfgBgBlur: Int,
    val timerSeq: Int,
    val timerCmd: String,
    val timerVal: Int,
    val powerSeq: Int,
    val powerCmd: String,
    val notifSeq: Int,
    val notifAccess: Boolean,
    val notifSync: Boolean,
    val notifications: List<OsNotification>,
    val pushImgSeq: Int,
    val pushImgBase64: String,
    val bgSeq: Int,
    val bgBase64: String
) {
    companion object {
        fun fromJson(line: String): ResourceSnapshot {
            val obj = JSONObject(line)
            val notifList = ArrayList<OsNotification>()
            val notifSync = obj.optBoolean("notif_sync", false)
            if (notifSync) {
                val arr = obj.optJSONArray("notifications")
                if (arr != null) {
                    for (i in 0 until arr.length()) {
                        val item = arr.optJSONObject(i) ?: continue
                        notifList.add(
                            OsNotification(
                                id = item.optInt("id", 0),
                                app = item.optString("app", "Windows"),
                                title = item.optString("title", ""),
                                body = item.optString("body", ""),
                                time = item.optString("time", "")
                            )
                        )
                    }
                }
            }

            return ResourceSnapshot(
                cpuPercent = obj.optDouble("cpu", 0.0).toFloat(),
                ramPercent = obj.optDouble("ram_percent", 0.0).toFloat(),
                ramUsedGb = obj.optDouble("ram_used_gb", 0.0).toFloat(),
                ramTotalGb = obj.optDouble("ram_total_gb", 0.0).toFloat(),
                diskPercent = obj.optDouble("disk_percent", 0.0).toFloat(),
                diskUsedGb = obj.optDouble("disk_used_gb", 0.0).toFloat(),
                diskTotalGb = obj.optDouble("disk_total_gb", 0.0).toFloat(),
                currentTask = obj.optString("task", ""),
                currentDue = obj.optString("due", ""),
                currentNote = obj.optString("note", ""),
                hostname = obj.optString("hostname", "PC"),
                mediaTitle = obj.optString("media_title", "Not playing"),
                mediaArtist = obj.optString("media_artist", ""),
                mediaStatus = obj.optString("media_status", "Not playing"),
                mediaPosSec = obj.optInt("media_pos", 0),
                mediaDurSec = obj.optInt("media_dur", 0),
                thumbSeq = obj.optInt("thumb_seq", 0),
                mediaThumbBase64 = obj.optString("media_thumb", ""),
                cfgSeq = obj.optInt("cfg_seq", 0),
                cfgLayout = obj.optInt("cfg_layout", -1),
                cfgFont = obj.optInt("cfg_font", -1),
                cfgShowPlaying = obj.optBoolean("cfg_show_playing", true),
                cfgPlayingLeft = obj.optBoolean("cfg_playing_left", false),
                cfgTimeMode = obj.optInt("cfg_time_mode", 0),
                cfgL3ShowTask = obj.optBoolean("cfg_l3_show_task", true),
                cfgL3ShowTimer = obj.optBoolean("cfg_l3_show_timer", true),
                cfgL3ShowRings = obj.optBoolean("cfg_l3_show_rings", true),
                cfgL3ShowNotif = obj.optBoolean("cfg_l3_show_notif", true),
                cfgL3SplitPct = obj.optInt("cfg_l3_split_pct", 54),
                cfgL3RightSplitPct = obj.optInt("cfg_l3_right_split_pct", 55),
                cfgL3TextScale = obj.optInt("cfg_l3_text_scale", 100),
                cfgL3MetricScale = obj.optInt("cfg_l3_metric_scale", 100),
                cfgShowTopBar = obj.optBoolean("cfg_show_top_bar", true),
                cfgShowLogo = obj.optBoolean("cfg_show_logo", true),
                cfgFullscreen = obj.optBoolean("cfg_fullscreen", false),
                cfgBgEnabled = obj.optBoolean("cfg_bg_enabled", true),
                cfgBgMode = obj.optInt("cfg_bg_mode", 0),
                cfgBgOpacity = obj.optInt("cfg_bg_opacity", 32),
                cfgBgBlur = obj.optInt("cfg_bg_blur", 6),
                timerSeq = obj.optInt("timer_seq", 0),
                timerCmd = obj.optString("timer_cmd", ""),
                timerVal = obj.optInt("timer_val", 25),
                powerSeq = obj.optInt("power_seq", 0),
                powerCmd = obj.optString("power_cmd", ""),
                notifSeq = obj.optInt("notif_seq", 0),
                notifAccess = obj.optBoolean("notif_access", true),
                notifSync = notifSync,
                notifications = notifList,
                pushImgSeq = obj.optInt("push_img_seq", 0),
                pushImgBase64 = obj.optString("push_img_b64", ""),
                bgSeq = obj.optInt("bg_seq", 0),
                bgBase64 = obj.optString("bg_b64", "")
            )
        }
    }
}
