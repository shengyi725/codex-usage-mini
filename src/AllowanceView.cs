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
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace CodexUsageMini
{
    public sealed partial class HistoryWindow
    {
        private Func<List<AllowanceReading>> allowanceSource;
        private Func<string> allowanceNotice, allowanceAccount;
        private ComboBox allowanceSeries;
        private string selectedSeries;
        private List<AllowanceReading> AccountReadings()
        {
            string account = allowanceAccount == null ? null : allowanceAccount();
            return (allowanceSource == null ? new List<AllowanceReading>() : allowanceSource()).Where(r => AllowanceHistory.SameAccount(r.AccountKey, account)).OrderBy(r => r.UtcTicks).ToList();
        }
        public void SelectAllowance() { tab = 2; RefreshView(); }
        private List<AllowanceReading> SelectedAllowance(HistoryRange period, bool includePrior)
        {
            var all = AccountReadings().Where(r => r.Series == selectedSeries && r.UtcTicks < period.End.UtcDateTime.Ticks).ToList();
            var chosen = all.Where(r => r.UtcTicks >= period.Start.UtcDateTime.Ticks).ToList();
            if (includePrior) {
                var prior = all.LastOrDefault(r => r.UtcTicks < period.Start.UtcDateTime.Ticks);
                if (prior != null) chosen.Insert(0, prior);
            }
            return chosen;
        }
        private void RenderAllowance(HistoryRange period)
        {
            var all = AccountReadings();
            var windows = all.GroupBy(r => r.Series).Select(g => g.Last()).OrderBy(r => r.BucketName).ThenBy(r => r.DurationMinutes).ToList();
            if (selectedSeries == null || !windows.Any(r => r.Series == selectedSeries)) {
                var first = windows.Where(r => r.Valid).OrderBy(r => r.Remaining).FirstOrDefault() ?? windows.FirstOrDefault();
                selectedSeries = first == null ? null : first.Series;
            }
            // Rebuilding the selector must not recursively invoke RefreshView.
            allowanceSeries.SelectionChanged -= AllowanceSeriesChanged;
            allowanceSeries.ItemsSource = windows.Select(r => new KeyValuePair<string, string>(r.Series, (r.BucketName ?? "Codex") + " · " + r.Label + " (" + r.WindowKey + ")")).ToList();
            allowanceSeries.SelectedValue = selectedSeries;
            allowanceSeries.SelectionChanged += AllowanceSeriesChanged;
            var chosen = SelectedAllowance(period, false);
            var valid = chosen.Where(r => r.Valid).ToList();
            var events = AllowanceQuery.Events(SelectedAllowance(period, true)).Where(e => e.After.UtcTicks >= period.Start.UtcDateTime.Ticks).ToList();
            string notice = allowanceNotice == null ? null : allowanceNotice();
            coverage.Text = period.Description + "\nAllowance recorded by this widget · current account · " + chosen.Count.ToString("N0") + " samples" + (String.IsNullOrEmpty(notice) ? "" : " · " + notice);
            if (chosen.Count == 0) {
                body.Children.Add(Ui.Text("No allowance readings in this range yet", 20, skin.Ink));
                body.Children.Add(Ui.Text("Recording starts while this widget is running. Successful readings are saved every minute, independently of Codex's token logs. Earlier percentages cannot be reconstructed reliably.", 13, skin.Muted));
                return;
            }
            var latest = chosen.Last();
            bool current = latest.Valid && period.End.UtcDateTime.Ticks - latest.UtcTicks <= 3 * TimeSpan.TicksPerMinute && (!latest.ResetUtcTicks.HasValue || latest.ResetUtcTicks > period.End.UtcDateTime.Ticks);
            int rollovers = events.Count(e => e.Rollover), replenishments = events.Count(e => e.Kind == "Early replenishment");
            var cards = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 16) };
            cards.Children.Add(Card(current ? "REMAINING" : "LAST VALID READING", valid.Count == 0 ? "Unknown" : UsageModel.Percent(valid.Last().Remaining.Value)));
            cards.Children.Add(Card("OBSERVED DROP", AllowanceQuery.Number(AllowanceQuery.Drop(chosen)) + " pp"));
            cards.Children.Add(Card("ROLLOVERS / EARLY REFILLS", rollovers + " / " + replenishments)); body.Children.Add(cards);
            if (!current) body.Children.Add(Ui.Text("Live allowance is unavailable or awaiting a fresh reset reading. The value above is historical.", 12, skin.Muted));
            var mostRecent = valid.LastOrDefault();
            string resetText = mostRecent != null && mostRecent.ResetUtcTicks.HasValue ? LocalStamp(mostRecent.ResetUtcTicks.Value) : "Not provided";
            body.Children.Add(Ui.Text((mostRecent == null ? "No valid reading yet" : "Last valid reading: " + LocalStamp(mostRecent.UtcTicks)) + " · latest reported reset: " + resetText + (mostRecent != null && mostRecent.AvailableResets.HasValue ? " · reset credits available: " + mostRecent.AvailableResets.Value : ""), 12, skin.Muted));
            body.Children.Add(Ui.Text("Remaining allowance · " + (latest.BucketName ?? "Codex") + " · " + latest.Label, 17, skin.Ink));
            PlotAllowance(chosen, period);
            body.Children.Add(Ui.Text("The line uses a fixed 0–100% scale. Gaps mark missing readings, pauses, or reset transitions. Observed drop counts percentage-point decreases between readings within three minutes in the same window; usage during gaps and across resets is unknown.", 11, skin.Muted));
            var rolloverEvents = events.Where(e => e.Rollover).ToList();
            if (rolloverEvents.Count >= 2) {
                double hours = (rolloverEvents.Last().After.UtcTicks - rolloverEvents.First().After.UtcTicks) / (double)TimeSpan.TicksPerHour / (rolloverEvents.Count - 1);
                body.Children.Add(Ui.Text("Average separation of observed rollovers: " + AllowanceQuery.Number(hours) + " hours. Recording gaps may hide additional resets.", 12, skin.Muted));
            }
            var title = Ui.Text(period.Daily ? "Daily allowance readings" : "Hourly allowance readings", 17, skin.Ink); title.Margin = new Thickness(0, 20, 0, 10); body.Children.Add(title);
            AddRow(new[] { period.Daily ? "Date (local)" : "Hour (local)", "Last left", "Min–max", "Drop (pp)", "Samples", "Rollover / refill" }, true);
            foreach (var group in chosen.GroupBy(r => TimeZoneInfo.ConvertTime(new DateTimeOffset(r.UtcTicks, TimeSpan.Zero), period.Zone).ToString(period.Daily ? "yyyy-MM-dd" : "yyyy-MM-dd HH:00 zzz", CultureInfo.InvariantCulture)).Reverse()) {
                var fresh = group.Where(r => r.Valid).ToList();
                long from = group.First().UtcTicks, to = group.Last().UtcTicks;
                // Include the preceding sample for a boundary-crossing decrease, once only.
                var previous = chosen.LastOrDefault(r => r.UtcTicks < from);
                var bucket = group.ToList(); if (previous != null) bucket.Insert(0, previous);
                var signals = events.Where(e => e.After.UtcTicks >= from && e.After.UtcTicks <= to).ToList();
                string last = group.Last().Valid ? UsageModel.Percent(group.Last().Remaining.Value) : "Unknown";
                AddRow(new[] { group.Key, last, fresh.Count == 0 ? "—" : AllowanceQuery.Number(fresh.Min(r => r.Remaining.Value)) + "–" + AllowanceQuery.Number(fresh.Max(r => r.Remaining.Value)) + "%", AllowanceQuery.Number(AllowanceQuery.Drop(bucket)), group.Count().ToString("N0"), signals.Count(e => e.Rollover) + " / " + signals.Count(e => e.Kind == "Early replenishment") }, false);
            }
            var resetTitle = Ui.Text("Reset and allowance changes", 17, skin.Ink); resetTitle.Margin = new Thickness(0, 20, 0, 10); body.Children.Add(resetTitle);
            body.Children.Add(Ui.Text("A rollover means the reported window advanced after its previous deadline. An early refill is evidence of replenishment; its cause is not supplied. Percentage increases alone are listed separately. Each event happened between its two observations; this is a minimum observed count.", 11, skin.Muted));
            if (events.Count == 0) body.Children.Add(Ui.Text("No reset or replenishment observed yet in this range.", 12, skin.Muted));
            else {
                AddRow(new[] { "Observed between (local)", "Change", "Before → after", "Recording" }, true);
                foreach (var change in events.AsEnumerable().Reverse().Take(100))
                    AddRow(new[] { LocalStamp(change.Before.UtcTicks) + " → " + LocalStamp(change.After.UtcTicks), change.Kind, UsageModel.Percent(change.Before.Remaining.Value) + " → " + UsageModel.Percent(change.After.Remaining.Value), change.Gap ? "Gap; may hide resets" : "Within 3 minutes" }, false);
                if (events.Count > 100) body.Children.Add(Ui.Text("Showing the latest 100 changes; export CSV for the full selected range.", 11, skin.Muted));
            }
            body.Children.Add(Ui.Text("Retained for 90 days while the widget records. CSV exports every saved reading for the selected window and range, including unavailable states. Percentages describe account allowance and cannot be assigned to individual models.", 11, skin.Muted));
        }
        private void AllowanceSeriesChanged(object sender, SelectionChangedEventArgs args)
        {
            if (allowanceSeries.SelectedValue == null) return;
            selectedSeries = allowanceSeries.SelectedValue as string; RefreshView();
        }
        private static string LocalStamp(long ticks)
        {
            return new DateTimeOffset(ticks, TimeSpan.Zero).ToLocalTime().ToString("MMM d, HH:mm:ss zzz", CultureInfo.InvariantCulture);
        }
        private void PlotAllowance(List<AllowanceReading> rows, HistoryRange period)
        {
            var chart = new Grid { Height = 190, Margin = new Thickness(0, 12, 0, 12), Background = skin.Surface };
            chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) }); chart.ColumnDefinitions.Add(new ColumnDefinition());
            var labels = new Canvas(); chart.Children.Add(labels);
            var canvas = new Canvas { ClipToBounds = true, Margin = new Thickness(0, 8, 8, 26) }; Grid.SetColumn(canvas, 1); chart.Children.Add(canvas);
            foreach (int percent in new[] { 100, 50, 0 }) { var label = Ui.Text(percent.ToString(CultureInfo.InvariantCulture) + "%", 10, skin.Muted); Canvas.SetTop(label, 2 + (100 - percent) / 100d * 156); labels.Children.Add(label); }
            var axis = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(38, 0, 8, 3) }; Grid.SetColumnSpan(axis, 2);
            var first = Ui.Text(TimeZoneInfo.ConvertTime(period.Start, period.Zone).ToString(period.Daily ? "MMM d" : "MMM d HH:mm", CultureInfo.InvariantCulture), 10, skin.Muted);
            var last = Ui.Text(TimeZoneInfo.ConvertTime(period.End, period.Zone).ToString("MMM d HH:mm", CultureInfo.InvariantCulture), 10, skin.Muted); last.HorizontalAlignment = HorizontalAlignment.Right; axis.Children.Add(first); axis.Children.Add(last); chart.Children.Add(axis);
            canvas.SizeChanged += delegate {
                canvas.Children.Clear(); if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
                foreach (double percent in new[] { 0d, 50d, 100d }) { double y = (100 - percent) / 100 * canvas.ActualHeight; canvas.Children.Add(new Line { X2 = canvas.ActualWidth, Y1 = y, Y2 = y, Stroke = skin.Track, StrokeThickness = 1 }); }
                var geometry = new StreamGeometry();
                using (var drawing = geometry.Open()) {
                    AllowanceReading previous = null;
                    Point previousPoint = new Point();
                    foreach (var r in rows) {
                        if (!r.Valid) { previous = null; continue; }
                        double x = (r.UtcTicks - period.Start.UtcDateTime.Ticks) / (double)(period.End.UtcDateTime.Ticks - period.Start.UtcDateTime.Ticks) * canvas.ActualWidth;
                        var point = new Point(x, (100 - r.Remaining.Value) / 100 * canvas.ActualHeight);
                        if (!AllowanceQuery.Continuous(previous, r)) { drawing.BeginFigure(point, false, false); previousPoint = point; }
                        else if (Math.Abs(point.X - previousPoint.X) >= .5 || Math.Abs(point.Y - previousPoint.Y) >= .5) { drawing.LineTo(point, true, false); previousPoint = point; }
                        previous = r;
                    }
                }
                geometry.Freeze(); canvas.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Stroke = skin.Highlight, StrokeThickness = 2 });
                var recent = rows.LastOrDefault(r => r.Valid);
                if (recent != null) {
                    var dot = new Ellipse { Width = 7, Height = 7, Fill = skin.Highlight, ToolTip = LocalStamp(recent.UtcTicks) + " · " + UsageModel.Percent(recent.Remaining.Value) };
                    Canvas.SetLeft(dot, Math.Max(0, Math.Min(canvas.ActualWidth - 7, (recent.UtcTicks - period.Start.UtcDateTime.Ticks) / (double)(period.End.UtcDateTime.Ticks - period.Start.UtcDateTime.Ticks) * canvas.ActualWidth - 3.5)));
                    Canvas.SetTop(dot, (100 - recent.Remaining.Value) / 100 * canvas.ActualHeight - 3.5); canvas.Children.Add(dot);
                }
            };
            body.Children.Add(chart);
        }
        private void ExportAllowance()
        {
            string profile = Path.Combine("E:\\Users", Environment.UserName, "Documents");
            var picker = new Microsoft.Win32.SaveFileDialog { Title = "Export allowance readings", Filter = "CSV file (*.csv)|*.csv", FileName = "Codex-allowance-" + DateTime.Today.ToString("yyyy-MM-dd") + ".csv", InitialDirectory = Directory.Exists(profile) ? profile : LocalData.DirectoryPath };
            if (picker.ShowDialog(this) != true) return;
            try { File.WriteAllText(picker.FileName, AllowanceQuery.Export(SelectedAllowance(HistoryRange.Create(Days, DateTimeOffset.UtcNow, TimeZoneInfo.Local), false)), new UTF8Encoding(true)); }
            catch (IOException) { MessageBox.Show(this, "The CSV could not be saved. Choose a writable location.", "Export allowance"); }
            catch (UnauthorizedAccessException) { MessageBox.Show(this, "The CSV could not be saved. Choose a writable location.", "Export allowance"); }
        }
        internal void CheckAllowanceView(string folder)
        {
            var now = DateTimeOffset.UtcNow;
            var samples = new List<AllowanceReading>();
            for (int i = 0; i < 1440; i++) {
                var stamp = now.AddMinutes(i - 1440);
                samples.Add(new AllowanceReading { UtcTicks = stamp.UtcDateTime.Ticks, BucketId = "codex", BucketName = "Codex", WindowKey = "primary", Label = "Weekly", DurationMinutes = 10080, Remaining = i < 720 ? 82 - i / 15d : 100 - (i - 720) / 25d,
                    ResetUtcTicks = (i < 720 ? now.AddHours(-12) : now.AddDays(7)).UtcDateTime.Ticks, AvailableResets = i < 720 ? 1 : 0, Status = i >= 900 && i < 930 ? "unavailable" : "ok" });
            }
            allowanceSource = delegate { return samples; }; allowanceAccount = delegate { return null; }; allowanceNotice = delegate { return "Synthetic preview"; };
            SelectAllowance(); UpdateLayout();
            if (allowanceSeries.Visibility != Visibility.Visible || internalModels.Visibility != Visibility.Collapsed || !body.Children.OfType<TextBlock>().Any(t => t.Text == "Reset and allowance changes")) throw new Exception("Allowance history view did not render");
            Ui.Capture(this, Path.Combine(folder, "preview-history-allowance.png"));
            for (int i = 1; i <= 3; i++) { range.SelectedIndex = i; RefreshView(); UpdateLayout(); if (!body.Children.OfType<TextBlock>().Any(t => t.Text == "Daily allowance readings")) throw new Exception("Daily allowance view failed"); }
            allowanceSource = delegate { return new List<AllowanceReading>(); }; RefreshView();
            if (!body.Children.OfType<TextBlock>().Any(t => t.Text == "No allowance readings in this range yet")) throw new Exception("Empty allowance view failed");
            range.SelectedIndex = 0; SelectHours(); UpdateLayout();
            if (allowanceSeries.Visibility != Visibility.Collapsed || internalModels.Visibility != Visibility.Visible) throw new Exception("History controls did not restore");
        }
    }
}
