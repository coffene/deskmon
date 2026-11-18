package com.demo.pcmonitor

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import org.json.JSONObject
import java.io.BufferedReader
import java.io.Closeable
import java.io.InputStream
import java.io.InputStreamReader
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.util.UUID
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

enum class ConnectionMode {
    NETWORK,
    USB,
    BLUETOOTH
}

class MonitorClient(
    private val onStateChanged: (connected: Boolean, statusText: String) -> Unit,
    private val onSnapshot: (ResourceSnapshot) -> Unit,
    private val onTargetDetected: (mode: ConnectionMode, host: String, port: Int) -> Unit = { _, _, _ -> }
) {
    companion object {
        const val DEFAULT_PORT = 8765
        private const val DISCOVERY_PORT = 8766
        private const val DISCOVERY_MAGIC = "PC_MONITOR_DISCOVER"
        private const val USB_LOOPBACK_HOST = "127.0.0.1"
        private const val BT_FALLBACK_CHANNEL = 4
        private const val RECONNECT_DELAY_SECONDS = 3L
        private val SPP_UUID: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")
    }

    private val readerExecutor: ExecutorService = Executors.newSingleThreadExecutor()
    private val writerExecutor: ExecutorService = Executors.newSingleThreadExecutor()
    private val scheduler: ScheduledExecutorService = Executors.newSingleThreadScheduledExecutor()

    @Volatile
    private var running = false

    @Volatile
    private var shouldReconnect = false

    @Volatile
    private var connectionGeneration = 0L

    @Volatile
    private var activeResource: Closeable? = null

    @Volatile
    private var activeOut: OutputStream? = null

    @Volatile
    private var reconnectFuture: ScheduledFuture<*>? = null

    fun autoDetectAndConnect(
        preferredMode: ConnectionMode,
        savedHost: String,
        savedPort: Int,
        bluetoothAddress: String?,
        bluetoothAdapter: BluetoothAdapter?
    ) {
        val (gen, oldRes) = resetSessionState()
        running = true
        shouldReconnect = true

        readerExecutor.execute {
            if (oldRes != null) {
                closeQuietly(oldRes)
                try { Thread.sleep(150) } catch (_: InterruptedException) {}
            }
            attemptAutoDetectAndConnect(
                gen,
                preferredMode,
                savedHost,
                savedPort,
                bluetoothAddress,
                bluetoothAdapter
            )
        }
    }

    fun connect(
        mode: ConnectionMode,
        host: String,
        port: Int,
        bluetoothAddress: String?,
        bluetoothAdapter: BluetoothAdapter?
    ) {
        val (gen, oldRes) = resetSessionState()
        running = true
        shouldReconnect = true

        readerExecutor.execute {
            if (oldRes != null) {
                closeQuietly(oldRes)
                try { Thread.sleep(150) } catch (_: InterruptedException) {}
            }
            attemptConnect(gen, mode, host, port, bluetoothAddress, bluetoothAdapter)
        }
    }

    private fun resetSessionState(): Pair<Long, Closeable?> {
        val gen = ++connectionGeneration
        reconnectFuture?.cancel(false)
        reconnectFuture = null
        activeOut = null
        val toClose = activeResource
        activeResource = null
        return gen to toClose
    }

    private fun attemptAutoDetectAndConnect(
        gen: Long,
        preferredMode: ConnectionMode,
        savedHost: String,
        savedPort: Int,
        bluetoothAddress: String?,
        bluetoothAdapter: BluetoothAdapter?
    ) {
        if (!running || gen != connectionGeneration) return

        onStateChanged(false, "Detecting server")

        // 1. Check USB loopback (adb reverse): must receive actual server handshake/snapshot,
        //    not just adbd's local TCP accept when PC server is stopped
        if (isMonitorServerReachable(USB_LOOPBACK_HOST, savedPort, 650)) {
            onTargetDetected(ConnectionMode.USB, savedHost, savedPort)
            attemptConnect(gen, ConnectionMode.USB, savedHost, savedPort, bluetoothAddress, bluetoothAdapter)
            return
        }

        // 2. Broadcast UDP discovery on local network
        val discovered = discoverServerOnLan(750)
        if (discovered != null) {
            val (lanIp, lanPort) = discovered
            onTargetDetected(ConnectionMode.NETWORK, lanIp, lanPort)
            attemptConnect(gen, ConnectionMode.NETWORK, lanIp, lanPort, bluetoothAddress, bluetoothAdapter)
            return
        }

        // 3. Fallback to saved LAN IP if reachable
        if (savedHost.isNotBlank() && isMonitorServerReachable(savedHost, savedPort, 650)) {
            onTargetDetected(ConnectionMode.NETWORK, savedHost, savedPort)
            attemptConnect(gen, ConnectionMode.NETWORK, savedHost, savedPort, bluetoothAddress, bluetoothAdapter)
            return
        }

        // 4. Fallback to Bluetooth if a paired PC was previously configured
        if (!bluetoothAddress.isNullOrBlank() && bluetoothAdapter?.isEnabled == true && preferredMode == ConnectionMode.BLUETOOTH) {
            onTargetDetected(ConnectionMode.BLUETOOTH, savedHost, savedPort)
            attemptConnect(gen, ConnectionMode.BLUETOOTH, savedHost, savedPort, bluetoothAddress, bluetoothAdapter)
            return
        }

        if (running && shouldReconnect && gen == connectionGeneration) {
            scheduleReconnectCountdown(gen, "Server not found") {
                attemptAutoDetectAndConnect(
                    gen,
                    preferredMode,
                    savedHost,
                    savedPort,
                    bluetoothAddress,
                    bluetoothAdapter
                )
            }
        }
    }

    private fun attemptConnect(
        gen: Long,
        mode: ConnectionMode,
        host: String,
        port: Int,
        bluetoothAddress: String?,
        bluetoothAdapter: BluetoothAdapter?
    ) {
        if (!running || gen != connectionGeneration) return

        val connectingPhrase = when (mode) {
            ConnectionMode.USB -> "Connecting through USB…"
            ConnectionMode.NETWORK -> "Connecting over local network…"
            ConnectionMode.BLUETOOTH -> "Connecting over Bluetooth…"
        }
        onStateChanged(false, connectingPhrase)

        var disconnectReason = "Disconnected"
        try {
            val (stream, out) = when (mode) {
                ConnectionMode.NETWORK -> openTcpStreams(host, port)
                ConnectionMode.USB -> openTcpStreams(USB_LOOPBACK_HOST, port)
                ConnectionMode.BLUETOOTH -> openBluetoothStreams(bluetoothAddress, bluetoothAdapter)
            }

            if (!running || gen != connectionGeneration) {
                closeQuietly(activeResource)
                return
            }

            activeOut = out
            val connectedPhrase = when (mode) {
                ConnectionMode.USB -> "Connected through USB"
                ConnectionMode.NETWORK -> "Connected over local network"
                ConnectionMode.BLUETOOTH -> "Connected over Bluetooth"
            }
            // Wait for the first real server JSON frame before marking connected,
            // so adbd's empty TCP accept (when PC server is off) never triggers a false connected state.
            val gotFirstFrame = readLoop(stream, gen) {
                onStateChanged(true, connectedPhrase)
            }
            disconnectReason = if (gotFirstFrame) {
                "Connection closed by server"
            } else {
                "Server not running"
            }
        } catch (e: Exception) {
            disconnectReason = e.message?.takeIf { it.isNotBlank() } ?: "Connection failed"
        } finally {
            activeOut = null
            closeQuietly(activeResource)
            activeResource = null
        }

        if (running && shouldReconnect && gen == connectionGeneration) {
            scheduleReconnectCountdown(gen, disconnectReason) {
                attemptConnect(gen, mode, host, port, bluetoothAddress, bluetoothAdapter)
            }
        }
    }

    private fun scheduleReconnectCountdown(gen: Long, reason: String, onReady: () -> Unit) {
        val remaining = AtomicInteger(RECONNECT_DELAY_SECONDS.toInt())
        reconnectFuture?.cancel(false)
        reconnectFuture = scheduler.scheduleAtFixedRate(
            {
                if (!running || !shouldReconnect || gen != connectionGeneration) {
                    reconnectFuture?.cancel(false)
                    return@scheduleAtFixedRate
                }
                val sec = remaining.getAndDecrement()
                if (sec > 0) {
                    onStateChanged(false, "$reason, reconnecting in ${sec}s")
                } else {
                    reconnectFuture?.cancel(false)
                    readerExecutor.execute {
                        if (running && shouldReconnect && gen == connectionGeneration) {
                            onReady()
                        }
                    }
                }
            },
            0L,
            1L,
            TimeUnit.SECONDS
        )
    }

    private fun isMonitorServerReachable(host: String, port: Int, timeoutMs: Int): Boolean {
        return try {
            Socket().use { probe ->
                probe.connect(InetSocketAddress(host, port), timeoutMs)
                probe.soTimeout = timeoutMs
                val reader = BufferedReader(InputStreamReader(probe.getInputStream(), Charsets.UTF_8))
                val line = reader.readLine()
                !line.isNullOrBlank() && line.trimStart().startsWith("{")
            }
        } catch (_: Exception) {
            false
        }
    }

    private fun discoverServerOnLan(timeoutMs: Int): Pair<String, Int>? {
        return try {
            DatagramSocket().use { udp ->
                udp.broadcast = true
                udp.soTimeout = timeoutMs
                val reqBytes = DISCOVERY_MAGIC.toByteArray(Charsets.UTF_8)
                val packet = DatagramPacket(
                    reqBytes,
                    reqBytes.size,
                    InetAddress.getByName("255.255.255.255"),
                    DISCOVERY_PORT
                )
                udp.send(packet)

                val buf = ByteArray(512)
                val resp = DatagramPacket(buf, buf.size)
                udp.receive(resp)
                val ip = resp.address?.hostAddress ?: return null
                val text = String(resp.data, 0, resp.length, Charsets.UTF_8)
                val port = runCatching { JSONObject(text).optInt("port", DEFAULT_PORT) }
                    .getOrDefault(DEFAULT_PORT)
                ip to port
            }
        } catch (_: Exception) {
            null
        }
    }

    fun sendCommand(cmd: String) {
        sendJson(JSONObject().put("cmd", cmd))
    }

    fun sendDismissNotification(notifId: Int) {
        sendJson(JSONObject().put("cmd", "dismiss_notif").put("id", notifId))
    }

    fun sendClearNotifications() {
        sendJson(JSONObject().put("cmd", "clear_notifs"))
    }

    fun sendClientConfig(
        layout: Int,
        font: Int,
        showPlaying: Boolean,
        playingLeft: Boolean,
        timeMode: Int,
        l3ShowTask: Boolean,
        l3ShowTimer: Boolean,
        l3ShowRings: Boolean,
        l3ShowNotif: Boolean,
        l3SplitPct: Int,
        l3RightSplitPct: Int,
        l3TextScale: Int,
        l3MetricScale: Int,
        showTopBar: Boolean,
        showLogo: Boolean
    ) {
        val obj = JSONObject()
            .put("cmd", "client_cfg")
            .put("layout", layout)
            .put("font", font)
            .put("show_playing", showPlaying)
            .put("playing_left", playingLeft)
            .put("time_mode", timeMode)
            .put("l3_show_task", l3ShowTask)
            .put("l3_show_timer", l3ShowTimer)
            .put("l3_show_rings", l3ShowRings)
            .put("l3_show_notif", l3ShowNotif)
            .put("l3_split_pct", l3SplitPct)
            .put("l3_right_split_pct", l3RightSplitPct)
            .put("l3_text_scale", l3TextScale)
            .put("l3_metric_scale", l3MetricScale)
            .put("show_top_bar", showTopBar)
            .put("show_logo", showLogo)
        sendJson(obj)
    }

    private fun sendJson(obj: JSONObject) {
        val out = activeOut ?: return
        if (writerExecutor.isShutdown) return
        val payload = (obj.toString() + "\n").toByteArray(Charsets.UTF_8)
        writerExecutor.execute {
            try {
                synchronized(out) {
                    out.write(payload)
                    out.flush()
                }
            } catch (_: Exception) {
            }
        }
    }

    fun isAutoReconnecting(): Boolean = shouldReconnect

    fun disconnect() {
        connectionGeneration++
        running = false
        shouldReconnect = false
        reconnectFuture?.cancel(false)
        reconnectFuture = null
        activeOut = null
        val toClose = activeResource
        activeResource = null
        if (toClose != null && !writerExecutor.isShutdown) {
            writerExecutor.execute { closeQuietly(toClose) }
        } else if (toClose != null) {
            closeQuietly(toClose)
        }
        onStateChanged(false, "Disconnected")
    }

    fun shutdown() {
        disconnect()
        scheduler.shutdownNow()
        readerExecutor.shutdownNow()
        writerExecutor.shutdownNow()
    }

    private fun openTcpStreams(host: String, port: Int): Pair<InputStream, OutputStream> {
        val socket = Socket()
        activeResource = socket
        socket.connect(InetSocketAddress(host, port), 5000)
        socket.soTimeout = 5000
        return socket.getInputStream() to socket.getOutputStream()
    }

    @SuppressLint("MissingPermission")
    private fun openBluetoothStreams(
        address: String?,
        adapter: BluetoothAdapter?
    ): Pair<InputStream, OutputStream> {
        if (adapter == null || !adapter.isEnabled) {
            throw IllegalStateException("Bluetooth is disabled")
        }
        if (address.isNullOrBlank()) {
            throw IllegalStateException("No paired PC selected")
        }

        adapter.cancelDiscovery()
        val device: BluetoothDevice = adapter.getRemoteDevice(address)

        val socket: BluetoothSocket = try {
            val sdpSocket = device.createRfcommSocketToServiceRecord(SPP_UUID)
            activeResource = sdpSocket
            sdpSocket.connect()
            sdpSocket
        } catch (_: Exception) {
            closeQuietly(activeResource)
            val createMethod = device.javaClass.getMethod("createRfcommSocket", Int::class.javaPrimitiveType)
            val fallbackSocket = createMethod.invoke(device, BT_FALLBACK_CHANNEL) as BluetoothSocket
            activeResource = fallbackSocket
            fallbackSocket.connect()
            fallbackSocket
        }

        return socket.inputStream to socket.outputStream
    }

    private fun readLoop(stream: InputStream, gen: Long, onFirstSnapshot: () -> Unit): Boolean {
        var confirmed = false
        BufferedReader(InputStreamReader(stream, Charsets.UTF_8)).use { reader ->
            while (running && gen == connectionGeneration) {
                val line = reader.readLine() ?: break
                if (line.isNotBlank()) {
                    try {
                        val snapshot = ResourceSnapshot.fromJson(line)
                        if (!confirmed) {
                            confirmed = true
                            onFirstSnapshot()
                        }
                        onSnapshot(snapshot)
                    } catch (_: Exception) {
                    }
                }
            }
        }
        return confirmed
    }

    private fun closeQuietly(closeable: Closeable?) {
        try {
            closeable?.close()
        } catch (_: Exception) {
        }
    }
}
