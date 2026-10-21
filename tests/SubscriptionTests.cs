using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using CodexUsageMini;

internal static class SubscriptionTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly DateTimeOffset Now = new DateTimeOffset(2030, 5, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Key = HistoryImporter.Hash("account-one");
    private static string Stamp(DateTimeOffset value) { return value.ToString("o", CultureInfo.InvariantCulture); }
    private static Dictionary<string, object> Account()
    {
        return new Dictionary<string, object> { { "chatgpt_account_id", "account-one" }, { "chatgpt_plan_type", "pro" }, { "chatgpt_subscription_active_start", Stamp(Now.AddDays(-10)) }, { "chatgpt_subscription_active_until", Stamp(Now.AddDays(20)) }, { "chatgpt_subscription_last_checked", Stamp(Now.AddHours(-1)) } };
    }
    private static string SignIn(Dictionary<string, object> account, string selected = "account-one", string mode = "chatgpt", string issuer = "https://auth.openai.com")
    {
        string payload = Json.Serialize(new Dictionary<string, object> { { "iss", issuer }, { "exp", 946684800 }, { "https://api.openai.com/auth", account } });
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Json.Serialize(new { auth_mode = mode, tokens = new { account_id = selected, id_token = "eyJhbGciOiJSUzI1NiJ9." + encoded + ".synthetic-signature", access_token = "SENSITIVE-ACCESS-MUST-NOT-ESCAPE", refresh_token = "SENSITIVE-REFRESH-MUST-NOT-ESCAPE" } });
    }
    private static SubscriptionReadResult Parse(Dictionary<string, object> account) { return CodexSubscriptionReader.ParseManagedSignIn(SignIn(account), Key, Now); }
    public static void Run(Action<bool, string> check)
    {
        check(Json.Deserialize<Preferences>("{\"Scale\":1}").DetectPlanExpiry, "Existing preferences enable requested automatic detection by default");
        var valid = Parse(Account());
        check(valid.Snapshot != null && valid.Snapshot.PaidThrough == Now.AddDays(20), "Detect subscription period from Codex metadata");
        check(valid.Snapshot.AccountKey == Key && valid.Snapshot.CheckedAt == Now.AddHours(-1), "Bind automatic date to the account and preserve source check time");
        check(valid.Snapshot.PaidThrough.Year == 2030, "Never use the authentication JWT expiry as the subscription date");
        var noDate = Account(); noDate.Remove("chatgpt_subscription_active_until");
        check(Parse(noDate).Snapshot == null, "Missing subscription date stays unknown even when JWT exp exists");
        check(CodexSubscriptionReader.ParseManagedSignIn(SignIn(Account()), HistoryImporter.Hash("account-two"), Now).AccountMismatch, "Reject metadata for a different allowance account");
        check(CodexSubscriptionReader.ParseManagedSignIn(SignIn(Account(), "account-two"), null, Now).AccountMismatch, "Selected account and token account must match");
        check(CodexSubscriptionReader.ParseManagedSignIn(SignIn(Account(), "account-one", "apikey"), Key, Now).Snapshot == null, "API-key sign-ins do not infer a subscription");
        check(CodexSubscriptionReader.ParseManagedSignIn(SignIn(Account(), "account-one", "chatgpt", "https://untrusted.invalid"), Key, Now).Snapshot == null, "Reject an unexpected metadata issuer");
        check(CodexSubscriptionReader.ParseManagedSignIn("broken", Key, Now).Snapshot == null, "Invalid JSON does not crash billing detection");
        check(CodexSubscriptionReader.ParseManagedSignIn("{\"tokens\":{\"account_id\":\"account-one\",\"id_token\":\"x.@@.z\"}}", Key, Now).Snapshot == null, "Invalid JWT payload does not crash detection");
        var invalidDate = Account(); invalidDate["chatgpt_subscription_active_until"] = "2030-06-35T12:00:00Z";
        check(Parse(invalidDate).Snapshot == null, "Reject an impossible subscription date");
        invalidDate["chatgpt_subscription_active_until"] = "2030-06-04";
        check(Parse(invalidDate).Snapshot == null, "Require an explicit timestamp and timezone");
        invalidDate["chatgpt_subscription_active_until"] = 1900000000;
        check(Parse(invalidDate).Snapshot == null, "Do not treat arbitrary numeric timestamps as subscription dates");
        var future = Account(); future["chatgpt_subscription_last_checked"] = Stamp(Now.AddHours(2));
        check(Parse(future).Snapshot == null, "Reject an implausible future source check");
        var reversed = Account(); reversed["chatgpt_subscription_active_start"] = Stamp(Now.AddDays(21));
        check(Parse(reversed).Snapshot == null, "Reject a subscription period ending before it starts");
        var settings = new Preferences { PlanExpiry = "2040-12-31", ExpiryAccountKey = Key };
        var view = SubscriptionDisplay.Create(valid, settings, Key, Now);
        check(view.Automatic && view.Heading == "PAID THROUGH" && view.Value.Contains(Now.AddDays(20).ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture)), "Detected period takes precedence over a manual fallback and is labelled accurately");
        check(settings.PlanExpiry == "2040-12-31", "Automatic detection never overwrites a manual date");
        check(view.Detail.Contains("Renewal/cancellation status is not included") && view.Detail.Contains("Codex last checked"), "Show source freshness and avoid claiming a confirmed cancellation");
        settings.DetectPlanExpiry = false; view = SubscriptionDisplay.Create(valid, settings, Key, Now);
        check(!view.Automatic && view.Value.Contains("Dec 31, 2040") && view.Detail.Contains("off"), "Disabling detection uses the entered date");
        settings.DetectPlanExpiry = true; view = SubscriptionDisplay.Create(Parse(noDate), settings, Key, Now);
        check(!view.Automatic && view.Value.Contains("Dec 31, 2040"), "Unavailable automatic lookup preserves manual fallback");
        view = SubscriptionDisplay.Create(valid, settings, HistoryImporter.Hash("account-two"), Now);
        check(!view.Automatic && view.Value == "Account changed", "Never show either account's date as a match after an account switch");
        var boundary = Account(); boundary["chatgpt_subscription_last_checked"] = Stamp(Now.AddDays(-3));
        check(!SubscriptionDisplay.Create(Parse(boundary), settings, Key, Now).LastKnown, "Source exactly three days old stays within the documented freshness window");
        var old = Account(); old["chatgpt_subscription_last_checked"] = Stamp(Now.AddDays(-5));
        view = SubscriptionDisplay.Create(Parse(old), settings, Key, Now);
        check(view.LastKnown && view.Value.Contains("last known"), "Old source metadata is explicitly last known");
        var uncheckedAccount = Account(); uncheckedAccount.Remove("chatgpt_subscription_last_checked");
        check(SubscriptionDisplay.Create(Parse(uncheckedAccount), settings, Key, Now).LastKnown, "Unknown source freshness is not labelled current");
        var ended = Account(); ended["chatgpt_subscription_active_until"] = Stamp(Now.AddDays(-1));
        check(SubscriptionDisplay.Create(Parse(ended), settings, Key, Now).LastKnown, "A past paid-through date is not presented as current entitlement");
        string sanitized = Json.Serialize(valid);
        check(!sanitized.Contains("SENSITIVE") && !sanitized.Contains("account-one") && !sanitized.Contains("synthetic-signature"), "No credentials or raw account identifiers leave the reader");
        string baseDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string folder = Path.Combine(baseDir, "subscription-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "auth.json"), SignIn(Account()));
            check(CodexSubscriptionReader.Read(folder, Key, Now, CancellationToken.None).Snapshot != null, "Read Codex's local file through the actual provider");
            File.WriteAllText(Path.Combine(folder, "auth.json"), "partial");
            check(CodexSubscriptionReader.Read(folder, Key, Now, CancellationToken.None).Snapshot == null, "A partial sign-in write leaves lookup unavailable");
            File.WriteAllText(Path.Combine(folder, "auth.json"), new string('x', 1024 * 1024 + 1));
            check(CodexSubscriptionReader.Read(folder, Key, Now, CancellationToken.None).Snapshot == null, "Bound the credential metadata read");
            File.Delete(Path.Combine(folder, "auth.json"));
            check(CodexSubscriptionReader.Read(folder, Key, Now, CancellationToken.None).Snapshot == null, "Missing or secure-store credentials fall back safely");
            using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); bool cancelled = false; try { CodexSubscriptionReader.Read(folder, Key, Now, cancel.Token); } catch (OperationCanceledException) { cancelled = true; } check(cancelled, "Subscription reads honour shutdown cancellation"); }
        }
        finally
        {
            string resolved = Path.GetFullPath(folder);
            if (Path.GetDirectoryName(resolved) == baseDir && Path.GetFileName(resolved).StartsWith("subscription-tests-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
        }
    }
}
