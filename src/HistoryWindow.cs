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
    public sealed class HistoryWindow : Window
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
        public HistoryWindow(Func<List<UsageEntry>> source, Func<string> importStatus, string theme)
        {
            entries = source; status = importStatus; skin = Palette.For(theme);
            Title = "Codex Usage Mini · Usage history";
            Width = 900; Height = 680; MinWidth = 740; MinHeight = 520;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            Background = skin.Background; Foreground = skin.Ink;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var layout = new DockPanel { Margin = new Thickness(24) };
            var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
            header.Children.Add(Ui.Text("Your usage, over time", 25, skin.Ink));
            var subtitle = Ui.Text("Observed token usage from this local Codex profile. Retained for 90 days.", 12, skin.Muted); subtitle.Margin = new Thickness(0, 5, 0, 18); header.Children.Add(subtitle);
            var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            tools.Children.Add(Ui.Button("Hours", delegate { tab = 0; RefreshView(); }, skin));
            tools.Children.Add(Ui.Button("Models", delegate { tab = 1; RefreshView(); }, skin));
            range = new ComboBox { ItemsSource = new[] { "Last 24 hours", "Last 7 days", "Last 30 days", "Last 90 days" }, SelectedIndex = 0, Width = 132, Margin = new Thickness(8, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(range, "History time range");
            range.SelectionChanged += delegate { RefreshView(); }; tools.Children.Add(range);
            internalModels = new CheckBox { Content = "Include internal review models", Foreground = skin.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 0) };
            internalModels.Checked += delegate { RefreshView(); }; internalModels.Unchecked += delegate { RefreshView(); }; tools.Children.Add(internalModels);
            tools.Children.Add(Ui.Button("Export CSV", delegate { Export(); }, skin)); header.Children.Add(tools);
            coverage = Ui.Text("", 11, skin.Muted); coverage.Margin = new Thickness(0, 0, 0, 12); header.Children.Add(coverage);
            layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = layout;
            timer.Interval = TimeSpan.FromSeconds(10); timer.Tick += delegate { RefreshView(); };
            Loaded += delegate { RefreshView(); timer.Start(); }; Closed += delegate { timer.Stop(); };
        }
        public void SelectModels() { tab = 1; RefreshView(); }
        public void SelectHours() { tab = 0; RefreshView(); }
        private int Days { get { return range.SelectedIndex == 0 ? 1 : range.SelectedIndex == 1 ? 7 : range.SelectedIndex == 2 ? 30 : 90; } }
        private List<UsageEntry> Selected()
        {
            var now = DateTimeOffset.UtcNow;
            return HistoryQuery.Select(entries(), now.AddDays(-Days), now.AddSeconds(1), internalModels.IsChecked == true);
        }
        public void RefreshView()
        {
            if (range == null || internalModels == null || coverage == null) return;
            var selected = Selected(); body.Children.Clear();
            string extra = status();
            coverage.Text = "Local records only · " + selected.Count.ToString("N0") + " usage events" + (String.IsNullOrEmpty(extra) ? "" : " · " + extra);
            if (selected.Count == 0)
            {
                body.Children.Add(Ui.Text("No recorded usage in this range yet", 20, skin.Ink));
                var empty = Ui.Text("Available local session history is imported automatically. Keep Codex and this widget running to record new activity. Cloud-only activity and other devices are not included.", 13, skin.Muted); empty.Margin = new Thickness(0, 12, 0, 0); body.Children.Add(empty); return;
            }
            long total = selected.Sum(e => e.Total);
            var hours = HistoryQuery.Hours(selected);
            var models = HistoryQuery.Models(selected);
            var cards = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 20) };
            cards.Children.Add(Card("OBSERVED TOKENS", HistoryQuery.Compact(total)));
            cards.Children.Add(Card("ACTIVE HOURS", hours.Count.ToString(CultureInfo.InvariantCulture)));
            cards.Children.Add(Card("MODELS USED", models.Count.ToString(CultureInfo.InvariantCulture)));
            body.Children.Add(cards);
            if (tab == 0) RenderHours(selected, hours); else RenderModels(models, total);
            var note = Ui.Text("Cached input is part of input; reasoning is part of output. Total tokens include cached input. These records do not reveal each model’s charge against your plan allowance.", 11, skin.Muted); note.Margin = new Thickness(0, 18, 0, 0); body.Children.Add(note);
        }
        private Border Card(string label, string value)
        {
            var stack = new StackPanel(); stack.Children.Add(Ui.Text(label, 10, skin.Muted)); var text = Ui.Text(value, 25, skin.Ink); text.Margin = new Thickness(0, 5, 0, 0); stack.Children.Add(text);
            return new Border { Child = stack, Background = skin.Surface, CornerRadius = new CornerRadius(9), Padding = new Thickness(15), Margin = new Thickness(0, 0, 9, 0) };
        }
        private void RenderHours(List<UsageEntry> selected, List<UsageGroup> hours)
        {
            body.Children.Add(Ui.Text(Days == 1 ? "Hourly activity · last 24 hours" : "Hour-of-day pattern · selected range", 17, skin.Ink));
            int columns = Days == 1 ? 25 : 24;
            long[] totals = new long[columns]; int[] counts = new int[columns]; string[] labels = new string[columns]; string[] tips = new string[columns];
            if (Days == 1)
            {
                long current = DateTime.UtcNow.Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour;
                for (int i = 0; i < columns; i++)
                {
                    long ticks = current - (columns - 1 - i) * TimeSpan.TicksPerHour;
                    var group = hours.FirstOrDefault(h => h.UtcTicks == ticks);
                    var time = new DateTimeOffset(ticks, TimeSpan.Zero).ToLocalTime();
                    totals[i] = group == null ? 0 : group.Total; counts[i] = group == null ? 0 : group.Events;
                    labels[i] = time.ToString("HH"); tips[i] = time.ToString("MMM d HH:mm zzz", CultureInfo.InvariantCulture);
                }
            }
            else
            {
                foreach (var entry in selected) { int hour = new DateTimeOffset(entry.UtcTicks, TimeSpan.Zero).ToLocalTime().Hour; totals[hour] += entry.Total; counts[hour]++; }
                for (int i = 0; i < 24; i++) { labels[i] = i.ToString("00"); tips[i] = labels[i] + ":00–" + labels[i] + ":59, all selected days"; }
            }
            long maximum = Math.Max(1, totals.Max());
            var bars = new UniformGrid { Columns = columns, Margin = new Thickness(0, 15, 0, 6) };
            for (int i = 0; i < columns; i++)
            {
                var column = new StackPanel { Margin = new Thickness(2, 0, 2, 0), ToolTip = tips[i] + "\n" + totals[i].ToString("N0") + " tokens · " + counts[i] + " events" };
                var track = new Grid { Height = 120, Background = skin.Surface };
                track.Children.Add(new Border { Height = totals[i] == 0 ? 2 : Math.Max(4, (double)totals[i] / maximum * 120), Background = totals[i] == 0 ? skin.Track : skin.Highlight, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(2) });
                column.Children.Add(track);
                var label = Ui.Text(i % 3 == 0 || i == columns - 1 ? labels[i] : "", 10, skin.Muted); label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 5, 0, 0); column.Children.Add(label); bars.Children.Add(column);
            }
            body.Children.Add(bars);
            body.Children.Add(Ui.Text("Local hour · hover a bar for tokens and activity count. Empty bars mean no observed events.", 11, skin.Muted));
            var peak = hours.OrderByDescending(h => h.Total).First();
            var peakText = Ui.Text("Busiest recorded hour: " + new DateTimeOffset(peak.UtcTicks, TimeSpan.Zero).ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture) + " · " + HistoryQuery.Compact(peak.Total) + " tokens · " + peak.Events + " events", 12, skin.Ink); peakText.Margin = new Thickness(0, 14, 0, 18); body.Children.Add(peakText);
            AddRow(new[] { "Hour (local)", "Events", "Input", "Cached", "Output", "Total" }, true);
            foreach (var hour in hours.OrderByDescending(h => h.UtcTicks).Take(48))
                AddRow(new[] { new DateTimeOffset(hour.UtcTicks, TimeSpan.Zero).ToLocalTime().ToString("MMM d HH:mm zzz", CultureInfo.InvariantCulture), hour.Events.ToString("N0"), HistoryQuery.Compact(hour.Input), HistoryQuery.Compact(hour.Cached), HistoryQuery.Compact(hour.Output), HistoryQuery.Compact(hour.Total) }, false);
            if (hours.Count > 48) body.Children.Add(Ui.Text("Showing the latest 48 active hours. Export CSV includes the full selected range.", 11, skin.Muted));
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
            string profile = Path.Combine("E:\\Users", Environment.UserName, "Documents");
            var picker = new Microsoft.Win32.SaveFileDialog { Title = "Export hourly model usage", Filter = "CSV file (*.csv)|*.csv", FileName = "Codex-usage-" + DateTime.Today.ToString("yyyy-MM-dd") + ".csv", InitialDirectory = Directory.Exists(profile) ? profile : LocalData.DirectoryPath };
            if (picker.ShowDialog(this) != true) return;
            var output = new StringBuilder("hour_local,hour_utc,model,events,input_tokens,cached_input_tokens,output_tokens,reasoning_output_tokens,total_tokens\r\n");
            foreach (var group in Selected().GroupBy(e => new { Hour = e.UtcTicks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour, e.Model }).OrderBy(g => g.Key.Hour).ThenBy(g => g.Key.Model))
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
