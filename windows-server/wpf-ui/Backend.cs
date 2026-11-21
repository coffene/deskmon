using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace PCMonitorServer
{
    internal static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        [DllImport("user32.dll")]
        internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct SOCKADDR_BTH
        {
            public ushort addressFamily;
            public ulong btAddr;
            public Guid serviceClassId;
            public uint port;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WSADATA
        {
            public ushort wVersion;
            public ushort wHighVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
            public string szDescription;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)]
            public string szSystemStatus;
            public ushort iMaxSockets;
            public ushort iMaxUdpDg;
            public IntPtr lpVendorInfo;
        }

        [DllImport("ws2_32.dll")]
        internal static extern int WSAStartup(ushort ver, out WSADATA data);

        [DllImport("ws2_32.dll")]
        internal static extern IntPtr socket(int af, int type, int proto);

        [DllImport("ws2_32.dll")]
        internal static extern int bind(IntPtr s, ref SOCKADDR_BTH addr, int namelen);

        [DllImport("ws2_32.dll")]
        internal static extern int listen(IntPtr s, int backlog);

        [DllImport("ws2_32.dll")]
        internal static extern IntPtr accept(IntPtr s, IntPtr addr, IntPtr addrlen);

        [DllImport("ws2_32.dll")]
        internal static extern int recv(IntPtr s, byte[] buf, int len, int flags);

        [DllImport("ws2_32.dll")]
        internal static extern int send(IntPtr s, byte[] buf, int len, int flags);

        [DllImport("ws2_32.dll")]
        internal static extern int setsockopt(IntPtr s, int level, int optname, ref int optval, int optlen);

        [DllImport("ws2_32.dll")]
        internal static extern int closesocket(IntPtr s);

        internal static void TrimWorkingSet()
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
                GC.WaitForPendingFinalizers();
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }
    }

    internal interface IClientTransport : IDisposable
    {
        void SendLine(byte[] payload);
        string ReadLine();
    }

    internal sealed class TcpTransport : IClientTransport
    {
        private readonly TcpClient client;
        private readonly StreamReader reader;
        private readonly NetworkStream stream;
        private readonly object writeLock = new object();

        public TcpTransport(TcpClient c)
        {
            client = c;
            client.NoDelay = true;
            client.ReceiveTimeout = 2000;
            client.SendTimeout = 2000;
            stream = client.GetStream();
            reader = new StreamReader(stream, Encoding.UTF8);
        }

        public void SendLine(byte[] payload)
        {
            lock (writeLock)
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
        }

        public string ReadLine()
        {
            while (true)
            {
                try
                {
                    return reader.ReadLine();
                }
                catch (IOException ex)
                {
                    var se = ex.InnerException as SocketException;
                    if (se != null && se.SocketErrorCode == SocketError.TimedOut)
                    {
                        continue;
                    }
                    throw;
                }
            }
        }

        public void Dispose()
        {
            try { reader.Dispose(); } catch { }
            try { stream.Dispose(); } catch { }
            try { client.Close(); } catch { }
        }
    }

    internal sealed class BluetoothTransport : IClientTransport
    {
        private IntPtr sock;
        private readonly MemoryStream lineBuf = new MemoryStream();
        private readonly object writeLock = new object();
        private volatile bool closed;

        public BluetoothTransport(IntPtr acceptedSocket)
        {
            sock = acceptedSocket;
            int timeoutMs = 1500;
            Native.setsockopt(sock, 0xFFFF, 0x1006, ref timeoutMs, sizeof(int));
            Native.setsockopt(sock, 0xFFFF, 0x1005, ref timeoutMs, sizeof(int));
        }

        public void SendLine(byte[] payload)
        {
            lock (writeLock)
            {
                if (closed || sock == IntPtr.Zero || sock == new IntPtr(-1)) throw new IOException("Closed");
                int sentTotal = 0;
                while (sentTotal < payload.Length)
                {
                    int n = Native.send(sock, payload, payload.Length - sentTotal, 0);
                    if (n <= 0) throw new IOException("Send failed");
                    sentTotal += n;
                    if (sentTotal < payload.Length)
                    {
                        byte[] rem = new byte[payload.Length - sentTotal];
                        System.Buffer.BlockCopy(payload, sentTotal, rem, 0, rem.Length);
                        payload = rem;
                        sentTotal = 0;
                    }
                }
            }
        }

        public string ReadLine()
        {
            byte[] buf = new byte[1024];
            while (!closed)
            {
                int n = Native.recv(sock, buf, buf.Length, 0);
                if (n == 0) return null;
                if (n < 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 10060) continue;
                    return null;
                }
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b == (byte)'\n')
                    {
                        string s = Encoding.UTF8.GetString(lineBuf.ToArray()).Trim();
                        lineBuf.SetLength(0);
                        return s;
                    }
                    lineBuf.WriteByte(b);
                }
            }
            return null;
        }

        public void Dispose()
        {
            closed = true;
            IntPtr s = Interlocked.Exchange(ref sock, new IntPtr(-1));
            if (s != IntPtr.Zero && s != new IntPtr(-1))
            {
                Native.closesocket(s);
            }
        }
    }

    internal static class WinRtHelper
    {
        public static T AwaitOp<T>(IAsyncOperation<T> op, int timeoutMs = 900)
        {
            using (var ev = new ManualResetEventSlim(false))
            {
                op.Completed = (info, status) => ev.Set();
                if (!ev.Wait(timeoutMs)) return default(T);
                return op.GetResults();
            }
        }

        public static string ResizeThumbnailToBase64(IRandomAccessStreamReference thumbRef)
        {
            if (thumbRef == null) return "";
            try
            {
                using (var stream = AwaitOp(thumbRef.OpenReadAsync(), 800))
                {
                    if (stream == null || stream.Size == 0 || stream.Size > 8000000) return "";
                    uint sz = (uint)stream.Size;
                    byte[] raw = new byte[sz];
                    using (var reader = new DataReader(stream))
                    {
                        uint loaded = AwaitOp(reader.LoadAsync(sz), 800);
                        if (loaded == 0) return "";
                        reader.ReadBytes(raw);
                    }

                    using (var msIn = new MemoryStream(raw))
                    using (var src = new Bitmap(msIn))
                    {
                        int maxDim = 256;
                        int tw = src.Width;
                        int th = src.Height;
                        if (tw > maxDim || th > maxDim)
                        {
                            double scale = Math.Min((double)maxDim / tw, (double)maxDim / th);
                            tw = Math.Max(1, (int)Math.Round(tw * scale));
                            th = Math.Max(1, (int)Math.Round(th * scale));
                        }
                        using (var dst = new Bitmap(tw, th, PixelFormat.Format24bppRgb))
                        using (var g = Graphics.FromImage(dst))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, 0, 0, tw, th);
                            using (var msOut = new MemoryStream())
                            {
                                ImageCodecInfo jpgCodec = GetJpegCodec();
                                if (jpgCodec != null)
                                {
                                    using (var ep = new EncoderParameters(1))
                                    {
                                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                                        dst.Save(msOut, jpgCodec, ep);
                                    }
                                }
                                else
                                {
                                    dst.Save(msOut, ImageFormat.Jpeg);
                                }
                                return Convert.ToBase64String(msOut.ToArray());
                            }
                        }
                    }
                }
            }
            catch
            {
                return "";
            }
        }

        public static string LoadAndEncodeImageBase64(string source, int maxW, int maxH, long quality)
        {
            if (string.IsNullOrWhiteSpace(source)) return "";
            source = source.Trim().Trim('"');
            byte[] raw;
            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "Deskmon/1.0");
                    raw = wc.DownloadData(source);
                }
            }
            else if (File.Exists(source))
            {
                raw = File.ReadAllBytes(source);
            }
            else
            {
                return "";
            }
            if (raw == null || raw.Length == 0 || raw.Length > 20000000) return "";
            using (var msIn = new MemoryStream(raw))
            using (var src = new Bitmap(msIn))
            {
                int tw = src.Width;
                int th = src.Height;
                if (tw > maxW || th > maxH)
                {
                    double scale = Math.Min((double)maxW / tw, (double)maxH / th);
                    tw = Math.Max(1, (int)Math.Round(tw * scale));
                    th = Math.Max(1, (int)Math.Round(th * scale));
                }
                using (var dst = new Bitmap(tw, th, PixelFormat.Format24bppRgb))
                using (var g = Graphics.FromImage(dst))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, 0, 0, tw, th);
                    using (var msOut = new MemoryStream())
                    {
                        ImageCodecInfo jpgCodec = GetJpegCodec();
                        if (jpgCodec != null)
                        {
                            using (var ep = new EncoderParameters(1))
                            {
                                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                                dst.Save(msOut, jpgCodec, ep);
                            }
                        }
                        else
                        {
                            dst.Save(msOut, ImageFormat.Jpeg);
                        }
                        return Convert.ToBase64String(msOut.ToArray());
                    }
                }
            }
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders())
            {
                if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            }
            return null;
        }
    }

    internal sealed class MonitorServer
    {
        public const int DefaultTcpPort = 8765;
        public int TcpPort { get; private set; }
        public const int DiscoveryPort = 8766;

        private readonly string configPath;
        private readonly string bgPath;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        private readonly object stateLock = new object();
        private readonly object clientLock = new object();

        private IClientTransport activeClient;
        private string activeChannel = "";
        private int clientLastThumbSeq = -1;
        private int clientLastNotifSeq = -1;
        private int clientLastPushImgSeq = -1;
        private int clientLastBgSeq = -1;

        private volatile bool running;
        public bool Enabled { get; private set; }
        public string BtStatus { get; private set; }
        public bool UsbReady { get; private set; }

        private TcpListener tcpListener;
        private UdpClient udpListener;
        private IntPtr btListenSock = new IntPtr(-1);

        private long prevIdle, prevKernel, prevUser;

        public string CurrentTask = "";
        public string CurrentDue = "";
        public string CurrentNote = "";
        public int CfgSeq = 1;
        public int CfgLayout = 0;
        public int CfgFont = 0;
        public bool CfgShowPlaying = true;
        public bool CfgPlayingLeft = false;
        public int CfgTimeMode = 0;
        public bool CfgL3ShowTask = true;
        public bool CfgL3ShowTimer = true;
        public bool CfgL3ShowRings = true;
        public bool CfgL3ShowNotif = true;
        public int CfgL3SplitPct = 54;
        public int CfgL3RightSplitPct = 55;
        public int CfgL3TextScale = 100;
        public int CfgL3MetricScale = 100;
        public bool CfgShowTopBar = true;
        public bool CfgShowLogo = true;
        public bool CfgFullscreen = false;
        public bool CfgBgEnabled = true;
        public int CfgBgMode = 0; // 0 = Custom image, 1 = Sync with playing
        public int CfgBgOpacity = 32;
        public int CfgBgBlur = 6;
        public bool StartWithWindows = false;
        public bool ShowWindowOnStartup = true;

        private int timerSeq = 0;
        private string timerCmd = "";
        private int timerVal = 25;

        private int powerSeq = 0;
        private string powerCmd = "";
        private DateTime powerExpiresAt = DateTime.MinValue;

        private string cachedMediaKey = "";
        private string cachedThumbB64 = "";
        private int thumbSeq = 0;
        private int cachedMediaDur = 0;
        private int cachedMediaPos = 0;
        private DateTime cachedMediaPosAt = DateTime.MinValue;

        private string cachedNotifSig = "";
        private List<Dictionary<string, object>> cachedNotifs = new List<Dictionary<string, object>>();
        private int notifSeq = 1;
        private bool notifAccessOk = true;
        private bool notifAccessRequested = false;

        private int pushImgSeq = 0;
        private string pushImgB64 = "";
        private int bgSeq = 1;
        private string bgB64 = "";
        public bool HasPushImage { get { lock (stateLock) return !string.IsNullOrEmpty(pushImgB64); } }
        public bool HasBackgroundImage { get { lock (stateLock) return !string.IsNullOrEmpty(bgB64); } }

        public event Action StateChanged;

        public MonitorServer(string baseDir)
        {
            configPath = Path.Combine(baseDir, "server_config.json");
            bgPath = Path.Combine(baseDir, "server_bg.jpg");
            TcpPort = DefaultTcpPort;
            BtStatus = "Bluetooth: starting";
            Enabled = true;
            Native.GetSystemTimes(out prevIdle, out prevKernel, out prevUser);
            LoadConfig();
            LoadSavedBackground();
        }

        private void LoadSavedBackground()
        {
            try
            {
                if (File.Exists(bgPath))
                {
                    byte[] raw = File.ReadAllBytes(bgPath);
                    if (raw != null && raw.Length > 0 && raw.Length < 1024 * 1024)
                    {
                        lock (stateLock)
                        {
                            bgB64 = Convert.ToBase64String(raw);
                            bgSeq = 1;
                        }
                    }
                }
            }
            catch { }
        }

        public void SetPushImage(string b64)
        {
            lock (stateLock)
            {
                pushImgB64 = b64 ?? "";
                pushImgSeq++;
            }
            NotifyStateChanged();
            PushNow();
        }

        public string GetBackgroundBase64()
        {
            lock (stateLock) return bgB64 ?? "";
        }

        public string GetCurrentMediaThumbBase64()
        {
            return cachedThumbB64 ?? "";
        }

        public void SetBackgroundStyle(bool enabled, int mode, int opacity, int blur)
        {
            int nextMode = Clamp(mode, 0, 1);
            int nextOp = Clamp(opacity, 5, 100);
            int nextBlur = Clamp(blur, 0, 25);
            lock (stateLock)
            {
                if (CfgBgEnabled == enabled && CfgBgMode == nextMode && CfgBgOpacity == nextOp && CfgBgBlur == nextBlur) return;
                CfgBgEnabled = enabled;
                CfgBgMode = nextMode;
                CfgBgOpacity = nextOp;
                CfgBgBlur = nextBlur;
                CfgSeq++;
            }
            SaveConfig();
            NotifyStateChanged();
            PushNow();
        }

        public void SetBackgroundImage(string b64)
        {
            string clean = b64 ?? "";
            try
            {
                if (string.IsNullOrEmpty(clean))
                {
                    if (File.Exists(bgPath)) File.Delete(bgPath);
                }
                else
                {
                    File.WriteAllBytes(bgPath, Convert.FromBase64String(clean));
                }
            }
            catch { }
            lock (stateLock)
            {
                bgB64 = clean;
                bgSeq++;
            }
            NotifyStateChanged();
            PushNow();
        }

        public bool HasActiveClient()
        {
            lock (clientLock) return activeClient != null;
        }

        public string GetActiveChannel()
        {
            lock (clientLock) return activeClient != null ? activeChannel : "";
        }

        private static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }

        private void ApplyCfgDict(IDictionary<string, object> d)
        {
            if (d.ContainsKey("layout")) CfgLayout = Clamp(Convert.ToInt32(d["layout"]), 0, 2);
            if (d.ContainsKey("font")) CfgFont = Clamp(Convert.ToInt32(d["font"]), 0, 3);
            if (d.ContainsKey("show_playing")) CfgShowPlaying = Convert.ToBoolean(d["show_playing"]);
            if (d.ContainsKey("playing_left")) CfgPlayingLeft = Convert.ToBoolean(d["playing_left"]);
            if (d.ContainsKey("time_mode")) CfgTimeMode = Clamp(Convert.ToInt32(d["time_mode"]), 0, 3);
            if (d.ContainsKey("l3_show_task")) CfgL3ShowTask = Convert.ToBoolean(d["l3_show_task"]);
            if (d.ContainsKey("l3_show_timer")) CfgL3ShowTimer = Convert.ToBoolean(d["l3_show_timer"]);
            if (d.ContainsKey("l3_show_rings")) CfgL3ShowRings = Convert.ToBoolean(d["l3_show_rings"]);
            if (d.ContainsKey("l3_show_notif")) CfgL3ShowNotif = Convert.ToBoolean(d["l3_show_notif"]);
            if (d.ContainsKey("l3_split_pct")) CfgL3SplitPct = Clamp(Convert.ToInt32(d["l3_split_pct"]), 37, 66);
            if (d.ContainsKey("l3_right_split_pct")) CfgL3RightSplitPct = Clamp(Convert.ToInt32(d["l3_right_split_pct"]), 38, 65);
            if (d.ContainsKey("l3_text_scale")) CfgL3TextScale = Clamp(Convert.ToInt32(d["l3_text_scale"]), 85, 130);
            if (d.ContainsKey("l3_metric_scale")) CfgL3MetricScale = Clamp(Convert.ToInt32(d["l3_metric_scale"]), 80, 125);
            if (d.ContainsKey("show_top_bar")) CfgShowTopBar = Convert.ToBoolean(d["show_top_bar"]);
            if (d.ContainsKey("show_logo")) CfgShowLogo = Convert.ToBoolean(d["show_logo"]);
            if (d.ContainsKey("fullscreen")) CfgFullscreen = Convert.ToBoolean(d["fullscreen"]);
            if (d.ContainsKey("bg_enabled")) CfgBgEnabled = Convert.ToBoolean(d["bg_enabled"]);
            if (d.ContainsKey("bg_mode")) CfgBgMode = Clamp(Convert.ToInt32(d["bg_mode"]), 0, 1);
            else if (d.ContainsKey("bg_sync_playing")) CfgBgMode = Convert.ToBoolean(d["bg_sync_playing"]) ? 1 : 0;
            if (d.ContainsKey("bg_opacity")) CfgBgOpacity = Clamp(Convert.ToInt32(d["bg_opacity"]), 5, 100);
            if (d.ContainsKey("bg_blur")) CfgBgBlur = Clamp(Convert.ToInt32(d["bg_blur"]), 0, 25);
        }

        private void LoadConfig()
        {
            if (!File.Exists(configPath)) return;
            try
            {
                string raw = File.ReadAllText(configPath, Encoding.UTF8);
                var d = json.Deserialize<Dictionary<string, object>>(raw);
                if (d == null) return;
                lock (stateLock)
                {
                    ApplyCfgDict(d);
                    if (d.ContainsKey("task") && d["task"] != null) CurrentTask = d["task"].ToString();
                    if (d.ContainsKey("due") && d["due"] != null) CurrentDue = d["due"].ToString();
                    if (d.ContainsKey("note") && d["note"] != null) CurrentNote = d["note"].ToString();
                    if (d.ContainsKey("start_with_windows")) StartWithWindows = Convert.ToBoolean(d["start_with_windows"]);
                    if (d.ContainsKey("show_window_on_startup")) ShowWindowOnStartup = Convert.ToBoolean(d["show_window_on_startup"]);
                    if (d.ContainsKey("tcp_port")) TcpPort = Clamp(Convert.ToInt32(d["tcp_port"]), 1024, 65535);
                }
            }
            catch { }
        }

        public void SaveServerPrefs(bool startWithWin, bool showWinOnStart)
        {
            lock (stateLock)
            {
                StartWithWindows = startWithWin;
                ShowWindowOnStartup = showWinOnStart;
            }
            SaveConfig();
        }

        public void SetPort(int port)
        {
            int next = Clamp(port, 1024, 65535);
            if (next == TcpPort) return;
            bool wasEnabled = Enabled;
            if (wasEnabled) Stop();
            lock (stateLock)
            {
                TcpPort = next;
            }
            SaveConfig();
            if (wasEnabled) Start();
            else NotifyStateChanged();
        }

        private void SaveConfig()
        {
            try
            {
                Dictionary<string, object> d;
                lock (stateLock)
                {
                    d = new Dictionary<string, object>
                    {
                        { "layout", CfgLayout },
                        { "font", CfgFont },
                        { "show_playing", CfgShowPlaying },
                        { "playing_left", CfgPlayingLeft },
                        { "time_mode", CfgTimeMode },
                        { "l3_show_task", CfgL3ShowTask },
                        { "l3_show_timer", CfgL3ShowTimer },
                        { "l3_show_rings", CfgL3ShowRings },
                        { "l3_show_notif", CfgL3ShowNotif },
                        { "l3_split_pct", CfgL3SplitPct },
                        { "l3_right_split_pct", CfgL3RightSplitPct },
                        { "l3_text_scale", CfgL3TextScale },
                        { "l3_metric_scale", CfgL3MetricScale },
                        { "show_top_bar", CfgShowTopBar },
                        { "show_logo", CfgShowLogo },
                        { "fullscreen", CfgFullscreen },
                        { "bg_enabled", CfgBgEnabled },
                        { "bg_mode", CfgBgMode },
                        { "bg_opacity", CfgBgOpacity },
                        { "bg_blur", CfgBgBlur },
                        { "task", CurrentTask },
                        { "due", CurrentDue },
                        { "note", CurrentNote },
                        { "start_with_windows", StartWithWindows },
                        { "show_window_on_startup", ShowWindowOnStartup },
                        { "tcp_port", TcpPort }
                    };
                }
                File.WriteAllText(configPath, json.Serialize(d), Encoding.UTF8);
            }
            catch { }
        }

        public void SetTask(string task, string due, string note)
        {
            lock (stateLock)
            {
                CurrentTask = (task ?? "").Trim();
                CurrentDue = (due ?? "").Trim();
                CurrentNote = (note ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim('\n');
            }
            SaveConfig();
            PushNow();
        }

        public void SyncFromUi(
            int layout, int font, bool showPlaying, bool playingLeft, int timeMode,
            bool l3Task, bool l3Timer, bool l3Rings, bool l3Notif,
            int l3Split, int l3RightSplit, int l3Text, int l3Metric,
            bool showTopBar, bool showLogo, bool fullscreen)
        {
            lock (stateLock)
            {
                CfgSeq++;
                CfgLayout = layout;
                CfgFont = font;
                CfgShowPlaying = showPlaying;
                CfgPlayingLeft = playingLeft;
                CfgTimeMode = timeMode;
                CfgL3ShowTask = l3Task;
                CfgL3ShowTimer = l3Timer;
                CfgL3ShowRings = l3Rings;
                CfgL3ShowNotif = l3Notif;
                CfgL3SplitPct = Clamp(l3Split, 37, 66);
                CfgL3RightSplitPct = Clamp(l3RightSplit, 38, 65);
                CfgL3TextScale = Clamp(l3Text, 85, 130);
                CfgL3MetricScale = Clamp(l3Metric, 80, 125);
                CfgShowTopBar = showTopBar;
                CfgShowLogo = showLogo;
                CfgFullscreen = fullscreen;
            }
            SaveConfig();
            PushNow();
        }

        public void TriggerTimer(string cmd, int val)
        {
            lock (stateLock)
            {
                timerSeq++;
                timerCmd = cmd;
                timerVal = val;
            }
            PushNow();
        }

        public bool TriggerPower(string cmd)
        {
            if (!HasActiveClient()) return false;
            lock (stateLock)
            {
                powerSeq++;
                powerCmd = cmd;
                powerExpiresAt = DateTime.UtcNow.AddSeconds(2.5);
            }
            PushNow();
            return true;
        }

        public void Start()
        {
            if (running) return;
            Enabled = true;
            running = true;
            SpawnThread(TcpAcceptLoop);
            SpawnThread(UdpDiscoveryLoop);
            SpawnThread(BtAcceptLoop);
            SpawnThread(AutoAdbLoop);
            SpawnThread(BroadcastLoop);
            NotifyStateChanged();
        }

        public void Stop()
        {
            running = false;
            Enabled = false;
            lock (clientLock)
            {
                if (activeClient != null)
                {
                    try { activeClient.Dispose(); } catch { }
                    activeClient = null;
                    activeChannel = "";
                }
            }
            try { if (tcpListener != null) tcpListener.Stop(); } catch { }
            try { if (udpListener != null) udpListener.Close(); } catch { }
            IntPtr bs = Interlocked.Exchange(ref btListenSock, new IntPtr(-1));
            if (bs != IntPtr.Zero && bs != new IntPtr(-1)) Native.closesocket(bs);
            NotifyStateChanged();
        }

        private static void SpawnThread(ThreadStart start)
        {
            var t = new Thread(start) { IsBackground = true };
            t.Start();
        }

        private void NotifyStateChanged()
        {
            var h = StateChanged;
            if (h != null) h();
        }

        public void PushNow()
        {
            if (!running) return;
            ThreadPool.QueueUserWorkItem(delegate { SendOnce(); });
        }

        private double ReadCpuPercent()
        {
            long idle, kernel, user;
            if (!Native.GetSystemTimes(out idle, out kernel, out user)) return 0.0;
            long dIdle = idle - prevIdle;
            long dKernel = kernel - prevKernel;
            long dUser = user - prevUser;
            prevIdle = idle;
            prevKernel = kernel;
            prevUser = user;
            long total = dKernel + dUser;
            if (total <= 0) return 0.0;
            double busy = (total - dIdle) * 100.0 / total;
            return Math.Max(0.0, Math.Min(100.0, Math.Round(busy, 1)));
        }

        private Dictionary<string, object> FetchMediaState()
        {
            var res = new Dictionary<string, object>
            {
                { "media_title", "Not playing" },
                { "media_artist", "" },
                { "media_status", "Not playing" },
                { "media_pos", 0 },
                { "media_dur", 0 },
                { "thumb_seq", thumbSeq },
                { "media_thumb", "" }
            };
            try
            {
                var mgr = WinRtHelper.AwaitOp(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), 700);
                var session = mgr != null ? mgr.GetCurrentSession() : null;
                if (session == null)
                {
                    ClearCachedMedia();
                    res["thumb_seq"] = thumbSeq;
                    return res;
                }
                var info = session.GetPlaybackInfo();
                int st = info != null ? (int)info.PlaybackStatus : 0;
                if (st != 4 && st != 5)
                {
                    ClearCachedMedia();
                    res["thumb_seq"] = thumbSeq;
                    return res;
                }
                var props = WinRtHelper.AwaitOp(session.TryGetMediaPropertiesAsync(), 700);
                string title = (props != null && props.Title != null) ? props.Title.Trim() : "";
                if (string.IsNullOrEmpty(title))
                {
                    ClearCachedMedia();
                    res["thumb_seq"] = thumbSeq;
                    return res;
                }
                string artist = "";
                if (props != null)
                {
                    if (!string.IsNullOrEmpty(props.Artist)) artist = props.Artist.Trim();
                    else if (!string.IsNullOrEmpty(props.AlbumArtist)) artist = props.AlbumArtist.Trim();
                }
                if (string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(session.SourceAppUserModelId))
                {
                    artist = session.SourceAppUserModelId.Split('!')[0].Replace(".exe", "");
                }

                string key = artist + "\n" + title;
                if (key != cachedMediaKey)
                {
                    cachedMediaKey = key;
                    cachedThumbB64 = WinRtHelper.ResizeThumbnailToBase64(props != null ? props.Thumbnail : null);
                    cachedMediaDur = 0;
                    cachedMediaPos = 0;
                    cachedMediaPosAt = DateTime.MinValue;
                    thumbSeq++;
                }

                var tl = session.GetTimelineProperties();
                int rawPos = tl != null ? Math.Max(0, (int)tl.Position.TotalSeconds) : 0;
                int rawDur = 0;
                if (tl != null)
                {
                    rawDur = Math.Max(0, (int)tl.EndTime.TotalSeconds);
                    if (rawDur <= 0)
                    {
                        rawDur = Math.Max(0, (int)tl.MaxSeekTime.TotalSeconds);
                    }
                }

                int pos = rawPos;
                int dur = rawDur;
                bool tlDropped = (rawDur <= 0 && rawPos <= 0 && cachedMediaDur > 0);
                if (tlDropped)
                {
                    dur = cachedMediaDur;
                    if (st == 4 && cachedMediaPosAt != DateTime.MinValue)
                    {
                        int extra = Math.Max(0, (int)(DateTime.UtcNow - cachedMediaPosAt).TotalSeconds);
                        pos = Math.Min(dur, cachedMediaPos + extra);
                    }
                    else
                    {
                        pos = cachedMediaPos;
                    }
                }
                else
                {
                    if (dur > 0) cachedMediaDur = dur;
                    else if (cachedMediaDur > 0) dur = cachedMediaDur;

                    if (st == 4 && tl != null && tl.LastUpdatedTime.Year >= 2000)
                    {
                        double elapsed = (DateTimeOffset.UtcNow - tl.LastUpdatedTime).TotalSeconds;
                        if (elapsed > 0 && elapsed < 3600)
                        {
                            pos = dur > 0 ? Math.Min(dur, pos + (int)elapsed) : pos + (int)elapsed;
                        }
                    }
                    cachedMediaPos = pos;
                    cachedMediaPosAt = DateTime.UtcNow;
                }

                res["media_title"] = title;
                res["media_artist"] = artist;
                res["media_status"] = (st == 4) ? "Playing" : "Paused";
                res["media_pos"] = pos;
                res["media_dur"] = dur;
                res["thumb_seq"] = thumbSeq;
                res["media_thumb"] = cachedThumbB64;
            }
            catch { }
            return res;
        }

        private void ClearCachedMedia()
        {
            if (cachedMediaKey != "")
            {
                cachedMediaKey = "";
                cachedThumbB64 = "";
                cachedMediaDur = 0;
                cachedMediaPos = 0;
                cachedMediaPosAt = DateTime.MinValue;
                thumbSeq++;
            }
        }

        private void ExecuteMediaCommand(string cmd)
        {
            try
            {
                var mgr = WinRtHelper.AwaitOp(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), 700);
                var session = mgr != null ? mgr.GetCurrentSession() : null;
                if (session != null)
                {
                    bool ok = false;
                    if (cmd == "play_pause") ok = WinRtHelper.AwaitOp(session.TryTogglePlayPauseAsync(), 700);
                    else if (cmd == "stop")
                    {
                        ok = WinRtHelper.AwaitOp(session.TryStopAsync(), 700);
                        if (!ok) ok = WinRtHelper.AwaitOp(session.TryPauseAsync(), 700);
                    }
                    else if (cmd == "next") ok = WinRtHelper.AwaitOp(session.TrySkipNextAsync(), 700);
                    else if (cmd == "prev") ok = WinRtHelper.AwaitOp(session.TrySkipPreviousAsync(), 700);
                    if (ok) return;
                }
            }
            catch { }

            byte vk = 0;
            if (cmd == "play_pause") vk = 0xB3;
            else if (cmd == "stop") vk = 0xB2;
            else if (cmd == "next") vk = 0xB0;
            else if (cmd == "prev") vk = 0xB1;
            if (vk != 0)
            {
                Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
                Native.keybd_event(vk, 0, 2, UIntPtr.Zero);
            }
        }

        private UserNotificationListener GetAllowedListener()
        {
            var listener = UserNotificationListener.Current;
            UserNotificationListenerAccessStatus status;
            if (!notifAccessRequested)
            {
                notifAccessRequested = true;
                status = WinRtHelper.AwaitOp(listener.RequestAccessAsync(), 800);
            }
            else
            {
                status = listener.GetAccessStatus();
            }
            notifAccessOk = (status == UserNotificationListenerAccessStatus.Allowed);
            return notifAccessOk ? listener : null;
        }

        private void RefreshNotifications()
        {
            try
            {
                var listener = GetAllowedListener();
                if (listener == null)
                {
                    if (cachedNotifSig != "DENIED")
                    {
                        cachedNotifSig = "DENIED";
                        cachedNotifs = new List<Dictionary<string, object>>();
                        notifSeq++;
                    }
                    return;
                }

                var rawList = WinRtHelper.AwaitOp(listener.GetNotificationsAsync(NotificationKinds.Toast), 800);
                if (rawList == null) return;

                var sortable = new List<KeyValuePair<long, Dictionary<string, object>>>();
                var sigSb = new StringBuilder();

                foreach (var n in rawList)
                {
                    try
                    {
                        string app = "";
                        if (n.AppInfo != null && n.AppInfo.DisplayInfo != null && n.AppInfo.DisplayInfo.DisplayName != null)
                        {
                            app = n.AppInfo.DisplayInfo.DisplayName.Trim();
                        }
                        if (string.IsNullOrEmpty(app)) app = "Windows";

                        var texts = new List<string>();
                        var visual = n.Notification != null ? n.Notification.Visual : null;
                        var binding = visual != null ? visual.GetBinding(KnownNotificationBindings.ToastGeneric) : null;
                        if (binding != null)
                        {
                            foreach (var el in binding.GetTextElements())
                            {
                                string t = el.Text != null ? el.Text.Trim() : "";
                                if (!string.IsNullOrEmpty(t)) texts.Add(t);
                            }
                        }

                        string title = texts.Count >= 1 ? texts[0] : app;
                        string body = "";
                        if (texts.Count >= 2)
                        {
                            texts.RemoveAt(0);
                            body = string.Join("\n", texts.ToArray());
                        }
                        if (app.Length > 40) app = app.Substring(0, 40);
                        if (title.Length > 140) title = title.Substring(0, 140);
                        if (body.Length > 280) body = body.Substring(0, 280);

                        string timeStr = n.CreationTime.LocalDateTime.ToString("HH:mm");
                        long ticks = n.CreationTime.UtcTicks;

                        sortable.Add(new KeyValuePair<long, Dictionary<string, object>>(ticks, new Dictionary<string, object>
                        {
                            { "id", (int)n.Id },
                            { "app", app },
                            { "title", title },
                            { "body", body },
                            { "time", timeStr }
                        }));
                    }
                    catch { }
                }

                sortable.Sort((a, b) =>
                {
                    int cmp = b.Key.CompareTo(a.Key);
                    if (cmp != 0) return cmp;
                    return ((int)b.Value["id"]).CompareTo((int)a.Value["id"]);
                });

                var top = new List<Dictionary<string, object>>();
                int count = Math.Min(10, sortable.Count);
                for (int i = 0; i < count; i++)
                {
                    var item = sortable[i].Value;
                    top.Add(item);
                    sigSb.Append(item["id"]).Append('|').Append(item["app"]).Append('|').Append(item["title"]).Append('\n');
                }

                string sig = "OK:" + sigSb.ToString();
                if (sig != cachedNotifSig)
                {
                    cachedNotifSig = sig;
                    cachedNotifs = top;
                    notifSeq++;
                }
            }
            catch { }
        }

        private void SendOnce()
        {
            if (!running) return;
            IClientTransport client;
            int lastThumb, lastNotif, lastPushImg, lastBg;
            lock (clientLock)
            {
                client = activeClient;
                lastThumb = clientLastThumbSeq;
                lastNotif = clientLastNotifSeq;
                lastPushImg = clientLastPushImgSeq;
                lastBg = clientLastBgSeq;
            }
            if (client == null) return;

            double cpu = ReadCpuPercent();
            var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX)) };
            Native.GlobalMemoryStatusEx(ref mem);
            double ramTotalGb = Math.Round(mem.ullTotalPhys / 1073741824.0, 1);
            double ramUsedGb = Math.Round((mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0, 1);
            double ramPct = mem.dwMemoryLoad;

            double diskTotalGb = 0, diskUsedGb = 0, diskPct = 0;
            try
            {
                string sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
                var di = new DriveInfo(sysRoot);
                if (di.TotalSize > 0)
                {
                    long used = di.TotalSize - di.AvailableFreeSpace;
                    diskTotalGb = Math.Round(di.TotalSize / 1073741824.0, 1);
                    diskUsedGb = Math.Round(used / 1073741824.0, 1);
                    diskPct = Math.Round(used * 100.0 / di.TotalSize, 1);
                }
            }
            catch { }

            var media = FetchMediaState();
            RefreshNotifications();

            int curThumb = Convert.ToInt32(media["thumb_seq"]);
            if (curThumb == lastThumb) media["media_thumb"] = "";

            bool notifSync = (notifSeq != lastNotif);
            string activePowerCmd = "";
            int curPowerSeq = 0;
            int curPushImgSeq = 0;
            int curBgSeq = 0;

            var snap = new Dictionary<string, object>();
            lock (stateLock)
            {
                if (!string.IsNullOrEmpty(powerCmd) && DateTime.UtcNow <= powerExpiresAt)
                {
                    activePowerCmd = powerCmd;
                    curPowerSeq = powerSeq;
                }
                else
                {
                    curPowerSeq = powerSeq;
                }

                snap["cpu"] = cpu;
                snap["ram_percent"] = ramPct;
                snap["ram_used_gb"] = ramUsedGb;
                snap["ram_total_gb"] = ramTotalGb;
                snap["disk_percent"] = diskPct;
                snap["disk_used_gb"] = diskUsedGb;
                snap["disk_total_gb"] = diskTotalGb;
                snap["task"] = CurrentTask;
                snap["due"] = CurrentDue;
                snap["note"] = CurrentNote;
                snap["hostname"] = Environment.MachineName;
                snap["cfg_seq"] = CfgSeq;
                snap["cfg_layout"] = CfgLayout;
                snap["cfg_font"] = CfgFont;
                snap["cfg_show_playing"] = CfgShowPlaying;
                snap["cfg_playing_left"] = CfgPlayingLeft;
                snap["cfg_time_mode"] = CfgTimeMode;
                snap["cfg_l3_show_task"] = CfgL3ShowTask;
                snap["cfg_l3_show_timer"] = CfgL3ShowTimer;
                snap["cfg_l3_show_rings"] = CfgL3ShowRings;
                snap["cfg_l3_show_notif"] = CfgL3ShowNotif;
                snap["cfg_l3_split_pct"] = CfgL3SplitPct;
                snap["cfg_l3_right_split_pct"] = CfgL3RightSplitPct;
                snap["cfg_l3_text_scale"] = CfgL3TextScale;
                snap["cfg_l3_metric_scale"] = CfgL3MetricScale;
                snap["cfg_show_top_bar"] = CfgShowTopBar;
                snap["cfg_show_logo"] = CfgShowLogo;
                snap["cfg_fullscreen"] = CfgFullscreen;
                snap["cfg_bg_enabled"] = CfgBgEnabled;
                snap["cfg_bg_mode"] = CfgBgMode;
                snap["cfg_bg_opacity"] = CfgBgOpacity;
                snap["cfg_bg_blur"] = CfgBgBlur;
                snap["timer_seq"] = timerSeq;
                snap["timer_cmd"] = timerCmd;
                snap["timer_val"] = timerVal;
                snap["power_seq"] = curPowerSeq;
                snap["power_cmd"] = activePowerCmd;
                snap["notif_seq"] = notifSeq;
                snap["notif_access"] = notifAccessOk;
                snap["notif_sync"] = notifSync;
                snap["notifications"] = notifSync ? cachedNotifs : new List<Dictionary<string, object>>();
                curPushImgSeq = pushImgSeq;
                curBgSeq = bgSeq;
                snap["push_img_seq"] = curPushImgSeq;
                snap["push_img_b64"] = (curPushImgSeq != lastPushImg) ? pushImgB64 : "";
                snap["bg_seq"] = curBgSeq;
                snap["bg_b64"] = (curBgSeq != lastBg) ? bgB64 : "";
            }
            foreach (var kv in media) snap[kv.Key] = kv.Value;

            try
            {
                byte[] payload = Encoding.UTF8.GetBytes(json.Serialize(snap) + "\n");
                client.SendLine(payload);

                if (!string.IsNullOrEmpty(activePowerCmd))
                {
                    lock (stateLock)
                    {
                        if (powerSeq == curPowerSeq)
                        {
                            powerCmd = "";
                            powerExpiresAt = DateTime.MinValue;
                        }
                    }
                }
                lock (clientLock)
                {
                    if (activeClient == client)
                    {
                        clientLastThumbSeq = curThumb;
                        clientLastNotifSeq = notifSeq;
                        clientLastPushImgSeq = curPushImgSeq;
                        clientLastBgSeq = curBgSeq;
                    }
                }
            }
            catch
            {
                RemoveClient(client);
            }
        }

        private void TryAcceptClient(IClientTransport transport, string channelName)
        {
            lock (clientLock)
            {
                if (activeClient != null)
                {
                    try { transport.Dispose(); } catch { }
                    return;
                }
                activeClient = transport;
                activeChannel = channelName;
                clientLastThumbSeq = -1;
                clientLastNotifSeq = -1;
                clientLastPushImgSeq = -1;
                clientLastBgSeq = -1;
            }
            lock (stateLock)
            {
                powerCmd = "";
                powerExpiresAt = DateTime.MinValue;
            }
            SpawnThread(delegate { ClientReaderLoop(transport); });
            NotifyStateChanged();
            PushNow();
        }

        private void ClientReaderLoop(IClientTransport transport)
        {
            while (running)
            {
                try
                {
                    string line = transport.ReadLine();
                    if (line == null) break;
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    var msg = json.Deserialize<Dictionary<string, object>>(line);
                    if (msg == null || !msg.ContainsKey("cmd")) continue;
                    string cmd = msg["cmd"] != null ? msg["cmd"].ToString() : "";

                    if (cmd == "client_cfg")
                    {
                        lock (stateLock)
                        {
                            CfgSeq++;
                            ApplyCfgDict(msg);
                        }
                        SaveConfig();
                        NotifyStateChanged();
                    }
                    else if (cmd == "dismiss_notif")
                    {
                        int id = msg.ContainsKey("id") ? Convert.ToInt32(msg["id"]) : 0;
                        var l = GetAllowedListener();
                        if (l != null && id != 0) l.RemoveNotification((uint)id);
                        PushNow();
                    }
                    else if (cmd == "clear_notifs")
                    {
                        var l = GetAllowedListener();
                        if (l != null) l.ClearNotifications();
                        PushNow();
                    }
                    else if (!string.IsNullOrEmpty(cmd))
                    {
                        ExecuteMediaCommand(cmd);
                    }
                }
                catch
                {
                    break;
                }
            }
            RemoveClient(transport);
        }

        private void RemoveClient(IClientTransport transport)
        {
            bool removed = false;
            lock (clientLock)
            {
                if (activeClient == transport)
                {
                    activeClient = null;
                    activeChannel = "";
                    removed = true;
                }
            }
            try { transport.Dispose(); } catch { }
            if (removed)
            {
                lock (stateLock)
                {
                    powerCmd = "";
                    powerExpiresAt = DateTime.MinValue;
                }
                NotifyStateChanged();
            }
        }

        private void TcpAcceptLoop()
        {
            try
            {
                tcpListener = new TcpListener(IPAddress.Any, TcpPort);
                tcpListener.Start(5);
                while (running)
                {
                    TcpClient c = tcpListener.AcceptTcpClient();
                    string ipStr = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString();
                    string ch = ipStr.StartsWith("127.") ? "USB" : ("Local network (" + ipStr + ")");
                    TryAcceptClient(new TcpTransport(c), ch);
                }
            }
            catch { }
        }

        private void UdpDiscoveryLoop()
        {
            try
            {
                udpListener = new UdpClient(DiscoveryPort);
                byte[] reply = Encoding.UTF8.GetBytes("{\"port\":" + TcpPort + ",\"hostname\":\"" + Environment.MachineName + "\"}");
                while (running)
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = udpListener.Receive(ref remote);
                    if (!HasActiveClient() && data != null && Encoding.ASCII.GetString(data).Trim() == "PC_MONITOR_DISCOVER")
                    {
                        udpListener.Send(reply, reply.Length, remote);
                    }
                }
            }
            catch { }
        }

        private void BtAcceptLoop()
        {
            try
            {
                Native.WSADATA wsa;
                Native.WSAStartup(0x0202, out wsa);
                IntPtr s = Native.socket(32, 1, 3);
                if (s == IntPtr.Zero || s == new IntPtr(-1))
                {
                    BtStatus = "Bluetooth: unavailable";
                    NotifyStateChanged();
                    return;
                }
                var addr = new Native.SOCKADDR_BTH { addressFamily = 32, btAddr = 0, serviceClassId = Guid.Empty, port = 4 };
                if (Native.bind(s, ref addr, Marshal.SizeOf(typeof(Native.SOCKADDR_BTH))) != 0 || Native.listen(s, 3) != 0)
                {
                    Native.closesocket(s);
                    BtStatus = "Bluetooth: unavailable";
                    NotifyStateChanged();
                    return;
                }
                btListenSock = s;
                BtStatus = "Bluetooth: listening";
                NotifyStateChanged();

                while (running)
                {
                    IntPtr client = Native.accept(s, IntPtr.Zero, IntPtr.Zero);
                    if (client == IntPtr.Zero || client == new IntPtr(-1)) break;
                    TryAcceptClient(new BluetoothTransport(client), "Bluetooth");
                }
            }
            catch
            {
                BtStatus = "Bluetooth: unavailable";
                NotifyStateChanged();
            }
        }

        private void AutoAdbLoop()
        {
            while (running)
            {
                if (!HasActiveClient())
                {
                    try
                    {
                        var psi = new ProcessStartInfo("adb", "reverse tcp:" + TcpPort + " tcp:" + TcpPort)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        using (var p = Process.Start(psi))
                        {
                            if (p != null && p.WaitForExit(3000))
                            {
                                bool ready = (p.ExitCode == 0);
                                if (ready != UsbReady)
                                {
                                    UsbReady = ready;
                                    NotifyStateChanged();
                                }
                            }
                        }
                    }
                    catch { }
                }
                Thread.Sleep(4000);
            }
        }

        private void BroadcastLoop()
        {
            while (running)
            {
                if (HasActiveClient()) SendOnce();
                Thread.Sleep(1000);
            }
        }

        private static bool IsTailscaleOrApipa(IPAddress addr)
        {
            byte[] b = addr.GetAddressBytes();
            if (b.Length != 4) return true;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true; // CGNAT / Tailscale
            if (b[0] == 169 && b[1] == 254) return true; // APIPA
            return false;
        }

        private static bool IsVirtualAdapter(NetworkInterface ni)
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                return true;
            }
            string info = ((ni.Name ?? "") + " " + (ni.Description ?? "")).ToLowerInvariant();
            string[] skip = new string[]
            {
                "tailscale", "wireguard", "zerotier", "vpn", "virtual", "vethernet",
                "hyper-v", "wsl", "docker", "vmware", "virtualbox", "radmin", "hamachi"
            };
            for (int i = 0; i < skip.Length; i++)
            {
                if (info.Contains(skip[i])) return true;
            }
            return false;
        }

        public static string GetLocalIp()
        {
            string fallback = null;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || IsVirtualAdapter(ni)) continue;
                    var ipProps = ni.GetIPProperties();
                    bool hasGateway = ipProps.GatewayAddresses != null && ipProps.GatewayAddresses.Count > 0;
                    foreach (var ua in ipProps.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                        if (IsTailscaleOrApipa(ua.Address)) continue;
                        string ip = ua.Address.ToString();
                        if (hasGateway) return ip;
                        if (fallback == null) fallback = ip;
                    }
                }
            }
            catch { }
            return fallback ?? "127.0.0.1";
        }
    }
}
