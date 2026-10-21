using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexUsageMini
{
    public static class Ui
    {
        public static TextBlock Text(string text, double size, Brush color)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap };
        }
        public static Button Button(string label, RoutedEventHandler action, Palette theme)
        {
            var button = new Button { Content = label, Background = theme.Surface, Foreground = theme.Ink, BorderBrush = theme.Border, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand };
            if (action != null) button.Click += action;
            return button;
        }
        public static void Capture(Window window, string path)
        {
            window.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 2), (int)Math.Ceiling(window.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            using (var stream = System.IO.File.Create(path)) png.Save(stream);
        }
    }

    public sealed class SettingsWindow : Window
    {
        private readonly Preferences settings;
        private readonly Action apply;
        private readonly string accountKey;
        private StackPanel panel;
        private bool building;
        private readonly Func<SubscriptionDisplay> subscriptionInfo;
        private readonly Func<Task> refreshSubscription;
        private TextBlock subscriptionStatus;
        private Button checkSubscription;
        public SettingsWindow(Preferences prefs, Action changed, string plan, string account, bool save = true, Func<SubscriptionDisplay> subscriptionInfo = null, Func<Task> refreshSubscription = null)
        {
            settings = prefs; apply = changed; accountKey = account; this.subscriptionInfo = subscriptionInfo; this.refreshSubscription = refreshSubscription;
            MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 32);
            Title = "Codex Usage Mini · Appearance & plan";
            Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Rebuild(plan);
            Closed += delegate { if (save) settings.Save(); };
        }
        private void Rebuild(string plan)
        {
            building = true;
            Palette skin = Palette.For(settings.Theme);
            Background = skin.Background; Foreground = skin.Ink;
            panel = new StackPanel { Margin = new Thickness(24) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            panel.Children.Add(Ui.Text("Make it your mini", 22, skin.Ink));
            AddLabel("THEME", skin, 20);
            var themes = new ComboBox { ItemsSource = Palette.Names, SelectedItem = settings.Theme, Height = 30 };
            System.Windows.Automation.AutomationProperties.SetName(themes, "Display theme");
            themes.SelectionChanged += delegate { if (!building && themes.SelectedItem != null) { settings.Theme = themes.SelectedItem.ToString(); apply(); Rebuild(plan); } };
            panel.Children.Add(themes);
            AddLabel("FULL WIDGET SIZE", skin, 20);
            var sizeValue = Ui.Text("", 12, skin.Muted);
            var size = new Slider { Minimum = 50, Maximum = 125, Value = settings.Scale * 100, TickFrequency = 5, IsSnapToTickEnabled = true, Margin = new Thickness(0, 8, 0, 5) };
            System.Windows.Automation.AutomationProperties.SetName(size, "Full widget size percent");
            Action updateSize = delegate { sizeValue.Text = ((int)size.Value) + "% of width and height · " + Math.Round(size.Value * size.Value / 100) + "% of original area"; };
            size.ValueChanged += delegate { updateSize(); if (!building) { settings.Scale = size.Value / 100; apply(); } }; updateSize();
            panel.Children.Add(size); panel.Children.Add(sizeValue);
            panel.Children.Add(Ui.Text("100% is the original readable size. Click the allowance percentage for a small percentage-only view. That view keeps its own readable font size.", 11, skin.Muted));
            AddLabel("TRANSPARENCY", skin, 20);
            var opacityValue = Ui.Text("", 12, skin.Muted);
            var opacity = new Slider { Minimum = 0, Maximum = 50, Value = settings.Transparency, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 8, 0, 5) };
            System.Windows.Automation.AutomationProperties.SetName(opacity, "Transparency percent");
            Action updateOpacity = delegate { opacityValue.Text = ((int)opacity.Value) + "% transparent · " + (100 - (int)opacity.Value) + "% opaque"; };
            opacity.ValueChanged += delegate { updateOpacity(); if (!building) { settings.Transparency = opacity.Value; apply(); } }; updateOpacity();
            panel.Children.Add(opacity); panel.Children.Add(opacityValue);
            AddLabel("SUBSCRIPTION", skin, 22);
            panel.Children.Add(Ui.Text("Plan: " + (String.IsNullOrWhiteSpace(plan) ? "Not provided by Codex" : plan), 14, skin.Ink));
            var automatic = new CheckBox { Content = "Detect subscription date from Codex", IsChecked = settings.DetectPlanExpiry, Margin = new Thickness(0, 12, 0, 8), Foreground = skin.Ink };
            System.Windows.Automation.AutomationProperties.SetName(automatic, "Detect subscription date from Codex");
            RoutedEventHandler detectionChanged = async delegate {
                if (building) return; settings.DetectPlanExpiry = automatic.IsChecked == true; apply(); RefreshSubscription();
                if (settings.DetectPlanExpiry && refreshSubscription != null) { await refreshSubscription(); RefreshSubscription(); }
            };
            automatic.Checked += detectionChanged; automatic.Unchecked += detectionChanged; panel.Children.Add(automatic);
            subscriptionStatus = Ui.Text("", 11, skin.Muted); panel.Children.Add(subscriptionStatus); RefreshSubscription();
            if (refreshSubscription != null) {
                var detect = Ui.Button("Check now", null, skin); checkSubscription = detect; detect.IsEnabled = settings.DetectPlanExpiry; detect.Visibility = settings.DetectPlanExpiry ? Visibility.Visible : Visibility.Collapsed; detect.Margin = new Thickness(0, 8, 0, 6); detect.HorizontalAlignment = HorizontalAlignment.Left;
                detect.Click += async delegate { detect.IsEnabled = false; detect.Content = "Checking…"; try { await refreshSubscription(); RefreshSubscription(); } finally { detect.IsEnabled = settings.DetectPlanExpiry; detect.Content = "Check now"; } };
                panel.Children.Add(detect);
            }
            var note = Ui.Text("Automatic dates are the paid-through period in Codex sign-in metadata. Renewal status may be unavailable. Your manual fallback stays separate from automatic detection and allowance resets.", 11, skin.Muted);
            note.Margin = new Thickness(0, 7, 0, 10); panel.Children.Add(note);
            panel.Children.Add(Ui.Text("Manual fallback expiry (optional)", 12, skin.Ink));
            DateTime parsed;
            var expiry = new DatePicker { SelectedDate = DateTime.TryParseExact(settings.PlanExpiry, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) ? (DateTime?)parsed : null, Margin = new Thickness(0, 6, 0, 10), Height = 30 };
            System.Windows.Automation.AutomationProperties.SetName(expiry, "Plan expiry date");
            expiry.SelectedDateChanged += delegate { if (!building) { settings.PlanExpiry = expiry.SelectedDate.HasValue ? expiry.SelectedDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null; settings.ExpiryAccountKey = expiry.SelectedDate.HasValue ? accountKey : null; apply(); RefreshSubscription(); } };
            panel.Children.Add(expiry);
            if (!String.IsNullOrEmpty(settings.ExpiryAccountKey) && !String.IsNullOrEmpty(accountKey) && settings.ExpiryAccountKey != accountKey) panel.Children.Add(Ui.Text("The saved expiry belongs to a different sign-in. Enter a date for this account.", 11, skin.Muted));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 13, 0, 0) };
            buttons.Children.Add(Ui.Button("Clear expiry", delegate { expiry.SelectedDate = null; }, skin));
            buttons.Children.Add(Ui.Button("Done", delegate { Close(); }, skin));
            panel.Children.Add(buttons);
            building = false;
        }
        public void RefreshSubscription()
        {
            if (subscriptionStatus == null) return;
            if (checkSubscription != null) { checkSubscription.IsEnabled = settings.DetectPlanExpiry; checkSubscription.Visibility = settings.DetectPlanExpiry ? Visibility.Visible : Visibility.Collapsed; }
            SubscriptionDisplay view = subscriptionInfo == null ? SubscriptionDisplay.Create(null, settings, accountKey, DateTimeOffset.Now) : subscriptionInfo();
            subscriptionStatus.Text = (view.Automatic ? "Paid through: " + view.Value + "\n" : "") + view.Detail;
        }
        private void AddLabel(string value, Palette skin, double margin)
        {
            var label = Ui.Text(value, 10, skin.Muted); label.FontWeight = FontWeights.SemiBold; label.Margin = new Thickness(0, margin, 0, 4); panel.Children.Add(label);
        }
    }
}
