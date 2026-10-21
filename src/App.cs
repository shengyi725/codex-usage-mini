using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CodexUsageMini
{
    public sealed class Preferences
    {
        public bool Pinned = true;
        public double Left = -1;
        public double Top = -1;
        private static string FilePath()
        {
            string custom = Environment.GetEnvironmentVariable("CODEX_USAGE_MINI_DATA");
            string eProfile = Path.Combine("E:\\Users", Environment.UserName);
            string folder = !String.IsNullOrWhiteSpace(custom) ? custom : Directory.Exists(eProfile) ? Path.Combine(eProfile, "AppData", "Local", "CodexUsageMini") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageMini");
            return Path.Combine(folder, "preferences.json");
        }
        public static Preferences Load()
        {
            try { return new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(FilePath())) ?? new Preferences(); }
            catch { return new Preferences(); }
        }
        public void Save()
        {
            try {
                string path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public sealed class WidgetWindow : Window
    {
        private readonly StackPanel body = new StackPanel();
        private readonly TextBlock footer = new TextBlock();
        private readonly Button refresh;
        private readonly Button pin;
        private readonly Preferences preferences;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly DispatcherTimer refreshTimer = new DispatcherTimer();
        private readonly DispatcherTimer clockTimer = new DispatcherTimer();
        private readonly bool testMode;
        private Forms.NotifyIcon tray;
        private Drawing.Icon trayIcon;
        private UsageSnapshot latest;
        private UsageSnapshot preview;
        private bool busy;
        private bool failed;
        private string problem;
        private static readonly Brush Ink = ColorBrush("#F1F5F3");
        private static readonly Brush Muted = ColorBrush("#91A4A1");
        private static readonly Brush Track = ColorBrush("#293637");
        private static readonly Brush Neutral = ColorBrush("#91A4A1");
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

        public WidgetWindow(bool testMode) : this(testMode, Preferences.Load()) { }
        private WidgetWindow(bool testing, Preferences prefs)
        {
            testMode = testing;
            preferences = prefs;
            Title = "Codex Usage Mini";
            Width = 354;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = preferences.Pinned;
            ShowInTaskbar = true;
            FontFamily = new FontFamily("Segoe UI");
            Foreground = Ink;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Rect work = SystemParameters.WorkArea;
            Left = Double.IsNaN(preferences.Left) || Double.IsInfinity(preferences.Left) || preferences.Left < work.Left || preferences.Left > work.Right - Width ? work.Right - Width - 22 : preferences.Left;
            Top = Double.IsNaN(preferences.Top) || Double.IsInfinity(preferences.Top) || preferences.Top < work.Top || preferences.Top > work.Bottom - 430 ? work.Top + 52 : preferences.Top;

            var shell = new Border { Background = ColorBrush("#111B1C"), BorderBrush = ColorBrush("#344344"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Margin = new Thickness(9), Padding = new Thickness(21, 18, 21, 17), Effect = new DropShadowEffect { Color = Colors.Black, Opacity = .28, BlurRadius = 14, ShadowDepth = 3 } };
            var stack = new StackPanel();
            shell.Child = stack;
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 25), Background = Brushes.Transparent };
            header.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is TextBlock || e.OriginalSource is DockPanel || e.OriginalSource is Border) { try { DragMove(); } catch (InvalidOperationException) { } } };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);
            pin = Action("\uE718", "Always on top", delegate { TogglePin(); }, true);
            actions.Children.Add(pin);
            actions.Children.Add(Action("\uE921", "Hide to tray", delegate { Hide(); }, true));
            actions.Children.Add(Action("\uE8BB", "Quit", delegate { Close(); }, true));
            header.Children.Add(actions);
            var brand = new StackPanel();
            brand.Children.Add(Text("Codex", 16, Ink, FontWeights.SemiBold));
            var subtitle = Text("U S A G E  M I N I", 9, Muted, FontWeights.Medium);
            subtitle.Margin = new Thickness(0, 3, 0, 0);
            brand.Children.Add(subtitle);
            header.Children.Add(brand);
            stack.Children.Add(header);
            stack.Children.Add(body);

            var footerPanel = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
            refresh = Action("\uE72C", "Refresh now", async delegate { preview = null; await RefreshAsync(); }, true);
            DockPanel.SetDock(refresh, Dock.Right);
            footerPanel.Children.Add(refresh);
            footer.FontSize = 10;
            footer.Foreground = Muted;
            footer.VerticalAlignment = VerticalAlignment.Center;
            footerPanel.Children.Add(footer);
            stack.Children.Add(footerPanel);
            var scroll = new ScrollViewer { Content = shell, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(200, work.Height - 32) };
            Content = scroll;
            ContextMenu = MakeMenu();
            refreshTimer.Interval = TimeSpan.FromSeconds(60);
            refreshTimer.Tick += async delegate { if (preview == null) await RefreshAsync(); };
            clockTimer.Interval = TimeSpan.FromSeconds(15);
            clockTimer.Tick += delegate { Render(); };
            Loaded += async delegate {
                if (!testMode) { MakeTray(); refreshTimer.Start(); clockTimer.Start(); await RefreshAsync(); }
            };
            Closed += delegate {
                lifetime.Cancel(); refreshTimer.Stop(); clockTimer.Stop();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (trayIcon != null) trayIcon.Dispose();
                if (!testMode) { preferences.Pinned = Topmost; preferences.Left = Left; preferences.Top = Top; preferences.Save(); }
            };
            PreviewKeyDown += async delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.F5) { preview = null; await RefreshAsync(); e.Handled = true; }
                if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
            };
            Render();
            UpdatePin();
        }

        private static SolidColorBrush ColorBrush(string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush; }
        private static TextBlock Text(string value, double size, Brush color, FontWeight weight)
        {
            return new TextBlock { Text = value, FontSize = size, Foreground = color, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        }
        private Button Action(string value, string tip, RoutedEventHandler action, bool glyph)
        {
            var button = new Button { Content = value, ToolTip = tip, Width = 27, Height = 27, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, Foreground = Muted, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12, FontFamily = new FontFamily(glyph ? "Segoe MDL2 Assets" : "Segoe UI") };
            System.Windows.Automation.AutomationProperties.SetName(button, tip);
            var template = new ControlTemplate(typeof(Button));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            chrome.AppendChild(presenter);
            template.VisualTree = chrome;
            var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Button.BackgroundProperty, Track));
            template.Triggers.Add(hover);
            button.Template = template;
            button.Click += action;
            return button;
        }

        public void TogglePin() { Topmost = !Topmost; UpdatePin(); }
        private void UpdatePin() { pin.Foreground = Topmost ? Ink : Muted; pin.ToolTip = Topmost ? "Unpin from top" : "Pin on top"; }
        public void ShowWidget() { Show(); WindowState = WindowState.Normal; Activate(); }
        public void PreviewAllowance(double remaining) { preview = UsageModel.Demo(remaining); Render(); }
        public void PreviewSnapshot(UsageSnapshot snapshot) { preview = snapshot; Render(); }
        public void PreviewError() { preview = null; latest = UsageModel.Demo(77); failed = true; problem = "Connection unavailable. Check your connection and refresh."; Render(); }

        public async Task RefreshAsync()
        {
            if (busy || lifetime.IsCancellationRequested) return;
            busy = true; refresh.IsEnabled = false; footer.Text = "Connecting to Codex\u2026";
            try {
                UsageSnapshot data = await Task.Run(async delegate { return await new CodexClient().ReadAsync(lifetime.Token); });
                latest = data; failed = false; problem = null;
            }
            catch (OperationCanceledException) { }
            catch (FileNotFoundException ex) { failed = true; problem = ex.Message; }
            catch (FormatException ex) { failed = true; problem = ex.Message; }
            catch (TimeoutException ex) { failed = true; problem = ex.Message; }
            catch { failed = true; problem = "Connection unavailable. Check your ChatGPT sign-in in Codex and refresh."; }
            finally { busy = false; if (!lifetime.IsCancellationRequested) { refresh.IsEnabled = true; Render(); } }
        }

        public void Render()
        {
            UsageSnapshot data = preview ?? latest;
            QuotaWindow limit = data == null ? null : data.Limiting;
            bool stale = preview == null && (failed || UsageModel.IsStale(data, DateTimeOffset.Now));
            Brush accent = limit == null || stale ? Neutral : data.Blocked ? Accent(AllowanceLevel.Red) : Accent(UsageModel.Level(limit.Remaining));
            body.Children.Clear();
            var heading = new DockPanel();
            string badge = preview != null ? "PREVIEW" : data == null ? "\u00b7\u00b7\u00b7" : String.IsNullOrEmpty(data.Plan) ? "LIVE" : data.Plan.ToUpperInvariant();
            var plan = new Border { Background = Track, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 3, 7, 3), Child = Text(badge, 9, Muted, FontWeights.SemiBold) };
            DockPanel.SetDock(plan, Dock.Right); heading.Children.Add(plan);
            var eyebrow = Text(limit == null ? "YOUR ALLOWANCE" : limit.Label.ToUpperInvariant() + " ALLOWANCE", 10, Muted, FontWeights.SemiBold);
            eyebrow.VerticalAlignment = VerticalAlignment.Center;
            heading.Children.Add(eyebrow); body.Children.Add(heading);
            var number = new TextBlock { Foreground = accent, FontWeight = FontWeights.SemiBold, Margin = new Thickness(-3, 5, 0, 0) };
            number.Inlines.Add(new System.Windows.Documents.Run(limit == null ? "\u2014" : UsageModel.Percent(limit.Remaining).TrimEnd('%')) { FontSize = 66 });
            if (limit != null) number.Inlines.Add(new System.Windows.Documents.Run("%") { FontSize = 31 });
            body.Children.Add(number);
            body.Children.Add(Text(stale && limit != null ? "last known allowance remaining" : "allowance remaining", 12, Muted, FontWeights.Normal));
            var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 15, 0, 15) };
            status.Children.Add(new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            string state = limit == null ? (failed ? "Unable to connect" : "Waiting for your allowance") : stale ? "Awaiting fresh data" : data.Blocked ? "Usage currently blocked" : UsageModel.Level(limit.Remaining) == AllowanceLevel.Green ? "Plenty of room" : UsageModel.Level(limit.Remaining) == AllowanceLevel.Orange ? "Keep an eye on usage" : "Running low";
            status.Children.Add(Text(state, 12, accent, FontWeights.Medium)); body.Children.Add(status);
            body.Children.Add(Bar(limit == null ? 0 : limit.Remaining, accent, 7));
            var used = Text(limit == null ? "Sign in to Codex with your ChatGPT account." : UsageModel.Percent(100 - limit.Remaining) + " used  \u00b7  " + limit.BucketName, 10, Muted, FontWeights.Normal);
            used.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(used);
            if (limit != null)
            {
                var resetPanel = new Border { Background = ColorBrush("#1B2829"), CornerRadius = new CornerRadius(10), Padding = new Thickness(13, 12, 13, 12), Margin = new Thickness(0, 18, 0, 0) };
                var row = new DockPanel();
                var time = Text(UsageModel.Countdown(limit.ResetsAt, DateTimeOffset.Now), 20, Ink, FontWeights.SemiBold);
                time.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(time, Dock.Right); row.Children.Add(time);
                var resetLabel = new StackPanel();
                resetLabel.Children.Add(Text("RESETS IN", 9, Muted, FontWeights.SemiBold));
                var date = Text(limit.ResetsAt.HasValue ? limit.ResetsAt.Value.ToLocalTime().ToString("ddd, MMM d \u00b7 HH:mm", CultureInfo.InvariantCulture) : "Reset time not provided", 10, Muted, FontWeights.Normal);
                date.Margin = new Thickness(0, 5, 10, 0); resetLabel.Children.Add(date); row.Children.Add(resetLabel); resetPanel.Child = row; body.Children.Add(resetPanel);
                foreach (QuotaWindow other in data.Windows.Where(w => w != limit))
                {
                    var extra = new StackPanel { Margin = new Thickness(0, 15, 0, 0) };
                    Brush otherAccent = stale ? Neutral : Accent(UsageModel.Level(other.Remaining));
                    extra.Children.Add(Text(other.BucketName + " \u00b7 " + other.Label + "  /  " + UsageModel.Percent(other.Remaining) + " left", 11, Ink, FontWeights.Medium));
                    var bar = Bar(other.Remaining, otherAccent, 4); bar.Margin = new Thickness(0, 6, 0, 6); extra.Children.Add(bar);
                    extra.Children.Add(Text("Resets in " + UsageModel.Countdown(other.ResetsAt, DateTimeOffset.Now), 10, Muted, FontWeights.Normal)); body.Children.Add(extra);
                }
            }
            if (failed && preview == null)
            {
                var error = Text(problem ?? "Refresh to reconnect.", 11, Muted, FontWeights.Normal); error.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(error);
            }
            footer.Text = busy ? "Connecting to Codex\u2026" : preview != null ? "Preview only \u00b7 refresh to return to live" : latest == null ? "Auto-refreshes every 60 seconds" : (stale ? "Last sync " : "Updated ") + latest.FetchedAt.ToLocalTime().ToString("HH:mm") + "  \u00b7  every 60s";
            if (tray != null) UpdateTray(accent, limit, stale, preview != null);
        }

        private static Brush Accent(AllowanceLevel level)
        {
            return level == AllowanceLevel.Green ? ColorBrush("#63D9AC") : level == AllowanceLevel.Orange ? ColorBrush("#FFB45E") : level == AllowanceLevel.Red ? ColorBrush("#FF777F") : Neutral;
        }
        private static Border Bar(double remaining, Brush accent, double height)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, remaining), GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 100 - remaining), GridUnitType.Star) });
            grid.Children.Add(new Border { Background = accent, CornerRadius = new CornerRadius(height / 2) });
            return new Border { Height = height, Background = Track, CornerRadius = new CornerRadius(height / 2), Child = grid };
        }
        private ContextMenu MakeMenu()
        {
            var menu = new ContextMenu();
            AddMenu(menu.Items, "Refresh now", async delegate { preview = null; await RefreshAsync(); });
            AddMenu(menu.Items, "Always on top", delegate { TogglePin(); });
            var colors = new MenuItem { Header = "Preview colors" };
            AddMenu(colors.Items, "Green \u00b7 77% remaining", delegate { PreviewAllowance(77); });
            AddMenu(colors.Items, "Orange \u00b7 35% remaining", delegate { PreviewAllowance(35); });
            AddMenu(colors.Items, "Red \u00b7 7% remaining", delegate { PreviewAllowance(7); });
            menu.Items.Add(colors);
            AddMenu(menu.Items, "Hide to tray", delegate { Hide(); });
            AddMenu(menu.Items, "Quit", delegate { Close(); });
            return menu;
        }
        private static void AddMenu(ItemCollection items, string label, RoutedEventHandler action)
        {
            var item = new MenuItem { Header = label }; item.Click += action; items.Add(item);
        }
        private void MakeTray()
        {
            tray = new Forms.NotifyIcon { Text = "Codex Usage Mini", Visible = false };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show widget", null, delegate { ShowWidget(); });
            menu.Items.Add("Refresh now", null, async delegate { preview = null; await RefreshAsync(); });
            menu.Items.Add("Always on top", null, delegate { TogglePin(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Preview green / 77%", null, delegate { PreviewAllowance(77); ShowWidget(); });
            menu.Items.Add("Preview orange / 35%", null, delegate { PreviewAllowance(35); ShowWidget(); });
            menu.Items.Add("Preview red / 7%", null, delegate { PreviewAllowance(7); ShowWidget(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowWidget(); };
            Render(); tray.Visible = true;
        }
        private void UpdateTray(Brush accent, QuotaWindow window, bool stale, bool isPreview)
        {
            Color color = ((SolidColorBrush)accent).Color;
            using (var bitmap = new Drawing.Bitmap(32, 32))
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            using (var pen = new Drawing.Pen(Drawing.Color.FromArgb(color.R, color.G, color.B), 4))
            using (var track = new Drawing.Pen(Drawing.Color.FromArgb(55, 67, 68), 4))
            {
                graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.DrawEllipse(track, 3, 3, 25, 25);
                if (window != null && window.Remaining > 0) graphics.DrawArc(pen, 3, 3, 25, 25, -90, (float)(window.Remaining * 3.6));
                using (var font = new Drawing.Font("Segoe UI", 9, Drawing.FontStyle.Bold))
                using (var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(color.R, color.G, color.B)))
                using (var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center })
                    graphics.DrawString(stale || window == null ? "?" : Math.Floor(window.Remaining).ToString(CultureInfo.InvariantCulture), font, brush, new Drawing.RectangleF(0, 0, 32, 32), format);
                IntPtr handle = bitmap.GetHicon();
                Drawing.Icon next;
                try { using (var borrowed = Drawing.Icon.FromHandle(handle)) next = (Drawing.Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
                Drawing.Icon old = trayIcon; trayIcon = next; tray.Icon = next; if (old != null) old.Dispose();
            }
            string text = "Codex: " + (window == null ? "connecting" : UsageModel.Percent(window.Remaining) + " left") + (isPreview ? " (preview)" : stale ? " (stale)" : "");
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }
        public void Capture(string path)
        {
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * 2), (int)Math.Ceiling(ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (var stream = File.Create(path)) png.Save(stream);
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool smoke = args.Contains("--smoke");
            int demoIndex = Array.IndexOf(args, "--demo");
            bool testing = smoke || demoIndex >= 0;
            bool created;
            string suffix = WindowsIdentity.GetCurrent().User.Value;
            using (var mutex = new Mutex(true, "Local\\CodexUsageMini." + suffix, out created))
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexUsageMini.Show." + suffix))
            {
                if (!created && !testing) { show.Set(); return 0; }
                var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var window = new WidgetWindow(testing);
                RegisteredWaitHandle listener = null;
                if (!testing) listener = ThreadPool.RegisterWaitForSingleObject(show, delegate { if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(new System.Action(window.ShowWidget)); }, null, Timeout.Infinite, false);
                if (demoIndex >= 0) { double value = 77; if (args.Length > demoIndex + 1) Double.TryParse(args[demoIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value); window.PreviewAllowance(Math.Max(0, Math.Min(100, value))); }
                if (smoke)
                {
                    int index = Array.IndexOf(args, "--smoke");
                    string folder = args.Length > index + 1 ? args[index + 1] : "docs";
                    window.ContentRendered += delegate {
                        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new System.Action(delegate {
                            try {
                                window.PreviewAllowance(77); window.Capture(Path.Combine(folder, "preview-green.png"));
                                window.PreviewAllowance(35); window.Capture(Path.Combine(folder, "preview-orange.png"));
                                window.PreviewAllowance(7); window.Capture(Path.Combine(folder, "preview-red.png"));
                                bool oldPin = window.Topmost; window.TogglePin(); if (window.Topmost == oldPin) throw new Exception("Pin toggle failed"); window.TogglePin();
                                window.Hide(); if (window.IsVisible) throw new Exception("Hide failed"); window.ShowWidget();
                                window.PreviewError(); window.Capture(Path.Combine(folder, "preview-offline.png"));
                                var multiple = UsageModel.Demo(77); multiple.Windows.Add(new QuotaWindow { BucketId = "codex", BucketName = "Codex", Label = "5-hour", Remaining = 35, ResetsAt = DateTimeOffset.Now.AddHours(2) });
                                window.PreviewSnapshot(multiple); window.Capture(Path.Combine(folder, "preview-multiple.png"));
                                File.WriteAllText(Path.Combine(folder, "ui-smoke.txt"), "PASS: green, orange, red, stale/offline, multiple windows, pin, hide, show.\n");
                            } catch (Exception ex) { Environment.ExitCode = 1; File.WriteAllText(Path.Combine(folder, "ui-smoke.txt"), "FAIL: " + ex.Message); }
                            window.Close();
                        }));
                    };
                }
                app.Run(window);
                if (listener != null) listener.Unregister(null);
                if (created) mutex.ReleaseMutex();
            }
            return Environment.ExitCode;
        }
    }
}
