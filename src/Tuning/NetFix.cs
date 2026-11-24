using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace DriveUp
{
    /// <summary>
    /// One advanced property of a network adapter's driver (or a few that mean the same, e.g. the makers' own
    /// "green Ethernet" switches next to the standard *EEE), kept as the driver reads it: REG_SZ under the
    /// adapter's key of the network class. The adapter is restarted for a change to apply (<see cref="NicRestart"/>).
    /// Value "1" = as the fix wants it.
    /// </summary>
    class NicProperty : Tweak
    {
        public readonly string Guid, Kind;
        readonly string key;
        readonly string[] keywords;
        readonly string target;

        public NicProperty(string guid, string kind)
        {
            Guid = guid;
            Kind = kind;
            Id = "net.nic." + kind + "." + guid;
            Category = TuneCat.Network;
            key = NetCheck.ClassKeyOf(guid);
            switch (kind)
            {
                case "eee":
                    // Only the switches this driver really has (listed in its Ndi\params, or already set).
                    keywords = key == null ? new string[0] : NetCheck.SavingKeywords.Where(k => Has(k) && OffValue(k) != null).ToArray();
                    target = "0";
                    Glyph = "\uE83E";
                    Name = () => L.T("Энергосбережение Ethernet (EEE)", "Ethernet power saving (EEE)");
                    break;
                case "duplex":
                    keywords = new[] { "*SpeedDuplex" };
                    target = "0";
                    Glyph = "\uE839";
                    Name = () => L.T("Скорость и дуплекс сетевой карты", "Network card speed and duplex");
                    break;
                case "rxbuf":
                    // As many receive buffers as the driver allows (its INF gives the maximum).
                    keywords = new[] { "*ReceiveBuffers" };
                    target = key == null ? "" : Reg.Get(true, key + @"\Ndi\params\*ReceiveBuffers", "max") as string ?? "";
                    if (target.Length == 0) keywords = new string[0];
                    Glyph = "\uE839";
                    Name = () => L.T("Буферы приёма сетевой карты", "Network card receive buffers");
                    break;
                case "jumbo":
                    keywords = new[] { "*JumboPacket" };
                    target = "1514";
                    Glyph = "\uE9D2";
                    Name = () => L.T("Большие кадры (Jumbo)", "Jumbo frames");
                    break;
                case "suspend":
                    keywords = new[] { "*SelectiveSuspend" };
                    target = "0";
                    Glyph = "\uE83E";
                    Name = () => L.T("Усыпление USB-адаптера", "USB adapter suspend");
                    break;
                default:
                    keywords = new[] { "*RSS" };
                    target = "1";
                    Glyph = "\uE950";
                    Name = () => L.T("Распределение сети по ядрам (RSS) у карты", "Spreading network work over cores (RSS) on the card");
                    break;
            }
        }

        bool Has(string kw)
        {
            return Reg.Get(true, key, kw) != null || Reg.KeyExists(true, key + @"\Ndi\params\" + kw);
        }

        /// <summary>The "off" value of a maker's switch: "0" where the driver lists it as a choice.</summary>
        string OffValue(string kw)
        {
            string e = key + @"\Ndi\params\" + kw + @"\enum";
            if (!Reg.KeyExists(true, e)) return Reg.Get(true, key, kw) != null ? "0" : null;
            return Reg.ValueNames(true, e).Contains("0") ? "0" : null;
        }

        /// <summary>A keyword's value as the driver uses it: set, or the default its INF gives.</summary>
        string Effective(string kw, string raw)
        {
            if (raw.StartsWith("s:")) return raw.Substring(2);
            if (raw.StartsWith("d:")) return raw.Substring(2);
            return Reg.Get(true, key + @"\Ndi\params\" + kw, "default") as string ?? "";
        }

        public override bool Available(Machine m) { return key != null && keywords.Length > 0; }

        public override string Capture()
        {
            if (key == null) throw new InvalidOperationException(L.T("Сетевая карта не найдена", "The network card was not found"));
            return string.Join(";", keywords.Select(k => k + "=" + Reg.Capture(true, key, k)));
        }

        static Dictionary<string, string> Parse(string raw)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in (raw ?? "").Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) d[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            return d;
        }

        public override void Restore(string raw)
        {
            foreach (var kv in Parse(raw)) Reg.Restore(true, key, kv.Key, kv.Value);
            NicRestart.Mark(Guid);
        }

        public override string ValueOf(string raw)
        {
            var d = Parse(raw);
            if (d.Count == 0) return "0";
            return d.All(kv => Effective(kv.Key, kv.Value) == target) ? "1" : "0";
        }

        public override void Set(string value)
        {
            if (value != "1") return;
            foreach (var k in keywords) Reg.SetString(true, key, k, target);
            NicRestart.Mark(Guid);
        }

        public override string Describe(string value)
        {
            bool on = value == "1";
            switch (Kind)
            {
                case "eee": return on ? L.T("выключено", "off") : L.T("включено", "on");
                case "duplex": return on ? L.T("авто", "auto") : L.T("задано вручную", "set by hand");
                case "rxbuf": return on ? L.T("максимум (", "maximum (") + target + ")" : L.T("меньше максимума", "below the maximum");
                case "jumbo": return on ? L.T("выключены (1514)", "off (1514)") : L.T("включены", "on");
                case "suspend": return on ? L.T("выключено", "off") : L.T("включено", "on");
                default: return on ? L.T("включено", "on") : L.T("выключено", "off");
            }
        }
    }

    /// <summary>Adapters whose driver settings changed: restarted once, after a batch of changes.</summary>
    static class NicRestart
    {
        static readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static bool Pending { get { lock (pending) return pending.Count > 0; } }

        public static void Mark(string guid)
        {
            if (Reg.TestRoot != null) return;
            lock (pending) pending.Add(guid);
        }

        /// <summary>Restarts the marked adapters (the connection drops for a few seconds). Returns the ones that failed.</summary>
        public static List<string> Flush()
        {
            List<string> list;
            lock (pending) { list = pending.ToList(); pending.Clear(); }
            var failed = new List<string>();
            foreach (var g in list)
                if (!Restart(g)) failed.Add(g);
            return failed;
        }

        static bool Restart(string guid)
        {
            // MSFT_NetAdapter.DeviceID is the interface GUID; Disable + Enable is what Restart-NetAdapter does.
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT * FROM MSFT_NetAdapter WHERE DeviceID='" + guid.Replace("'", "") + "'"))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                        {
                            try { o.InvokeMethod("Disable", null); }
                            finally
                            {
                                // Enabled again whatever happened: an adapter left off would cut the computer off.
                                bool on = false;
                                for (int i = 0; i < 3 && !on; i++)
                                {
                                    try { o.InvokeMethod("Enable", null); on = true; }
                                    catch { Thread.Sleep(1000); }
                                }
                            }
                            return true;
                        }
            }
            catch { }
            return false;
        }
    }

    /// <summary>
    /// Whether Windows may switch the network adapter off to save power (Device Manager → Power Management):
    /// PnPCapabilities in the driver's key, 24 = not allowed. "1" = not allowed (as the fix wants it).
    /// </summary>
    class NicPower : Tweak
    {
        public readonly string Guid;
        readonly string key;

        public NicPower(string guid)
        {
            Guid = guid;
            Id = "net.pnp." + guid;
            Category = TuneCat.Network;
            Glyph = "\uE83E";
            Name = () => L.T("Отключение сетевой карты ради экономии", "Network card turned off to save power");
            key = NetCheck.ClassKeyOf(guid);
        }

        public override bool Available(Machine m) { return key != null; }
        public override string Capture()
        {
            if (key == null) throw new InvalidOperationException(L.T("Сетевая карта не найдена", "The network card was not found"));
            return Reg.Capture(true, key, "PnPCapabilities");
        }
        public override void Restore(string raw) { Reg.Restore(true, key, "PnPCapabilities", raw); NicRestart.Mark(Guid); }
        public override string ValueOf(string raw)
        {
            int? v = Reg.RawDword(raw);
            return v.HasValue && (v.Value & 0x18) == 0x18 ? "1" : "0";
        }
        public override void Set(string value)
        {
            if (value != "1") return;
            int now = Reg.Dword(true, key, "PnPCapabilities") ?? 0;
            Reg.SetDword(true, key, "PnPCapabilities", now | 0x18);
            NicRestart.Mark(Guid);
        }
        public override string Describe(string value) { return value == "1" ? L.T("запрещено", "not allowed") : L.T("разрешено", "allowed"); }
    }

    /// <summary>The TCP congestion control of the Internet template (netsh int tcp supplemental): CUBIC as Windows ships it.</summary>
    class Congestion : Tweak
    {
        static readonly string[] Known = { "newreno", "ctcp", "dctcp", "ledbat", "cubic", "bbr2" };

        public Congestion()
        {
            Id = "net.congestion";
            Category = TuneCat.Network;
            Glyph = "\uE713";
            Name = () => L.T("Управление перегрузкой TCP", "TCP congestion control");
        }

        public override string Capture()
        {
            using (var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT CongestionProvider FROM MSFT_NetTCPSetting WHERE SettingName='Internet'"))
                foreach (ManagementObject o in s.Get())
                    using (o) return NetCheck.CongestionName(Convert.ToInt32(o["CongestionProvider"], CultureInfo.InvariantCulture));
            throw new InvalidOperationException(L.T("Windows не сообщила настройку TCP", "Windows did not report the TCP setting"));
        }

        public override void Restore(string raw) { Set(raw); }
        public override string ValueOf(string raw) { return raw == "default" ? "cubic" : raw; }
        public override void Set(string value)
        {
            if (Array.IndexOf(Known, value) < 0) return;
            NetFix.Netsh("int tcp set supplemental template=internet congestionprovider=" + value);
        }
        public override string Describe(string value) { return value == "cubic" ? L.T("CUBIC, как в Windows", "CUBIC, Windows default") : value.ToUpperInvariant(); }
        public override bool Available(Machine m)
        {
            try { Capture(); return true; } catch { return false; }
        }
    }

    /// <summary>Receive window auto-tuning (netsh int tcp global): "normal" as Windows ships it.</summary>
    class TcpTuning : Tweak
    {
        public TcpTuning()
        {
            Id = "net.autotuning";
            Category = TuneCat.Network;
            Glyph = "\uE713";
            Name = () => L.T("Автонастройка окна TCP", "TCP receive window auto-tuning");
        }

        public override string Capture()
        {
            using (var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT AutoTuningLevelLocal FROM MSFT_NetTCPSetting WHERE SettingName='Internet'"))
                foreach (ManagementObject o in s.Get())
                    using (o) return NetFix.TuningName(Convert.ToInt32(o["AutoTuningLevelLocal"], CultureInfo.InvariantCulture));
            throw new InvalidOperationException(L.T("Windows не сообщила настройку TCP", "Windows did not report the TCP setting"));
        }

        public override void Restore(string raw) { Set(raw); }
        public override string ValueOf(string raw) { return raw; }

        public override void Set(string value)
        {
            if (!Regex.IsMatch(value, "^(disabled|highlyrestricted|restricted|normal|experimental)$")) return;
            NetFix.Netsh("int tcp set global autotuninglevel=" + value);
        }

        public override string Describe(string value)
        {
            switch (value)
            {
                case "normal": return L.T("как в Windows (normal)", "Windows default (normal)");
                case "disabled": return L.T("выключена", "off");
                default: return L.T("урезана (", "restricted (") + value + ")";
            }
        }

        public override bool Available(Machine m)
        {
            try { Capture(); return true; } catch { return false; }
        }
    }

    /// <summary>Receive Side Scaling for the whole system (netsh int tcp global rss).</summary>
    class RssGlobal : Tweak
    {
        public RssGlobal()
        {
            Id = "net.rss";
            Category = TuneCat.Network;
            Glyph = "\uE950";
            Name = () => L.T("Распределение сети по ядрам (RSS)", "Spreading network work over cores (RSS)");
        }

        public override string Capture()
        {
            using (var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT ReceiveSideScaling FROM MSFT_NetOffloadGlobalSetting"))
                foreach (ManagementObject o in s.Get())
                    using (o) return Convert.ToInt32(o["ReceiveSideScaling"], CultureInfo.InvariantCulture) == 0 ? "0" : "1";
            throw new InvalidOperationException(L.T("Windows не сообщила настройку RSS", "Windows did not report the RSS setting"));
        }

        public override void Restore(string raw) { Set(raw); }
        public override string ValueOf(string raw) { return raw; }
        public override void Set(string value) { NetFix.Netsh("int tcp set global rss=" + (value == "1" ? "enabled" : "disabled")); }
        public override string Describe(string value) { return value == "1" ? L.T("включено", "on") : L.T("выключено", "off"); }

        public override bool Available(Machine m)
        {
            try { Capture(); return true; } catch { return false; }
        }
    }

    /// <summary>
    /// The IPv4 DNS servers of one adapter: from the router / provider (DHCP, value "auto") or set by hand
    /// ("1.1.1.1,1.0.0.1"). Changed through Windows (WMI), as the adapter's IPv4 properties would.
    /// </summary>
    class DnsServers : Tweak
    {
        public readonly string Guid;
        const string Ifaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";

        public DnsServers(string guid)
        {
            Guid = guid;
            Id = "net.dns." + guid;
            Category = TuneCat.Network;
            Glyph = "\uE8B9";
            Name = () => L.T("DNS-серверы", "DNS servers");
        }

        public override bool Available(Machine m) { return Reg.KeyExists(true, Ifaces + Guid); }

        public override string Capture() { return Reg.Capture(true, Ifaces + Guid, "NameServer"); }

        public override string ValueOf(string raw)
        {
            string v = raw != null && raw.StartsWith("s:") ? raw.Substring(2).Trim() : "";
            return v.Length == 0 ? "auto" : string.Join(",", v.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public override void Restore(string raw) { Set(ValueOf(raw)); }

        [DllImport("dnsapi.dll")]
        static extern bool DnsFlushResolverCache();

        public override void Set(string value)
        {
            if (Reg.TestRoot != null)
            {
                Reg.SetString(true, Ifaces + Guid, "NameServer", value == "auto" ? "" : value);
                return;
            }
            string[] list = value == "auto" ? null : value.Split(',').Where(s => s.Length > 0).ToArray();
            if (list != null && list.Any(s => { System.Net.IPAddress ip; return !System.Net.IPAddress.TryParse(s, out ip); })) throw new ArgumentException(value);
            uint code = 1;
            using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID='" + Guid.Replace("'", "") + "'"))
                foreach (ManagementObject o in s.Get())
                    using (o)
                    {
                        var inp = o.GetMethodParameters("SetDNSServerSearchOrder");
                        inp["DNSServerSearchOrder"] = list;
                        using (var r = o.InvokeMethod("SetDNSServerSearchOrder", inp, null))
                            code = Convert.ToUInt32(r["ReturnValue"], CultureInfo.InvariantCulture);
                    }
            // 0 = done, 1 = done after a restart.
            if (code > 1) throw new InvalidOperationException(L.T("Windows не приняла DNS-серверы (код ", "Windows did not accept the DNS servers (code ") + code + ")");
            try { DnsFlushResolverCache(); } catch { }
        }

        public override string Describe(string value)
        {
            if (value == "auto") return L.T("от роутера / провайдера", "from the router / provider");
            var names = value.Split(',').Select(NetFix.ResolverName).Distinct().ToList();
            return names.Count == 1 && names[0].Length > 0 ? names[0] + " (" + value.Replace(",", ", ") + ")" : value.Replace(",", ", ");
        }
    }

    /// <summary>A machine DWORD with its own texts: "-" = no value (Windows' default), else the number.</summary>
    class RegValue : Tweak
    {
        readonly string path, name;
        readonly Func<string, string> text;
        public Func<Machine, bool> When = m => true;

        public RegValue(string path, string name, Func<string, string> text)
        {
            this.path = path;
            this.name = name;
            this.text = text;
        }

        public override string Capture() { return Reg.Capture(true, path, name); }
        public override void Restore(string raw) { Reg.Restore(true, path, name, raw); }

        public override string ValueOf(string raw)
        {
            int? v = Reg.RawDword(raw);
            return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "-";
        }

        public override void Set(string value)
        {
            int n;
            if (value == "-") Reg.Delete(true, path, name);
            else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) Reg.SetDword(true, path, name, n);
        }

        public override string Describe(string value) { return text(value); }
        public override bool Available(Machine m) { return When(m); }
    }

    /// <summary>
    /// The fixes "Check network" offers, each chosen from what the check found (never on its own, never by a
    /// tuning mode), and each can be returned under "Changed" like any other setting.
    /// </summary>
    static class NetFix
    {
        public static readonly Guid SubWireless = new Guid("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
        public static readonly Guid WirelessSaving = new Guid("12bbebe6-58d6-4636-95bb-3217ef867c1a");

        /// <summary>What changes made by the network check are stamped with (not one of the tuning modes).</summary>
        public static readonly TuneMode Mode = new TuneMode
        {
            Id = "net", Glyph = "\uE701",
            Name = () => L.T("Проверка сети", "Network check"),
            Desc = () => "",
            Steps = m => new List<TuneStep>(),
        };

        static PowerSetting wifiPower;
        static TcpTuning tuning;
        static RssGlobal rss;
        static Congestion congestion;
        static RegValue ipv6, doLimit;
        static readonly Dictionary<string, RegValue> tcpValues = new Dictionary<string, RegValue>();

        /// <summary>The fixed ones go into the tuner's catalog (so "Changed" finds them); per-adapter ones are made by id.</summary>
        public static void Register(List<Tweak> catalog)
        {
            wifiPower = new PowerSetting(SubWireless, WirelessSaving)
            {
                Text = v => v == 0 ? L.T("максимальная производительность", "maximum performance") : v == 3 ? L.T("максимальное энергосбережение", "maximum power saving")
                          : v == 1 ? L.T("слабое энергосбережение", "low power saving") : L.T("среднее энергосбережение", "medium power saving"),
                Id = "net.wifipower", Category = TuneCat.Network, Glyph = "\uE701",
                Name = () => L.T("Энергосбережение Wi-Fi", "Wi-Fi power saving"),
            };
            tuning = new TcpTuning();
            rss = new RssGlobal();
            ipv6 = new RegValue(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", v =>
                v == "-" || v == "0" ? L.T("как в Windows", "Windows default")
                : v == "32" ? L.T("предпочитать IPv4", "prefer IPv4")
                : (int.Parse(v, CultureInfo.InvariantCulture) & 0xFF) == 0xFF ? L.T("IPv6 отключён", "IPv6 disabled")
                : L.T("изменено (", "changed (") + v + ")")
            { Id = "net.ipv6", Category = TuneCat.Network, Glyph = "\uE774", Name = () => L.T("Приоритет IPv6", "IPv6 preference"), NeedsReboot = true };
            doLimit = new RegValue(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DOPercentageMaxBackgroundBandwidth", v =>
                v == "-" || v == "0" ? L.T("без ограничения", "no limit") : L.T("до ", "up to ") + v + L.T(" % канала", " % of the line"))
            { Id = "net.dolimit", Category = TuneCat.Network, Glyph = "\uE896", Name = () => L.T("Фоновые загрузки обновлений Windows", "Windows Update background downloads") };
            congestion = new Congestion();
            catalog.AddRange(new Tweak[] { wifiPower, tuning, rss, congestion, ipv6, doLimit });
            foreach (var name in NetCheck.HarmfulTcp)
            {
                string n = name;
                var t = new RegValue(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", n, v => v == "-" ? L.T("нет (как в Windows)", "none (Windows default)") : n + " = " + v)
                { Id = "net.tcp." + n, Category = TuneCat.Network, Glyph = "\uE713", Name = () => L.T("Параметр TCP ", "TCP parameter ") + n, NeedsReboot = true };
                tcpValues[n] = t;
                catalog.Add(t);
            }
        }

        /// <summary>A per-adapter or per-service fix saved earlier, made again from its id.</summary>
        public static Tweak FromId(string id)
        {
            if (id.StartsWith("net.nic.", StringComparison.Ordinal))
            {
                var parts = id.Split('.');
                if (parts.Length < 4) return null;
                var t = new NicProperty(id.Substring(("net.nic." + parts[2] + ".").Length), parts[2]);
                return t.Available(null) ? t : null;
            }
            if (id.StartsWith("net.dns.", StringComparison.Ordinal)) return new DnsServers(id.Substring(8));
            if (id.StartsWith("net.pnp.", StringComparison.Ordinal))
            {
                var t = new NicPower(id.Substring(8));
                return t.Available(null) ? t : null;
            }
            if (id.StartsWith("net.svc.", StringComparison.Ordinal))
            {
                string name = id.Substring(8);
                string title = Reg.Get(true, @"SYSTEM\CurrentControlSet\Services\" + name, "DisplayName") as string ?? name;
                if (title.StartsWith("@")) title = name;
                return new ServiceTweak(name) { Id = id, Category = TuneCat.Network, Glyph = "\uE7BA", Name = () => title };
            }
            return null;
        }

        public static string TuningName(int v)
        {
            switch (v)
            {
                case 0: return "disabled";
                case 1: return "highlyrestricted";
                case 2: return "restricted";
                case 3: return "normal";
                case 4: return "experimental";
                default: return "normal";
            }
        }

        public static string ResolverName(string ip)
        {
            switch (ip.Trim())
            {
                case "1.1.1.1": case "1.0.0.1": return "Cloudflare";
                case "8.8.8.8": case "8.8.4.4": return "Google";
                case "9.9.9.9": case "149.112.112.112": return "Quad9";
                default: return "";
            }
        }

        static string Pair(string first)
        {
            switch (first)
            {
                case "1.1.1.1": return "1.1.1.1,1.0.0.1";
                case "8.8.8.8": return "8.8.8.8,8.8.4.4";
                case "9.9.9.9": return "9.9.9.9,149.112.112.112";
                default: return first;
            }
        }

        public static void Netsh(string args)
        {
            var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "netsh.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Environment.SystemDirectory,
            };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } throw new TimeoutException("netsh"); }
                if (p.ExitCode != 0)
                {
                    string msg = (o.Result + "\n" + e.Result).Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0);
                    throw new InvalidOperationException(msg ?? ("netsh: " + p.ExitCode));
                }
            }
        }

        static readonly Regex Booster = new Regex(@"SmartByte|Killer (Network|Analytics|Dynamic Bandwidth|Smart AP)|Rivet Bandwidth|cFosSpeed|GameFirst", RegexOptions.IgnoreCase);

        /// <summary>Services of "network booster" programs that are not turned off: (service name, display name).</summary>
        public static List<Tuple<string, string>> FindOptimizers()
        {
            var list = new List<Tuple<string, string>>();
            const string Root = @"SYSTEM\CurrentControlSet\Services";
            foreach (var name in Reg.SubKeyNames(true, Root))
            {
                try
                {
                    string title = Reg.Get(true, Root + "\\" + name, "DisplayName") as string ?? "";
                    if (!Booster.IsMatch(title) && !Booster.IsMatch(name)) continue;
                    // Only services (type 0x10 / 0x20), not drivers, and only those not already off.
                    int type = Reg.Dword(true, Root + "\\" + name, "Type") ?? 0;
                    if ((type & 0x30) == 0 || Reg.Dword(true, Root + "\\" + name, "Start") == 4) continue;
                    list.Add(Tuple.Create(name, title.StartsWith("@") ? name : title));
                }
                catch { }
            }
            return list;
        }

        static TuneStep Step(Tweak t, string value, Func<string> reason, bool optional = false, Func<string> warn = null)
        {
            return new TuneStep { Tweak = t, Value = value, Reason = reason, Optional = optional, Warn = warn };
        }

        /// <summary>What to fix, from what the check found. Each one only where the check saw the reason for it.</summary>
        public static List<TuneStep> Steps(NetCheck c)
        {
            var s = new List<TuneStep>();
            var p = c.Physical;
            var m = c.Pc;
            if (p != null && p.ClassKey != null && p.Kind == LinkKind.Ethernet)
            {
                var eee = new NicProperty(p.Guid, "eee");
                if (eee.Available(m) && NetCheck.SavingKeywords.Any(k => c.NicValues.ContainsKey(k) && c.NicValues[k] != "0") && (c.WiredTrouble || p.IsIntelI225))
                    s.Add(Step(eee, "1", () => p.IsIntelI225
                        ? L.T("Intel советует выключить его на I225/I226: из-за него обрывы и подвисания связи", "Intel advises turning it off on I225/I226: it causes drops and stutter")
                        : L.T("Связь по кабелю теряет пакеты или скачет — карта перестанет засыпать между пакетами", "The wired link loses packets or jumps — the card stops dozing between packets"),
                        false, null));
                string sd;
                if (c.NicValues.TryGetValue("*SpeedDuplex", out sd) && sd != "0")
                    s.Add(Step(new NicProperty(p.Guid, "duplex"), "1", () => L.T("Карта и роутер сами договорятся о лучшей скорости и полном дуплексе", "The card and the router agree on the best speed and full duplex themselves")));
            }
            if (p != null && p.ClassKey != null)
            {
                if (c.DiscardsUnderLoad && !c.RxAtMax)
                {
                    var rx = new NicProperty(p.Guid, "rxbuf");
                    if (rx.Available(m))
                        s.Add(Step(rx, "1", () => L.T("Под нагрузкой карта отбрасывала пакеты: с большим запасом буферов она успеет их принять", "Under load the card dropped packets: with more buffers it keeps up")));
                }
                if (c.JumboBroken)
                    s.Add(Step(new NicProperty(p.Guid, "jumbo"), "1", () => L.T("Роутер не принимает большие кадры: стандартный размер вернёт крупные пакеты в домашней сети", "The router does not take jumbo frames: the standard size brings large packets on the home network back")));
                string ss;
                if (c.UsbAdapter && c.NicValues.TryGetValue("*SelectiveSuspend", out ss) && ss != "0")
                {
                    var su = new NicProperty(p.Guid, "suspend");
                    if (su.Available(m))
                        s.Add(Step(su, "1", () => L.T("USB-адаптер не будет засыпать между передачами — меньше обрывов", "The USB adapter no longer sleeps between transfers — fewer drops"), !c.DropTrouble));
                }
                string pnp;
                int caps;
                bool mayOff = !(c.NicValues.TryGetValue("PnPCapabilities", out pnp) && int.TryParse(pnp, out caps) && (caps & 0x18) == 0x18);
                bool trouble = c.DropTrouble || (p.Kind == LinkKind.WiFi ? c.WifiTrouble : c.WiredTrouble);
                if (mayOff && trouble)
                    s.Add(Step(new NicPower(p.Guid), "1", () => L.T("Связь пропадала: Windows больше не будет отключать карту ради экономии энергии", "The link dropped: Windows will no longer turn the card off to save power")));
            }

            string nicRss;
            if (p != null && p.ClassKey != null && c.NicValues.TryGetValue("*RSS", out nicRss) && nicRss == "0" && m.Cores > 1)
                s.Add(Step(new NicProperty(p.Guid, "rss"), "1", () => L.T("Приём сети снова делят несколько ядер, как задумано в Windows", "Several cores share the network work again, as Windows intends")));

            if (p != null && p.Kind == LinkKind.WiFi && wifiPower.Available(m))
            {
                bool trouble = c.WifiTrouble;
                s.Add(Step(wifiPower, "0,*", () => trouble
                    ? L.T("Wi-Fi скачет по задержке: от сети адаптер больше не дремлет между пакетами (от батареи — как было)", "The Wi-Fi delay jumps: on mains the adapter no longer dozes between packets (battery as it was)")
                    : L.T("От сети адаптер не дремлет между пакетами — ровнее задержка (от батареи — как было)", "On mains the adapter does not doze between packets — a steadier delay (battery as it was)"),
                    !trouble));
            }

            if (c.AutoTuning != "" && c.AutoTuning != "normal")
                s.Add(Step(tuning, "normal", () => L.T("Без неё скорость на быстрых и дальних соединениях падает в разы — вернётся как в Windows", "Without it fast and distant connections lose most of their speed — back to the Windows default")));
            if (c.RssOff)
                s.Add(Step(rss, "1", () => L.T("Приём сети снова делят несколько ядер, как задумано в Windows", "Several cores share the network work again, as Windows intends")));
            if (c.Congestion != "" && c.Congestion != "cubic" && c.Congestion != "default" && congestion.Available(m))
                s.Add(Step(congestion, "cubic", () => c.Congestion == "bbr2"
                    ? L.T("BBR2 в Windows ещё экспериментальный: с ним зависают соединения (WSL, Docker, лаунчеры) — вернётся CUBIC", "BBR2 is still experimental in Windows: connections hang with it (WSL, Docker, launchers) — back to CUBIC")
                    : L.T("Вернётся CUBIC, как в Windows 10/11: старые алгоритмы на современных линиях медленнее", "Back to CUBIC, as in Windows 10/11: old algorithms are slower on modern lines")));
            foreach (var t in c.TcpLeftovers)
            {
                RegValue rv;
                if (!tcpValues.TryGetValue(t.Item1, out rv)) continue;
                string n = t.Item1;
                s.Add(Step(rv, "-", () => n == "EnablePMTUDiscovery" ? L.T("Windows снова будет узнавать размер пакета на пути, а не слать по 576 байт", "Windows finds the path's packet size again instead of sending 576 bytes")
                    : n == "DisableTaskOffload" ? L.T("Карта снова будет сама считать контрольные суммы и нарезать пакеты — меньше нагрузки на процессор", "The card computes checksums and splits packets itself again — less load on the processor")
                    : L.T("Пакеты снова будут доживать до дальних серверов", "Packets live long enough to reach distant servers again")));
            }
            foreach (var d in c.DeadServices)
            {
                var svc = new ServiceTweak(d.Item1) { Id = "net.svc." + d.Item1, Category = TuneCat.Network, Glyph = "\uE912" };
                string title = Reg.Get(true, @"SYSTEM\CurrentControlSet\Services\" + d.Item1, "DisplayName") as string ?? d.Item1;
                if (title.StartsWith("@")) title = d.Item1;
                svc.Name = () => title;
                s.Add(Step(svc, d.Item2, () => L.T("Служба нужна для работы сети — вернётся обычный тип запуска", "The network needs this service — its usual start type comes back")));
            }

            foreach (var o in c.Optimizers)
            {
                var svc = new ServiceTweak(o.Item1) { Id = "net.svc." + o.Item1, Category = TuneCat.Network, Glyph = "\uE7BA" };
                string title = o.Item2;
                svc.Name = () => title;
                bool smartByte = title.IndexOf("SmartByte", StringComparison.OrdinalIgnoreCase) >= 0;
                s.Add(Step(svc, "disabled", () => L.T("Перестанет перехватывать и «приоритизировать» трафик — скорость и задержка без посредника", "Stops intercepting and \"prioritising\" traffic — speed and delay without a middleman"),
                    !smartByte, smartByte ? null : (Func<string>)(() => L.T("его «приоритеты» в фирменной программе перестанут действовать", "its \"priorities\" in the maker's app stop working"))));
            }

            // DNS: only where the network's own fails or is clearly slower, never on a managed PC or through a VPN.
            var best = c.BestPublic;
            var main = c.Main ?? p;
            if (c.SlowDns && best != null && main != null && main == p && !m.Managed && !c.Vpn && !c.InternalDns)
            {
                string list = Pair(best.Server.ToString());
                s.Add(Step(new DnsServers(main.Guid), list,
                    () => best.Name + L.T(" отвечает за ", " answers in ") + Math.Round(best.Median) + L.T(" мс против ", " ms against ") +
                          (c.SystemDns.Works ? Math.Round(c.SystemDns.Median) + L.T(" мс", " ms") : L.T("отказов", "failures")) + L.T(" у вашего DNS", " for your DNS"),
                    true, () => L.T("внутренние адреса провайдера или домашней сети могут не открываться", "the provider's or home network's own names may not open")));
            }

            if (c.V6Broken && !(c.Ipv6Components.HasValue && (c.Ipv6Components.Value & 0xFF) == 0xFF))
                s.Add(Step(ipv6, "32", () => L.T("IPv6 не работает: программы перестанут ждать его и сразу пойдут через IPv4 (так советует Microsoft вместо отключения)",
                                                 "IPv6 does not work: programs stop waiting for it and go over IPv4 at once (Microsoft's advice instead of disabling it)")));
            else if (c.Ipv6Components.HasValue && (c.Ipv6Components.Value & 0xFF) == 0xFF)
                s.Add(Step(ipv6, "-", () => L.T("Microsoft не рекомендует отключать IPv6 — вернётся как в Windows", "Microsoft advises against disabling IPv6 — back to the Windows default"), true));

            // Bufferbloat: Windows' own background downloads are what this PC can keep off the line.
            int limit;
            string nowLimit = "-";
            try { nowLimit = doLimit.Current(); } catch { }
            bool looser = !int.TryParse(nowLimit, out limit) || limit == 0 || limit > 30;
            if (c.Bloated && looser && !m.Managed && !HomeEdition)
                s.Add(Step(doLimit, "30", () => L.T("Пока качаются обновления Windows, задержка не взлетит: в фоне они займут не больше 30 % канала", "Windows updates no longer send the delay up: in the background they take at most 30 % of the line")));
            return s;
        }

        /// <summary>Windows Home: Delivery Optimization policies do not apply there (Settings has its own limit).</summary>
        static bool HomeEdition
        {
            get
            {
                var e = Reg.Get(true, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID") as string ?? "";
                return e.StartsWith("Core", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
