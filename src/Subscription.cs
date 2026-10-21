using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexUsageMini
{
    public sealed class SubscriptionSnapshot
    {
        public string AccountKey;
        public string Plan;
        public DateTimeOffset PaidThrough;
        public DateTimeOffset? CheckedAt;
    }

    public sealed class SubscriptionReadResult
    {
        public SubscriptionSnapshot Snapshot;
        public string Notice;
        public bool AccountMismatch;
        public static SubscriptionReadResult Unavailable(string notice) { return new SubscriptionReadResult { Notice = notice }; }
    }

    // Reads subscription metadata already stored by Codex. This is not a billing-session
    // client or a JWT verifier: no token is used for authorization, sent, logged, or saved.
    public static class CodexSubscriptionReader
    {
        private const int MaxBytes = 1024 * 1024;
        private static readonly Regex IsoDate = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant);

        public static SubscriptionReadResult Read(string codexHome, string expectedAccountKey, DateTimeOffset now, CancellationToken cancellation)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                string path = Path.Combine(codexHome, "auth.json");
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > MaxBytes) return SubscriptionReadResult.Unavailable("Codex sign-in metadata is too large to read safely.");
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                    {
                        var text = new StringBuilder(); var buffer = new char[4096]; int count;
                        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (text.Length + count > MaxBytes) return SubscriptionReadResult.Unavailable("Codex sign-in metadata is too large to read safely.");
                            text.Append(buffer, 0, count);
                        }
                        return ParseManagedSignIn(text.ToString(), expectedAccountKey, now);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { return SubscriptionReadResult.Unavailable("Codex file-based sign-in metadata is unavailable. Secure-store sign-ins may not expose this date."); }
            catch (UnauthorizedAccessException) { return SubscriptionReadResult.Unavailable("Codex sign-in metadata cannot be read by this Windows user."); }
            catch (ArgumentException) { return SubscriptionReadResult.Unavailable("Codex sign-in metadata could not be read."); }
            catch (NotSupportedException) { return SubscriptionReadResult.Unavailable("Codex sign-in metadata could not be read."); }
            catch (System.Security.SecurityException) { return SubscriptionReadResult.Unavailable("Windows did not allow reading Codex subscription metadata."); }
        }

        public static SubscriptionReadResult ParseManagedSignIn(string json, string expectedAccountKey, DateTimeOffset now)
        {
            try
            {
                if (String.IsNullOrEmpty(json) || json.Length > MaxBytes) return Invalid();
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaxBytes, RecursionLimit = 32 };
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                string mode = UsageModel.Value(root, "auth_mode") as string;
                if (mode != null && mode != "chatgpt") return SubscriptionReadResult.Unavailable("This Codex sign-in does not provide ChatGPT subscription metadata.");
                var tokens = UsageModel.Obj(root, "tokens");
                string idToken = UsageModel.Value(tokens, "id_token") as string;
                string selectedAccount = UsageModel.Value(tokens, "account_id") as string;
                if (String.IsNullOrWhiteSpace(idToken) || String.IsNullOrWhiteSpace(selectedAccount)) return SubscriptionReadResult.Unavailable("This Codex sign-in does not expose a subscription date.");
                string[] parts = idToken.Split('.');
                if (parts.Length != 3 || parts[0].Length == 0 || parts[2].Length == 0 || parts[1].Length > 128 * 1024) return Invalid();
                string encoded = parts[1].Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
                string payload = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
                var claims = serializer.DeserializeObject(payload) as Dictionary<string, object>;
                string issuer = UsageModel.Value(claims, "iss") as string;
                if (issuer != "https://auth.openai.com" && issuer != "https://auth0.openai.com/" && issuer != "https://auth0.openai.com") return Invalid();
                var account = UsageModel.Obj(claims, "https://api.openai.com/auth");
                string claimAccount = UsageModel.Value(account, "chatgpt_account_id") as string;
                if (claimAccount != selectedAccount) return Mismatch();
                string key = HistoryImporter.Hash(selectedAccount);
                if (!String.IsNullOrEmpty(expectedAccountKey) && key != expectedAccountKey) return Mismatch();
                DateTimeOffset until;
                // JWT exp and quota reset timestamps are deliberately never used as plan dates.
                if (!TryDate(UsageModel.Value(account, "chatgpt_subscription_active_until"), out until)) return SubscriptionReadResult.Unavailable("Codex has not supplied a subscription paid-through date.");
                DateTimeOffset start, checkedAt;
                object rawStart = UsageModel.Value(account, "chatgpt_subscription_active_start");
                if (rawStart != null && (!TryDate(rawStart, out start) || start >= until)) return Invalid();
                DateTimeOffset? checkedTime = null;
                object rawChecked = UsageModel.Value(account, "chatgpt_subscription_last_checked");
                if (rawChecked != null)
                {
                    if (!TryDate(rawChecked, out checkedAt) || checkedAt > now.AddMinutes(5)) return Invalid();
                    checkedTime = checkedAt;
                }
                string plan = UsageModel.Value(account, "chatgpt_plan_type") as string;
                if (plan != null && !Regex.IsMatch(plan, "^[a-z0-9_-]{1,40}$", RegexOptions.CultureInvariant)) plan = null;
                return new SubscriptionReadResult { Snapshot = new SubscriptionSnapshot { AccountKey = key, Plan = plan, PaidThrough = until, CheckedAt = checkedTime }, Notice = "Read from Codex's local subscription metadata." };
            }
            catch (ArgumentException) { return Invalid(); }
            catch (FormatException) { return Invalid(); }
            catch (InvalidOperationException) { return Invalid(); }
        }

        private static bool TryDate(object value, out DateTimeOffset date)
        {
            date = default(DateTimeOffset); string text = value as string;
            return text != null && text.Length <= 40 && IsoDate.IsMatch(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
        private static SubscriptionReadResult Invalid() { return SubscriptionReadResult.Unavailable("Codex subscription metadata is incomplete or invalid; no date was inferred."); }
        private static SubscriptionReadResult Mismatch() { return new SubscriptionReadResult { AccountMismatch = true, Notice = "Codex subscription metadata belongs to a different account. Refresh after switching accounts." }; }
    }

    public sealed class SubscriptionDisplay
    {
        public string Heading;
        public string Value;
        public string Detail;
        public bool Automatic;
        public bool LastKnown;

        public static SubscriptionDisplay Create(SubscriptionReadResult result, Preferences settings, string accountKey, DateTimeOffset now)
        {
            var display = new SubscriptionDisplay { Heading = "PLAN EXPIRY", Value = settings.ExpiryLabel(accountKey) };
            string fallback = display.Value == "Not provided" ? "You can enter a manual fallback below." : "Showing your manually entered fallback.";
            if (!settings.DetectPlanExpiry) { display.Detail = "Automatic detection is off. " + fallback; return display; }
            if (result == null) { display.Detail = "Checking Codex subscription metadata. " + fallback; return display; }
            SubscriptionSnapshot snapshot = result.Snapshot;
            if (result.AccountMismatch || (snapshot != null && !String.IsNullOrEmpty(accountKey) && snapshot.AccountKey != accountKey))
                return new SubscriptionDisplay { Heading = "PLAN EXPIRY", Value = "Account changed", Detail = "The subscription account does not match the allowance account. Refresh Codex before using this date." };
            if (snapshot == null) { display.Detail = result.Notice + " " + fallback; return display; }
            display.Automatic = true; display.Heading = "PAID THROUGH";
            display.LastKnown = !snapshot.CheckedAt.HasValue || now - snapshot.CheckedAt.Value > TimeSpan.FromDays(3) || snapshot.PaidThrough <= now;
            display.Value = snapshot.PaidThrough.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture) + (display.LastKnown ? " (last known)" : " (Codex)");
            string check = snapshot.CheckedAt.HasValue ? snapshot.CheckedAt.Value.ToLocalTime().ToString("MMM d, yyyy HH:mm zzz", CultureInfo.InvariantCulture) : "not supplied";
            display.Detail = "Subscription period end reported by Codex sign-in metadata. Codex last checked: " + check + ". Renewal/cancellation status is not included in this source; this date is not a confirmed cancellation or allowance reset.";
            if (display.LastKnown) display.Detail += " This is a last-known date; check Codex Billing for current details.";
            return display;
        }
    }
}
