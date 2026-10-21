using System;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Media;

namespace CodexUsageMini
{
    public static class LocalData
    {
        public static string DirectoryPath
        {
            get
            {
                string custom = Environment.GetEnvironmentVariable("CODEX_USAGE_MINI_DATA");
                if (!String.IsNullOrWhiteSpace(custom)) return Path.GetFullPath(custom);
                string profile = Path.Combine("E:\\Users", Environment.UserName);
                return Directory.Exists(profile) ? Path.Combine(profile, "AppData", "Local", "CodexUsageMini") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageMini");
            }
        }
        public static void AtomicWrite(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        }
    }

    public sealed class Preferences
    {
        public bool Pinned = true;
        public bool Compact;
        public double Left = -1;
        public double Top = -1;
        public double Scale = 1;
        public double Transparency;
        public string Theme = "Dark";
        public string PlanExpiry;
        public bool DetectPlanExpiry = true;
        public string CachedPlan;
        public string CachedAccountKey;
        public string ExpiryAccountKey;
        public void Normalize()
        {
            Scale = Double.IsNaN(Scale) || Double.IsInfinity(Scale) ? 1 : Math.Max(.5, Math.Min(1.25, Scale));
            Transparency = Double.IsNaN(Transparency) || Double.IsInfinity(Transparency) ? 0 : Math.Max(0, Math.Min(50, Transparency));
            if (Array.IndexOf(Palette.Names, Theme) < 0) Theme = "Dark";
            DateTime parsed;
            if (!String.IsNullOrEmpty(PlanExpiry) && !DateTime.TryParseExact(PlanExpiry, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out parsed)) PlanExpiry = null;
        }
        public static Preferences Load()
        {
            try { var data = new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(Path.Combine(LocalData.DirectoryPath, "preferences.json"))) ?? new Preferences(); data.Normalize(); return data; }
            catch { return new Preferences(); }
        }
        public bool Save()
        {
            try { Normalize(); LocalData.AtomicWrite(Path.Combine(LocalData.DirectoryPath, "preferences.json"), new JavaScriptSerializer().Serialize(this)); return true; }
            catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }
        public string ExpiryLabel(string accountKey)
        {
            if (String.IsNullOrEmpty(PlanExpiry)) return "Not provided";
            if (!String.IsNullOrEmpty(ExpiryAccountKey) && !String.IsNullOrEmpty(accountKey) && ExpiryAccountKey != accountKey) return "Not set for this account";
            DateTime date;
            return DateTime.TryParseExact(PlanExpiry, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date) ? date.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture) + " (entered)" : "Not provided";
        }
    }

    public sealed class Palette
    {
        public static readonly string[] Names = { "Daylight", "Dark", "Ocean", "Forest" };
        public string Name;
        public Brush Background;
        public Brush Surface;
        public Brush Ink;
        public Brush Muted;
        public Brush Track;
        public Brush Border;
        public Brush Highlight;
        public bool Light;
        public static Brush Brush(string value) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush; }
        public static Palette For(string name)
        {
            string[] values;
            switch (name)
            {
                case "Daylight": values = new[] { "#F5F7F9", "#E7EDF1", "#17272B", "#536970", "#D1DCE0", "#B9C9CD", "#126881" }; break;
                case "Ocean": values = new[] { "#0C2336", "#15364C", "#EDF8FF", "#A0BDCF", "#284C65", "#406680", "#83D7FF" }; break;
                case "Forest": values = new[] { "#14281F", "#233D2D", "#EFF8EB", "#A6C2AC", "#34563F", "#4E7358", "#B5DFA0" }; break;
                default: name = "Dark"; values = new[] { "#111B1C", "#1B2829", "#F1F5F3", "#91A4A1", "#293637", "#344344", "#8DE2CA" }; break;
            }
            return new Palette { Name = name, Background = Brush(values[0]), Surface = Brush(values[1]), Ink = Brush(values[2]), Muted = Brush(values[3]), Track = Brush(values[4]), Border = Brush(values[5]), Highlight = Brush(values[6]), Light = name == "Daylight" };
        }
        public Brush Allowance(AllowanceLevel level)
        {
            if (level == AllowanceLevel.Green) return Brush(Light ? "#087B53" : "#63D9AC");
            if (level == AllowanceLevel.Orange) return Brush(Light ? "#A35A00" : "#FFB45E");
            if (level == AllowanceLevel.Red) return Brush(Light ? "#BB3047" : "#FF777F");
            return Muted;
        }
    }
}
