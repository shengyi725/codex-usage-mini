using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using CodexUsageMini;

internal static class CoreTests
{
    private static int passed;
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
    private const string Weekly = "{\"usedPercent\":23,\"windowDurationMins\":10080,\"resetsAt\":1900000000}";
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; }
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { passed++; return; } throw new Exception(name);
    }
    private static UsageSnapshot Parse(string fields) { return UsageModel.Parse("{" + fields + "}", Now); }
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--live-check") { try { var live = new CodexClient().ReadAsync(CancellationToken.None).GetAwaiter().GetResult(); Console.WriteLine("LIVE PASS: " + live.Windows.Count + " window(s), " + UsageModel.Percent(live.Limiting.Remaining) + " remaining."); return 0; } catch { Console.WriteLine("LIVE CHECK UNAVAILABLE: Codex could not return allowance data."); return 1; } }
        if (args.Length > 0 && args[0] == "--history-live-check") { var history = new HistoryImporter(Path.Combine(LocalData.DirectoryPath, "usage-history.json")); var result = history.Refresh(CancellationToken.None); var events = history.Snapshot(); Console.WriteLine("LOCAL HISTORY: " + events.Count + " events, " + HistoryQuery.Models(events).Count + " models; more=" + result.More + "; " + result.Notice); return 0; }
        if (args.Length > 0 && args[0] == "--subscription-live-check") {
            var prefs = Preferences.Load(); var result = CodexSubscriptionReader.Read(HistoryImporter.CodexHome, prefs.CachedAccountKey, DateTimeOffset.Now, CancellationToken.None);
            if (result.Snapshot == null) { Console.WriteLine("SUBSCRIPTION UNAVAILABLE: " + result.Notice); return 1; }
            Console.WriteLine("SUBSCRIPTION PASS: account matched; paid through " + result.Snapshot.PaidThrough.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "; source checked " + (result.Snapshot.CheckedAt.HasValue ? result.Snapshot.CheckedAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "unknown") + ". No manual date used."); return 0;
        }
        if (args.Length > 0 && args[0] == "--fake-server") return FakeServer(args.Length > 1 ? args[1] : "ok");
        try
        {
            Check(UsageModel.Level(50.01) == AllowanceLevel.Green, "Above 50 is green");
            Check(UsageModel.Level(50) == AllowanceLevel.Orange, "Exactly 50 is orange");
            Check(UsageModel.Level(10) == AllowanceLevel.Orange, "Exactly 10 is orange");
            Check(UsageModel.Level(9.99) == AllowanceLevel.Red, "Below 10 is red");
            Check(UsageModel.Level(0) == AllowanceLevel.Red, "Zero is red");
            Check(UsageModel.Level(100) == AllowanceLevel.Green, "100 is green");
            Check(UsageModel.Level(Double.NaN) == AllowanceLevel.Unknown, "NaN is unknown");
            Check(UsageModel.Level(Double.PositiveInfinity) == AllowanceLevel.Unknown, "Infinity is unknown");
            Check(UsageModel.Percent(50.001) == ">50%", "Rounding must not contradict green");
            Check(UsageModel.Percent(9.999) == "<10%", "Rounding must not contradict red");
            var legacy = Parse("\"rateLimits\":{\"primary\":" + Weekly + ",\"secondary\":null,\"planType\":\"pro\"}");
            Check(legacy.Limiting.Remaining == 77 && legacy.Plan == "pro", "Legacy snapshot");
            Check(legacy.Limiting.Label == "Weekly", "Duration labels");
            Check(legacy.Windows.Count == 1, "Null secondary skipped");
            var multi = Parse("\"rateLimitsByLimitId\":{\"codex\":{\"primary\":" + Weekly + ",\"secondary\":{\"usedPercent\":95,\"windowDurationMins\":300}},\"other\":{\"primary\":{\"usedPercent\":40}}},\"rateLimits\":{\"primary\":{\"usedPercent\":99}}");
            Check(multi.Windows.Count == 3, "Multi-bucket preferred without legacy duplication");
            Check(multi.Limiting.Remaining == 5 && multi.Limiting.Label == "5-hour", "Most restrictive window");
            Check(Parse("\"rateLimitsByLimitId\":{},\"rateLimits\":{\"primary\":" + Weekly + "}").Limiting.Remaining == 77, "Empty map falls back");
            Throws<FormatException>(() => Parse("\"rateLimits\":{\"primary\":{\"usedPercent\":null}}"), "Null is not zero");
            Throws<FormatException>(() => Parse("\"rateLimits\":{\"primary\":{}}"), "Missing is not zero");
            Throws<FormatException>(() => Parse("\"rateLimits\":{\"primary\":{\"usedPercent\":true}}"), "Boolean is not a percentage");
            Throws<FormatException>(() => Parse("\"rateLimits\":null"), "Unavailable is not 100%");
            Throws<FormatException>(() => Parse("\"rateLimits\":{\"primary\":{\"usedPercent\":\"NaN\"}}"), "Reject NaN data");
            Check(Parse("\"rateLimits\":{\"primary\":{\"usedPercent\":110}}").Limiting.Remaining == 0, "Clamp overused");
            Check(Parse("\"rateLimits\":{\"primary\":{\"usedPercent\":-10}}").Limiting.Remaining == 100, "Clamp negative usage");
            Check(!Parse("\"rateLimits\":{\"primary\":{\"usedPercent\":12,\"resetsAt\":null}}").Limiting.ResetsAt.HasValue, "Missing reset stays unavailable");
            Check(Parse("\"ordinaryUsageAllowed\":false,\"rateLimits\":{\"primary\":" + Weekly + "}").Blocked, "Account blocked");
            Check(Parse("\"rateLimits\":{\"spendControlReached\":true,\"primary\":" + Weekly + "}").Blocked, "Spend control blocked");
            Check(UsageModel.IsStale(legacy, Now.AddMinutes(4)), "Old snapshot is stale");
            Check(!UsageModel.IsStale(legacy, Now.AddSeconds(30)), "Fresh snapshot usable");
            legacy.Limiting.ResetsAt = Now.AddSeconds(-1);
            Check(UsageModel.IsStale(legacy, Now), "Reset snapshot must refresh");
            Check(UsageModel.Countdown(null, Now) == "Not provided", "Unknown reset");
            Check(UsageModel.Countdown(Now.AddDays(2).AddHours(3), Now) == "2d 3h", "Countdown days");
            Check(UsageModel.Countdown(Now.AddHours(2).AddMinutes(5), Now) == "2h 5m", "Countdown hours");
            Check(UsageModel.Countdown(Now.AddSeconds(2), Now) == "1m", "Countdown under a minute");
            Check(UsageModel.Countdown(Now, Now) == "Refreshing soon", "Reset doesn't invent fresh quota");
            HistoryTests.Run(Check);
            SubscriptionTests.Run(Check);
            ProtocolTests().GetAwaiter().GetResult();
            Console.WriteLine("PASS: " + passed + " allowance, protocol, history, subscription, and preference checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex.Message); return 1; }
    }

    private static async Task ProtocolTests()
    {
        string self = typeof(CoreTests).Assembly.Location;
        var success = await CodexClient.ReadFromAsync(self, "--fake-server ok", CancellationToken.None, TimeSpan.FromSeconds(5));
        Check(success.Limiting.Remaining == 77, "JSON-RPC handshake and unrelated notification");
        try { await CodexClient.ReadFromAsync(self, "--fake-server error", CancellationToken.None, TimeSpan.FromSeconds(5)); throw new Exception("Expected server error"); }
        catch (IOException) { passed++; }
        try { await CodexClient.ReadFromAsync(self, "--fake-server exit", CancellationToken.None, TimeSpan.FromSeconds(5)); throw new Exception("Expected closed connection"); }
        catch (IOException) { passed++; }
        var elapsed = Stopwatch.StartNew();
        try { await CodexClient.ReadFromAsync(self, "--fake-server hang", CancellationToken.None, TimeSpan.FromMilliseconds(350)); throw new Exception("Expected timeout"); }
        catch (TimeoutException) { passed++; }
        Check(elapsed.Elapsed < TimeSpan.FromSeconds(5), "Hung subprocess terminated");
        using (var cancellation = new CancellationTokenSource(250))
        {
            try { await CodexClient.ReadFromAsync(self, "--fake-server hang", cancellation.Token, TimeSpan.FromSeconds(5)); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { passed++; }
        }
    }

    private static int FakeServer(string mode)
    {
        // Emulate Codex UTF-8 pipes independently of the Windows console code page.
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), true));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        var serializer = new JavaScriptSerializer();
        var first = serializer.DeserializeObject(Console.ReadLine()) as Dictionary<string, object>;
        if ((string)first["method"] != "initialize") return 2;
        Console.WriteLine("{\"id\":1,\"result\":{}}");
        var notification = serializer.DeserializeObject(Console.ReadLine()) as Dictionary<string, object>;
        if ((string)notification["method"] != "initialized") return 3;
        var read = serializer.DeserializeObject(Console.ReadLine()) as Dictionary<string, object>;
        if ((string)read["method"] != "account/rateLimits/read") return 4;
        if (mode == "hang") { Thread.Sleep(15000); return 0; }
        if (mode == "exit") return 0;
        if (mode == "error") { Console.WriteLine("{\"id\":2,\"error\":{\"message\":\"private upstream detail\"}}"); return 0; }
        Console.WriteLine("not-json");
        Console.WriteLine("{\"method\":\"account/updated\",\"params\":{}}");
        Console.WriteLine("{\"id\":55,\"result\":{}}");
        Console.WriteLine("{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":" + Weekly + "}}}");
        return 0;
    }
}
