import argparse
import asyncio
import base64
import ctypes
import io
import json
import os
import socket
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone

import psutil

try:
    import winsdk.windows.media.control as wmc
    import winsdk.windows.storage.streams as wss
    from PIL import Image

    HAS_WINSDK = True
except ImportError:
    HAS_WINSDK = False

try:
    import winsdk.windows.ui.notifications as wun
    import winsdk.windows.ui.notifications.management as wunm

    HAS_WIN_NOTIF = True
except ImportError:
    HAS_WIN_NOTIF = False

TCP_PORT = 8765
DISCOVERY_PORT = 8766
DISCOVERY_MAGIC = b"PC_MONITOR_DISCOVER"
CONFIG_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "server_config.json")

VK_MEDIA_NEXT_TRACK = 0xB0
VK_MEDIA_PREV_TRACK = 0xB1
VK_MEDIA_STOP = 0xB2
VK_MEDIA_PLAY_PAUSE = 0xB3
KEYEVENTF_KEYUP = 0x0002


def send_media_key(vk_code: int):
    try:
        user32 = ctypes.windll.user32
        user32.keybd_event(vk_code, 0, 0, 0)
        user32.keybd_event(vk_code, 0, KEYEVENTF_KEYUP, 0)
    except Exception:
        pass


def get_local_ip() -> str:
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect(("8.8.8.8", 80))
            return s.getsockname()[0]
    except OSError:
        return "127.0.0.1"


def get_primary_disk_path() -> str:
    return os.environ.get("SystemDrive", "C:") + "\\"


class MediaBridge:
    def __init__(self):
        self._cached_key = None
        self._cached_thumb_b64 = ""
        self._thumb_seq = 0
        self._loop = asyncio.new_event_loop()
        threading.Thread(target=self._run_loop, daemon=True).start()

    def _run_loop(self):
        asyncio.set_event_loop(self._loop)
        self._loop.run_forever()

    def get_media_state(self) -> dict:
        if not HAS_WINSDK:
            return self._empty_state()
        try:
            fut = asyncio.run_coroutine_threadsafe(self._fetch_media_async(), self._loop)
            return fut.result(timeout=0.8)
        except Exception:
            return self._empty_state()

    def execute_command(self, cmd: str):
        if HAS_WINSDK:
            try:
                fut = asyncio.run_coroutine_threadsafe(self._exec_cmd_async(cmd), self._loop)
                if fut.result(timeout=0.8):
                    return
            except Exception:
                pass

        if cmd == "play_pause":
            send_media_key(VK_MEDIA_PLAY_PAUSE)
        elif cmd == "stop":
            send_media_key(VK_MEDIA_STOP)
        elif cmd == "next":
            send_media_key(VK_MEDIA_NEXT_TRACK)
        elif cmd == "prev":
            send_media_key(VK_MEDIA_PREV_TRACK)

    async def _exec_cmd_async(self, cmd: str) -> bool:
        mgr = await wmc.GlobalSystemMediaTransportControlsSessionManager.request_async()
        session = mgr.get_current_session()
        if not session:
            return False
        if cmd == "play_pause":
            return bool(await session.try_toggle_play_pause_async())
        if cmd == "stop":
            ok = await session.try_stop_async()
            if not ok:
                ok = await session.try_pause_async()
            return bool(ok)
        if cmd == "next":
            return bool(await session.try_skip_next_async())
        if cmd == "prev":
            return bool(await session.try_skip_previous_async())
        return False

    async def _fetch_media_async(self) -> dict:
        mgr = await wmc.GlobalSystemMediaTransportControlsSessionManager.request_async()
        session = mgr.get_current_session()
        if not session:
            if self._cached_key is not None:
                self._cached_key = None
                self._cached_thumb_b64 = ""
                self._thumb_seq += 1
            return self._empty_state()

        info = session.get_playback_info()
        status_enum = int(info.playback_status) if info else 0
        if status_enum not in (4, 5):
            if self._cached_key is not None:
                self._cached_key = None
                self._cached_thumb_b64 = ""
                self._thumb_seq += 1
            return self._empty_state()

        props = await session.try_get_media_properties_async()
        title = (props.title or "").strip() if props else ""
        if not title:
            if self._cached_key is not None:
                self._cached_key = None
                self._cached_thumb_b64 = ""
                self._thumb_seq += 1
            return self._empty_state()

        artist = (props.artist or props.album_artist or "").strip() if props else ""
        if not artist and session.source_app_user_model_id:
            artist = session.source_app_user_model_id.split("!")[0].replace(".exe", "")

        status_str = "Playing" if status_enum == 4 else "Paused"

        timeline = session.get_timeline_properties()
        pos_sec = max(0, int(timeline.position.total_seconds())) if timeline else 0
        dur_sec = max(0, int(timeline.end_time.total_seconds())) if timeline else 0

        if status_enum == 4 and timeline and timeline.last_updated_time:
            try:
                elapsed = (datetime.now(timezone.utc) - timeline.last_updated_time).total_seconds()
                if 0 < elapsed < 3600:
                    pos_sec = min(dur_sec if dur_sec > 0 else pos_sec + int(elapsed), pos_sec + int(elapsed))
            except Exception:
                pass

        key = (artist, title)
        if key != self._cached_key or not self._cached_thumb_b64:
            new_thumb = await self._read_thumbnail_b64(props.thumbnail if props else None)
            if key != self._cached_key or new_thumb != self._cached_thumb_b64:
                self._cached_key = key
                self._cached_thumb_b64 = new_thumb
                self._thumb_seq += 1

        return {
            "media_title": title,
            "media_artist": artist,
            "media_status": status_str,
            "media_pos": pos_sec,
            "media_dur": dur_sec,
            "thumb_seq": self._thumb_seq,
            "media_thumb": self._cached_thumb_b64,
        }

    async def _read_thumbnail_b64(self, thumb_ref) -> str:
        if not thumb_ref:
            return ""
        try:
            stream = await thumb_ref.open_read_async()
            size = int(stream.size)
            if size <= 0 or size > 8_000_000:
                return ""
            reader = wss.DataReader(stream)
            await reader.load_async(size)
            buf = bytearray(size)
            reader.read_bytes(buf)
            reader.close()

            img = Image.open(io.BytesIO(buf)).convert("RGB")
            if img.width > 512 or img.height > 512:
                img.thumbnail((512, 512), Image.Resampling.LANCZOS)
            out = io.BytesIO()
            img.save(out, format="JPEG", quality=92, subsampling=0)
            return base64.b64encode(out.getvalue()).decode("ascii")
        except Exception:
            return ""

    def _empty_state(self) -> dict:
        return {
            "media_title": "Not playing",
            "media_artist": "",
            "media_status": "Not playing",
            "media_pos": 0,
            "media_dur": 0,
            "thumb_seq": self._thumb_seq,
            "media_thumb": "",
        }


class NotificationBridge:
    def __init__(self, loop: asyncio.AbstractEventLoop):
        self._loop = loop
        self._cached_key = None
        self._cached_items = []
        self._access_ok = HAS_WIN_NOTIF
        self._notif_seq = 1
        self._access_requested = False

    def get_notifications_state(self) -> dict:
        if not HAS_WIN_NOTIF:
            return {
                "notif_seq": self._notif_seq,
                "notif_access": False,
                "notifications": [],
            }
        try:
            fut = asyncio.run_coroutine_threadsafe(self._fetch_async(), self._loop)
            return fut.result(timeout=0.8)
        except Exception:
            return {
                "notif_seq": self._notif_seq,
                "notif_access": self._access_ok,
                "notifications": list(self._cached_items),
            }

    def dismiss(self, notif_id: int):
        if not HAS_WIN_NOTIF:
            return
        try:
            fut = asyncio.run_coroutine_threadsafe(self._dismiss_async(notif_id), self._loop)
            fut.result(timeout=0.8)
        except Exception:
            pass

    def clear_all(self):
        if not HAS_WIN_NOTIF:
            return
        try:
            fut = asyncio.run_coroutine_threadsafe(self._clear_async(), self._loop)
            fut.result(timeout=0.8)
        except Exception:
            pass

    async def _get_allowed_listener(self):
        listener = wunm.UserNotificationListener.current
        if not self._access_requested:
            self._access_requested = True
            status = await listener.request_access_async()
        else:
            status = listener.get_access_status()
        allowed = (status == wunm.UserNotificationListenerAccessStatus.ALLOWED)
        self._access_ok = allowed
        return listener if allowed else None

    async def _dismiss_async(self, notif_id: int):
        listener = await self._get_allowed_listener()
        if listener is not None:
            listener.remove_notification(int(notif_id))
            await self._fetch_async()

    async def _clear_async(self):
        listener = await self._get_allowed_listener()
        if listener is not None:
            listener.clear_notifications()
            await self._fetch_async()

    async def _fetch_async(self) -> dict:
        listener = await self._get_allowed_listener()
        if listener is None:
            key = (False, ())
            if key != self._cached_key:
                self._cached_key = key
                self._cached_items = []
                self._notif_seq += 1
            return {
                "notif_seq": self._notif_seq,
                "notif_access": False,
                "notifications": [],
            }

        raw_list = await listener.get_notifications_async(wun.NotificationKinds.TOAST)
        parsed = []
        for n in list(raw_list):
            try:
                app_name = ""
                if n.app_info and n.app_info.display_info:
                    app_name = (n.app_info.display_info.display_name or "").strip()
                if not app_name:
                    app_name = "Windows"

                texts = []
                visual = n.notification.visual if n.notification else None
                binding = (
                    visual.get_binding(wun.KnownNotificationBindings.toast_generic)
                    if visual
                    else None
                )
                if binding:
                    for el in binding.get_text_elements():
                        t = (el.text or "").strip()
                        if t:
                            texts.append(t)

                title = texts[0] if len(texts) >= 1 else app_name
                body = "\n".join(texts[1:]) if len(texts) >= 2 else ""

                dt = n.creation_time
                ts_val = dt.timestamp() if dt else 0.0
                time_str = dt.astimezone().strftime("%H:%M") if dt else ""

                parsed.append((
                    ts_val,
                    int(n.id),
                    {
                        "id": int(n.id),
                        "app": app_name[:40],
                        "title": title[:140],
                        "body": body[:280],
                        "time": time_str,
                    },
                ))
            except Exception:
                continue

        parsed.sort(key=lambda row: (row[0], row[1]), reverse=True)
        latest = [row[2] for row in parsed[:10]]
        sig = tuple((it["id"], it["app"], it["title"], it["body"], it["time"]) for it in latest)
        key = (True, sig)
        if key != self._cached_key:
            self._cached_key = key
            self._cached_items = latest
            self._notif_seq += 1

        return {
            "notif_seq": self._notif_seq,
            "notif_access": True,
            "notifications": list(self._cached_items),
        }


class ResourceServer:
    def __init__(self, on_status_change):
        self.on_status_change = on_status_change
        # Tuple: (sock, channel_name, last_sent_thumb_seq)
        self.active_client = None
        self.clients_lock = threading.Lock()
        self.running = False
        self.current_task = ""
        self.current_due = ""
        self.current_note = ""
        self.cfg_seq = 1
        self.cfg_layout = 0
        self.cfg_font = 0
        self.cfg_show_playing = True
        self.cfg_playing_left = False
        self.cfg_time_mode = 0
        self.cfg_l3_show_task = True
        self.cfg_l3_show_timer = True
        self.cfg_l3_show_rings = True
        self.cfg_l3_show_notif = True
        self.cfg_l3_split_pct = 54
        self.cfg_l3_right_split_pct = 55
        self.cfg_l3_text_scale = 100
        self.cfg_l3_metric_scale = 100
        self.timer_seq = 0
        self.timer_cmd = ""
        self.timer_val = 25
        self.power_seq = 0
        self.power_cmd = ""
        self.power_expires_at = 0.0
        self.task_lock = threading.Lock()
        self.bt_status = "Bluetooth: starting"
        self.usb_ready = False
        self.enabled = True
        self._tcp_sock = None
        self._udp_sock = None
        self._bt_sock = None
        self.media = MediaBridge()
        self.notif = NotificationBridge(self.media._loop)
        self._load_config()

    @staticmethod
    def _clamp(val: int, lo: int, hi: int) -> int:
        return max(lo, min(hi, int(val)))

    def _apply_cfg_dict(self, data: dict):
        if "layout" in data:
            self.cfg_layout = self._clamp(data["layout"], 0, 2)
        if "font" in data:
            self.cfg_font = self._clamp(data["font"], 0, 3)
        if "show_playing" in data:
            self.cfg_show_playing = bool(data["show_playing"])
        if "playing_left" in data:
            self.cfg_playing_left = bool(data["playing_left"])
        if "time_mode" in data:
            self.cfg_time_mode = self._clamp(data["time_mode"], 0, 3)
        if "l3_show_task" in data:
            self.cfg_l3_show_task = bool(data["l3_show_task"])
        if "l3_show_timer" in data:
            self.cfg_l3_show_timer = bool(data["l3_show_timer"])
        if "l3_show_rings" in data:
            self.cfg_l3_show_rings = bool(data["l3_show_rings"])
        if "l3_show_notif" in data:
            self.cfg_l3_show_notif = bool(data["l3_show_notif"])
        if "l3_split_pct" in data:
            self.cfg_l3_split_pct = self._clamp(data["l3_split_pct"], 37, 66)
        if "l3_right_split_pct" in data:
            self.cfg_l3_right_split_pct = self._clamp(data["l3_right_split_pct"], 38, 65)
        if "l3_text_scale" in data:
            self.cfg_l3_text_scale = self._clamp(data["l3_text_scale"], 85, 130)
        if "l3_metric_scale" in data:
            self.cfg_l3_metric_scale = self._clamp(data["l3_metric_scale"], 80, 125)

    def _load_config(self):
        if not os.path.exists(CONFIG_FILE):
            return
        try:
            with open(CONFIG_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)
            with self.task_lock:
                self._apply_cfg_dict(data)
                self.current_task = str(data.get("task", ""))
                self.current_due = str(data.get("due", ""))
                self.current_note = str(data.get("note", ""))
        except Exception:
            pass

    def _save_config(self):
        try:
            with self.task_lock:
                data = {
                    "layout": self.cfg_layout,
                    "font": self.cfg_font,
                    "show_playing": self.cfg_show_playing,
                    "playing_left": self.cfg_playing_left,
                    "time_mode": self.cfg_time_mode,
                    "l3_show_task": self.cfg_l3_show_task,
                    "l3_show_timer": self.cfg_l3_show_timer,
                    "l3_show_rings": self.cfg_l3_show_rings,
                    "l3_show_notif": self.cfg_l3_show_notif,
                    "l3_split_pct": self.cfg_l3_split_pct,
                    "l3_right_split_pct": self.cfg_l3_right_split_pct,
                    "l3_text_scale": self.cfg_l3_text_scale,
                    "l3_metric_scale": self.cfg_l3_metric_scale,
                    "task": self.current_task,
                    "due": self.current_due,
                    "note": self.current_note,
                }
            with open(CONFIG_FILE, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=2)
        except Exception:
            pass

    def has_active_client(self) -> bool:
        with self.clients_lock:
            return self.active_client is not None

    def get_active_channel(self) -> str:
        with self.clients_lock:
            return self.active_client[1] if self.active_client else ""

    def set_task(self, text: str, note: str = "", due: str = ""):
        with self.task_lock:
            self.current_task = text.strip()
            self.current_due = due.strip()
            self.current_note = note.replace("\r\n", "\n").replace("\r", "\n").strip("\n")
        self._save_config()
        self.push_now()

    def update_remote_config(self, msg: dict):
        with self.task_lock:
            self.cfg_seq += 1
            self._apply_cfg_dict(msg)
        self._save_config()
        self.push_now()

    def apply_client_config(self, msg: dict):
        with self.task_lock:
            self.cfg_seq += 1
            self._apply_cfg_dict(msg)
        self._save_config()
        self._notify_status()

    def trigger_remote_timer(self, timer_cmd: str, timer_val: int = 25):
        with self.task_lock:
            self.timer_seq += 1
            self.timer_cmd = timer_cmd
            self.timer_val = int(timer_val)
        self.push_now()

    def trigger_remote_power(self, power_cmd: str):
        if not self.has_active_client():
            return
        cmd = str(power_cmd).strip()
        if cmd not in ("close_and_lock", "lock_only", "close_only"):
            return
        with self.task_lock:
            self.power_seq += 1
            self.power_cmd = cmd
            self.power_expires_at = time.monotonic() + 2.5
        self.push_now()

    def get_snapshot(self) -> dict:
        cpu = psutil.cpu_percent(interval=None)
        vm = psutil.virtual_memory()
        disk = psutil.disk_usage(get_primary_disk_path())
        now_mono = time.monotonic()
        with self.task_lock:
            task = self.current_task
            due = self.current_due
            note = self.current_note
            cfg_seq = self.cfg_seq
            cfg_layout = self.cfg_layout
            cfg_font = self.cfg_font
            cfg_show_playing = self.cfg_show_playing
            cfg_playing_left = self.cfg_playing_left
            cfg_time_mode = self.cfg_time_mode
            cfg_l3_show_task = self.cfg_l3_show_task
            cfg_l3_show_timer = self.cfg_l3_show_timer
            cfg_l3_show_rings = self.cfg_l3_show_rings
            cfg_l3_show_notif = self.cfg_l3_show_notif
            cfg_l3_split_pct = self.cfg_l3_split_pct
            cfg_l3_right_split_pct = self.cfg_l3_right_split_pct
            cfg_l3_text_scale = self.cfg_l3_text_scale
            cfg_l3_metric_scale = self.cfg_l3_metric_scale
            timer_seq = self.timer_seq
            timer_cmd = self.timer_cmd
            timer_val = self.timer_val
            power_seq = self.power_seq
            power_cmd = self.power_cmd if (self.power_cmd and now_mono <= self.power_expires_at) else ""

        data = {
            "cpu": round(cpu, 1),
            "ram_percent": round(vm.percent, 1),
            "ram_used_gb": round(vm.used / (1024 ** 3), 1),
            "ram_total_gb": round(vm.total / (1024 ** 3), 1),
            "disk_percent": round(disk.percent, 1),
            "disk_used_gb": round(disk.used / (1024 ** 3), 1),
            "disk_total_gb": round(disk.total / (1024 ** 3), 1),
            "task": task,
            "due": due,
            "note": note,
            "hostname": socket.gethostname(),
            "cfg_seq": cfg_seq,
            "cfg_layout": cfg_layout,
            "cfg_font": cfg_font,
            "cfg_show_playing": cfg_show_playing,
            "cfg_playing_left": cfg_playing_left,
            "cfg_time_mode": cfg_time_mode,
            "cfg_l3_show_task": cfg_l3_show_task,
            "cfg_l3_show_timer": cfg_l3_show_timer,
            "cfg_l3_show_rings": cfg_l3_show_rings,
            "cfg_l3_show_notif": cfg_l3_show_notif,
            "cfg_l3_split_pct": cfg_l3_split_pct,
            "cfg_l3_right_split_pct": cfg_l3_right_split_pct,
            "cfg_l3_text_scale": cfg_l3_text_scale,
            "cfg_l3_metric_scale": cfg_l3_metric_scale,
            "timer_seq": timer_seq,
            "timer_cmd": timer_cmd,
            "timer_val": timer_val,
            "power_seq": power_seq,
            "power_cmd": power_cmd,
        }
        data.update(self.media.get_media_state())
        data.update(self.notif.get_notifications_state())
        return data

    def start(self):
        if self.running:
            return
        self.enabled = True
        self.running = True
        psutil.cpu_percent(interval=None)
        threading.Thread(target=self._tcp_accept_loop, daemon=True).start()
        threading.Thread(target=self._udp_discovery_loop, daemon=True).start()
        threading.Thread(target=self._bt_accept_loop, daemon=True).start()
        threading.Thread(target=self._auto_adb_reverse_loop, daemon=True).start()
        threading.Thread(target=self._broadcast_loop, daemon=True).start()
        self._notify_status()

    def stop(self):
        self.running = False
        self.enabled = False
        with self.clients_lock:
            if self.active_client:
                try:
                    self.active_client[0].close()
                except OSError:
                    pass
                self.active_client = None
        for attr in ("_tcp_sock", "_udp_sock", "_bt_sock"):
            s = getattr(self, attr, None)
            if s is not None:
                try:
                    s.close()
                except OSError:
                    pass
                setattr(self, attr, None)
        self._notify_status()

    def _notify_status(self):
        self.on_status_change(1 if self.has_active_client() else 0, self.bt_status)

    def push_now(self):
        threading.Thread(target=self._send_once, daemon=True).start()

    def _send_once(self):
        if not self.running:
            return
        with self.clients_lock:
            client_entry = self.active_client
        if not client_entry:
            return

        sock, channel, last_thumb_seq, last_notif_seq = client_entry
        snap = self.get_snapshot()
        cur_thumb_seq = snap.get("thumb_seq", 0)
        cur_notif_seq = snap.get("notif_seq", 0)

        # Delta optimization: only transmit Base64 thumbnail / OS notifications when changed
        if cur_thumb_seq == last_thumb_seq:
            snap["media_thumb"] = ""
        if cur_notif_seq == last_notif_seq:
            snap["notif_sync"] = False
            snap["notifications"] = []
        else:
            snap["notif_sync"] = True

        payload = (json.dumps(snap) + "\n").encode("utf-8")
        try:
            sock.sendall(payload)
            if snap.get("power_cmd"):
                with self.task_lock:
                    if self.power_seq == snap.get("power_seq"):
                        self.power_cmd = ""
                        self.power_expires_at = 0.0
            if cur_thumb_seq != last_thumb_seq or cur_notif_seq != last_notif_seq:
                with self.clients_lock:
                    if self.active_client and self.active_client[0] is sock:
                        self.active_client = (sock, channel, cur_thumb_seq, cur_notif_seq)
        except OSError:
            self._remove_client(sock)

    def _try_accept_client(self, sock: socket.socket, channel_name: str):
        with self.clients_lock:
            if self.active_client is not None:
                # Reject other channels while an active connection is alive
                try:
                    sock.close()
                except OSError:
                    pass
                return
            self.active_client = (sock, channel_name, -1, -1)
        with self.task_lock:
            self.power_cmd = ""
            self.power_expires_at = 0.0

        threading.Thread(target=self._client_reader_loop, args=(sock,), daemon=True).start()
        self._notify_status()
        self.push_now()

    def _client_reader_loop(self, sock: socket.socket):
        buf = b""
        while self.running:
            try:
                chunk = sock.recv(1024)
                if not chunk:
                    break
                buf += chunk
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    text = line.decode("utf-8", errors="ignore").strip()
                    if text:
                        try:
                            msg = json.loads(text)
                            cmd = msg.get("cmd", "")
                            if cmd == "client_cfg":
                                self.apply_client_config(msg)
                            elif cmd == "dismiss_notif":
                                self.notif.dismiss(int(msg.get("id", 0)))
                                self.push_now()
                            elif cmd == "clear_notifs":
                                self.notif.clear_all()
                                self.push_now()
                            elif cmd:
                                self.media.execute_command(cmd)
                        except Exception:
                            pass
            except OSError:
                break
        self._remove_client(sock)

    def _remove_client(self, sock: socket.socket):
        removed = False
        with self.clients_lock:
            if self.active_client and self.active_client[0] is sock:
                self.active_client = None
                removed = True
        try:
            sock.close()
        except OSError:
            pass
        if removed:
            with self.task_lock:
                self.power_cmd = ""
                self.power_expires_at = 0.0
            self._notify_status()

    def _tcp_accept_loop(self):
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("0.0.0.0", TCP_PORT))
        srv.listen(5)
        self._tcp_sock = srv
        while self.running:
            try:
                client, addr = srv.accept()
                client.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                ip_str = addr[0]
                channel = "USB" if ip_str.startswith("127.") else f"Local network ({ip_str})"
                self._try_accept_client(client, channel)
            except OSError:
                break

    def _udp_discovery_loop(self):
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.bind(("0.0.0.0", DISCOVERY_PORT))
        except OSError:
            return
        self._udp_sock = sock
        while self.running:
            try:
                data, addr = sock.recvfrom(1024)
                # Only respond to discovery if no client is currently connected
                if not self.has_active_client() and data.strip() == DISCOVERY_MAGIC:
                    reply = json.dumps({"port": TCP_PORT, "hostname": socket.gethostname()}).encode("utf-8")
                    sock.sendto(reply, addr)
            except OSError:
                break

    def _bt_accept_loop(self):
        if not hasattr(socket, "AF_BLUETOOTH"):
            self.bt_status = "Bluetooth: unavailable"
            self._notify_status()
            return

        try:
            bt_sock = socket.socket(
                socket.AF_BLUETOOTH, socket.SOCK_STREAM, socket.BTPROTO_RFCOMM
            )
            bt_sock.bind((socket.BDADDR_ANY, 4))
            bt_sock.listen(3)
            self._bt_sock = bt_sock
            self.bt_status = "Bluetooth: listening"
            self._notify_status()
        except OSError:
            self.bt_status = "Bluetooth: unavailable"
            self._notify_status()
            return

        while self.running:
            try:
                client, _addr = bt_sock.accept()
                self._try_accept_client(client, "Bluetooth")
            except OSError:
                break

    def _auto_adb_reverse_loop(self):
        while self.running:
            if not self.has_active_client():
                try:
                    res = subprocess.run(
                        ["adb", "reverse", f"tcp:{TCP_PORT}", f"tcp:{TCP_PORT}"],
                        capture_output=True,
                        timeout=3,
                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                    )
                    ready = (res.returncode == 0)
                    if ready != self.usb_ready:
                        self.usb_ready = ready
                        self._notify_status()
                except Exception:
                    pass
            time.sleep(4.0)

    def _broadcast_loop(self):
        while self.running:
            if self.has_active_client():
                self._send_once()
            time.sleep(1.0)


def run_headless():
    state = {"clients": 0, "bt": "Bluetooth: starting"}
    ip = get_local_ip()
    srv_ref = [None]

    def emit_stdout_state():
        srv = srv_ref[0]
        if not srv:
            return
        with srv.task_lock:
            out = {
                "ip": ip,
                "port": TCP_PORT,
                "enabled": 1 if srv.enabled else 0,
                "clients": state["clients"],
                "channel": srv.get_active_channel(),
                "usb_ready": 1 if srv.usb_ready else 0,
                "bt": state["bt"],
                "layout": srv.cfg_layout,
                "font": srv.cfg_font,
                "show_playing": 1 if srv.cfg_show_playing else 0,
                "playing_left": 1 if srv.cfg_playing_left else 0,
                "time_mode": srv.cfg_time_mode,
                "l3_show_task": 1 if srv.cfg_l3_show_task else 0,
                "l3_show_timer": 1 if srv.cfg_l3_show_timer else 0,
                "l3_show_rings": 1 if srv.cfg_l3_show_rings else 0,
                "l3_show_notif": 1 if srv.cfg_l3_show_notif else 0,
                "l3_split_pct": srv.cfg_l3_split_pct,
                "l3_right_split_pct": srv.cfg_l3_right_split_pct,
                "l3_text_scale": srv.cfg_l3_text_scale,
                "l3_metric_scale": srv.cfg_l3_metric_scale,
                "task_b64": base64.b64encode(srv.current_task.encode("utf-8")).decode("ascii"),
                "due_b64": base64.b64encode(srv.current_due.encode("utf-8")).decode("ascii"),
                "note_b64": base64.b64encode(srv.current_note.encode("utf-8")).decode("ascii"),
            }
        sys.stdout.write(json.dumps(out) + "\n")
        sys.stdout.flush()

    def on_status(client_count: int, bt_status: str):
        state["clients"] = client_count
        state["bt"] = bt_status
        emit_stdout_state()

    srv = ResourceServer(on_status)
    srv_ref[0] = srv
    srv.start()

    def stdin_loop():
        for raw in sys.stdin:
            line = raw.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
                cmd = msg.get("cmd")
                if cmd == "set_task":
                    if "task_b64" in msg:
                        t_str = base64.b64decode(msg.get("task_b64", "")).decode("utf-8", errors="replace")
                        d_str = base64.b64decode(msg.get("due_b64", "")).decode("utf-8", errors="replace")
                        n_str = base64.b64decode(msg.get("note_b64", "")).decode("utf-8", errors="replace")
                        srv.set_task(t_str, n_str, d_str)
                    else:
                        srv.set_task(msg.get("task", ""), msg.get("note", ""), msg.get("due", ""))
                elif cmd == "remote_cfg":
                    srv.update_remote_config(msg)
                    emit_stdout_state()
                elif cmd == "remote_timer":
                    srv.trigger_remote_timer(msg.get("action", ""), msg.get("value", 25))
                elif cmd == "remote_power":
                    srv.trigger_remote_power(msg.get("action", ""))
                elif cmd == "server_enable":
                    if msg.get("enabled", True):
                        srv.start()
                    else:
                        srv.stop()
                    emit_stdout_state()
                elif cmd == "quit":
                    srv.stop()
                    os._exit(0)
            except Exception:
                pass

    threading.Thread(target=stdin_loop, daemon=True).start()

    try:
        while True:
            emit_stdout_state()
            time.sleep(2.0)
    except KeyboardInterrupt:
        srv.stop()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--headless", action="store_true")
    parser.parse_args()
    run_headless()
