using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexUsageMini;

internal static class AllowanceTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 23, 3, 40, 0, TimeSpan.Zero);
    private static UsageSnapshot Snapshot(DateTimeOffset time, double remaining, DateTimeOffset? reset, string account)
    {
        var sample = new UsageSnapshot { FetchedAt = time, AccountKey = HistoryImporter.Hash(account), AvailableResets = 1 };
        sample.Windows.Add(new QuotaWindow { BucketId = "codex", BucketName = "Codex", WindowKey = "primary", Label = "Weekly", DurationMinutes = 10080, Remaining = remaining, ResetsAt = reset });
        return sample;
    }
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "allowance-tests-" + Guid.NewGuid().ToString("N"));
        var store = new AllowanceHistory(root, Now);
        var reset = Now.AddDays(7);
        store.Record(Snapshot(Now, 82, reset, "private-account"));
        store.Record(Snapshot(Now.AddSeconds(20), 82, reset, "private-account"));
        check(store.Snapshot().Count == 1, "Same-minute identical reads are not duplicated");
        store.Record(Snapshot(Now.AddSeconds(40), 81, reset, "private-account"));
        store.Record(Snapshot(Now.AddMinutes(1), 81, reset, "private-account"));
        check(store.Snapshot().Count == 3, "Changed readings and minute heartbeats are persisted");
        check(AllowanceQuery.Drop(store.Snapshot()) == 1, "Percentage-point consumption uses snapshots, not tokens");
        store.RecordFailure(Now.AddMinutes(2), HistoryImporter.Hash("private-account"));
        store.Record(Snapshot(Now.AddMinutes(3), 75, reset, "private-account"));
        check(AllowanceQuery.Drop(store.Snapshot()) == 1, "Failed refresh creates a gap, not invented consumption");
        check(store.Snapshot().Single(r => r.Status == "unavailable").Remaining == null, "Unavailable does not mean full or zero allowance");
        store = new AllowanceHistory(root, Now.AddMinutes(3));
        check(store.Snapshot().Count == 5, "Allowance observations survive restart");
        string file = Path.Combine(root, "2026-10-23.jsonl");
        check(!File.ReadAllText(file).Contains("private-account"), "Raw account identity is not saved");
        File.AppendAllText(file, "{\"UtcTicks\":");
        store = new AllowanceHistory(root, Now.AddMinutes(4));
        check(store.Snapshot().Count == 5 && store.Notice != null, "An interrupted final record preserves earlier observations");
        store.Record(Snapshot(Now.AddMinutes(4), 74, reset, "private-account"));
        store = new AllowanceHistory(root, Now.AddMinutes(4));
        check(store.Snapshot().Count == 6 && store.Snapshot().Last().Remaining == 74, "Append after a torn line remains readable");
        var list = store.Snapshot();
        var before = list.Last();
        var after = Reading(Now.AddMinutes(5), 100, Now.AddDays(8));
        before.AccountKey = null;
        var early = AllowanceQuery.Events(new[] { before, after });
        check(early.Count == 1 && early[0].Kind == "Early replenishment" && !early[0].Rollover, "Early refill is not claimed to be a scheduled or manual reset");
        after.ResetUtcTicks = before.ResetUtcTicks;
        check(AllowanceQuery.Events(new[] { before, after }).Single().Kind == "Allowance increase", "A percentage increase alone is not a reset");
        var a = Reading(Now, 5, Now.AddMinutes(1)); var b = Reading(Now.AddMinutes(1), 98, Now.AddDays(7));
        check(AllowanceQuery.Events(new[] { a, b }).Single().Rollover, "Advancing expired window supplies rollover evidence");
        var missing = new AllowanceReading { UtcTicks = Now.AddSeconds(30).UtcDateTime.Ticks, BucketId = "codex", WindowKey = "primary", DurationMinutes = 10080, Status = "unavailable" };
        check(AllowanceQuery.Events(new[] { a, missing, b }).Single().Gap, "Even a short failed reading makes the reset interval uncertain");
        check(AllowanceQuery.Drop(new[] { a, b }) == 0, "A reset jump cannot become negative consumption");
        b.Remaining = 1;
        check(AllowanceQuery.Drop(new[] { a, b }) == 0, "Do not infer consumption across a reset even when percentage fell");
        b.UtcTicks = Now.AddHours(20).UtcDateTime.Ticks;
        check(AllowanceQuery.Events(new[] { a, b }).Single().Gap, "A reset across downtime is explicitly uncertain");
        b = Reading(Now.AddMinutes(1), 5, Now.AddDays(7)); a.ResetUtcTicks = Now.AddDays(6).UtcDateTime.Ticks;
        check(AllowanceQuery.Events(new[] { a, b }).Single().Kind == "Reset schedule changed", "Deadline changes without refill are not reset counts");
        b = Reading(Now.AddHours(2), 1, new DateTimeOffset(a.ResetUtcTicks.Value, TimeSpan.Zero));
        check(AllowanceQuery.Drop(new[] { a, b }) == 0, "Long sampling gaps have unknown consumption");
        b = Reading(Now.AddMinutes(1), 1, new DateTimeOffset(a.ResetUtcTicks.Value, TimeSpan.Zero)); b.WindowKey = "secondary";
        check(AllowanceQuery.Drop(new[] { a, b }) == 0 && AllowanceQuery.Events(new[] { a, b }).Count == 0, "Quota windows are never blended");
        b.WindowKey = a.WindowKey; b.AccountKey = HistoryImporter.Hash("other-account");
        check(AllowanceQuery.Drop(new[] { a, b }) == 0 && AllowanceQuery.Events(new[] { a, b }).Count == 0, "Accounts are never blended");
        string pendingDir = Path.Combine(root, "pending");
        var pending = new AllowanceHistory(pendingDir, Now);
        pending.Record(Snapshot(Now, 82, Now.AddSeconds(-1), "a"));
        check(pending.Snapshot().Single().Status == "reset_pending" && pending.Snapshot().Single().Remaining == null, "Expired snapshots cannot look like fresh allowance");
        var multi = Snapshot(Now.AddMinutes(1), 82, reset, "a");
        multi.Windows.Add(new QuotaWindow { BucketId = "codex", WindowKey = "secondary", Label = "Weekly", DurationMinutes = 10080, Remaining = 12 });
        pending.Record(multi);
        check(pending.Snapshot().Count == 3 && pending.Snapshot().Last().Series != pending.Snapshot().First().Series, "Equal-duration primary and secondary remain distinct");
        pending.Record(Snapshot(Now.AddMinutes(2), 82, reset, "a"));
        check(pending.Snapshot().Last().WindowKey == "secondary" && !pending.Snapshot().Last().Valid, "Disappearing windows are marked unavailable");
        var csvRows = new[] { Reading(Now, 82, reset), new AllowanceReading { UtcTicks = Now.AddMinutes(1).UtcDateTime.Ticks, BucketId = "codex", WindowKey = "primary", Status = "unavailable" } };
        string csv = AllowanceQuery.Export(csvRows);
        check(csv.Contains("remaining_percent,used_percent") && csv.Contains(",82,18,") && csv.Contains("unavailable"), "CSV includes actual percentages and missing states");
        check(!csv.Contains("AccountKey"), "CSV excludes hashed account identity");
        csvRows[0].BucketName = "=private-formula";
        check(AllowanceQuery.Export(csvRows).Contains("'="), "CSV escapes spreadsheet formulas in service-provided labels");
        var parsed = UsageModel.Parse("{\"accountId\":\"private\",\"rateLimitResetCredits\":{\"availableCount\":1},\"rateLimits\":{\"primary\":{\"usedPercent\":18}}}", Now);
        check(parsed.AvailableResets == 1 && parsed.Limiting.WindowKey == "primary", "Read reset credits and stable window slot from service");
        parsed = UsageModel.Parse("{\"rateLimitResetCredits\":{\"availableCount\":true},\"rateLimits\":{\"primary\":{\"usedPercent\":18}}}", Now);
        check(!parsed.AvailableResets.HasValue, "Missing or invalid reset credits stay unknown");
        string oldFile = Path.Combine(root, "2026-01-01.jsonl"); File.WriteAllText(oldFile, "old");
        string customFile = Path.Combine(root, "keep-my-file.jsonl"); File.WriteAllText(customFile, "personal");
        store.Record(Snapshot(Now.AddDays(1), 70, reset, "private-account"));
        check(!File.Exists(oldFile) && File.Exists(customFile), "Retention deletes only expired dated observation files");
        var blocked = new AllowanceHistory(Path.Combine(file, "not-a-directory"), Now);
        blocked.Record(Snapshot(Now, 82, reset, "a"));
        check(blocked.Snapshot().Count == 0 && blocked.Notice != null, "Failed persistence is visible and never reported as saved");
        string next = Path.Combine(root, "midnight"); var midnight = new AllowanceHistory(next, Now);
        var end = new DateTimeOffset(2026, 10, 23, 23, 59, 0, TimeSpan.Zero);
        midnight.Record(Snapshot(end, 70, reset, "a")); midnight.Record(Snapshot(end.AddMinutes(1), 69, reset, "a"));
        check(Directory.GetFiles(next, "*.jsonl").Length == 2 && new AllowanceHistory(next, end.AddMinutes(1)).Snapshot().Count == 2, "UTC day rotation preserves adjacent readings");
    }
    private static AllowanceReading Reading(DateTimeOffset stamp, double percent, DateTimeOffset reset)
    {
        return new AllowanceReading { UtcTicks = stamp.UtcDateTime.Ticks, BucketId = "codex", BucketName = "Codex", WindowKey = "primary", Label = "Weekly", DurationMinutes = 10080, Remaining = percent, ResetUtcTicks = reset.UtcDateTime.Ticks, Status = "ok" };
    }
}
