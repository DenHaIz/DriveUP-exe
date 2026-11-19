using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DriveUp
{
    /// <summary>
    /// "Pause updates" of Windows Settings for a number of days (the same registry values Settings
    /// writes). The old state — including an earlier pause — comes back exactly.
    /// </summary>
    class PauseUpdates : Tweak
    {
        const string Path = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
        static readonly string[] Names = { "PauseUpdatesExpiryTime", "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime" };
        public int Days = 7;

        public override string Capture() { return string.Join("|", Names.Select(n => Reg.Capture(true, Path, n))); }

        public override void Restore(string raw)
        {
            var parts = raw.Split('|');
            if (parts.Length != Names.Length) return;
            for (int i = 0; i < Names.Length; i++) Reg.Restore(true, Path, Names[i], parts[i]);
        }

        public override string ValueOf(string raw)
        {
            var parts = raw.Split('|');
            DateTime until;
            return parts.Length > 0 && parts[0].StartsWith("s:") &&
                   DateTime.TryParse(parts[0].Substring(2), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out until) &&
                   until > DateTime.UtcNow ? "1" : "0";
        }

        public override void Set(string value)
        {
            if (value != "1") { foreach (var n in Names) Reg.Delete(true, Path, n); return; }
            string start = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string end = DateTime.UtcNow.AddDays(Days).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            Reg.SetString(true, Path, Names[0], end);
            Reg.SetString(true, Path, Names[1], start);
            Reg.SetString(true, Path, Names[2], end);
            Reg.SetString(true, Path, Names[3], start);
            Reg.SetString(true, Path, Names[4], end);
        }

        public override string Describe(string value)
        {
            return value == "1" ? L.T("на паузе", "paused") : L.T("приходят как обычно", "arrive as usual");
        }
    }

    /// <summary>
    /// A preinstalled Store app (games, trials, promotions). Removed for every user of the PC with
    /// Remove-AppxPackage -AllUsers; "Return" registers it again for the person at the desktop from the copy
    /// Windows keeps for new users (the others can take it from the Store), and where there is none, opens its
    /// Microsoft Store page.
    /// </summary>
    class StoreApp : Tweak
    {
        /// <summary>Package family name prefix ("king.com.CandyCrushSaga_").</summary>
        public readonly string Prefix;

        public StoreApp(string prefix, string title)
        {
            Prefix = prefix;
            Id = "app." + prefix.TrimEnd('_');
            Category = TuneCat.Apps;
            Glyph = "\uE74D";
            Name = () => title;
        }

        string Family() { return Appx.Find(Prefix); }

        /// <summary>Installed for some user of the PC and not a part of Windows that cannot be removed.</summary>
        public override bool Available(Machine m)
        {
            string f = Family();
            return f != null && Appx.Removable(f);
        }

        /// <summary>"1|family" while installed, "0|family" after it was removed.</summary>
        public override string Capture()
        {
            string f = Family();
            return (f != null ? "1|" : "0|") + (f ?? Prefix);
        }

        public override void Restore(string raw)
        {
            var p = raw.Split('|');
            if (p.Length != 2 || p[0] != "1" || Family() != null) return;
            if (Reg.TestRoot != null) { Reg.SetDword(false, Appx.Families + "\\" + p[1], "x", 1); Appx.Changed(); return; }
            if (!Appx.ValidFamily(p[1])) return;
            // A preinstalled app stays on the disk for new users: registered again from there, as it was
            // (for the person at the desktop, by a PowerShell running as them, when DriveUP runs as another account).
            string command = "Add-AppxPackage -RegisterByFamilyName -MainPackage '" + p[1] + "' -ErrorAction Stop";
            try
            {
                if (DesktopUser.Other) UserShell.RunAsUser(Appx.PowerShellExe, Appx.Args(command), 180000);
                else Appx.PowerShell(command);
            }
            catch { }
            Appx.Changed();
            // Not on this computer any more: its Store page opens for the user (with the user's rights).
            if (Family() == null) UserShell.OpenUrl("ms-windows-store://pdp/?PFN=" + p[1]);
        }

        public override string ValueOf(string raw) { return raw.StartsWith("1|") ? "1" : "0"; }

        public override void Set(string value)
        {
            if (value == "1") return;
            string f = Family();
            if (f == null) return;
            if (!Appx.ValidFamily(f)) throw new InvalidOperationException("package name");
            if (Reg.TestRoot != null)
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Reg.TestRoot + @"\HKCU\" + Appx.Families, true)) if (k != null) k.DeleteSubKeyTree(f, false);
                Appx.Changed();
                return;
            }
            // For every user of the PC (as administrator); unelevated (the test build) only for this account.
            string all = SafePath.Elevated ? " -AllUsers" : "";
            // Removed and checked in one go: exit 2 = Windows still has it installed for someone.
            string err;
            int code = Appx.PowerShell(
                "$ErrorActionPreference = 'Stop'; " +
                "try { @(Get-AppxPackage" + all + " | Where-Object { $_.PackageFamilyName -eq '" + f + "' }) | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName" + all + " } } " +
                "catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }; " +
                "if (@(Get-AppxPackage" + all + " | Where-Object { $_.PackageFamilyName -eq '" + f + "' " + Appx.InstalledFilter(all.Length > 0) + " }).Count -gt 0) { exit 2 }", out err);
            Appx.Changed();
            if (code == 0) return;
            throw new InvalidOperationException(Appx.Explain(err, code));
        }

        public override string Describe(string value) { return value == "1" ? L.T("установлено", "installed") : L.T("удалено", "removed"); }
    }

    /// <summary>
    /// Store packages installed for the person at the desktop, as Windows' package manager lists them.
    /// The registry's AppModel\Repository\Families is not that list: it can be missing (newer Windows keeps it in
    /// the state repository) and keeps families of packages removed long ago — an app was offered or not by
    /// chance, and a removal that worked was reported as failed because its old key was still there.
    /// </summary>
    static class Appx
    {
        public const string Families = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Families";

        static readonly object sync = new object();
        /// <summary>Family name → can be removed (not a part of Windows), for the desktop user.</summary>
        static Dictionary<string, bool> cache;
        static DateTime cachedAt;

        public static bool ValidFamily(string f) { return f != null && Regex.IsMatch(f, @"^[A-Za-z0-9.\-]+_[a-z0-9]+$"); }

        /// <summary>The installed family that starts with this prefix ("king.com.CandyCrushSaga_"), or null.</summary>
        public static string Find(string prefix)
        {
            return List().Keys.FirstOrDefault(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        public static bool Has(string family) { return List().ContainsKey(family); }

        public static bool Removable(string family)
        {
            bool r;
            return List().TryGetValue(family, out r) && r;
        }

        /// <summary>After installing or removing: the next question asks Windows again.</summary>
        public static void Changed() { lock (sync) cache = null; }

        static Dictionary<string, bool> List()
        {
            // Tests: their scratch registry, read each time.
            if (Reg.TestRoot != null) return FromRegistry(false);
            lock (sync)
            {
                // One question per plan (a second or two), not one per app.
                if (cache != null && (DateTime.UtcNow - cachedAt).TotalSeconds < 60) return cache;
                cache = Query() ?? FromRegistry(!DesktopUser.Other);
                cachedAt = DateTime.UtcNow;
                return cache;
            }
        }

        /// <summary>
        /// With -AllUsers Windows also lists packages only kept on disk for new accounts (staged, provisioned):
        /// those are not installed for anyone. The filter keeps the ones some user really has.
        /// </summary>
        public static string InstalledFilter(bool allUsers)
        {
            return allUsers ? "-and @($_.PackageUserInformation | Where-Object { $_.InstallState -eq 'Installed' }).Count -gt 0" : "";
        }

        /// <summary>Get-AppxPackage for every user of the PC (as administrator; this account alone otherwise); null if it failed.</summary>
        static Dictionary<string, bool> Query()
        {
            bool all = SafePath.Elevated;
            string output, err;
            int code;
            try
            {
                code = Run("Get-AppxPackage" + (all ? " -AllUsers" : "") +
                           " | Where-Object { -not $_.IsFramework " + InstalledFilter(all) + " } | ForEach-Object { $_.PackageFamilyName + '|' + $_.NonRemovable }", out output, out err);
            }
            catch { return null; }
            if (code != 0) return null;
            var d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n'))
            {
                var p = line.Trim().Split('|');
                if (p.Length != 2 || !ValidFamily(p[0])) continue;
                bool removable = !p[1].Equals("True", StringComparison.OrdinalIgnoreCase);
                bool was;
                d[p[0]] = d.TryGetValue(p[0], out was) ? was && removable : removable;
            }
            // Every Windows has some packages: an empty answer means the question failed, not "nothing installed".
            return d.Count > 0 ? d : null;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr fullNames, ref uint bufferLength, IntPtr buffer);

        /// <summary>
        /// Without PowerShell: the registry's families, each checked with Windows (for the account DriveUP runs as)
        /// when it can be, so a stale key is not taken for an installed app.
        /// </summary>
        static Dictionary<string, bool> FromRegistry(bool verify)
        {
            var d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Reg.SubKeyNames(false, Families))
            {
                if (verify)
                    try
                    {
                        uint count = 0, len = 0;
                        int r = GetPackagesByPackageFamily(f, ref count, IntPtr.Zero, ref len, IntPtr.Zero);
                        if ((r != 0 && r != 122 /* ERROR_INSUFFICIENT_BUFFER */) || count == 0) continue;
                    }
                    catch (EntryPointNotFoundException) { }
                d[f] = true;
            }
            return d;
        }

        public static string PowerShellExe { get { return System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"); } }

        /// <summary>
        /// The command base64-encoded (no quoting to break), with the progress bar off (redirected, PowerShell
        /// writes it to stderr as "#&lt; CLIXML", which used to come out as the error text) and UTF-8 output
        /// (the console's OEM code page read as ANSI turned Russian messages into garbage).
        /// </summary>
        public static string Args(string command)
        {
            string script = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " + command;
            return "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        }

        /// <summary>Runs one command without a window; returns the exit code and what it wrote to stderr.</summary>
        public static int PowerShell(string command, out string err)
        {
            string output;
            return Run(command, out output, out err);
        }

        public static void PowerShell(string command)
        {
            string err;
            if (PowerShell(command, out err) != 0) throw new InvalidOperationException(Explain(err, 1));
        }

        static int Run(string command, out string output, out string err)
        {
            var psi = new ProcessStartInfo(PowerShellExe, Args(command))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Environment.SystemDirectory,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            {
                // Read alongside: a plain ReadToEnd would wait for the end and the time limit would never apply.
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(180000)) { try { p.Kill(); } catch { } throw new TimeoutException(L.T("PowerShell не ответил за 3 минуты", "PowerShell did not answer in 3 minutes")); }
                output = o.Result;
                err = e.Result;
                return p.ExitCode;
            }
        }

        /// <summary>A removal error in words: the first meaningful line, the usual causes named.</summary>
        public static string Explain(string err, int code)
        {
            if (code == 2) return L.T("Windows не удалила приложение — попробуйте после перезагрузки", "Windows did not remove the app — try again after a restart");
            string e = err ?? "";
            if (e.IndexOf("0x80073D02", StringComparison.OrdinalIgnoreCase) >= 0)
                return L.T("приложение сейчас открыто — закройте его и повторите", "the app is open — close it and try again");
            if (e.IndexOf("0x80073CFA", StringComparison.OrdinalIgnoreCase) >= 0 || e.IndexOf("0x80070032", StringComparison.OrdinalIgnoreCase) >= 0)
                return L.T("это часть Windows, её нельзя удалить", "it is a part of Windows and cannot be removed");
            if (e.IndexOf("0x80073D19", StringComparison.OrdinalIgnoreCase) >= 0)
                return L.T("приложение обновляется — повторите через несколько минут", "the app is being updated — try again in a few minutes");
            // "Deployment failed with HRESULT: …" first, then the explanation in words, then a NOTE about the event log.
            var lines = e.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith("#<") && !s.StartsWith("<")).ToList();
            if (lines.Count == 0) return L.T("не удалось удалить", "could not remove");
            if (lines.Count > 1 && lines[0].IndexOf("HRESULT", StringComparison.OrdinalIgnoreCase) >= 0 &&
                !lines[1].StartsWith("NOTE", StringComparison.OrdinalIgnoreCase) && !lines[1].StartsWith("ПРИМЕЧАНИЕ", StringComparison.OrdinalIgnoreCase))
                return lines[1];
            return lines[0];
        }
    }

    /// <summary>A snapshot of how busy the system is, to compare before and after a mode.</summary>
    class Measure
    {
        public DateTime When;
        public int Processes, Services, Startup;
        public long MemoryMb;
        public double Cpu;
        /// <summary>The last boot's duration from the Windows boot performance log (s), 0 when unknown.</summary>
        public double BootSec;

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX { public uint Length, Load; public ulong Total, Avail, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended; }

        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        /// <summary>Takes about two seconds (processor load is averaged over them). Off the UI thread.</summary>
        public static Measure Take()
        {
            var m = new Measure { When = DateTime.Now };
            try { m.Processes = Process.GetProcesses().Length; } catch { }
            try
            {
                var ms = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                if (GlobalMemoryStatusEx(ref ms)) m.MemoryMb = (long)((ms.Total - ms.Avail) >> 20);
            }
            catch { }
            try
            {
                long i1, k1, u1, i2, k2, u2;
                GetSystemTimes(out i1, out k1, out u1);
                System.Threading.Thread.Sleep(2000);
                GetSystemTimes(out i2, out k2, out u2);
                long total = (k2 - k1) + (u2 - u1), idle = i2 - i1;
                m.Cpu = total > 0 ? Math.Round(100.0 * (total - idle) / total, 1) : 0;
            }
            catch { }
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Name FROM Win32_Service WHERE State='Running'"))
                    m.Services = q.Get().Count;
            }
            catch { }
            try
            {
                m.Startup = StartupScan.CountEnabled();
            }
            catch { }
            try { m.BootSec = LastBoot(); } catch { }
            return m;
        }

        static double LastBoot()
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational",
                System.Diagnostics.Eventing.Reader.PathType.LogName, "*[System[EventID=100]]") { ReverseDirection = true };
            using (var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q))
            using (var e = r.ReadEvent())
            {
                if (e == null) return 0;
                var mm = Regex.Match(e.ToXml(), @"<Data Name='BootTime'>(\d+)</Data>");
                return mm.Success ? Math.Round(long.Parse(mm.Groups[1].Value) / 1000.0, 1) : 0;
            }
        }

        public override string ToString()
        {
            return string.Join("|", new[]
            {
                When.ToString("s", CultureInfo.InvariantCulture), Processes.ToString(CultureInfo.InvariantCulture), MemoryMb.ToString(CultureInfo.InvariantCulture),
                Cpu.ToString(CultureInfo.InvariantCulture), Services.ToString(CultureInfo.InvariantCulture), Startup.ToString(CultureInfo.InvariantCulture),
                BootSec.ToString(CultureInfo.InvariantCulture),
            });
        }

        public static Measure Parse(string s)
        {
            var p = (s ?? "").Split('|');
            if (p.Length != 7) return null;
            var m = new Measure();
            var ci = CultureInfo.InvariantCulture;
            DateTime.TryParse(p[0], ci, DateTimeStyles.None, out m.When);
            int.TryParse(p[1], NumberStyles.Integer, ci, out m.Processes);
            long.TryParse(p[2], NumberStyles.Integer, ci, out m.MemoryMb);
            double.TryParse(p[3], NumberStyles.Float, ci, out m.Cpu);
            int.TryParse(p[4], NumberStyles.Integer, ci, out m.Services);
            int.TryParse(p[5], NumberStyles.Integer, ci, out m.Startup);
            double.TryParse(p[6], NumberStyles.Float, ci, out m.BootSec);
            return m;
        }

        /// <summary>The comparison as lines of text: "Processes: 182 → 141 (−41)".</summary>
        public static string Compare(Measure before, Measure now)
        {
            var lines = new List<string>();
            Func<double, double, bool, string> diff = (a, b, lowerBetter) =>
            {
                double d = b - a;
                if (Math.Abs(d) < 0.05) return "";
                string sign = d < 0 ? "−" : "+";
                return "  (" + sign + Math.Abs(Math.Round(d, 1)).ToString(CultureInfo.CurrentCulture) + ((d < 0) == lowerBetter ? " ✓" : "") + ")";
            };
            Action<string, double, double, string> line = (name, a, b, unit) =>
                lines.Add(name + ": " + (before != null ? a.ToString("0.#", CultureInfo.CurrentCulture) + unit + " → " : "") + b.ToString("0.#", CultureInfo.CurrentCulture) + unit +
                          (before != null ? diff(a, b, true) : ""));
            var b0 = before ?? now;
            line(L.T("Процессов", "Processes"), b0.Processes, now.Processes, "");
            line(L.T("Занято памяти", "Memory in use"), b0.MemoryMb, now.MemoryMb, L.T(" МБ", " MB"));
            line(L.T("Загрузка процессора в простое", "Processor load at rest"), b0.Cpu, now.Cpu, " %");
            line(L.T("Работающих служб", "Running services"), b0.Services, now.Services, "");
            line(L.T("Программ в автозагрузке", "Startup programs"), b0.Startup, now.Startup, "");
            if (now.BootSec > 0 || b0.BootSec > 0)
                line(L.T("Последняя загрузка Windows", "Last Windows boot"), b0.BootSec, now.BootSec, L.T(" с", " s"));
            return string.Join("\n", lines);
        }
    }
}
