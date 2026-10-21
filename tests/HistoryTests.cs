using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using CodexUsageMini;

internal static class HistoryTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static string Event(string type, object payload, DateTimeOffset stamp) { return Json.Serialize(new { timestamp = stamp.ToString("o"), type = type, payload = payload }); }
    private static object Tokens(long input, long cached, long output) { return new { input_tokens = input, cached_input_tokens = cached, output_tokens = output, reasoning_output_tokens = output / 2, total_tokens = input + output }; }
    private static string Context(string model) { return Event("turn_context", new { model = model }, Now.AddMinutes(-30)); }
    private static string Legacy(long input, long cached, long output, long lastInput, long lastCached, long lastOutput, DateTimeOffset stamp)
    { return Event("event_msg", new { type = "token_count", info = new { total_token_usage = Tokens(input, cached, output), last_token_usage = Tokens(lastInput, lastCached, lastOutput) } }, stamp); }
    private static string Canonical(long input, long cached, long output, long lastInput, long lastCached, long lastOutput, DateTimeOffset stamp)
    { return Event("token_usage_record", new { thread_id = "test-private-thread", response_id = "response-" + input, thread_token_usage = Tokens(input, cached, output), usage = Tokens(lastInput, lastCached, lastOutput) }, stamp); }
    private static void Import(HistoryImporter store, string path) { store.ImportFiles(new[] { path }, Now, 10000000, CancellationToken.None); }
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "session.jsonl"), db = Path.Combine(root, "history.json");
        string[] lines = {
            Event("session_meta", new { id = "test-private-thread" }, Now.AddHours(-3)),
            Event("response_item", new { text = "PRIVATE PROMPT MUST NOT BE STORED" }, Now.AddHours(-2)),
            Context("model-a"),
            Legacy(100,40,10,100,40,10,Now.AddMinutes(-29)),
            Canonical(100,40,10,100,40,10,Now.AddMinutes(-29)),
            Legacy(100,40,10,100,40,10,Now.AddMinutes(-29)),
            Context("model-b"),
            Canonical(300,120,30,200,80,20,Now.AddMinutes(-10)),
            Legacy(300,120,30,200,80,20,Now.AddMinutes(-10)),
            Legacy(900,300,90,200,80,20,Now.AddMinutes(-10))
        };
        File.WriteAllText(path, String.Join("\n", lines) + "\n", new UTF8Encoding(false));
        var store = new HistoryImporter(db); Import(store, path);
        var list = store.Snapshot();
        check(list.Count == 2, "Do not double-count token_count and canonical usage");
        check(list.Sum(e => e.Total) == 330, "Cumulative totals must not be re-summed");
        check(list.All(e => e.Precise), "Canonical response data replaces fallback counters");
        check(list.Single(e => e.Model == "model-a").Total == 110 && list.Single(e => e.Model == "model-b").Total == 220, "Attribute model changes per event");
        check(list.Sum(e => e.Cached) == 120 && list.Sum(e => e.Input) == 300, "Cached input stays a subset of input");
        Import(store, path); check(store.Snapshot().Count == 2, "Repeated refresh is idempotent");
        store = new HistoryImporter(db); Import(store, path); check(store.Snapshot().Count == 2, "Restart preserves cursor and avoids duplication");
        string partial = Legacy(350,140,35,50,20,5,Now.AddMinutes(-5));
        File.AppendAllText(path, partial.Substring(0, 30)); Import(store, path); check(store.Snapshot().Count == 2, "Ignore a partially written trailing line");
        File.AppendAllText(path, partial.Substring(30) + "\n"); Import(store, path);
        check(store.Snapshot().Count == 2 && store.Snapshot().Sum(e => e.Total) == 330, "Do not add transient UI counters after canonical records exist");
        File.AppendAllText(path, Canonical(350,140,35,50,20,5,Now.AddMinutes(-5)) + "\n"); Import(store, path);
        check(store.Snapshot().Count == 3 && store.Snapshot().All(e => e.Precise) && store.Snapshot().Sum(e => e.Total) == 385, "Late canonical usage supplies one authoritative event");
        string copy = Path.Combine(root, "archived-copy.jsonl"); File.Copy(path, copy); Import(store, copy);
        check(store.Snapshot().Count == 3, "Copied or archived session is not counted twice");
        File.AppendAllText(path, Canonical(10,0,2,10,0,2,Now.AddMinutes(-2)) + "\n"); Import(store, path);
        check(store.Snapshot().Count == 4 && store.Snapshot().Sum(e => e.Total) == 397, "Counter reset starts a new epoch without negative usage");
        string saved = File.ReadAllText(db);
        check(!saved.Contains("PRIVATE PROMPT") && !saved.Contains("test-private-thread") && !saved.Contains("response-") && !saved.Contains(path), "Only sanitized counters and hashed cursor identities are saved");
        var groups = HistoryQuery.Models(store.Snapshot());
        check(groups.Sum(g => g.Total) == 397 && groups.Count == 2, "Model aggregates conserve total tokens");
        check(HistoryQuery.Hours(store.Snapshot()).Sum(g => g.Events) == 4, "Hourly aggregates preserve event frequency");
        var boundary = new List<UsageEntry> {
            new UsageEntry { UtcTicks = Now.AddDays(-2).UtcDateTime.Ticks, Model = "a", Total = 10 },
            new UsageEntry { UtcTicks = Now.AddMinutes(-1).UtcDateTime.Ticks, Model = "codex-auto-review", Total = 20, Internal = true },
            new UsageEntry { UtcTicks = Now.AddMinutes(-1).UtcDateTime.Ticks, Model = "a", Total = 30 }
        };
        check(HistoryQuery.Select(boundary, Now.AddDays(-1), Now, false).Sum(e => e.Total) == 30, "Range filter and internal-model exclusion");
        check(HistoryQuery.Select(boundary, Now.AddDays(-1), Now, true).Sum(e => e.Total) == 50, "Internal models can be included explicitly");
        string smallDb = Path.Combine(root, "budget.json"); var chunked = new HistoryImporter(smallDb); ImportResult result; int passes = 0;
        do { result = chunked.ImportFiles(new[] { path }, Now, 32, CancellationToken.None); passes++; } while (result.More && passes < 50);
        check(chunked.Snapshot().Count == 4 && passes > 1 && passes < 50, "Budgeted incremental import completes without loss");
        string legacyFile = Path.Combine(root, "legacy-only.jsonl");
        File.WriteAllText(legacyFile, Event("session_meta", new { id = "legacy-thread" }, Now.AddHours(-1)) + "\n" + Context("legacy-model") + "\n" + Legacy(100,40,10,100,40,10,Now.AddMinutes(-20)) + "\n" + Legacy(100,40,10,100,40,10,Now.AddMinutes(-20)) + "\n" + Legacy(150,60,15,50,20,5,Now.AddMinutes(-10)) + "\n");
        var legacyStore = new HistoryImporter(Path.Combine(root, "legacy-db.json")); Import(legacyStore, legacyFile);
        check(legacyStore.Snapshot().Count == 2 && legacyStore.Snapshot().Sum(e => e.Total) == 165, "Older files use cumulative differences without duplicate snapshots");
        string unknown = Path.Combine(root, "unknown.jsonl");
        File.WriteAllText(unknown, Event("session_meta", new { id = "another-thread" }, Now.AddHours(-1)) + "\n" + Legacy(20,0,3,20,0,3,Now.AddMinutes(-1)) + "\n");
        Import(store, unknown); check(store.Snapshot().Any(e => e.Model == "Unknown"), "Missing model is shown as unknown");
        string old = Path.Combine(root, "old.jsonl");
        File.WriteAllText(old, Event("session_meta", new { id = "old-thread" }, Now.AddDays(-100)) + "\n" + Legacy(50,0,5,50,0,5,Now.AddDays(-95)) + "\n");
        int beforeOld = store.Snapshot().Count; Import(store, old); check(store.Snapshot().Count == beforeOld, "90-day retention excludes old events");
        var prefs = new Preferences { Scale = 0.1, Transparency = 100, Theme = "invalid" }; prefs.Normalize();
        check(prefs.Scale == .5 && prefs.Transparency == 50 && prefs.Theme == "Dark", "Clamp scale/transparency and validate theme");
        prefs.Scale = Double.NaN; prefs.Transparency = Double.NaN; prefs.Normalize(); check(prefs.Scale == 1 && prefs.Transparency == 0, "Invalid appearance values recover safely");
        prefs.PlanExpiry = "2030-11-09"; prefs.ExpiryAccountKey = "account-a";
        check(prefs.ExpiryLabel("account-a").Contains("Nov 9, 2030"), "Plan expiry is an independent entered date");
        check(prefs.ExpiryLabel("account-b") == "Not set for this account", "Do not show another account’s expiry");
        check(prefs.ExpiryLabel(null).Contains("Nov 9, 2030"), "A temporarily unknown account must not falsely report an account mismatch");
        prefs.PlanExpiry = "2030-99-99"; prefs.Normalize(); check(prefs.PlanExpiry == null, "Reject invalid expiry dates");
        check(Palette.Names.Length == 4 && Palette.Names.All(n => Palette.For(n).Name == n), "All requested themes are available");
        check(HistoryQuery.Compact(1200000) == "1.2M", "Readable token units");
        var utf = new HistoryImporter(Path.Combine(root, "utf.json"));
        string unicode = Path.Combine(root, "unicode.jsonl"); File.WriteAllText(unicode, Event("session_meta", new { id = "unicode-thread" }, Now.AddHours(-1)) + "\n" + Context("模型-a") + "\n" + Legacy(10,0,2,10,0,2,Now.AddMinutes(-1)) + "\r\n", new UTF8Encoding(false));
        Import(utf, unicode); check(utf.Snapshot().Single().Model == "模型-a", "UTF-8 models and CRLF files import correctly");
        string partialDb = Path.Combine(root, "corrupt.json"); File.WriteAllText(partialDb, "broken"); File.Copy(db, partialDb + ".bak");
        var recovered = new HistoryImporter(partialDb); check(recovered.Snapshot().Count > 0, "Recover persisted counters from backup");
    }
}
