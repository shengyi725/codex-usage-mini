using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUsageMini
{
    public sealed class CodexClient
    {
        public static string FindExecutable()
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_USAGE_MINI_CODEX");
            if (!String.IsNullOrWhiteSpace(configured))
            {
                if (File.Exists(configured) && Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(configured);
                throw new FileNotFoundException("CODEX_USAGE_MINI_CODEX must point to codex.exe.");
            }
            // Discover versioned Codex desktop installations without storing an installation-specific path.
            string bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(bin))
            {
                try
                {
                    string latest = Directory.GetFiles(bin, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                    if (latest != null) return latest;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(folder.Trim().Trim('"'), "codex.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            string npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
            if (Directory.Exists(npm))
            {
                string exe = Directory.GetFiles(npm, "codex.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (exe != null) return exe;
            }
            throw new FileNotFoundException("Codex not found. Install Codex and sign in with ChatGPT, then refresh.");
        }

        public Task<UsageSnapshot> ReadAsync(CancellationToken cancellation)
        {
            return ReadFromAsync(FindExecutable(), "app-server --listen stdio://", cancellation, TimeSpan.FromSeconds(25));
        }

        public static async Task<UsageSnapshot> ReadFromAsync(string executable, string arguments, CancellationToken cancellation, TimeSpan timeout)
        {
            using (var process = new Process())
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(timeout);
                process.StartInfo = new ProcessStartInfo(executable, arguments) {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                if (!process.Start()) throw new IOException("Could not start Codex.");
                // Drain stderr to prevent a full pipe. Never log it: upstream diagnostics may contain account data.
                process.ErrorDataReceived += delegate { };
                process.BeginErrorReadLine();
                using (deadline.Token.Register(delegate { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }))
                {
                    try
                    {
                        var serializer = new JavaScriptSerializer();
                        await Send(process, new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "codex_usage_mini", title = "Codex Usage Mini", version = "0.3.0" } } });
                        await Response(process, 1, serializer, deadline.Token);
                        await Send(process, new { method = "initialized", @params = new { } });
                        await Send(process, new { id = 2, method = "account/rateLimits/read" });
                        string json = await Response(process, 2, serializer, deadline.Token);
                        return UsageModel.Parse(json, DateTimeOffset.Now);
                    }
                    catch (Exception)
                    {
                        if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                        if (deadline.IsCancellationRequested) throw new TimeoutException("Codex did not respond. Check your connection and refresh.");
                        throw;
                    }
                    finally
                    {
                        try { process.StandardInput.Close(); } catch (IOException) { }
                        try { if (!process.WaitForExit(1000)) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }

                    }
                }
            }
        }

        private static async Task Send(Process process, object message)
        {
            await process.StandardInput.WriteLineAsync(new JavaScriptSerializer().Serialize(message));
            await process.StandardInput.FlushAsync();
        }

        private static async Task<string> Response(Process process, int id, JavaScriptSerializer serializer, CancellationToken cancellation)
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                string line = await process.StandardOutput.ReadLineAsync();
                if (line == null) throw new IOException("Codex connection closed. Sign in to Codex, then refresh.");
                Dictionary<string, object> message;
                try { message = serializer.DeserializeObject(line) as Dictionary<string, object>; }
                catch (ArgumentException) { continue; }
                object receivedId = UsageModel.Value(message, "id");
                if (receivedId == null || Convert.ToString(receivedId, System.Globalization.CultureInfo.InvariantCulture) != id.ToString()) continue;
                if (UsageModel.Value(message, "error") != null)
                    throw new IOException("Usage unavailable. Check your ChatGPT sign-in in Codex, then refresh.");
                return line;
            }
        }
    }
}
