using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace CodexUsageMini
{
    public enum AllowanceLevel { Green, Orange, Red, Unknown }

    public sealed class QuotaWindow
    {
        public string BucketId;
        public string BucketName;
        public string Label;
        public double Remaining;
        public DateTimeOffset? ResetsAt;
        public int? DurationMinutes;
    }

    public sealed class UsageSnapshot
    {
        public readonly List<QuotaWindow> Windows = new List<QuotaWindow>();
        public string Plan;
        public string AccountKey;
        public bool Blocked;
        public DateTimeOffset FetchedAt;
        public QuotaWindow Limiting { get { return Windows.OrderBy(w => w.Remaining).FirstOrDefault(); } }
    }

    public static class UsageModel
    {
        public static AllowanceLevel Level(double remaining)
        {
            if (Double.IsNaN(remaining) || Double.IsInfinity(remaining)) return AllowanceLevel.Unknown;
            return remaining > 50 ? AllowanceLevel.Green : remaining >= 10 ? AllowanceLevel.Orange : AllowanceLevel.Red;
        }

        public static string Percent(double value)
        {
            // Keep decimal readings on their actual side of each color threshold.
            string formatted = value.ToString("0.##", CultureInfo.InvariantCulture);
            double rounded;
            if (Double.TryParse(formatted, NumberStyles.Float, CultureInfo.InvariantCulture, out rounded) && Level(rounded) != Level(value))
                return value > 50 ? ">50%" : "<10%";
            return formatted + "%";
        }

        public static string WindowName(int? minutes, string fallback)
        {
            if (!minutes.HasValue || minutes.Value <= 0) return fallback;
            int n = minutes.Value;
            if (n == 10080) return "Weekly";
            if (n == 1440) return "Daily";
            if (n % 1440 == 0) return (n / 1440) + "-day";
            if (n % 60 == 0) return (n / 60) + "-hour";
            return n + "-minute";
        }

        public static string Countdown(DateTimeOffset? reset, DateTimeOffset now)
        {
            if (!reset.HasValue) return "Not provided";
            TimeSpan delta = reset.Value - now;
            if (delta <= TimeSpan.Zero) return "Refreshing soon";
            if (delta.TotalDays >= 1) return (int)delta.TotalDays + "d " + delta.Hours + "h";
            if (delta.TotalHours >= 1) return (int)delta.TotalHours + "h " + delta.Minutes + "m";
            return Math.Max(1, (int)Math.Ceiling(delta.TotalMinutes)) + "m";
        }

        public static bool IsStale(UsageSnapshot data, DateTimeOffset now)
        {
            return data == null || now - data.FetchedAt > TimeSpan.FromMinutes(3) ||
                data.Windows.Any(w => w.ResetsAt.HasValue && w.ResetsAt.Value <= now);
        }

        public static UsageSnapshot Parse(string json, DateTimeOffset now)
        {
            var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("Invalid usage response.");
            var result = Obj(root, "result") ?? root;
            var snapshot = new UsageSnapshot { FetchedAt = now, Blocked = Value(result, "ordinaryUsageAllowed") is bool && !(bool)Value(result, "ordinaryUsageAllowed") };
            string accountId = Value(result, "accountId") as string;
            snapshot.AccountKey = String.IsNullOrEmpty(accountId) ? null : HistoryImporter.Hash(accountId);
            var buckets = Obj(result, "rateLimitsByLimitId");
            if (buckets != null && buckets.Count > 0)
            {
                foreach (var pair in buckets)
                {
                    var bucket = pair.Value as Dictionary<string, object>;
                    if (bucket != null) AddBucket(snapshot, bucket, pair.Key);
                }
            }
            else
            {
                var legacy = Obj(result, "rateLimits");
                if (legacy != null) AddBucket(snapshot, legacy, "codex");
            }
            if (snapshot.Windows.Count == 0) throw new FormatException("No allowance data returned. Sign in to Codex with ChatGPT, then refresh.");
            return snapshot;
        }

        private static void AddBucket(UsageSnapshot data, Dictionary<string, object> bucket, string key)
        {
            string id = Value(bucket, "limitId") as string ?? key;
            string name = Value(bucket, "limitName") as string;
            if (String.IsNullOrWhiteSpace(name)) name = id == "codex" ? "Codex" : id;
            string plan = Value(bucket, "planType") as string;
            if (data.Plan == null && plan != null) data.Plan = plan;
            if (Object.Equals(Value(bucket, "spendControlReached"), true) || Value(bucket, "rateLimitReachedType") != null) data.Blocked = true;
            AddWindow(data, Obj(bucket, "primary"), id, name, "Primary");
            AddWindow(data, Obj(bucket, "secondary"), id, name, "Secondary");
        }

        private static void AddWindow(UsageSnapshot data, Dictionary<string, object> window, string id, string name, string fallback)
        {
            if (window == null) return;
            object used = Value(window, "usedPercent");
            double percent;
            if (used == null || used is bool || !Double.TryParse(Convert.ToString(used, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out percent) || Double.IsNaN(percent) || Double.IsInfinity(percent)) return;
            double duration;
            int? minutes = Number(Value(window, "windowDurationMins"), out duration) && duration > 0 && duration <= Int32.MaxValue ? (int?)duration : null;
            double unix;
            DateTimeOffset? resets = null;
            if (Number(Value(window, "resetsAt"), out unix))
            {
                try { resets = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(unix); }
                catch (ArgumentOutOfRangeException) { }
            }
            data.Windows.Add(new QuotaWindow { BucketId = id, BucketName = name, Label = WindowName(minutes, fallback), DurationMinutes = minutes, Remaining = Math.Max(0, Math.Min(100, 100 - percent)), ResetsAt = resets });
        }

        private static bool Number(object value, out double number)
        {
            number = 0;
            return value != null && !(value is bool) && Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && !Double.IsNaN(number) && !Double.IsInfinity(number);
        }

        public static object Value(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value : null;
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> map, string key)
        {
            return Value(map, key) as Dictionary<string, object>;
        }

        public static UsageSnapshot Demo(double remaining)
        {
            var data = new UsageSnapshot { Plan = "Preview", FetchedAt = DateTimeOffset.Now };
            data.Windows.Add(new QuotaWindow { BucketId = "codex", BucketName = "Codex", Label = "Weekly", Remaining = remaining, DurationMinutes = 10080, ResetsAt = DateTimeOffset.Now.AddDays(3).AddHours(4) });
            return data;
        }
    }
}
