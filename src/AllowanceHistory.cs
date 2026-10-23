using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexUsageMini
{
    public sealed class AllowanceReading
    {
        public long UtcTicks;
        public string AccountKey, BucketId, BucketName, WindowKey, Label;
        public int? DurationMinutes, AvailableResets;
        public double? Remaining;
        public long? ResetUtcTicks;
        public string Status;
        public string Series { get { return (BucketId ?? "codex") + "/" + (WindowKey ?? Label ?? "primary"); } }
        public bool Valid { get { return Status == "ok" && Remaining.HasValue; } }
    }

    public sealed class AllowanceEvent
    {
        public AllowanceReading Before, After;
        public string Kind;
        public bool Rollover;
        public bool Gap;
    }

    // The quota service supplies snapshots, not history. These files are our own observations.
    public sealed class AllowanceHistory
    {
        private readonly string directory;
        private readonly List<AllowanceReading> readings = new List<AllowanceReading>();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private DateTime cleanedDate;
        private string loadNotice;
        public string Notice { get; private set; }
        public AllowanceHistory(string path) : this(path, DateTimeOffset.UtcNow) { }
        public AllowanceHistory(string path, DateTimeOffset now)
        {
            directory = Path.GetFullPath(path);
            if (!Directory.Exists(directory)) return;
            try {
                foreach (string file in Directory.GetFiles(directory, "*.jsonl").OrderBy(f => f)) {
                    DateTime date;
                    if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) || date < now.UtcDateTime.Date.AddDays(-90)) continue;
                    using (var stream = new StreamReader(file, Encoding.UTF8)) {
                        string line;
                        while ((line = stream.ReadLine()) != null) {
                            if (String.IsNullOrWhiteSpace(line)) continue;
                            try {
                                var item = json.Deserialize<AllowanceReading>(line);
                                if (!Sane(item)) throw new FormatException();
                                readings.Add(item);
                            } catch (ArgumentException) { loadNotice = "Some damaged allowance records were skipped; other readings remain available."; }
                            catch (FormatException) { loadNotice = "Some damaged allowance records were skipped; other readings remain available."; }
                            catch (InvalidOperationException) { loadNotice = "Some damaged allowance records were skipped; other readings remain available."; }
                        }
                    }
                }
                readings.Sort((a, b) => a.UtcTicks.CompareTo(b.UtcTicks));
            } catch (IOException) { loadNotice = "Some allowance history could not be read."; }
            catch (UnauthorizedAccessException) { loadNotice = "Some allowance history could not be read."; }
            Notice = loadNotice;
        }
        private static bool Sane(AllowanceReading r)
        {
            return r != null && r.UtcTicks > 0 && r.UtcTicks <= DateTime.MaxValue.Ticks &&
                (r.Status == "ok" || r.Status == "unavailable" || r.Status == "reset_pending") &&
                (!r.Remaining.HasValue || (!Double.IsNaN(r.Remaining.Value) && !Double.IsInfinity(r.Remaining.Value) && r.Remaining >= 0 && r.Remaining <= 100)) &&
                (r.Status != "ok" || r.Remaining.HasValue) &&
                (!r.ResetUtcTicks.HasValue || (r.ResetUtcTicks > 0 && r.ResetUtcTicks <= DateTime.MaxValue.Ticks));
        }
        public List<AllowanceReading> Snapshot() { return readings.ToList(); }
        public void Record(UsageSnapshot snapshot)
        {
            if (snapshot == null) return;
            var batch = new List<AllowanceReading>();
            foreach (var window in snapshot.Windows) {
                bool pending = window.ResetsAt.HasValue && window.ResetsAt <= snapshot.FetchedAt;
                batch.Add(new AllowanceReading { UtcTicks = snapshot.FetchedAt.UtcDateTime.Ticks, AccountKey = snapshot.AccountKey,
                    BucketId = window.BucketId, BucketName = window.BucketName, WindowKey = window.WindowKey ?? window.Label,
                    Label = window.Label, DurationMinutes = window.DurationMinutes, AvailableResets = snapshot.AvailableResets,
                    Remaining = pending ? (double?)null : window.Remaining, ResetUtcTicks = window.ResetsAt.HasValue ? (long?)window.ResetsAt.Value.UtcDateTime.Ticks : null,
                    Status = pending ? "reset_pending" : "ok" });
            }
            // A disappearing window is unavailable, rather than silently carrying its last value forward.
            foreach (var old in Latest(snapshot.AccountKey))
                if (!batch.Any(r => r.Series == old.Series)) batch.Add(Missing(old, snapshot.FetchedAt));
            Save(batch, snapshot.FetchedAt);
        }
        public void RecordFailure(DateTimeOffset now, string accountKey)
        {
            var batch = Latest(accountKey).Select(r => Missing(r, now)).ToList();
            if (batch.Count == 0) batch.Add(new AllowanceReading { UtcTicks = now.UtcDateTime.Ticks, AccountKey = accountKey, BucketId = "codex", BucketName = "Codex", WindowKey = "primary", Label = "Allowance", Status = "unavailable" });
            Save(batch, now);
        }
        private IEnumerable<AllowanceReading> Latest(string accountKey)
        {
            return readings.Where(r => SameAccount(r.AccountKey, accountKey)).GroupBy(r => r.Series).Select(g => g.Last()).ToList();
        }
        public static bool SameAccount(string a, string b) { return (a ?? "") == (b ?? ""); }
        private static AllowanceReading Missing(AllowanceReading old, DateTimeOffset now)
        {
            return new AllowanceReading { UtcTicks = now.UtcDateTime.Ticks, AccountKey = old.AccountKey, BucketId = old.BucketId, BucketName = old.BucketName, WindowKey = old.WindowKey, Label = old.Label, DurationMinutes = old.DurationMinutes, Status = "unavailable" };
        }
        private void Save(List<AllowanceReading> batch, DateTimeOffset now)
        {
            batch = batch.Where(Sane).Where(r => {
                var previous = readings.LastOrDefault(p => SameAccount(p.AccountKey, r.AccountKey) && p.Series == r.Series);
                return previous == null || (r.UtcTicks > previous.UtcTicks &&
                    (r.UtcTicks / TimeSpan.TicksPerMinute != previous.UtcTicks / TimeSpan.TicksPerMinute || r.Remaining != previous.Remaining || r.Status != previous.Status || r.ResetUtcTicks != previous.ResetUtcTicks || r.AvailableResets != previous.AvailableResets));
            }).ToList();
            if (batch.Count == 0) return;
            try {
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
                // A separator also isolates an incomplete final record after an interrupted append.
                using (var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read)) {
                    bool separator = false;
                    if (stream.Length > 0) { stream.Seek(-1, SeekOrigin.End); separator = stream.ReadByte() != 10; }
                    stream.Seek(0, SeekOrigin.End);
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) {
                        if (separator) writer.WriteLine();
                        foreach (var item in batch) writer.WriteLine(json.Serialize(item));
                        writer.Flush(); stream.Flush(true);
                    }
                }
                readings.AddRange(batch);
                Notice = loadNotice;
                if (cleanedDate != now.UtcDateTime.Date) {
                    cleanedDate = now.UtcDateTime.Date;
                    long cutoff = now.UtcDateTime.Date.AddDays(-90).Ticks;
                    readings.RemoveAll(r => r.UtcTicks < cutoff);
                    foreach (string old in Directory.GetFiles(directory, "*.jsonl")) {
                        DateTime date;
                        // Only our own dated files directly inside the explicitly configured data directory.
                        if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(old), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) && date.Ticks < cutoff) File.Delete(old);
                    }
                }
            } catch (IOException) { Notice = "Allowance history could not be saved. Check the data folder; live readings still work."; }
            catch (UnauthorizedAccessException) { Notice = "Allowance history could not be saved. Check the data folder; live readings still work."; }
        }
    }

    public static class AllowanceQuery
    {
        public static bool Continuous(AllowanceReading before, AllowanceReading after)
        {
            return before != null && after != null && before.Valid && after.Valid && before.Series == after.Series && AllowanceHistory.SameAccount(before.AccountKey, after.AccountKey) &&
                after.UtcTicks > before.UtcTicks && after.UtcTicks - before.UtcTicks <= 3 * TimeSpan.TicksPerMinute && before.ResetUtcTicks == after.ResetUtcTicks && before.DurationMinutes == after.DurationMinutes;
        }
        public static List<AllowanceEvent> Events(IEnumerable<AllowanceReading> source)
        {
            var result = new List<AllowanceEvent>();
            foreach (var group in source.GroupBy(r => (r.AccountKey ?? "") + ":" + r.Series)) {
                AllowanceReading previous = null;
                bool missing = false;
                foreach (var current in group.OrderBy(r => r.UtcTicks)) {
                    if (!current.Valid) { missing = true; continue; }
                    if (previous != null && previous.DurationMinutes == current.DurationMinutes) {
                        bool gap = missing || current.UtcTicks - previous.UtcTicks > 3 * TimeSpan.TicksPerMinute;
                        bool changed = previous.ResetUtcTicks.HasValue && current.ResetUtcTicks.HasValue && previous.ResetUtcTicks != current.ResetUtcTicks;
                        bool rollover = changed && current.ResetUtcTicks > previous.ResetUtcTicks && current.UtcTicks >= previous.ResetUtcTicks;
                        bool increase = current.Remaining > previous.Remaining;
                        if (rollover || changed || increase) {
                            bool early = changed && increase && current.ResetUtcTicks > previous.ResetUtcTicks && current.UtcTicks < previous.ResetUtcTicks;
                            string kind = rollover ? "Window rollover" : early ? "Early replenishment" : changed ? "Reset schedule changed" : "Allowance increase";
                            result.Add(new AllowanceEvent { Before = previous, After = current, Kind = kind, Rollover = rollover, Gap = gap });
                        }
                    }
                    previous = current;
                    missing = false;
                }
            }
            return result.OrderBy(e => e.After.UtcTicks).ToList();
        }
        public static double Drop(IEnumerable<AllowanceReading> source)
        {
            double total = 0;
            foreach (var group in source.GroupBy(r => (r.AccountKey ?? "") + ":" + r.Series)) {
                AllowanceReading previous = null;
                foreach (var current in group.OrderBy(r => r.UtcTicks)) {
                    if (Continuous(previous, current)) total += Math.Max(0, previous.Remaining.Value - current.Remaining.Value);
                    previous = current;
                }
            }
            return total;
        }
        public static string Number(double n) { return n.ToString("0.##", CultureInfo.InvariantCulture); }
        private static string Csv(string s) { if (!String.IsNullOrEmpty(s) && "=+-@".IndexOf(s[0]) >= 0) s = "'" + s; return "\"" + (s ?? "").Replace("\"", "\"\"") + "\""; }
        public static string Export(IEnumerable<AllowanceReading> source)
        {
            var output = new StringBuilder("observed_local,observed_utc,bucket,window,duration_minutes,remaining_percent,used_percent,next_reset_local,next_reset_utc,available_reset_credits,status,event,recording_gap\r\n");
            var list = source.OrderBy(r => r.UtcTicks).ToList();
            var events = Events(list).ToDictionary(e => e.After);
            foreach (var r in list) {
                var time = new DateTimeOffset(r.UtcTicks, TimeSpan.Zero);
                DateTimeOffset? reset = r.ResetUtcTicks.HasValue ? (DateTimeOffset?)new DateTimeOffset(r.ResetUtcTicks.Value, TimeSpan.Zero) : null;
                AllowanceEvent change; events.TryGetValue(r, out change);
                output.Append(String.Join(",", new[] { Csv(time.ToLocalTime().ToString("o")), Csv(time.ToString("o")), Csv(r.BucketName), Csv(r.Label),
                    r.DurationMinutes.HasValue ? r.DurationMinutes.Value.ToString(CultureInfo.InvariantCulture) : "", r.Remaining.HasValue ? Number(r.Remaining.Value) : "", r.Remaining.HasValue ? Number(100 - r.Remaining.Value) : "",
                    Csv(reset.HasValue ? reset.Value.ToLocalTime().ToString("o") : ""), Csv(reset.HasValue ? reset.Value.ToString("o") : ""), r.AvailableResets.HasValue ? r.AvailableResets.Value.ToString(CultureInfo.InvariantCulture) : "", Csv(r.Status), Csv(change == null ? "" : change.Kind), change == null ? "" : change.Gap ? "true" : "false" })).Append("\r\n");
            }
            return output.ToString();
        }
    }
}
