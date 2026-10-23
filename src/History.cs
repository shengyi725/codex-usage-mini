using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexUsageMini
{
    public sealed class TokenCounts
    {
        public long Input, Cached, Output, Reasoning, Total;
        public static TokenCounts Read(Dictionary<string, object> value)
        {
            if (value == null) return null;
            long input = Number(value, "input_tokens"), output = Number(value, "output_tokens");
            return new TokenCounts { Input = input, Cached = Math.Min(input, Number(value, "cached_input_tokens")), Output = output, Reasoning = Math.Min(output, Number(value, "reasoning_output_tokens")), Total = UsageModel.Value(value, "total_tokens") == null ? input + output : Number(value, "total_tokens") };
        }
        private static long Number(Dictionary<string, object> value, string key)
        {
            long n;
            return Int64.TryParse(Convert.ToString(UsageModel.Value(value, key), CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 && n <= 1000000000000L ? n : 0;
        }
        public bool Same(TokenCounts other) { return other != null && Input == other.Input && Output == other.Output && Total == other.Total; }
        public TokenCounts Minus(TokenCounts other)
        {
            return new TokenCounts { Input = Math.Max(0, Input - other.Input), Cached = Math.Max(0, Cached - other.Cached), Output = Math.Max(0, Output - other.Output), Reasoning = Math.Max(0, Reasoning - other.Reasoning), Total = Math.Max(0, Total - other.Total) };
        }
    }
    public sealed class UsageEntry
    {
        public string Id;
        public long UtcTicks;
        public string Model;
        public long Input, Cached, Output, Reasoning, Total;
        public bool Internal;
        public bool Precise;
    }
    public sealed class ImportCursor
    {
        public long Offset;
        public long LastWriteTicks;
        public string ThreadKey;
        public long CreatedTicks;
        public string Model = "Unknown";
        public int Epoch;
        public bool HasCanonical;
        public TokenCounts Last;
    }
    public sealed class HistoryData
    {
        public int Version = 2;
        public List<UsageEntry> Entries = new List<UsageEntry>();
        public Dictionary<string, ImportCursor> Files = new Dictionary<string, ImportCursor>();
    }
    public sealed class ImportResult
    {
        public bool More;
        public int Files;
        public int Added;
        public string Notice;
    }
    public sealed class UsageGroup
    {
        public string Label;
        public long UtcTicks;
        public int Events;
        public long Input, Cached, Output, Reasoning, Total;
    }
    public sealed class HistoryRange
    {
        public DateTimeOffset Start { get; private set; }
        public DateTimeOffset End { get; private set; }
        public TimeZoneInfo Zone { get; private set; }
        public int Days { get; private set; }
        public bool Daily { get { return Days > 1; } }
        public static HistoryRange Create(int days, DateTimeOffset now, TimeZoneInfo zone)
        {
            if (days != 1 && days != 7 && days != 30 && days != 90) throw new ArgumentOutOfRangeException("days");
            if (zone == null) throw new ArgumentNullException("zone");
            var today = TimeZoneInfo.ConvertTime(now, zone).Date;
            return new HistoryRange { Days = days, End = now, Zone = zone, Start = days == 1 ? now.AddHours(-24) : DayStart(today.AddDays(1 - days), zone) };
        }
        internal static DateTimeOffset DayStart(DateTime date, TimeZoneInfo zone)
        {
            date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(date)) date = date.AddMinutes(1);
            var offset = zone.IsAmbiguousTime(date) ? zone.GetAmbiguousTimeOffsets(date).Max() : zone.GetUtcOffset(date);
            return new DateTimeOffset(date, offset);
        }
        public string Description
        {
            get
            {
                var start = TimeZoneInfo.ConvertTime(Start, Zone); var end = TimeZoneInfo.ConvertTime(End, Zone);
                return start.ToString(Daily ? "MMM d, yyyy" : "MMM d, yyyy HH:mm", CultureInfo.InvariantCulture) + " – " + end.ToString("MMM d, yyyy HH:mm zzz", CultureInfo.InvariantCulture) + (Daily ? " · includes today so far" : " · last 24 hours");
            }
        }
    }
    public static class HistoryQuery
    {
        public static List<UsageEntry> Select(IEnumerable<UsageEntry> entries, DateTimeOffset start, DateTimeOffset end, bool includeInternal)
        {
            long a = start.UtcDateTime.Ticks, b = end.UtcDateTime.Ticks;
            return entries.Where(e => e.UtcTicks >= a && e.UtcTicks < b && (includeInternal || !e.Internal)).ToList();
        }
        public static List<UsageGroup> Hours(IEnumerable<UsageEntry> entries)
        {
            return entries.GroupBy(e => e.UtcTicks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour).OrderBy(g => g.Key).Select(g => Aggregate(g, g.Key.ToString(CultureInfo.InvariantCulture), g.Key)).ToList();
        }
        public static List<UsageGroup> Timeline(IEnumerable<UsageEntry> entries, HistoryRange range)
        {
            var selected = Select(entries, range.Start, range.End, true);
            var result = new List<UsageGroup>();
            if (range.Daily)
            {
                var byDate = selected.ToLookup(e => TimeZoneInfo.ConvertTime(new DateTimeOffset(e.UtcTicks, TimeSpan.Zero), range.Zone).Date);
                var first = TimeZoneInfo.ConvertTime(range.End, range.Zone).Date.AddDays(1 - range.Days);
                for (int i = 0; i < range.Days; i++)
                {
                    var date = first.AddDays(i);
                    result.Add(Aggregate(byDate[date], date.ToString("MMM d", CultureInfo.InvariantCulture), HistoryRange.DayStart(date, range.Zone).UtcDateTime.Ticks));
                }
            }
            else
            {
                var byHour = selected.ToLookup(e => e.UtcTicks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour);
                long first = range.Start.UtcDateTime.Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour;
                long last = (range.End.UtcDateTime.Ticks - 1) / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour;
                for (long ticks = first; ticks <= last; ticks += TimeSpan.TicksPerHour)
                    result.Add(Aggregate(byHour[ticks], TimeZoneInfo.ConvertTime(new DateTimeOffset(ticks, TimeSpan.Zero), range.Zone).ToString("HH", CultureInfo.InvariantCulture), ticks));
            }
            return result;
        }
        public static List<UsageGroup> Models(IEnumerable<UsageEntry> entries)
        {
            return entries.GroupBy(e => e.Model ?? "Unknown").Select(g => Aggregate(g, g.Key, 0)).OrderByDescending(g => g.Total).ThenBy(g => g.Label).ToList();
        }
        private static UsageGroup Aggregate(IEnumerable<UsageEntry> entries, string label, long ticks)
        {
            var list = entries.ToList();
            return new UsageGroup { Label = label, UtcTicks = ticks, Events = list.Count, Input = list.Sum(e => e.Input), Cached = list.Sum(e => e.Cached), Output = list.Sum(e => e.Output), Reasoning = list.Sum(e => e.Reasoning), Total = list.Sum(e => e.Total) };
        }
        public static string Compact(long value)
        {
            return value >= 1000000000 ? (value / 1000000000d).ToString("0.##", CultureInfo.InvariantCulture) + "B" : value >= 1000000 ? (value / 1000000d).ToString("0.##", CultureInfo.InvariantCulture) + "M" : value >= 1000 ? (value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "K" : value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public sealed class HistoryImporter
    {
        private readonly object gate = new object();
        private readonly string dataPath;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 128 * 1024 * 1024 };
        private HistoryData data = new HistoryData();
        private Dictionary<string, UsageEntry> records = new Dictionary<string, UsageEntry>();
        private string loadNotice;
        public string LastNotice { get; private set; }
        public HistoryImporter(string path)
        {
            dataPath = path;
            if (!File.Exists(path)) return;
            try { Load(path); }
            catch
            {
                try { Load(path + ".bak"); loadNotice = "Recovered history from its backup."; }
                catch { loadNotice = "History could not be loaded; rebuilding from available local logs."; data = new HistoryData(); records.Clear(); }
            }
        }
        private void Load(string path)
        {
            var loaded = json.Deserialize<HistoryData>(File.ReadAllText(path));
            if (loaded == null || loaded.Version != 2 || loaded.Entries == null || loaded.Files == null) throw new FormatException();
            data = loaded;
            records = loaded.Entries.Where(e => e != null && !String.IsNullOrEmpty(e.Id) && e.UtcTicks > 0 && e.UtcTicks <= DateTime.MaxValue.Ticks).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Precise).First());
        }
        public List<UsageEntry> Snapshot() { lock (gate) return records.Values.ToList(); }
        public static string Hash(string value)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
        public static string CodexHome
        {
            get { string configured = Environment.GetEnvironmentVariable("CODEX_HOME"); return String.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : configured; }
        }
        public ImportResult Refresh(CancellationToken cancellation)
        {
            var files = new List<FileInfo>();
            foreach (string folder in new[] { "sessions", "archived_sessions" })
            {
                string root = Path.Combine(CodexHome, folder);
                if (!Directory.Exists(root)) continue;
                try { files.AddRange(new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories).Where(f => f.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-90))); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return ImportFiles(files.OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName), DateTimeOffset.UtcNow, 24 * 1024 * 1024, cancellation);
        }
        public ImportResult ImportFiles(IEnumerable<string> paths, DateTimeOffset now, long budget, CancellationToken cancellation)
        {
            var result = new ImportResult();
            int priorCount;
            lock (gate) priorCount = records.Count;
            long used = 0;
            bool changed = false;
            int inaccessible = 0;
            foreach (string path in paths)
            {
                cancellation.ThrowIfCancellationRequested();
                if (used >= budget) { result.More = true; break; }
                try
                {
                    var info = new FileInfo(path);
                    string key = Hash(Path.GetFullPath(path).ToUpperInvariant());
                    ImportCursor cursor;
                    if (!data.Files.TryGetValue(key, out cursor)) { cursor = new ImportCursor { ThreadKey = key }; data.Files[key] = cursor; }
                    if (cursor.Offset > info.Length) { cursor = new ImportCursor { ThreadKey = key }; data.Files[key] = cursor; }
                    if (cursor.Offset == info.Length && cursor.LastWriteTicks == info.LastWriteTimeUtc.Ticks) continue;
                    long before = cursor.Offset;
                    bool more;
                    ReadFile(path, cursor, now, Math.Max(1, budget - used), cancellation, out more);
                    used += cursor.Offset - before;
                    cursor.LastWriteTicks = info.LastWriteTimeUtc.Ticks;
                    result.More |= more;
                    changed |= before != cursor.Offset;
                    result.Files++;
                }
                catch (IOException) { inaccessible++; }
                catch (UnauthorizedAccessException) { inaccessible++; }
            }
            lock (gate)
            {
                long cutoff = now.AddDays(-90).UtcDateTime.Ticks;
                foreach (string key in records.Where(p => p.Value.UtcTicks < cutoff).Select(p => p.Key).ToList()) { records.Remove(key); changed = true; }
                foreach (string key in data.Files.Where(p => p.Value.LastWriteTicks > 0 && p.Value.LastWriteTicks < cutoff).Select(p => p.Key).ToList()) data.Files.Remove(key);
                result.Added = Math.Max(0, records.Count - priorCount);
                if (changed) data.Entries = records.Values.OrderBy(e => e.UtcTicks).ToList();
            }
            // Disk serialization stays outside the snapshot lock so the UI remains responsive.
            if (changed)
            {
                try { LocalData.AtomicWrite(dataPath, json.Serialize(data)); }
                catch (IOException) { result.Notice = "History is visible but could not be saved. Check the data folder."; }
                catch (UnauthorizedAccessException) { result.Notice = "History is visible but could not be saved. Check the data folder."; }
            }
            if (result.Notice == null && inaccessible > 0) result.Notice = inaccessible + " local log file(s) could not be read; coverage may be incomplete.";
            if (result.Notice == null && loadNotice != null) { result.Notice = loadNotice; loadNotice = null; }
            if (result.Notice == null && !Directory.Exists(Path.Combine(CodexHome, "sessions")) && !records.Any()) result.Notice = "No local Codex sessions found. Usage will appear as local sessions become available.";
            LastNotice = result.Notice;
            return result;
        }
        private void ReadFile(string path, ImportCursor cursor, DateTimeOffset now, long budget, CancellationToken cancellation, out bool more)
        {
            more = false;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var line = new MemoryStream())
            {
                stream.Seek(cursor.Offset, SeekOrigin.Begin);
                byte[] buffer = new byte[65536];
                long absolute = cursor.Offset, consumed = 0;
                bool oversized = false;
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    for (int i = 0; i < count; i++)
                    {
                        absolute++; consumed++;
                        if (buffer[i] == 10)
                        {
                            if (!oversized)
                            {
                                string value = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                                ParseLine(value, cursor, now);
                            }
                            line.SetLength(0); oversized = false; cursor.Offset = absolute;
                            if (consumed >= budget) { more = absolute < stream.Length; return; }
                        }
                        else if (!oversized)
                        {
                            if (line.Length >= 16 * 1024 * 1024) { oversized = true; line.SetLength(0); }
                            else line.WriteByte(buffer[i]);
                        }
                    }
                }
                // Leave an incomplete trailing line unread until Codex finishes writing it.
            }
        }
        private void ParseLine(string line, ImportCursor cursor, DateTimeOffset now)
        {
            if (line.IndexOf("\"token_usage_record\"", StringComparison.Ordinal) < 0 && line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0 && line.IndexOf("\"turn_context\"", StringComparison.Ordinal) < 0 && line.IndexOf("\"session_meta\"", StringComparison.Ordinal) < 0) return;
            Dictionary<string, object> item;
            try { item = json.DeserializeObject(line) as Dictionary<string, object>; }
            catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
            if (item == null) return;
            string type = UsageModel.Value(item, "type") as string;
            var payload = UsageModel.Obj(item, "payload");
            if (payload == null) return;
            DateTimeOffset stamp;
            bool hasStamp = DateTimeOffset.TryParse(UsageModel.Value(item, "timestamp") as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out stamp);
            if (type == "session_meta")
            {
                string thread = UsageModel.Value(payload, "id") as string;
                if (!String.IsNullOrEmpty(thread)) cursor.ThreadKey = Hash(thread);
                if (hasStamp) cursor.CreatedTicks = stamp.UtcDateTime.Ticks;
                return;
            }
            if (type == "turn_context")
            {
                string model = UsageModel.Value(payload, "model") as string;
                cursor.Model = String.IsNullOrWhiteSpace(model) ? "Unknown" : new string(model.Where(c => !Char.IsControl(c)).Take(120).ToArray());
                return;
            }
            bool precise = type == "token_usage_record";
            if (!precise && (type != "event_msg" || (UsageModel.Value(payload, "type") as string) != "token_count")) return;
            var info = precise ? payload : UsageModel.Obj(payload, "info");
            if (info == null) return;
            TokenCounts cumulative = TokenCounts.Read(UsageModel.Obj(info, precise ? "thread_token_usage" : "total_token_usage"));
            TokenCounts usage = TokenCounts.Read(UsageModel.Obj(info, precise ? "usage" : "last_token_usage"));
            if (usage == null) return;
            // A file may contain diverging UI counters alongside authoritative response records.
            // Once the latter exist, never add the UI counters as a second stream of consumption.
            if (!precise && cursor.HasCanonical) return;
            if (precise) cursor.HasCanonical = true;
            TokenCounts prior = cursor.Last;
            if (cumulative != null)
            {
                if (prior != null && cumulative.Total < prior.Total) cursor.Epoch++;
                if (!precise && cumulative.Same(prior)) return;
                if (!precise && prior != null && cumulative.Total >= prior.Total && cumulative.Input >= prior.Input && cumulative.Output >= prior.Output) usage = cumulative.Minus(prior);
                cursor.Last = cumulative;
            }
            if (!hasStamp || stamp < now.AddDays(-90) || stamp > now.AddMinutes(5) || (!precise && cursor.CreatedTicks > stamp.UtcDateTime.Ticks) || usage.Total <= 0) return;
            string threadId = UsageModel.Value(payload, "thread_id") as string;
            string threadKey = String.IsNullOrEmpty(threadId) ? cursor.ThreadKey : Hash(threadId);
            string response = UsageModel.Value(payload, "response_id") as string;
            string identity = cumulative != null ? threadKey + "|" + cursor.Epoch + "|" + cumulative.Input + "|" + cumulative.Output + "|" + cumulative.Total : !String.IsNullOrEmpty(response) ? response : threadKey + "|" + stamp.UtcDateTime.Ticks + "|" + usage.Total;
            var entry = new UsageEntry { Id = Hash(identity), UtcTicks = stamp.UtcDateTime.Ticks, Model = cursor.Model, Input = usage.Input, Cached = Math.Min(usage.Cached, usage.Input), Output = usage.Output, Reasoning = Math.Min(usage.Reasoning, usage.Output), Total = usage.Total, Internal = cursor.Model.StartsWith("codex-auto-review", StringComparison.OrdinalIgnoreCase), Precise = precise };
            lock (gate)
            {
                UsageEntry existing;
                if (records.TryGetValue(entry.Id, out existing))
                {
                    if (existing.Precise && !entry.Precise) return;
                    if (entry.Model == "Unknown" && existing.Model != "Unknown") entry.Model = existing.Model;
                }
                records[entry.Id] = entry;
            }
        }
    }
}
