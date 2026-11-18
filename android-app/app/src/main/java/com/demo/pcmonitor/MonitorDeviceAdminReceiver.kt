package com.demo.pcmonitor

import android.app.admin.DeviceAdminReceiver
import android.content.Context
import android.content.Intent

/** Receives device admin lifecycle callbacks to permit one-tap screen locking via DevicePolicyManager. */
class MonitorDeviceAdminReceiver : DeviceAdminReceiver() {
    override fun onEnabled(context: Context, intent: Intent) {
        super.onEnabled(context, intent)
    }

    override fun onDisabled(context: Context, intent: Intent) {
        super.onDisabled(context, intent)
    }
}
