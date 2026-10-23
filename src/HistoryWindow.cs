using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUsageMini
{
    public sealed partial class HistoryWindow : Window
    {
        private readonly Func<List<UsageEntry>> entries;
        private readonly Func<string> status;
        private readonly Palette skin;
        private readonly StackPanel body = new StackPanel();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly ComboBox range;
        private readonly CheckBox internalModels;
        private readonly TextBlock coverage;
        private int tab;
        public HistoryWindow(Func<List<UsageEntry>> source, Func<string> importStatus, string theme) : this(source, importStatus, theme, null, null, null) { }
        public HistoryWindow(Func<List<UsageEntry>> source, Func<string> importStatus, string theme, Func<List<AllowanceReading>> allowance, Func<string> recordingNotice, Func<string> account)
        {
            entries = source; status = importStatus; skin = Palette.For(theme);
            allowanceSource = allowance; allowanceNotice = recordingNotice; allowanceAccount = account;
            Title = "Codex Usage Mini · Usage history";
            Width = 900; Height = 680; MinWidth = 740; MinHeight = 520;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            Background = skin.Background; Foreground = skin.Ink;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var layout = new DockPanel { Margin = new Thickness(24) };
            var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
            header.Children.Add(Ui.Text("Your usage, over time", 25, skin.Ink));
            var subtitle = Ui.Text("Token activity and independently recorded allowance history. Retained for 90 days.", 12, skin.Muted); subtitle.Margin = new Thickness(0, 5, 0, 18); header.Children.Add(subtitle);
            var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            tools.Children.Add(Ui.Button("Allowance", delegate { SelectAllowance(); }, skin));
            tools.Children.Add(Ui.Button("Activity", delegate { tab = 0; RefreshView(); }, skin));
            tools.Children.Add(Ui.Button("Models", delegate { tab = 1; RefreshView(); }, skin));
            range = new ComboBox { ItemsSource = new[] { "Last 24 hours", "Last 7 days", "Last 30 days", "Last 90 days" }, SelectedIndex = 0, Width = 132, Margin = new Thickness(8, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(range, "History time range");
            range.SelectionChanged += delegate { RefreshView(); }; tools.Children.Add(range);
            internalModels = new CheckBox { Content = "Include internal review models", Foreground = skin.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 0) };
            internalModels.Checked += delegate { RefreshView(); }; internalModels.Unchecked += delegate { RefreshView(); }; tools.Children.Add(internalModels);
            tools.Children.Add(Ui.Button("Export CSV", delegate { Export(); }, skin)); header.Children.Add(tools);
            allowanceSeries = new ComboBox { Width = 260, DisplayMemberPath = "Value", SelectedValuePath = "Key", Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
            System.Windows.Automation.AutomationProperties.SetName(allowanceSeries, "Allowance window");
            allowanceSeries.SelectionChanged += AllowanceSeriesChanged; header.Children.Add(allowanceSeries);
            coverage = Ui.Text("", 11, skin.Muted); coverage.Margin = new Thickness(0, 0, 0, 12); header.Children.Add(coverage);
            layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = layout;
            timer.Interval = TimeSpan.FromSeconds(10); timer.Tick += delegate { RefreshView(); };
            Loaded += delegate { RefreshView(); timer.Start(); }; Closed += delegate { timer.Stop(); };
        }
        public void SelectModels() { tab = 1; RefreshView(); }
        public void SelectHours() { tab = 0; RefreshView(); }
        private int Days { get { return range.SelectedIndex == 0 ? 1 : range.SelectedIndex == 1 ? 7 : range.SelectedIndex == 2 ? 30 : 90; } }
        private List<UsageEntry> Selected(HistoryRange period)
        {
            return HistoryQuery.Select(entries(), period.Start, period.End, internalModels.IsChecked == true);
        }
        public void RefreshView()
        {
            if (range == null || internalModels == null || coverage == null) return;
            var period = HistoryRange.Create(Days, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
            body.Children.Clear();
            internalModels.Visibility = tab == 2 ? Visibility.Collapsed : Visibility.Visible;
            allowanceSeries.Visibility = tab == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (tab == 2) { RenderAllowance(period); return; }
            var selected = Selected(period);
            string extra = status();
            coverage.Text = period.Description + "\nLocal records only · " + selected.Count.ToString("N0") + " usage events" + (String.IsNullOrEmpty(extra) ? "" : " · " + extra);
            if (selected.Count == 0)
            {
                body.Children.Add(Ui.Text("No recorded usage in this range yet", 20, skin.Ink));
                var empty = Ui.Text("Available local session history is imported automatically. Keep Codex and this widget running to record new activity. Cloud-only activity and other devices are not included.", 13, skin.Muted); empty.Margin = new Thickness(0, 12, 0, 0); body.Children.Add(empty); return;
            }
            long total = selected.Sum(e => e.Total);
            var timeline = HistoryQuery.Timeline(selected, period);
            var models = HistoryQuery.Models(selected);
            var cards = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 20) };
            cards.Children.Add(Card("OBSERVED TOKENS", HistoryQuery.Compact(total)));
            cards.Children.Add(Card(period.Daily ? "ACTIVE DAYS" : "ACTIVE HOURS", timeline.Count(g => g.Events > 0).ToString(CultureInfo.InvariantCulture)));
            cards.Children.Add(Card("MODELS USED", models.Count.ToString(CultureInfo.InvariantCulture)));
            body.Children.Add(cards);
            if (tab == 0) RenderActivity(timeline, period); else RenderModels(models, total);
            var note = Ui.Text("Cached input is part of input; reasoning is part of output. Total tokens include cached input. These records do not reveal each model’s charge against your plan allowance.", 11, skin.Muted); note.Margin = new Thickness(0, 18, 0, 0); body.Children.Add(note);
        }
        private Border Card(string label, string value)
        {
            var stack = new StackPanel(); stack.Children.Add(Ui.Text(label, 10, skin.Muted)); var text = Ui.Text(value, 25, skin.Ink); text.Margin = new Thickness(0, 5, 0, 0); stack.Children.Add(text);
            return new Border { Child = stack, Background = skin.Surface, CornerRadius = new CornerRadius(9), Padding = new Thickness(15), Margin = new Thickness(0, 0, 9, 0) };
        }
        private void RenderActivity(List<UsageGroup> timeline, HistoryRange period)
        {
            body.Children.Add(Ui.Text(period.Daily ? "Daily activity · last " + period.Days + " days" : "Hourly activity · last 24 hours", 17, skin.Ink));
            int columns = timeline.Count;
            long maximum = Math.Max(1, timeline.Max(g => g.Total));
            var bars = new UniformGrid { Columns = columns, Margin = new Thickness(0, 15, 0, 0) };
            for (int i = 0; i < columns; i++)
            {
                var group = timeline[i];
                var time = TimeZoneInfo.ConvertTime(new DateTimeOffset(group.UtcTicks, TimeSpan.Zero), period.Zone);
                string tip = time.ToString(period.Daily ? "ddd, MMM d, yyyy" : "MMM d, yyyy HH:mm zzz", CultureInfo.InvariantCulture);
                if (period.Daily && i == columns - 1) tip += " · today so far";
                var track = new Grid { Height = 120, Background = skin.Surface, Margin = new Thickness(columns > 30 ? 1 : 2, 0, columns > 30 ? 1 : 2, 0), ToolTip = tip + "\n" + group.Total.ToString("N0") + " tokens · " + group.Events.ToString("N0") + " events" };
                track.Children.Add(new Border { Height = group.Total == 0 ? 2 : Math.Max(4, (double)group.Total / maximum * 120), Background = group.Total == 0 ? skin.Track : skin.Highlight, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(2) });
                bars.Children.Add(track);
            }
            body.Children.Add(bars);
            // Keep date labels outside the bars so long ranges retain their full width.
            var axis = new Canvas { Height = 27, Margin = new Thickness(0, 5, 0, 0) };
            int step = Math.Max(1, (int)Math.Ceiling((columns - 1) / 6d));
            for (int i = 0; i < columns; i++)
            {
                if (i != 0 && i != columns - 1 && (i % step != 0 || columns - 1 - i < Math.Max(1, step / 2))) continue;
                var label = Ui.Text(timeline[i].Label, 10, skin.Muted); label.TextWrapping = TextWrapping.NoWrap; label.Tag = i; axis.Children.Add(label);
            }
            axis.SizeChanged += delegate {
                foreach (TextBlock label in axis.Children) {
                    label.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
                    double x = ((int)label.Tag + .5) / columns * axis.ActualWidth - label.DesiredSize.Width / 2;
                    Canvas.SetLeft(label, Math.Max(0, Math.Min(axis.ActualWidth - label.DesiredSize.Width, x)));
                }
            };
            body.Children.Add(axis);
            body.Children.Add(Ui.Text(period.Daily ? "Local date · one bar per day, including today so far. Hover for tokens and events. Empty bars mean no observed events." : "Local hour · hover a bar for tokens and activity count. Empty bars mean no observed events.", 11, skin.Muted));
            var peak = timeline.OrderByDescending(g => g.Total).First();
            var peakTime = TimeZoneInfo.ConvertTime(new DateTimeOffset(peak.UtcTicks, TimeSpan.Zero), period.Zone);
            var peakText = Ui.Text("Busiest recorded " + (period.Daily ? "day: " : "hour: ") + peakTime.ToString(period.Daily ? "MMM d, yyyy" : "MMM d, HH:mm", CultureInfo.InvariantCulture) + " · " + HistoryQuery.Compact(peak.Total) + " tokens · " + peak.Events.ToString("N0") + " events", 12, skin.Ink); peakText.Margin = new Thickness(0, 14, 0, 18); body.Children.Add(peakText);
            AddRow(new[] { period.Daily ? "Date (local)" : "Hour (local)", "Events", "Input", "Cached", "Output", "Total" }, true);
            foreach (var group in timeline.AsEnumerable().Reverse().Where(g => period.Daily || g.Events > 0))
                AddRow(new[] { TimeZoneInfo.ConvertTime(new DateTimeOffset(group.UtcTicks, TimeSpan.Zero), period.Zone).ToString(period.Daily ? "MMM d, yyyy" : "MMM d HH:mm zzz", CultureInfo.InvariantCulture), group.Events.ToString("N0"), HistoryQuery.Compact(group.Input), HistoryQuery.Compact(group.Cached), HistoryQuery.Compact(group.Output), HistoryQuery.Compact(group.Total) }, false);
        }
        internal void CheckTimeRanges(string folder)
        {
            tab = 0;
            for (int index = 1; index <= 3; index++)
            {
                range.SelectedIndex = index; RefreshView(); UpdateLayout();
                var chart = body.Children.OfType<UniformGrid>().Last();
                if (chart.Columns != Days || chart.Children.Count != Days || !body.Children.OfType<TextBlock>().Any(t => t.Text == "Daily activity · last " + Days + " days")) throw new Exception("Daily history range did not render every date");
                Ui.Capture(this, Path.Combine(folder, "preview-history-" + Days + "-days.png"));
            }
            range.SelectedIndex = 0; RefreshView();
            if (!body.Children.OfType<TextBlock>().Any(t => t.Text == "Hourly activity · last 24 hours")) throw new Exception("Hourly history was not restored");
        }
        private void RenderModels(List<UsageGroup> models, long total)
        {
            body.Children.Add(Ui.Text("Usage by model", 17, skin.Ink));
            var explanation = Ui.Text("Compare recorded input, cached input, output, and total tokens before choosing a smaller model. Model-specific allowance multipliers are not exposed here.", 12, skin.Muted); explanation.Margin = new Thickness(0, 8, 0, 18); body.Children.Add(explanation);
            AddRow(new[] { "Model / token share", "Events", "Input", "Cached", "Output", "Total" }, true);
            foreach (var model in models)
            {
                double share = total > 0 ? model.Total * 100d / total : 0;
                AddRow(new[] { model.Label + " · " + share.ToString("0.#", CultureInfo.InvariantCulture) + "%", model.Events.ToString("N0"), HistoryQuery.Compact(model.Input), HistoryQuery.Compact(model.Cached), HistoryQuery.Compact(model.Output), HistoryQuery.Compact(model.Total) }, false);
                var bar = new Grid(); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) }); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - share, GridUnitType.Star) }); bar.Children.Add(new Border { Background = skin.Highlight }); body.Children.Add(new Border { Background = skin.Track, Height = 3, Margin = new Thickness(0, 0, 0, 9), Child = bar });
            }
        }
        private void AddRow(string[] cells, bool heading)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, heading ? 8 : 11) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.6, GridUnitType.Star) });
            for (int i = 1; i < cells.Length; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < cells.Length; i++) { var cell = Ui.Text(cells[i], heading ? 10 : 12, heading ? skin.Muted : skin.Ink); cell.Margin = new Thickness(0, 3, 8, 3); if (heading) cell.FontWeight = FontWeights.SemiBold; Grid.SetColumn(cell, i); row.Children.Add(cell); }
            body.Children.Add(row);
        }
        private static string Csv(string value)
        {
            if (!String.IsNullOrEmpty(value) && "=+-@".IndexOf(value[0]) >= 0) value = "'" + value;
            return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        }
        private void Export()
        {
            if (tab == 2) { ExportAllowance(); return; }
            string profile = Path.Combine("E:\\Users", Environment.UserName, "Documents");
            var picker = new Microsoft.Win32.SaveFileDialog { Title = "Export hourly model usage", Filter = "CSV file (*.csv)|*.csv", FileName = "Codex-usage-" + DateTime.Today.ToString("yyyy-MM-dd") + ".csv", InitialDirectory = Directory.Exists(profile) ? profile : LocalData.DirectoryPath };
            if (picker.ShowDialog(this) != true) return;
            var output = new StringBuilder("hour_local,hour_utc,model,events,input_tokens,cached_input_tokens,output_tokens,reasoning_output_tokens,total_tokens\r\n");
            foreach (var group in Selected(HistoryRange.Create(Days, DateTimeOffset.UtcNow, TimeZoneInfo.Local)).GroupBy(e => new { Hour = e.UtcTicks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour, e.Model }).OrderBy(g => g.Key.Hour).ThenBy(g => g.Key.Model))
            {
                var stamp = new DateTimeOffset(group.Key.Hour, TimeSpan.Zero);
                output.Append(Csv(stamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture))).Append(',').Append(Csv(stamp.ToString("o"))).Append(',').Append(Csv(group.Key.Model)).Append(',').Append(group.Count()).Append(',').Append(group.Sum(e => e.Input)).Append(',').Append(group.Sum(e => e.Cached)).Append(',').Append(group.Sum(e => e.Output)).Append(',').Append(group.Sum(e => e.Reasoning)).Append(',').Append(group.Sum(e => e.Total)).Append("\r\n");
            }
            try { File.WriteAllText(picker.FileName, output.ToString(), new UTF8Encoding(true)); }
            catch (IOException) { MessageBox.Show(this, "The CSV could not be saved. Choose a writable location.", "Export usage"); }
            catch (UnauthorizedAccessException) { MessageBox.Show(this, "The CSV could not be saved. Choose a writable location.", "Export usage"); }
        }
    }
}
