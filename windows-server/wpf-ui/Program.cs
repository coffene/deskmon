using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PCMonitorServer
{
    internal static class NativeMethods
    {
        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attr,
            ref int attrValue,
            int attrSize
        );
    }

    internal sealed class ThemePalette
    {
        public bool IsDark;
        public Brush WindowBg;
        public Brush CardBg;
        public Brush CardBorder;
        public Brush InputBg;
        public Brush TextPrimary;
        public Brush TextSecondary;
        public Brush AccentBrush;
        public Brush AccentHoverBrush;
        public Brush OnAccentBrush;
        public Brush SecondaryBtnBg;
        public Brush SecondaryBtnHoverBg;

        public static ThemePalette LoadFromWindows()
        {
            bool isDark = true;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AppsUseLightTheme");
                        if (val is int)
                        {
                            isDark = ((int)val) == 0;
                        }
                    }
                }
            }
            catch { }

            Color accent = Color.FromRgb(0, 120, 212);
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\DWM"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AccentColor");
                        if (val is int)
                        {
                            int abgr = (int)val;
                            byte r = (byte)(abgr & 0xFF);
                            byte g = (byte)((abgr >> 8) & 0xFF);
                            byte b = (byte)((abgr >> 16) & 0xFF);
                            accent = Color.FromRgb(r, g, b);
                        }
                    }
                }
            }
            catch { }

            if (isDark && (accent.R + accent.G + accent.B) < 220)
            {
                accent = Color.FromRgb(96, 205, 255);
            }

            var p = new ThemePalette();
            p.IsDark = isDark;
            p.AccentBrush = Freeze(new SolidColorBrush(accent));
            Color hover = isDark
                ? Color.FromRgb(
                    (byte)Math.Min(255, accent.R + 20),
                    (byte)Math.Min(255, accent.G + 20),
                    (byte)Math.Min(255, accent.B + 20))
                : Color.FromRgb(
                    (byte)Math.Max(0, accent.R - 18),
                    (byte)Math.Max(0, accent.G - 18),
                    (byte)Math.Max(0, accent.B - 18));
            p.AccentHoverBrush = Freeze(new SolidColorBrush(hover));

            double lum = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B);
            p.OnAccentBrush = Freeze(new SolidColorBrush(lum > 155 ? Color.FromRgb(16, 16, 16) : Colors.White));

            if (isDark)
            {
                p.WindowBg = Freeze(new SolidColorBrush(Color.FromRgb(32, 32, 32)));
                p.CardBg = Freeze(new SolidColorBrush(Color.FromRgb(43, 43, 43)));
                p.CardBorder = Freeze(new SolidColorBrush(Color.FromRgb(58, 58, 58)));
                p.InputBg = Freeze(new SolidColorBrush(Color.FromRgb(36, 36, 36)));
                p.TextPrimary = Freeze(new SolidColorBrush(Color.FromRgb(243, 243, 243)));
                p.TextSecondary = Freeze(new SolidColorBrush(Color.FromRgb(176, 176, 176)));
                p.SecondaryBtnBg = Freeze(new SolidColorBrush(Color.FromRgb(54, 54, 54)));
                p.SecondaryBtnHoverBg = Freeze(new SolidColorBrush(Color.FromRgb(68, 68, 68)));
            }
            else
            {
                p.WindowBg = Freeze(new SolidColorBrush(Color.FromRgb(243, 243, 243)));
                p.CardBg = Freeze(new SolidColorBrush(Color.FromRgb(255, 255, 255)));
                p.CardBorder = Freeze(new SolidColorBrush(Color.FromRgb(229, 229, 229)));
                p.InputBg = Freeze(new SolidColorBrush(Color.FromRgb(255, 255, 255)));
                p.TextPrimary = Freeze(new SolidColorBrush(Color.FromRgb(27, 27, 27)));
                p.TextSecondary = Freeze(new SolidColorBrush(Color.FromRgb(97, 97, 97)));
                p.SecondaryBtnBg = Freeze(new SolidColorBrush(Color.FromRgb(249, 249, 249)));
                p.SecondaryBtnHoverBg = Freeze(new SolidColorBrush(Color.FromRgb(240, 240, 240)));
            }
            return p;
        }

        private static SolidColorBrush Freeze(SolidColorBrush b)
        {
            b.Freeze();
            return b;
        }
    }

    internal sealed class DuePickerWindow : Window
    {
        public string SelectedDueText { get; private set; }
        public bool Confirmed { get; private set; }

        private readonly Calendar calendar;
        private readonly CheckBox chkTime;
        private readonly ComboBox cbHour;
        private readonly ComboBox cbMin;

        public DuePickerWindow(Window owner, ThemePalette theme, DateTime? initialDate, bool includeTime, int initHour, int initMin)
        {
            Owner = owner;
            Title = "Due time";
            Width = 280;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = theme.WindowBg;
            FontFamily = new FontFamily("Segoe UI");

            SourceInitialized += delegate
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(this).Handle;
                    int dark = theme.IsDark ? 1 : 0;
                    if (NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                    {
                        NativeMethods.DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
                    }
                }
                catch { }
            };

            var root = new StackPanel();
            root.Margin = new Thickness(14);

            calendar = new Calendar();
            calendar.SelectionMode = CalendarSelectionMode.SingleDate;
            calendar.SelectedDate = initialDate ?? DateTime.Today;
            calendar.DisplayDate = calendar.SelectedDate.Value;
            calendar.HorizontalAlignment = HorizontalAlignment.Center;
            root.Children.Add(calendar);

            chkTime = new CheckBox();
            chkTime.Content = "Include time";
            chkTime.IsChecked = includeTime;
            chkTime.Foreground = theme.TextPrimary;
            chkTime.Margin = new Thickness(0, 10, 0, 6);
            root.Children.Add(chkTime);

            var timeRow = new StackPanel();
            timeRow.Orientation = Orientation.Horizontal;
            timeRow.Margin = new Thickness(0, 0, 0, 12);

            cbHour = new ComboBox();
            cbHour.Width = 68;
            for (int h = 0; h < 24; h++)
            {
                cbHour.Items.Add(h.ToString("00"));
            }
            cbHour.SelectedIndex = Math.Max(0, Math.Min(23, initHour));

            var colon = new TextBlock();
            colon.Text = ":";
            colon.Foreground = theme.TextPrimary;
            colon.FontSize = 14;
            colon.VerticalAlignment = VerticalAlignment.Center;
            colon.Margin = new Thickness(6, 0, 6, 0);

            cbMin = new ComboBox();
            cbMin.Width = 68;
            for (int m = 0; m < 60; m += 5)
            {
                cbMin.Items.Add(m.ToString("00"));
            }
            cbMin.SelectedIndex = Math.Max(0, Math.Min(11, initMin / 5));

            timeRow.Children.Add(cbHour);
            timeRow.Children.Add(colon);
            timeRow.Children.Add(cbMin);
            timeRow.IsEnabled = includeTime;
            timeRow.Opacity = includeTime ? 1.0 : 0.4;
            root.Children.Add(timeRow);

            chkTime.Checked += delegate
            {
                timeRow.IsEnabled = true;
                timeRow.Opacity = 1.0;
            };
            chkTime.Unchecked += delegate
            {
                timeRow.IsEnabled = false;
                timeRow.Opacity = 0.4;
            };

            var btnRow = new StackPanel();
            btnRow.Orientation = Orientation.Horizontal;
            btnRow.HorizontalAlignment = HorizontalAlignment.Right;

            var btnClear = new Button();
            btnClear.Content = "Clear";
            btnClear.Padding = new Thickness(12, 5, 12, 5);
            btnClear.Margin = new Thickness(0, 0, 8, 0);
            btnClear.Click += delegate
            {
                SelectedDueText = "";
                Confirmed = true;
                Close();
            };

            var btnApply = new Button();
            btnApply.Content = "Apply";
            btnApply.Padding = new Thickness(14, 5, 14, 5);
            btnApply.Click += delegate
            {
                DateTime dt = calendar.SelectedDate ?? DateTime.Today;
                string datePart = dt.ToString("dd/MM");
                if (chkTime.IsChecked == true)
                {
                    string hh = cbHour.SelectedItem != null ? cbHour.SelectedItem.ToString() : "00";
                    string mm = cbMin.SelectedItem != null ? cbMin.SelectedItem.ToString() : "00";
                    SelectedDueText = datePart + " " + hh + ":" + mm;
                }
                else
                {
                    SelectedDueText = datePart;
                }
                Confirmed = true;
                Close();
            };

            btnRow.Children.Add(btnClear);
            btnRow.Children.Add(btnApply);
            root.Children.Add(btnRow);

            Content = root;
        }
    }

    internal sealed class Layout3ResizeWindow : Window
    {
        private readonly ThemePalette theme;
        private readonly Grid previewGrid = new Grid();
        private readonly ColumnDefinition colLeft = new ColumnDefinition();
        private readonly ColumnDefinition colRight = new ColumnDefinition();
        private readonly RowDefinition rowTop = new RowDefinition();
        private readonly RowDefinition rowBottom = new RowDefinition();
        private readonly Border boxLeft;
        private readonly Border boxPlaying;
        private readonly Border boxNotif;
        private readonly TextBlock lblLeftInfo = new TextBlock();
        private readonly TextBlock lblPlayingInfo = new TextBlock();
        private readonly TextBlock lblNotifInfo = new TextBlock();

        private Slider sldSplit;
        private Slider sldStack;
        private Slider sldText;
        private Slider sldMetric;
        private TextBlock tvSplitVal;
        private TextBlock tvStackVal;
        private TextBlock tvTextVal;
        private TextBlock tvMetricVal;
        private bool suppressEvents = false;
        private readonly DispatcherTimer syncDebounce = new DispatcherTimer();

        public int SplitPct { get; private set; }
        public int RightSplitPct { get; private set; }
        public int TextScale { get; private set; }
        public int MetricScale { get; private set; }
        public Action OnChanged;

        public Layout3ResizeWindow(
            Window owner,
            ThemePalette palette,
            int splitPct,
            int rightSplitPct,
            int textScale,
            int metricScale,
            bool showTaskLeft,
            bool showPlaying,
            bool showNotif,
            bool playingLeft)
        {
            Owner = owner;
            theme = palette;
            SplitPct = Math.Max(37, Math.Min(66, splitPct));
            RightSplitPct = Math.Max(38, Math.Min(65, rightSplitPct));
            TextScale = Math.Max(85, Math.Min(130, textScale));
            MetricScale = Math.Max(80, Math.Min(125, metricScale));

            Title = "Layout 3 resize";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = theme.WindowBg;
            FontFamily = new FontFamily("Segoe UI");
            syncDebounce.Interval = TimeSpan.FromMilliseconds(90);
            syncDebounce.Tick += delegate
            {
                syncDebounce.Stop();
                if (OnChanged != null) OnChanged();
            };
            Closed += delegate
            {
                if (syncDebounce.IsEnabled)
                {
                    syncDebounce.Stop();
                    if (OnChanged != null) OnChanged();
                }
            };

            SourceInitialized += delegate
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(this).Handle;
                    int dark = theme.IsDark ? 1 : 0;
                    if (NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                    {
                        NativeMethods.DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
                    }
                }
                catch { }
            };

            var root = new StackPanel { Margin = new Thickness(14) };

            // Visual proportion diagram so resize effect is immediately clear
            var previewBorder = new Border
            {
                Background = theme.InputBg,
                BorderBrush = theme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8),
                Height = 112,
                Margin = new Thickness(0, 0, 0, 12)
            };

            previewGrid.ColumnDefinitions.Add(colLeft);
            previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            previewGrid.ColumnDefinitions.Add(colRight);

            var rightGrid = new Grid();
            rightGrid.RowDefinitions.Add(rowTop);
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(showPlaying && showNotif ? 6 : 0) });
            rightGrid.RowDefinitions.Add(rowBottom);

            boxLeft = CreatePreviewBox(lblLeftInfo, showTaskLeft);
            boxPlaying = CreatePreviewBox(lblPlayingInfo, showPlaying);
            boxNotif = CreatePreviewBox(lblNotifInfo, showNotif);

            Grid.SetRow(boxPlaying, 0);
            Grid.SetRow(boxNotif, 2);
            rightGrid.Children.Add(boxPlaying);
            rightGrid.Children.Add(boxNotif);

            if (playingLeft)
            {
                Grid.SetColumn(rightGrid, 0);
                Grid.SetColumn(boxLeft, 2);
            }
            else
            {
                Grid.SetColumn(boxLeft, 0);
                Grid.SetColumn(rightGrid, 2);
            }
            previewGrid.Children.Add(boxLeft);
            previewGrid.Children.Add(rightGrid);
            previewBorder.Child = previewGrid;
            root.Children.Add(previewBorder);

            bool hasLeft = showTaskLeft;
            bool hasRight = showPlaying || showNotif;
            AddSliderRow(root, "Task pane <-> Right pane", 37, 66, SplitPct, hasLeft && hasRight, out sldSplit, out tvSplitVal,
                delegate(int v) { SplitPct = v; UpdatePreview(playingLeft, showPlaying, showNotif); NotifyChanged(); });
            AddSliderRow(root, "Now Playing <-> Notifications", 38, 65, RightSplitPct, showPlaying && showNotif, out sldStack, out tvStackVal,
                delegate(int v) { RightSplitPct = v; UpdatePreview(playingLeft, showPlaying, showNotif); NotifyChanged(); });
            AddSliderRow(root, "Task & Notif text size", 85, 130, TextScale, true, out sldText, out tvTextVal,
                delegate(int v) { TextScale = v; NotifyChanged(); });
            AddSliderRow(root, "Rings & Timer size", 80, 125, MetricScale, true, out sldMetric, out tvMetricVal,
                delegate(int v) { MetricScale = v; NotifyChanged(); });

            var btnRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var btnReset = CreatePopupButton("Reset defaults", false);
            DockPanel.SetDock(btnReset, Dock.Left);
            btnReset.Click += delegate
            {
                suppressEvents = true;
                SplitPct = 54;
                RightSplitPct = 55;
                TextScale = 100;
                MetricScale = 100;
                sldSplit.Value = SplitPct;
                sldStack.Value = RightSplitPct;
                sldText.Value = TextScale;
                sldMetric.Value = MetricScale;
                tvSplitVal.Text = SplitPct + "%";
                tvStackVal.Text = RightSplitPct + "%";
                tvTextVal.Text = TextScale + "%";
                tvMetricVal.Text = MetricScale + "%";
                suppressEvents = false;
                UpdatePreview(playingLeft, showPlaying, showNotif);
                NotifyChanged();
            };

            var btnClose = CreatePopupButton("Close", true);
            btnClose.HorizontalAlignment = HorizontalAlignment.Right;
            btnClose.Click += delegate { Close(); };

            btnRow.Children.Add(btnReset);
            btnRow.Children.Add(btnClose);
            root.Children.Add(btnRow);

            UpdatePreview(playingLeft, showPlaying, showNotif);
            Content = root;
        }

        private Border CreatePreviewBox(TextBlock label, bool visible)
        {
            label.FontSize = 11;
            label.Foreground = theme.TextPrimary;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.TextAlignment = TextAlignment.Center;
            var bd = new Border
            {
                Background = theme.SecondaryBtnBg,
                BorderBrush = theme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = label,
                Visibility = visible ? Visibility.Visible : Visibility.Collapsed
            };
            return bd;
        }

        private void AddSliderRow(
            StackPanel parent,
            string title,
            int minVal,
            int maxVal,
            int initial,
            bool enabled,
            out Slider slider,
            out TextBlock valLabel,
            Action<int> onVal)
        {
            var hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var lbl = new TextBlock
            {
                Text = title,
                FontSize = 12,
                Foreground = enabled ? theme.TextPrimary : theme.TextSecondary
            };
            DockPanel.SetDock(lbl, Dock.Left);

            valLabel = new TextBlock
            {
                Text = initial + "%",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = theme.AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            hdr.Children.Add(lbl);
            hdr.Children.Add(valLabel);
            parent.Children.Add(hdr);

            var capturedVal = valLabel;
            slider = new Slider
            {
                Minimum = minVal,
                Maximum = maxVal,
                Value = initial,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                IsEnabled = enabled,
                Opacity = enabled ? 1.0 : 0.4,
                Margin = new Thickness(0, 0, 0, 10)
            };
            slider.ValueChanged += delegate(object s, RoutedPropertyChangedEventArgs<double> args)
            {
                int v = (int)Math.Round(args.NewValue);
                capturedVal.Text = v + "%";
                if (!suppressEvents)
                {
                    onVal(v);
                }
            };
            parent.Children.Add(slider);
        }

        private void UpdatePreview(bool playingLeft, bool showPlaying, bool showNotif)
        {
            if (playingLeft)
            {
                colLeft.Width = new GridLength(100 - SplitPct, GridUnitType.Star);
                colRight.Width = new GridLength(SplitPct, GridUnitType.Star);
            }
            else
            {
                colLeft.Width = new GridLength(SplitPct, GridUnitType.Star);
                colRight.Width = new GridLength(100 - SplitPct, GridUnitType.Star);
            }
            rowTop.Height = new GridLength(showPlaying ? (showNotif ? RightSplitPct : 100) : 0, GridUnitType.Star);
            rowBottom.Height = new GridLength(showNotif ? (showPlaying ? 100 - RightSplitPct : 100) : 0, GridUnitType.Star);

            lblLeftInfo.Text = "Active Task / Metrics\n" + SplitPct + "%";
            lblPlayingInfo.Text = showPlaying ? ("Now Playing " + (showNotif ? RightSplitPct + "%" : "")) : "";
            lblNotifInfo.Text = showNotif ? ("OS Notif " + (showPlaying ? (100 - RightSplitPct) + "%" : "")) : "";
        }

        private void NotifyChanged()
        {
            syncDebounce.Stop();
            syncDebounce.Start();
        }

        private Button CreatePopupButton(string text, bool primary)
        {
            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(12, 5, 12, 5),
                Foreground = primary ? theme.OnAccentBrush : theme.TextPrimary,
                Background = primary ? theme.AccentBrush : theme.SecondaryBtnBg,
                BorderBrush = theme.CardBorder,
                BorderThickness = new Thickness(primary ? 0 : 1),
                Cursor = Cursors.Hand
            };
            return btn;
        }
    }

    public sealed class ServerWindow : Window
    {
        private readonly ThemePalette theme;
        private readonly FontFamily uiFont = new FontFamily("Segoe UI");
        private readonly FontFamily iconFont = new FontFamily("Segoe MDL2 Assets");

        private StackPanel rowActive;
        private TextBlock tvActive;
        private TextBlock tvPausedHint;
        private StackPanel rowLocalNet;
        private TextBlock tvLocalNet;
        private StackPanel rowUsb;
        private TextBlock tvUsb;
        private StackPanel rowBluetooth;
        private TextBlock tvBluetooth;
        private StackPanel rowDisabled;
        private Button btnServerPower;

        private TextBox tbTask;
        private Button btnDuePicker;
        private TextBox tbNote;
        private TextBox tbPort;
        private Border portBoxBorder;
        private Button btnApplyPort;
        private TextBlock tvFeedback;

        private string currentDueText = "";
        private DateTime? dueDateCache = null;
        private bool dueIncludeTimeCache = false;
        private int dueHourCache = 18;
        private int dueMinCache = 0;

        private readonly Button[] btnLayouts = new Button[3];
        private readonly Button[] btnFonts = new Button[4];
        private CheckBox chkL3Task;
        private CheckBox chkL3Timer;
        private CheckBox chkL3Rings;
        private CheckBox chkPlaying;
        private CheckBox chkL3Notif;
        private CheckBox chkShowTopBar;
        private CheckBox chkShowLogo;
        private CheckBox chkSwapSide;
        private CheckBox chkFullscreen;
        private CheckBox chkStartWithWin;
        private CheckBox chkShowOnStart;
        private bool suppressCheckEvents = false;
        private Button btnTimeMode;
        private Button btnL3Resize;
        private Button btnPushImage;
        private Button btnBackground;
        private readonly List<Button> timerButtons = new List<Button>();
        private readonly List<Button> powerButtons = new List<Button>();

        private bool serverEnabled = true;
        private bool startWithWindows = false;
        private bool showWindowOnStartup = true;
        private bool hasActiveClient = false;
        public bool ShouldShowOnStartup { get { return showWindowOnStartup; } }
        private int remoteLayout = 0;
        private int remoteFont = 0;
        private bool remoteShowPlaying = true;
        private bool remotePlayingLeft = false;
        private int remoteTimeMode = 0;
        private bool l3ShowTask = true;
        private bool l3ShowTimer = true;
        private bool l3ShowRings = true;
        private bool l3ShowNotif = true;
        private int l3SplitPct = 54;
        private int l3RightSplitPct = 55;
        private int l3TextScale = 100;
        private int l3MetricScale = 100;
        private bool remoteShowTopBar = true;
        private bool remoteShowLogo = true;
        private bool remoteFullscreen = false;

        private MonitorServer backend;
        private string localIp;
        private System.Windows.Forms.NotifyIcon trayIcon;
        private System.Windows.Forms.ToolStripMenuItem trayToggleItem;
        private bool shuttingDown = false;
        private readonly string baseDir;

        public ServerWindow(System.Threading.EventWaitHandle wakeSignal)
        {
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            localIp = MonitorServer.GetLocalIp();
            theme = ThemePalette.LoadFromWindows();

            Title = "Deskmon server";
            Width = 820;
            Height = 640;
            SizeToContent = SizeToContent.Manual;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = theme.WindowBg;
            FontFamily = uiFont;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            SourceInitialized += OnWindowSourceInitialized;
            Closing += OnWindowClosing;
            StateChanged += delegate
            {
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                    ShowInTaskbar = false;
                    Hide();
                    Native.TrimWorkingSet();
                }
            };
            ContentRendered += delegate
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    System.Threading.Thread.Sleep(2000);
                    Native.TrimWorkingSet();
                });
            };

            SetupTrayIcon();
            BuildUi();
            StartBackend(wakeSignal);
        }

        private void SetupTrayIcon()
        {
            var appIcon = CreateTrayIcon();
            try
            {
                if (appIcon != null)
                {
                    Icon = Imaging.CreateBitmapSourceFromHIcon(
                        appIcon.Handle,
                        Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()
                    );
                }
            }
            catch { }

            trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = appIcon,
                Text = "Deskmon server",
                Visible = true
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            var openItem = new System.Windows.Forms.ToolStripMenuItem("Open");
            openItem.Click += delegate { Dispatcher.BeginInvoke(new Action(RestoreFromTray)); };
            trayToggleItem = new System.Windows.Forms.ToolStripMenuItem("Disable server");
            trayToggleItem.Click += delegate { Dispatcher.BeginInvoke(new Action(ToggleServerEnabled)); };
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
            exitItem.Click += delegate { Dispatcher.BeginInvoke(new Action(Close)); };
            menu.Items.Add(openItem);
            menu.Items.Add(trayToggleItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);
            trayIcon.ContextMenuStrip = menu;
            trayIcon.MouseClick += delegate(object s, System.Windows.Forms.MouseEventArgs me)
            {
                if (me.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    Dispatcher.BeginInvoke(new Action(RestoreFromTray));
                }
            };
        }

        private void RestoreFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private static System.Drawing.Icon CreateTrayIcon()
        {
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    var assoc = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (assoc != null) return assoc;
                }
            }
            catch { }

            using (var bmp = new System.Drawing.Bitmap(16, 16))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x3B, 0x9E, 0xFF), 1.8f))
            using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x3B, 0x9E, 0xFF)))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                g.DrawRectangle(pen, 2, 3, 12, 8);
                g.FillRectangle(fill, 6, 12, 4, 2);
                g.FillRectangle(fill, 4, 7, 2, 3);
                g.FillRectangle(fill, 7, 5, 2, 5);
                g.FillRectangle(fill, 10, 6, 2, 4);
                return System.Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }

        private void OnWindowSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int dark = theme.IsDark ? 1 : 0;
                if (NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                {
                    NativeMethods.DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
                }
            }
            catch { }
        }

        private void BuildUi()
        {
            var rootGrid = new Grid();
            rootGrid.Margin = new Thickness(14, 12, 14, 14);
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(365) });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var leftCol = new Grid();
            leftCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            leftCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(leftCol, 0);

            // Card 1: Connection with prominent Enable/Disable server button
            var statusStack = new StackPanel();
            statusStack.MinHeight = 66;
            rowActive = AddIconInfoRow(statusStack, "\uE73E", "Active connection", out tvActive);
            rowActive.Visibility = Visibility.Collapsed;

            tvPausedHint = new TextBlock();
            tvPausedHint.Text = "Other channels paused while connected";
            tvPausedHint.FontSize = 11.5;
            tvPausedHint.Foreground = theme.TextSecondary;
            tvPausedHint.Margin = new Thickness(23, 2, 0, 2);
            tvPausedHint.Visibility = Visibility.Collapsed;
            statusStack.Children.Add(tvPausedHint);

            rowLocalNet = AddIconInfoRow(statusStack, "\uE774", "Local network: checking", out tvLocalNet);
            rowUsb = AddIconInfoRow(statusStack, "\uE88E", "USB: checking", out tvUsb);
            rowBluetooth = AddIconInfoRow(statusStack, "\uE702", "Bluetooth: starting", out tvBluetooth);

            TextBlock dummyDisabled;
            rowDisabled = AddIconInfoRow(statusStack, "\uE711", "Server disabled", out dummyDisabled);
            rowDisabled.Visibility = Visibility.Collapsed;

            var portRow = new Grid();
            portRow.Margin = new Thickness(0, 8, 0, 0);
            portRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            portRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            portRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lblPort = new TextBlock();
            lblPort.Text = "Port";
            lblPort.FontSize = 12;
            lblPort.Foreground = theme.TextSecondary;
            lblPort.VerticalAlignment = VerticalAlignment.Center;
            lblPort.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(lblPort, 0);

            tbPort = new TextBox();
            tbPort.Text = MonitorServer.DefaultTcpPort.ToString();
            tbPort.FontSize = 12.5;
            tbPort.Foreground = theme.TextPrimary;
            tbPort.Background = Brushes.Transparent;
            tbPort.BorderThickness = new Thickness(0);
            tbPort.Padding = new Thickness(8, 5, 8, 5);
            tbPort.VerticalContentAlignment = VerticalAlignment.Center;
            tbPort.CaretBrush = theme.TextPrimary;
            tbPort.KeyDown += delegate(object s, KeyEventArgs args)
            {
                if (args.Key == Key.Enter) ApplyServerPort();
            };

            portBoxBorder = new Border();
            portBoxBorder.Background = theme.InputBg;
            portBoxBorder.BorderBrush = theme.CardBorder;
            portBoxBorder.BorderThickness = new Thickness(1);
            portBoxBorder.CornerRadius = new CornerRadius(4);
            portBoxBorder.Child = tbPort;
            Grid.SetColumn(portBoxBorder, 1);

            btnApplyPort = CreateFluentButton("Apply", false, new Thickness(14, 5, 14, 5));
            btnApplyPort.FontSize = 12;
            btnApplyPort.FontWeight = FontWeights.SemiBold;
            btnApplyPort.Margin = new Thickness(8, 0, 0, 0);
            btnApplyPort.Click += delegate { ApplyServerPort(); };
            Grid.SetColumn(btnApplyPort, 2);

            portRow.Children.Add(lblPort);
            portRow.Children.Add(portBoxBorder);
            portRow.Children.Add(btnApplyPort);

            var srvChkRow = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2, Margin = new Thickness(0, 8, 0, 0) };
            chkStartWithWin = CreateMediaCheckBox("Start with Windows", delegate { ToggleStartWithWindows(); });
            chkShowOnStart = CreateMediaCheckBox("Show window on startup", delegate { ToggleShowWindowOnStartup(); });
            srvChkRow.Children.Add(chkStartWithWin);
            srvChkRow.Children.Add(chkShowOnStart);

            btnServerPower = CreateFluentButton("Disable server", false, new Thickness(14, 7, 14, 7));
            btnServerPower.FontSize = 13;
            btnServerPower.FontWeight = FontWeights.SemiBold;
            btnServerPower.HorizontalAlignment = HorizontalAlignment.Stretch;
            btnServerPower.Margin = new Thickness(0, 8, 0, 0);
            btnServerPower.Click += delegate { ToggleServerEnabled(); };

            var connCardContent = new StackPanel();
            connCardContent.Children.Add(statusStack);
            connCardContent.Children.Add(portRow);
            connCardContent.Children.Add(srvChkRow);
            connCardContent.Children.Add(btnServerPower);
            var connCard = CreateTitledCard("Server & connection", connCardContent, new Thickness(0, 0, 0, 10));
            Grid.SetRow(connCard, 0);
            leftCol.Children.Add(connCard);

            // Card 2 (Right Column): Current task + spacious Note with formatting toolbar
            var taskGrid = new Grid();
            taskGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            taskGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            taskGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            taskGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var topInputGrid = new Grid();
            topInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            topInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });

            var colTask = new StackPanel();
            Grid.SetColumn(colTask, 0);
            colTask.Children.Add(CreateFieldLabel("Task"));
            tbTask = CreateSingleLineBox();
            colTask.Children.Add(tbTask);
            topInputGrid.Children.Add(colTask);

            var colDue = new StackPanel();
            Grid.SetColumn(colDue, 2);
            colDue.Children.Add(CreateFieldLabel("Due time"));
            btnDuePicker = CreateFluentButton("Select due", false, new Thickness(10, 6, 10, 6));
            btnDuePicker.FontSize = 12.5;
            btnDuePicker.FontWeight = FontWeights.Normal;
            btnDuePicker.HorizontalContentAlignment = HorizontalAlignment.Left;
            btnDuePicker.Click += delegate { OpenDuePicker(); };
            colDue.Children.Add(btnDuePicker);
            topInputGrid.Children.Add(colDue);

            Grid.SetRow(topInputGrid, 0);
            taskGrid.Children.Add(topInputGrid);

            var noteHeader = new DockPanel();
            noteHeader.Margin = new Thickness(0, 10, 0, 4);
            var lblNote = CreateFieldLabel("Note");
            lblNote.Margin = new Thickness(0);
            lblNote.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(lblNote, Dock.Left);

            var fmtBar = new StackPanel();
            fmtBar.Orientation = Orientation.Horizontal;
            fmtBar.HorizontalAlignment = HorizontalAlignment.Right;
            AddFormatButton(fmtBar, "B", FontWeights.Bold, FontStyles.Normal, delegate { WrapNoteSelection("**", "**", "bold"); });
            AddFormatButton(fmtBar, "I", FontWeights.Normal, FontStyles.Italic, delegate { WrapNoteSelection("*", "*", "italic"); });
            AddFormatButton(fmtBar, "S", FontWeights.Normal, FontStyles.Normal, delegate { WrapNoteSelection("~~", "~~", "strike"); });
            AddFormatButton(fmtBar, "\u2022", FontWeights.Bold, FontStyles.Normal, delegate { PrefixNoteLines("\u2022 "); });
            AddFormatButton(fmtBar, "\u2610", FontWeights.Normal, FontStyles.Normal, delegate { PrefixNoteLines("\u2610 "); });

            noteHeader.Children.Add(lblNote);
            noteHeader.Children.Add(fmtBar);
            Grid.SetRow(noteHeader, 1);
            taskGrid.Children.Add(noteHeader);

            tbNote = new TextBox();
            tbNote.FontSize = 13;
            tbNote.Foreground = theme.TextPrimary;
            tbNote.Background = theme.InputBg;
            tbNote.BorderBrush = theme.CardBorder;
            tbNote.BorderThickness = new Thickness(1);
            tbNote.Padding = new Thickness(10, 8, 10, 8);
            tbNote.CaretBrush = theme.TextPrimary;
            tbNote.AcceptsReturn = true;
            tbNote.TextWrapping = TextWrapping.Wrap;
            tbNote.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            tbNote.VerticalContentAlignment = VerticalAlignment.Top;
            tbNote.MinHeight = 135;
            Grid.SetRow(tbNote, 2);
            taskGrid.Children.Add(tbNote);

            var btnRow = new StackPanel();
            btnRow.Orientation = Orientation.Horizontal;
            btnRow.Margin = new Thickness(0, 10, 0, 0);

            var btnUpdate = CreateFluentButton("Update Task", true, new Thickness(14, 6, 14, 6));
            btnUpdate.Click += delegate { SendTaskToBackend(); };
            btnRow.Children.Add(btnUpdate);

            var btnRemove = CreateFluentButton("Remove Task", false, new Thickness(12, 6, 12, 6));
            btnRemove.Margin = new Thickness(8, 0, 0, 0);
            btnRemove.Click += delegate { RemoveTaskFromBackend(); };
            btnRow.Children.Add(btnRemove);

            tvFeedback = new TextBlock();
            tvFeedback.FontSize = 12;
            tvFeedback.Foreground = theme.TextSecondary;
            tvFeedback.VerticalAlignment = VerticalAlignment.Center;
            tvFeedback.Margin = new Thickness(10, 0, 0, 0);
            btnRow.Children.Add(tvFeedback);

            Grid.SetRow(btnRow, 3);
            taskGrid.Children.Add(btnRow);

            var taskCard = CreateStretchTitledCard("Current task", taskGrid, new Thickness(0));
            Grid.SetRow(taskCard, 1);

            // Card 2 (Left Column bottom): Display & layout (Layout + Font + Panels CheckBoxes + Display action buttons)
            var displayStack = new StackPanel();

            displayStack.Children.Add(CreateCategoryLabel("Layout"));
            var catLayout = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            btnLayouts[0] = AddSmallButton(catLayout, "Layout 1", delegate { SetRemoteLayout(0); });
            btnLayouts[1] = AddSmallButton(catLayout, "Layout 2", delegate { SetRemoteLayout(1); });
            btnLayouts[2] = AddSmallButton(catLayout, "Layout 3", delegate { SetRemoteLayout(2); });
            displayStack.Children.Add(catLayout);

            displayStack.Children.Add(CreateCategoryLabel("Font"));
            var catFont = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            btnFonts[0] = AddSmallButton(catFont, "Fira Sans", delegate { SetRemoteFont(0); });
            btnFonts[1] = AddSmallButton(catFont, "IBM Plex", delegate { SetRemoteFont(1); });
            btnFonts[2] = AddSmallButton(catFont, "Barlow", delegate { SetRemoteFont(2); });
            btnFonts[3] = AddSmallButton(catFont, "Roboto", delegate { SetRemoteFont(3); });
            displayStack.Children.Add(catFont);

            displayStack.Children.Add(CreateCategoryLabel("Panels & view"));
            var chkGrid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(0, 2, 0, 8) };
            chkL3Task = CreateMediaCheckBox("Task", delegate { ToggleL3Component(ref l3ShowTask, "Task"); });
            chkL3Timer = CreateMediaCheckBox("Timer", delegate { ToggleL3Component(ref l3ShowTimer, "Timer"); });
            chkL3Rings = CreateMediaCheckBox("Rings", delegate { ToggleL3Component(ref l3ShowRings, "Rings"); });
            chkPlaying = CreateMediaCheckBox("Playing", delegate { TogglePlayingOption(); });
            chkL3Notif = CreateMediaCheckBox("Notif", delegate { ToggleL3Component(ref l3ShowNotif, "Notifications"); });
            chkShowTopBar = CreateMediaCheckBox("Top bar", delegate
            {
                remoteShowTopBar = !remoteShowTopBar;
                UpdateOptionHighlights();
                SendRemoteConfig();
                ShowFeedback(remoteShowTopBar ? "Top bar on" : "Top bar off");
            });
            chkShowLogo = CreateMediaCheckBox("Logo", delegate
            {
                remoteShowLogo = !remoteShowLogo;
                UpdateOptionHighlights();
                SendRemoteConfig();
                ShowFeedback(remoteShowLogo ? "Logo on" : "Logo off");
            });
            chkSwapSide = CreateMediaCheckBox("Swap side", delegate
            {
                remotePlayingLeft = !remotePlayingLeft;
                UpdateOptionHighlights();
                SendRemoteConfig();
                ShowFeedback("Swapped side");
            });
            chkFullscreen = CreateMediaCheckBox("Fullscreen", delegate
            {
                remoteFullscreen = !remoteFullscreen;
                UpdateOptionHighlights();
                SendRemoteConfig();
                ShowFeedback(remoteFullscreen ? "Fullscreen on" : "Fullscreen off");
            });
            chkGrid.Children.Add(chkL3Task);
            chkGrid.Children.Add(chkL3Timer);
            chkGrid.Children.Add(chkL3Rings);
            chkGrid.Children.Add(chkPlaying);
            chkGrid.Children.Add(chkL3Notif);
            chkGrid.Children.Add(chkShowTopBar);
            chkGrid.Children.Add(chkShowLogo);
            chkGrid.Children.Add(chkSwapSide);
            chkGrid.Children.Add(chkFullscreen);
            displayStack.Children.Add(chkGrid);

            var dispBtnGrid = new Grid();
            dispBtnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dispBtnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            dispBtnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dispBtnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            dispBtnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            btnTimeMode = CreateActionGridButton("Time mode: 1", delegate
            {
                remoteTimeMode = (remoteTimeMode + 1) % 4;
                UpdateOptionHighlights();
                SendRemoteConfig();
                ShowFeedback("Time mode " + (remoteTimeMode + 1));
            });
            Grid.SetColumn(btnTimeMode, 0);
            dispBtnGrid.Children.Add(btnTimeMode);

            btnL3Resize = CreateActionGridButton("Resize L3...", delegate { OpenLayout3ResizePopup(); });
            Grid.SetColumn(btnL3Resize, 2);
            dispBtnGrid.Children.Add(btnL3Resize);

            btnBackground = CreateActionGridButton("Background...", delegate { OpenImagePicker(true); });
            Grid.SetColumn(btnBackground, 4);
            dispBtnGrid.Children.Add(btnBackground);

            displayStack.Children.Add(dispBtnGrid);
            var optCard = CreateTitledCard("Display & layout", displayStack, new Thickness(0));
            Grid.SetRow(optCard, 1);
            leftCol.Children.Add(optCard);

            // Card 3 (Right Column top): Quick actions (Timer & Pomodoro + Push image & Device power)
            var quickStack = new StackPanel();
            quickStack.Children.Add(CreateCategoryLabel("Timer & Pomodoro"));
            var catTimer = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            timerButtons.Add(AddSmallButton(catTimer, "10m", delegate { SendRemoteTimer("set_min", 10); }));
            timerButtons.Add(AddSmallButton(catTimer, "15m", delegate { SendRemoteTimer("set_min", 15); }));
            timerButtons.Add(AddSmallButton(catTimer, "20m", delegate { SendRemoteTimer("set_min", 20); }));
            timerButtons.Add(AddSmallButton(catTimer, "25m", delegate { SendRemoteTimer("set_min", 25); }));
            timerButtons.Add(AddSmallButton(catTimer, "Pomo 2", delegate { SendRemoteTimer("pomodoro", 2); }));
            timerButtons.Add(AddSmallButton(catTimer, "Pomo 4", delegate { SendRemoteTimer("pomodoro", 4); }));
            timerButtons.Add(AddSmallButton(catTimer, "Start / Pause", delegate { SendRemoteTimer("toggle", 0); }));
            timerButtons.Add(AddSmallButton(catTimer, "Reset", delegate { SendRemoteTimer("reset", 0); }));
            timerButtons.Add(AddSmallButton(catTimer, "Stop ring", delegate { SendRemoteTimer("stop_ring", 0); }));
            quickStack.Children.Add(catTimer);

            quickStack.Children.Add(CreateCategoryLabel("Device & screen"));
            var catPower = new WrapPanel();
            btnPushImage = AddSmallButton(catPower, "Push image...", delegate { OpenImagePicker(false); });
            powerButtons.Add(AddSmallButton(catPower, "Close app & lock", delegate { SendRemotePower("close_and_lock", "Sent: Close app & lock"); }));
            powerButtons.Add(AddSmallButton(catPower, "Lock screen", delegate { SendRemotePower("lock_only", "Sent: Lock screen"); }));
            powerButtons.Add(AddSmallButton(catPower, "Close app", delegate { SendRemotePower("close_only", "Sent: Close app"); }));
            quickStack.Children.Add(catPower);

            var quickCard = CreateTitledCard("Quick actions", quickStack, new Thickness(0, 0, 0, 10));
            Grid.SetRow(quickCard, 0);

            var rightCol = new Grid();
            rightCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rightCol.Children.Add(quickCard);
            rightCol.Children.Add(taskCard);
            Grid.SetColumn(rightCol, 2);

            rootGrid.Children.Add(leftCol);
            rootGrid.Children.Add(rightCol);

            UpdateOptionHighlights();

            var outerGrid = new Grid();
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(rootGrid, 0);
            outerGrid.Children.Add(rootGrid);

            var tvVersion = new TextBlock();
            tvVersion.Text = "26.1121";
            tvVersion.FontSize = 11;
            tvVersion.Foreground = theme.TextSecondary;
            tvVersion.HorizontalAlignment = HorizontalAlignment.Right;
            tvVersion.Margin = new Thickness(14, 0, 14, 8);
            Grid.SetRow(tvVersion, 1);
            outerGrid.Children.Add(tvVersion);

            Content = outerGrid;
            Loaded += delegate { tbTask.Focus(); };
        }

        private void AddFormatButton(Panel parent, string label, FontWeight fw, FontStyle fs, RoutedEventHandler onClick)
        {
            var btn = CreateFluentButton(label, false, new Thickness(7, 1, 7, 1));
            btn.FontSize = 11.5;
            btn.FontWeight = fw;
            btn.FontStyle = fs;
            btn.Margin = new Thickness(4, 0, 0, 0);
            btn.Click += onClick;
            parent.Children.Add(btn);
        }

        private void WrapNoteSelection(string prefix, string suffix, string placeholder)
        {
            if (tbNote == null) return;
            string text = tbNote.Text ?? "";
            int start = tbNote.SelectionStart;
            int len = tbNote.SelectionLength;
            string inner = (len > 0) ? text.Substring(start, len) : placeholder;
            string inserted = prefix + inner + suffix;
            tbNote.Text = text.Substring(0, start) + inserted + text.Substring(start + len);
            tbNote.Focus();
            tbNote.SelectionStart = start + prefix.Length;
            tbNote.SelectionLength = inner.Length;
        }

        private void PrefixNoteLines(string prefix)
        {
            if (tbNote == null) return;
            string text = tbNote.Text ?? "";
            int start = tbNote.SelectionStart;
            int len = tbNote.SelectionLength;
            int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1));
            lineStart = (lineStart < 0) ? 0 : lineStart + 1;
            int endIdx = start + len;
            string segment = text.Substring(lineStart, endIdx - lineStart);
            string[] lines = segment.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = prefix + lines[i];
            }
            string replaced = string.Join("\n", lines);
            tbNote.Text = text.Substring(0, lineStart) + replaced + text.Substring(endIdx);
            tbNote.Focus();
            tbNote.SelectionStart = lineStart + replaced.Length;
            tbNote.SelectionLength = 0;
        }

        private Border CreateStretchTitledCard(string header, UIElement content, Thickness margin)
        {
            var inner = new Grid();
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var lbl = new TextBlock();
            lbl.Text = header;
            lbl.FontSize = 12;
            lbl.FontWeight = FontWeights.SemiBold;
            lbl.Foreground = theme.TextSecondary;
            lbl.Margin = new Thickness(0, 0, 0, 6);
            Grid.SetRow(lbl, 0);
            Grid.SetRow(content, 1);
            inner.Children.Add(lbl);
            inner.Children.Add(content);
            return CreatePlainCard(inner, margin);
        }

        private void OpenLayout3ResizePopup()
        {
            var dlg = new Layout3ResizeWindow(
                this,
                theme,
                l3SplitPct,
                l3RightSplitPct,
                l3TextScale,
                l3MetricScale,
                l3ShowTask || l3ShowTimer || l3ShowRings,
                remoteShowPlaying,
                l3ShowNotif,
                remotePlayingLeft
            );
            dlg.OnChanged = delegate
            {
                l3SplitPct = dlg.SplitPct;
                l3RightSplitPct = dlg.RightSplitPct;
                l3TextScale = dlg.TextScale;
                l3MetricScale = dlg.MetricScale;
                SendRemoteConfig();
            };
            dlg.ShowDialog();
            ShowFeedback("Layout 3 size updated");
        }

        private void OpenDuePicker()
        {
            var dlg = new DuePickerWindow(this, theme, dueDateCache, dueIncludeTimeCache, dueHourCache, dueMinCache);
            dlg.ShowDialog();
            if (dlg.Confirmed)
            {
                currentDueText = dlg.SelectedDueText ?? "";
                btnDuePicker.Content = string.IsNullOrEmpty(currentDueText) ? "Select due" : currentDueText;
                if (!string.IsNullOrEmpty(currentDueText))
                {
                    dueIncludeTimeCache = currentDueText.Contains(":");
                }
            }
        }

        private void ApplyServerPort()
        {
            if (backend == null || tbPort == null) return;
            if (serverEnabled)
            {
                ShowFeedback("Disable server to change port");
                tbPort.Text = backend.TcpPort.ToString();
                return;
            }
            int port;
            if (!int.TryParse((tbPort.Text ?? "").Trim(), out port) || port < 1024 || port > 65535)
            {
                ShowFeedback("Port must be 1024-65535");
                tbPort.Text = backend.TcpPort.ToString();
                return;
            }
            if (port == backend.TcpPort)
            {
                ShowFeedback("Port " + port);
                return;
            }
            backend.SetPort(port);
            tbPort.Text = backend.TcpPort.ToString();
            ShowFeedback("Port changed to " + backend.TcpPort);
        }

        private static System.Windows.Media.Imaging.BitmapImage DecodeBase64Bitmap(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return null;
            try
            {
                byte[] bytes = Convert.FromBase64String(b64);
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    bi.BeginInit();
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bi.StreamSource = ms;
                    bi.EndInit();
                    bi.Freeze();
                }
                return bi;
            }
            catch { return null; }
        }

        private void OpenImagePicker(bool isBackground)
        {
            var dlg = new Window();
            dlg.Owner = this;
            dlg.Title = isBackground ? "App background" : "Push image";
            dlg.Width = 440;
            dlg.SizeToContent = SizeToContent.Height;
            dlg.ResizeMode = ResizeMode.NoResize;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dlg.Background = theme.WindowBg;
            dlg.FontFamily = uiFont;
            dlg.SourceInitialized += delegate
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(dlg).Handle;
                    int dark = theme.IsDark ? 1 : 0;
                    if (NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                    {
                        NativeMethods.DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
                    }
                }
                catch { }
            };

            string savedCustomB64 = isBackground && backend != null ? backend.GetBackgroundBase64() : "";
            string previewB64 = savedCustomB64;
            bool initBgEnabled = backend == null || backend.CfgBgEnabled;
            int initBgMode = backend != null ? backend.CfgBgMode : 0;
            int initOp = backend != null ? backend.CfgBgOpacity : 32;
            int initBlur = backend != null ? backend.CfgBgBlur : 6;

            var root = new StackPanel { Margin = new Thickness(14) };
            var lbl = CreateFieldLabel(isBackground ? "Image URL or local file for background" : "Image URL or local file to display");
            root.Children.Add(lbl);

            var inputRow = new Grid();
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tbSrc = new TextBox();
            tbSrc.FontSize = 12.5;
            tbSrc.Foreground = theme.TextPrimary;
            tbSrc.Background = theme.InputBg;
            tbSrc.BorderBrush = theme.CardBorder;
            tbSrc.BorderThickness = new Thickness(1);
            tbSrc.Padding = new Thickness(8, 6, 8, 6);
            tbSrc.CaretBrush = theme.TextPrimary;
            Grid.SetColumn(tbSrc, 0);

            var tvStatus = new TextBlock
            {
                FontSize = 11.5,
                Foreground = theme.TextSecondary,
                Margin = new Thickness(0, 6, 0, 0)
            };

            // Live 16:9 preview box
            var previewGrid = new Grid();
            var ivPreview = new Image
            {
                Stretch = isBackground ? Stretch.UniformToFill : Stretch.Uniform,
                Opacity = isBackground ? (initOp / 100.0) : 1.0
            };
            var blurEffect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = isBackground ? initBlur : 0
            };
            ivPreview.Effect = blurEffect;
            previewGrid.Children.Add(ivPreview);

            var tvPreviewEmpty = new TextBlock
            {
                Text = "No preview loaded",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            previewGrid.Children.Add(tvPreviewEmpty);

            if (isBackground)
            {
                var mockCard = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(140, 24, 24, 24)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(12, 8, 12, 8),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(12, 0, 12, 10)
                };
                var mockStack = new StackPanel();
                mockStack.Children.Add(new TextBlock
                {
                    Text = "Deskmon preview",
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White
                });
                mockStack.Children.Add(new TextBlock
                {
                    Text = "CPU 14%   -   RAM 50%   -   Storage 49%",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(3, 218, 198)),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                mockCard.Child = mockStack;
                previewGrid.Children.Add(mockCard);
            }

            var previewBorder = new Border
            {
                Height = 148,
                Background = new SolidColorBrush(Color.FromRgb(18, 18, 18)),
                BorderBrush = theme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                Margin = new Thickness(0, 10, 0, 2),
                Child = previewGrid
            };

            if (!string.IsNullOrEmpty(previewB64))
            {
                var bmp = DecodeBase64Bitmap(previewB64);
                if (bmp != null)
                {
                    ivPreview.Source = bmp;
                    tvPreviewEmpty.Visibility = Visibility.Collapsed;
                }
            }

            Action loadPreviewAsync = delegate
            {
                string src = (tbSrc.Text ?? "").Trim();
                if (string.IsNullOrEmpty(src)) return;
                tvStatus.Text = "Loading preview...";
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    string b64 = "";
                    try
                    {
                        b64 = WinRtHelper.LoadAndEncodeImageBase64(src, isBackground ? 800 : 960, isBackground ? 480 : 540, 82L);
                    }
                    catch { b64 = ""; }
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (string.IsNullOrEmpty(b64))
                        {
                            tvStatus.Text = "Failed to load image";
                            return;
                        }
                        previewB64 = b64;
                        var bmp = DecodeBase64Bitmap(b64);
                        if (bmp != null)
                        {
                            ivPreview.Source = bmp;
                            tvPreviewEmpty.Visibility = Visibility.Collapsed;
                            tvStatus.Text = "Preview ready";
                        }
                    }));
                });
            };

            tbSrc.KeyDown += delegate(object s, KeyEventArgs args)
            {
                if (args.Key == Key.Enter) loadPreviewAsync();
            };

            var btnPreview = CreateFluentButton("Preview", false, new Thickness(10, 6, 10, 6));
            btnPreview.FontSize = 12;
            btnPreview.FontWeight = FontWeights.Normal;
            btnPreview.Margin = new Thickness(6, 0, 0, 0);
            btnPreview.Click += delegate { loadPreviewAsync(); };
            Grid.SetColumn(btnPreview, 1);

            var btnBrowse = CreateFluentButton("Browse...", false, new Thickness(10, 6, 10, 6));
            btnBrowse.FontSize = 12;
            btnBrowse.FontWeight = FontWeights.Normal;
            btnBrowse.Margin = new Thickness(6, 0, 0, 0);
            btnBrowse.Click += delegate
            {
                var ofd = new Microsoft.Win32.OpenFileDialog();
                ofd.Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*";
                if (ofd.ShowDialog(dlg) == true)
                {
                    tbSrc.Text = ofd.FileName;
                    loadPreviewAsync();
                }
            };
            Grid.SetColumn(btnBrowse, 2);

            inputRow.Children.Add(tbSrc);
            inputRow.Children.Add(btnPreview);
            inputRow.Children.Add(btnBrowse);
            root.Children.Add(inputRow);
            root.Children.Add(previewBorder);

            Slider slOpacity = null;
            Slider slBlur = null;
            CheckBox chkBgEnable = null;
            RadioButton rbCustom = null;
            RadioButton rbSync = null;
            if (isBackground)
            {
                var topCtrlRow = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
                chkBgEnable = new CheckBox
                {
                    Content = "Enable background",
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = theme.TextPrimary,
                    IsChecked = initBgEnabled,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand
                };
                DockPanel.SetDock(chkBgEnable, Dock.Left);

                var modePanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                rbCustom = new RadioButton
                {
                    Content = "Custom image",
                    GroupName = "bgMode",
                    FontSize = 12,
                    Foreground = theme.TextPrimary,
                    IsChecked = (initBgMode == 0),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand
                };
                rbSync = new RadioButton
                {
                    Content = "Sync with playing",
                    GroupName = "bgMode",
                    FontSize = 12,
                    Foreground = theme.TextPrimary,
                    IsChecked = (initBgMode == 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand
                };
                modePanel.Children.Add(rbCustom);
                modePanel.Children.Add(rbSync);
                topCtrlRow.Children.Add(chkBgEnable);
                topCtrlRow.Children.Add(modePanel);

                var opHeader = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };
                var lblOp = CreateFieldLabel("Opacity");
                lblOp.Margin = new Thickness(0);
                DockPanel.SetDock(lblOp, Dock.Left);

                var tvOpVal = new TextBlock
                {
                    Text = initOp + "%",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = theme.TextPrimary,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                opHeader.Children.Add(lblOp);
                opHeader.Children.Add(tvOpVal);

                slOpacity = new Slider
                {
                    Minimum = 5,
                    Maximum = 100,
                    Value = initOp,
                    TickFrequency = 1,
                    IsSnapToTickEnabled = true,
                    Margin = new Thickness(0, 2, 0, 2)
                };

                var blurHeader = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };
                var lblBlur = CreateFieldLabel("Blur");
                lblBlur.Margin = new Thickness(0);
                DockPanel.SetDock(lblBlur, Dock.Left);

                var tvBlurVal = new TextBlock
                {
                    Text = initBlur.ToString(),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = theme.TextPrimary,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                blurHeader.Children.Add(lblBlur);
                blurHeader.Children.Add(tvBlurVal);

                slBlur = new Slider
                {
                    Minimum = 0,
                    Maximum = 25,
                    Value = initBlur,
                    TickFrequency = 1,
                    IsSnapToTickEnabled = true,
                    Margin = new Thickness(0, 2, 0, 2)
                };

                Action refreshBgPreview = delegate
                {
                    bool en = chkBgEnable.IsChecked == true;
                    int mode = (rbSync.IsChecked == true) ? 1 : 0;
                    inputRow.IsEnabled = (mode == 0);
                    inputRow.Opacity = (mode == 0) ? 1.0 : 0.45;
                    if (!en)
                    {
                        ivPreview.Source = null;
                        tvPreviewEmpty.Text = "Background disabled";
                        tvPreviewEmpty.Visibility = Visibility.Visible;
                        return;
                    }
                    string activeB64 = (mode == 1)
                        ? (backend != null ? backend.GetCurrentMediaThumbBase64() : "")
                        : (!string.IsNullOrEmpty(previewB64) ? previewB64 : savedCustomB64);
                    var bmp = DecodeBase64Bitmap(activeB64);
                    ivPreview.Source = bmp;
                    tvPreviewEmpty.Text = (mode == 1) ? "Waiting for media cover" : "No custom image";
                    tvPreviewEmpty.Visibility = (bmp != null) ? Visibility.Collapsed : Visibility.Visible;
                };

                Action pushBgStyle = delegate
                {
                    if (backend != null)
                    {
                        backend.SetBackgroundStyle(
                            chkBgEnable.IsChecked == true,
                            (rbSync.IsChecked == true) ? 1 : 0,
                            (int)Math.Round(slOpacity.Value),
                            (int)Math.Round(slBlur.Value)
                        );
                    }
                };

                chkBgEnable.Checked += delegate { refreshBgPreview(); pushBgStyle(); };
                chkBgEnable.Unchecked += delegate { refreshBgPreview(); pushBgStyle(); };
                rbCustom.Checked += delegate { refreshBgPreview(); pushBgStyle(); };
                rbSync.Checked += delegate { refreshBgPreview(); pushBgStyle(); };
                slOpacity.ValueChanged += delegate
                {
                    int v = (int)Math.Round(slOpacity.Value);
                    tvOpVal.Text = v + "%";
                    ivPreview.Opacity = v / 100.0;
                    pushBgStyle();
                };
                slBlur.ValueChanged += delegate
                {
                    int b = (int)Math.Round(slBlur.Value);
                    tvBlurVal.Text = b.ToString();
                    blurEffect.Radius = b;
                    pushBgStyle();
                };

                root.Children.Add(topCtrlRow);
                root.Children.Add(opHeader);
                root.Children.Add(slOpacity);
                root.Children.Add(blurHeader);
                root.Children.Add(slBlur);
                refreshBgPreview();
            }

            root.Children.Add(tvStatus);

            var actionRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var leftBtns = new StackPanel { Orientation = Orientation.Horizontal };
            var btnClear = CreateFluentButton(isBackground ? "Clear saved" : "Clear", false, new Thickness(12, 6, 12, 6));
            btnClear.FontSize = 12.5;
            btnClear.Click += delegate
            {
                if (backend != null)
                {
                    if (isBackground)
                    {
                        backend.SetBackgroundImage("");
                        savedCustomB64 = "";
                        previewB64 = "";
                        tbSrc.Text = "";
                        tvStatus.Text = "Saved custom image cleared";
                        if (rbCustom != null && rbCustom.IsChecked == true)
                        {
                            ivPreview.Source = null;
                            tvPreviewEmpty.Text = "No custom image";
                            tvPreviewEmpty.Visibility = Visibility.Visible;
                        }
                        ShowFeedback("Saved background cleared");
                        return;
                    }
                    else
                    {
                        backend.SetPushImage("");
                    }
                }
                ShowFeedback("Image cleared");
                dlg.Close();
            };
            leftBtns.Children.Add(btnClear);

            if (isBackground)
            {
                var btnLoadSaved = CreateFluentButton("Load saved", false, new Thickness(10, 6, 10, 6));
                btnLoadSaved.FontSize = 12.5;
                btnLoadSaved.Margin = new Thickness(6, 0, 0, 0);
                btnLoadSaved.Click += delegate
                {
                    savedCustomB64 = backend != null ? backend.GetBackgroundBase64() : "";
                    if (string.IsNullOrEmpty(savedCustomB64))
                    {
                        tvStatus.Text = "No saved background found";
                        return;
                    }
                    previewB64 = savedCustomB64;
                    tbSrc.Text = "";
                    if (rbCustom != null) rbCustom.IsChecked = true;
                    if (chkBgEnable != null) chkBgEnable.IsChecked = true;
                    var bmp = DecodeBase64Bitmap(savedCustomB64);
                    ivPreview.Source = bmp;
                    tvPreviewEmpty.Visibility = (bmp != null) ? Visibility.Collapsed : Visibility.Visible;
                    tvStatus.Text = "Loaded saved background";
                };
                leftBtns.Children.Add(btnLoadSaved);
            }
            DockPanel.SetDock(leftBtns, Dock.Left);

            var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnSend = CreateFluentButton(isBackground ? "Apply" : "Send", true, new Thickness(16, 6, 16, 6));
            btnSend.FontSize = 12.5;
            btnSend.Click += delegate
            {
                string src = (tbSrc.Text ?? "").Trim();
                if (isBackground && slOpacity != null && slBlur != null && chkBgEnable != null && rbSync != null && backend != null)
                {
                    backend.SetBackgroundStyle(
                        chkBgEnable.IsChecked == true,
                        (rbSync.IsChecked == true) ? 1 : 0,
                        (int)Math.Round(slOpacity.Value),
                        (int)Math.Round(slBlur.Value)
                    );
                }
                if (isBackground && string.IsNullOrEmpty(src))
                {
                    ShowFeedback("Background updated");
                    dlg.Close();
                    return;
                }
                if (string.IsNullOrEmpty(src) && string.IsNullOrEmpty(previewB64))
                {
                    tvStatus.Text = "Enter a URL or choose a file";
                    return;
                }
                btnSend.IsEnabled = false;
                tvStatus.Text = "Loading image...";
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    string b64 = previewB64;
                    if (!string.IsNullOrEmpty(src))
                    {
                        try
                        {
                            b64 = WinRtHelper.LoadAndEncodeImageBase64(src, isBackground ? 800 : 960, isBackground ? 480 : 540, 82L);
                        }
                        catch { b64 = ""; }
                    }
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        btnSend.IsEnabled = true;
                        if (string.IsNullOrEmpty(b64))
                        {
                            tvStatus.Text = "Failed to load image";
                            return;
                        }
                        if (backend != null)
                        {
                            if (isBackground) backend.SetBackgroundImage(b64);
                            else backend.SetPushImage(b64);
                        }
                        ShowFeedback(isBackground ? "Background updated" : "Image sent");
                        dlg.Close();
                    }));
                });
            };
            rightBtns.Children.Add(btnSend);

            actionRow.Children.Add(leftBtns);
            actionRow.Children.Add(rightBtns);
            root.Children.Add(actionRow);

            dlg.Content = root;
            dlg.ShowDialog();
        }

        private void ToggleServerEnabled()
        {
            serverEnabled = !serverEnabled;
            btnServerPower.Content = serverEnabled ? "Disable server" : "Enable server";
            if (trayToggleItem != null) trayToggleItem.Text = serverEnabled ? "Disable server" : "Enable server";
            if (backend != null)
            {
                if (serverEnabled) backend.Start();
                else backend.Stop();
            }
            ShowFeedback(serverEnabled ? "Server enabled" : "Server disabled");
        }

        private TextBlock CreateFieldLabel(string text)
        {
            var lbl = new TextBlock();
            lbl.Text = text;
            lbl.FontSize = 12;
            lbl.Foreground = theme.TextSecondary;
            lbl.Margin = new Thickness(0, 0, 0, 4);
            return lbl;
        }

        private TextBlock CreateCategoryLabel(string text)
        {
            var lbl = new TextBlock();
            lbl.Text = text;
            lbl.FontSize = 11;
            lbl.Foreground = theme.TextSecondary;
            lbl.Margin = new Thickness(0, 0, 0, 4);
            return lbl;
        }

        private TextBox CreateSingleLineBox()
        {
            var tb = new TextBox();
            tb.FontSize = 13;
            tb.Foreground = theme.TextPrimary;
            tb.Background = theme.InputBg;
            tb.BorderBrush = theme.CardBorder;
            tb.BorderThickness = new Thickness(1);
            tb.Padding = new Thickness(10, 6, 10, 6);
            tb.CaretBrush = theme.TextPrimary;
            tb.KeyDown += delegate(object s, KeyEventArgs args)
            {
                if (args.Key == Key.Enter)
                {
                    SendTaskToBackend();
                }
            };
            return tb;
        }

        private Button AddSmallButton(Panel parent, string text, RoutedEventHandler onClick)
        {
            var btn = CreateFluentButton(text, false, new Thickness(8, 3, 8, 3));
            btn.FontSize = 11.5;
            btn.FontWeight = FontWeights.Normal;
            btn.Margin = new Thickness(0, 0, 5, 4);
            btn.Click += onClick;
            parent.Children.Add(btn);
            return btn;
        }

        private int VisibleL3ComponentCount()
        {
            int c = 0;
            if (l3ShowTask) c++;
            if (l3ShowTimer) c++;
            if (l3ShowRings) c++;
            if (remoteShowPlaying) c++;
            if (l3ShowNotif) c++;
            return c;
        }

        private void ToggleL3Component(ref bool flag, string label)
        {
            if (flag && VisibleL3ComponentCount() <= 1)
            {
                UpdateOptionHighlights();
                ShowFeedback("Keep at least 1 component");
                return;
            }
            flag = !flag;
            UpdateOptionHighlights();
            SendRemoteConfig();
            ShowFeedback(label + (flag ? " on" : " off"));
        }

        private void TogglePlayingOption()
        {
            if (remoteLayout == 2 && remoteShowPlaying && VisibleL3ComponentCount() <= 1)
            {
                UpdateOptionHighlights();
                ShowFeedback("Keep at least 1 component");
                return;
            }
            remoteShowPlaying = !remoteShowPlaying;
            UpdateOptionHighlights();
            SendRemoteConfig();
            ShowFeedback(remoteShowPlaying ? "Playing on" : "Playing off");
        }

        private CheckBox CreateMediaCheckBox(string text, Action onToggle)
        {
            var cb = new CheckBox();
            cb.Content = text;
            cb.FontSize = 12;
            cb.Foreground = theme.TextPrimary;
            cb.VerticalContentAlignment = VerticalAlignment.Center;
            cb.Margin = new Thickness(0, 3, 6, 3);
            cb.Cursor = Cursors.Hand;
            cb.Checked += delegate { if (!suppressCheckEvents) onToggle(); };
            cb.Unchecked += delegate { if (!suppressCheckEvents) onToggle(); };
            return cb;
        }

        private Button CreateActionGridButton(string text, RoutedEventHandler onClick)
        {
            var btn = CreateFluentButton(text, false, new Thickness(10, 5, 10, 5));
            btn.FontSize = 12;
            btn.FontWeight = FontWeights.Normal;
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btn.Click += onClick;
            return btn;
        }

        private void SetCheckState(CheckBox cb, bool isChecked, bool enabled)
        {
            if (cb == null) return;
            cb.IsChecked = isChecked;
            cb.IsEnabled = enabled;
            cb.Opacity = enabled ? 1.0 : 0.38;
        }

        private void UpdateOptionHighlights()
        {
            for (int i = 0; i < btnLayouts.Length; i++)
            {
                StyleOptionButton(btnLayouts[i], i == remoteLayout, true);
            }
            for (int i = 0; i < btnFonts.Length; i++)
            {
                StyleOptionButton(btnFonts[i], i == remoteFont, true);
            }

            bool isL3 = (remoteLayout == 2);
            bool hasPlaying = (remoteLayout == 1 || remoteLayout == 2);
            bool canSwap = (remoteLayout == 1 && remoteShowPlaying) ||
                           (isL3 && (l3ShowTask || l3ShowTimer || l3ShowRings) && (remoteShowPlaying || l3ShowNotif));

            suppressCheckEvents = true;
            SetCheckState(chkL3Task, l3ShowTask, isL3);
            SetCheckState(chkL3Timer, l3ShowTimer, isL3);
            SetCheckState(chkL3Rings, l3ShowRings, isL3);
            SetCheckState(chkPlaying, remoteShowPlaying, hasPlaying);
            SetCheckState(chkL3Notif, l3ShowNotif, isL3);
            SetCheckState(chkShowTopBar, remoteShowTopBar, hasPlaying);
            SetCheckState(chkShowLogo, remoteShowLogo, true);
            SetCheckState(chkSwapSide, remotePlayingLeft, canSwap);
            SetCheckState(chkFullscreen, remoteFullscreen, true);
            SetCheckState(chkStartWithWin, startWithWindows, true);
            SetCheckState(chkShowOnStart, showWindowOnStartup, true);
            suppressCheckEvents = false;

            if (btnTimeMode != null)
            {
                btnTimeMode.Content = "Time mode: " + (remoteTimeMode + 1);
                StyleOptionButton(btnTimeMode, false, hasPlaying && remoteShowPlaying);
            }
            StyleOptionButton(btnL3Resize, false, isL3);
            StyleOptionButton(btnPushImage, backend != null && backend.HasPushImage, hasActiveClient);
            StyleOptionButton(btnBackground, backend != null && backend.CfgBgEnabled && (backend.HasBackgroundImage || backend.CfgBgMode == 1), true);
            if (tbPort != null && btnApplyPort != null && portBoxBorder != null)
            {
                bool canEditPort = !serverEnabled;
                tbPort.IsEnabled = canEditPort;
                btnApplyPort.IsEnabled = canEditPort;
                portBoxBorder.Opacity = canEditPort ? 1.0 : 0.45;
                btnApplyPort.Opacity = canEditPort ? 1.0 : 0.45;
            }

            // Timer & Pomodoro exists only on Layout 3 (idx 2)
            bool hasTimer = isL3 && l3ShowTimer;
            foreach (var tb in timerButtons)
            {
                StyleOptionButton(tb, false, hasTimer);
            }

            foreach (var pb in powerButtons)
            {
                StyleOptionButton(pb, false, hasActiveClient);
            }
        }

        private const string StartupRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupRegName = "DeskMonServer";

        private static bool ReadStartupRegistry()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegPath, false))
                {
                    if (key == null) return false;
                    object val = key.GetValue(StartupRegName);
                    return val != null && !string.IsNullOrEmpty(val.ToString());
                }
            }
            catch { return false; }
        }

        private static bool WriteStartupRegistry(bool enable)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegPath, true))
                {
                    if (key == null) return false;
                    if (enable)
                    {
                        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        key.SetValue(StartupRegName, "\"" + exePath + "\"");
                    }
                    else
                    {
                        if (key.GetValue(StartupRegName) != null)
                        {
                            key.DeleteValue(StartupRegName, false);
                        }
                    }
                    return true;
                }
            }
            catch { return false; }
        }

        private void ToggleStartWithWindows()
        {
            bool next = !startWithWindows;
            if (WriteStartupRegistry(next))
            {
                startWithWindows = next;
                if (backend != null) backend.SaveServerPrefs(startWithWindows, showWindowOnStartup);
                UpdateOptionHighlights();
                ShowFeedback(startWithWindows ? "Start with Windows on" : "Start with Windows off");
            }
            else
            {
                UpdateOptionHighlights();
                ShowFeedback("Failed to update startup");
            }
        }

        private void ToggleShowWindowOnStartup()
        {
            showWindowOnStartup = !showWindowOnStartup;
            if (backend != null) backend.SaveServerPrefs(startWithWindows, showWindowOnStartup);
            UpdateOptionHighlights();
            ShowFeedback(showWindowOnStartup ? "Show window on startup" : "Start minimized to tray");
        }

        private void StyleOptionButton(Button btn, bool active, bool enabled)
        {
            if (btn == null) return;
            var pad = new Thickness(8, 3, 8, 3);
            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "bd";
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            borderFactory.SetValue(Border.PaddingProperty, pad);
            borderFactory.SetValue(Border.BackgroundProperty, active ? theme.AccentBrush : theme.SecondaryBtnBg);
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(active ? 0 : 1));
            borderFactory.SetValue(Border.BorderBrushProperty, theme.CardBorder);

            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(cpFactory);
            template.VisualTree = borderFactory;

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(
                Border.BackgroundProperty,
                active ? theme.AccentHoverBrush : theme.SecondaryBtnHoverBg,
                "bd"
            ));
            template.Triggers.Add(hoverTrigger);

            btn.Template = template;
            btn.Foreground = active ? theme.OnAccentBrush : theme.TextPrimary;
            btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            btn.IsEnabled = enabled;
            btn.Opacity = enabled ? 1.0 : 0.35;
        }

        private void SetRemoteLayout(int idx)
        {
            remoteLayout = idx;
            UpdateOptionHighlights();
            SendRemoteConfig();
            ShowFeedback("Layout " + (idx + 1));
        }

        private void SetRemoteFont(int idx)
        {
            remoteFont = idx;
            UpdateOptionHighlights();
            SendRemoteConfig();
            string[] names = new string[] { "Fira Sans", "IBM Plex Sans", "Barlow", "Roboto" };
            ShowFeedback(names[idx]);
        }

        private void SendRemoteConfig()
        {
            if (backend == null) return;
            backend.SyncFromUi(
                remoteLayout,
                remoteFont,
                remoteShowPlaying,
                remotePlayingLeft,
                remoteTimeMode,
                l3ShowTask,
                l3ShowTimer,
                l3ShowRings,
                l3ShowNotif,
                l3SplitPct,
                l3RightSplitPct,
                l3TextScale,
                l3MetricScale,
                remoteShowTopBar,
                remoteShowLogo,
                remoteFullscreen
            );
        }

        private void SendRemoteTimer(string action, int val)
        {
            if (backend == null) return;
            backend.TriggerTimer(action, val);
            ShowFeedback("Timer updated");
        }

        private void SendRemotePower(string action, string feedbackText)
        {
            if (backend == null || !backend.TriggerPower(action))
            {
                ShowFeedback("No connected device");
                return;
            }
            ShowFeedback(feedbackText);
        }

        private Border CreatePlainCard(UIElement content, Thickness margin)
        {
            var outer = new Border();
            outer.Background = theme.CardBg;
            outer.BorderBrush = theme.CardBorder;
            outer.BorderThickness = new Thickness(1);
            outer.CornerRadius = new CornerRadius(6);
            outer.Padding = new Thickness(12, 8, 12, 8);
            outer.Margin = margin;
            outer.Child = content;
            return outer;
        }

        private Border CreateTitledCard(string header, UIElement content, Thickness margin)
        {
            var inner = new StackPanel();
            var lbl = new TextBlock();
            lbl.Text = header;
            lbl.FontSize = 12;
            lbl.FontWeight = FontWeights.SemiBold;
            lbl.Foreground = theme.TextSecondary;
            lbl.Margin = new Thickness(0, 0, 0, 6);
            inner.Children.Add(lbl);
            inner.Children.Add(content);
            return CreatePlainCard(inner, margin);
        }

        private StackPanel AddIconInfoRow(StackPanel parent, string glyph, string initialText, out TextBlock label)
        {
            var row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Margin = new Thickness(0, 2, 0, 2);

            var icon = new TextBlock();
            icon.FontFamily = iconFont;
            icon.FontSize = 13;
            icon.Text = glyph;
            icon.Foreground = theme.TextSecondary;
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 10, 0);

            label = new TextBlock();
            label.FontSize = 13;
            label.Text = initialText;
            label.Foreground = theme.TextPrimary;
            label.VerticalAlignment = VerticalAlignment.Center;

            row.Children.Add(icon);
            row.Children.Add(label);
            parent.Children.Add(row);
            return row;
        }

        private Button CreateFluentButton(string text, bool isPrimary, Thickness pad)
        {
            var btn = new Button();
            btn.Content = text;
            btn.FontSize = 13;
            btn.FontWeight = FontWeights.SemiBold;
            btn.Padding = pad;
            btn.Cursor = Cursors.Hand;

            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "bd";
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            borderFactory.SetValue(Border.PaddingProperty, pad);
            borderFactory.SetValue(Border.BackgroundProperty, isPrimary ? theme.AccentBrush : theme.SecondaryBtnBg);
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(isPrimary ? 0 : 1));
            borderFactory.SetValue(Border.BorderBrushProperty, theme.CardBorder);

            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(cpFactory);

            template.VisualTree = borderFactory;

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(
                Border.BackgroundProperty,
                isPrimary ? theme.AccentHoverBrush : theme.SecondaryBtnHoverBg,
                "bd"
            ));
            template.Triggers.Add(hoverTrigger);

            btn.Template = template;
            btn.Foreground = isPrimary ? theme.OnAccentBrush : theme.TextPrimary;
            return btn;
        }

        private void StartBackend(System.Threading.EventWaitHandle wakeSignal)
        {
            backend = new MonitorServer(baseDir);
            startWithWindows = ReadStartupRegistry();
            showWindowOnStartup = backend.ShowWindowOnStartup;
            if (tbPort != null) tbPort.Text = backend.TcpPort.ToString();
            tbTask.Text = backend.CurrentTask;
            tbNote.Text = backend.CurrentNote;
            currentDueText = backend.CurrentDue;
            if (!string.IsNullOrEmpty(currentDueText))
            {
                btnDuePicker.Content = currentDueText;
                dueIncludeTimeCache = currentDueText.Contains(":");
            }

            backend.StateChanged += delegate
            {
                Dispatcher.BeginInvoke(new Action(RefreshFromBackend));
            };
            backend.Start();
            RefreshFromBackend();

            new System.Threading.Thread((System.Threading.ThreadStart)delegate
            {
                while (!shuttingDown)
                {
                    if (wakeSignal != null && wakeSignal.WaitOne(1000) && !shuttingDown)
                    {
                        Dispatcher.BeginInvoke(new Action(RestoreFromTray));
                    }
                }
            }) { IsBackground = true }.Start();
        }

        private void RefreshFromBackend()
        {
            if (backend == null) return;
            serverEnabled = backend.Enabled;
            btnServerPower.Content = serverEnabled ? "Disable server" : "Enable server";
            if (trayToggleItem != null) trayToggleItem.Text = serverEnabled ? "Disable server" : "Enable server";

            string channel = backend.GetActiveChannel();
            if (!serverEnabled)
            {
                rowDisabled.Visibility = Visibility.Visible;
                rowActive.Visibility = Visibility.Collapsed;
                tvPausedHint.Visibility = Visibility.Collapsed;
                rowLocalNet.Visibility = Visibility.Collapsed;
                rowUsb.Visibility = Visibility.Collapsed;
                rowBluetooth.Visibility = Visibility.Collapsed;
            }
            else if (!string.IsNullOrEmpty(channel))
            {
                rowDisabled.Visibility = Visibility.Collapsed;
                rowActive.Visibility = Visibility.Visible;
                tvActive.Text = "Active connection: " + channel;
                tvPausedHint.Visibility = Visibility.Visible;
                rowLocalNet.Visibility = Visibility.Collapsed;
                rowUsb.Visibility = Visibility.Collapsed;
                rowBluetooth.Visibility = Visibility.Collapsed;
            }
            else
            {
                rowDisabled.Visibility = Visibility.Collapsed;
                rowActive.Visibility = Visibility.Collapsed;
                tvPausedHint.Visibility = Visibility.Collapsed;
                rowLocalNet.Visibility = Visibility.Visible;
                rowUsb.Visibility = Visibility.Visible;
                rowBluetooth.Visibility = Visibility.Visible;

                localIp = MonitorServer.GetLocalIp();
                tvLocalNet.Text = string.Format("Local network: {0}:{1}", localIp, backend.TcpPort);
                tvUsb.Text = backend.UsbReady
                    ? ("USB: ready on port " + backend.TcpPort)
                    : ("USB: waiting on port " + backend.TcpPort);
                tvBluetooth.Text = backend.BtStatus;
            }

            hasActiveClient = serverEnabled && !string.IsNullOrEmpty(channel);
            remoteLayout = backend.CfgLayout;
            remoteFont = backend.CfgFont;
            remoteShowPlaying = backend.CfgShowPlaying;
            remotePlayingLeft = backend.CfgPlayingLeft;
            remoteTimeMode = backend.CfgTimeMode;
            l3ShowTask = backend.CfgL3ShowTask;
            l3ShowTimer = backend.CfgL3ShowTimer;
            l3ShowRings = backend.CfgL3ShowRings;
            l3ShowNotif = backend.CfgL3ShowNotif;
            l3SplitPct = backend.CfgL3SplitPct;
            l3RightSplitPct = backend.CfgL3RightSplitPct;
            l3TextScale = backend.CfgL3TextScale;
            l3MetricScale = backend.CfgL3MetricScale;
            remoteShowTopBar = backend.CfgShowTopBar;
            remoteShowLogo = backend.CfgShowLogo;
            remoteFullscreen = backend.CfgFullscreen;
            UpdateOptionHighlights();
        }

        private void SendTaskToBackend()
        {
            if (backend == null) return;
            backend.SetTask(tbTask.Text ?? "", currentDueText ?? "", tbNote.Text ?? "");
            ShowFeedback("Synced");
        }

        private void RemoveTaskFromBackend()
        {
            tbTask.Text = "";
            tbNote.Text = "";
            currentDueText = "";
            dueDateCache = null;
            dueIncludeTimeCache = false;
            btnDuePicker.Content = "Select due";
            if (backend != null)
            {
                backend.SetTask("", "", "");
            }
            ShowFeedback("Task removed");
        }

        private void ShowFeedback(string message)
        {
            tvFeedback.Text = message;
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            shuttingDown = true;
            try { if (backend != null) backend.Stop(); } catch { }
            try { if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); } } catch { }
            try { if (Application.Current != null) Application.Current.Shutdown(); } catch { }
        }

        [STAThread]
        public static void Main()
        {
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, @"Local\PCMonitorServer_SingleInstance", out createdNew))
            using (var wakeSignal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\PCMonitorServer_Wake"))
            {
                if (!createdNew)
                {
                    wakeSignal.Set();
                    return;
                }
                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var win = new ServerWindow(wakeSignal);
                app.MainWindow = win;
                if (win.ShouldShowOnStartup)
                {
                    win.Show();
                }
                else
                {
                    win.ShowInTaskbar = false;
                    Native.TrimWorkingSet();
                }
                app.Run();
            }
        }
    }
}
