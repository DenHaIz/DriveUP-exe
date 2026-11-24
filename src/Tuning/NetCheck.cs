using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DriveUp
{
    enum NetLevel { Pending, Info, Good, Warn, Bad }

    /// <summary>One line of the network report: what was measured, how it looks and what it means.</summary>
    class NetFinding
    {
        public string Id = "";
        public TuneCat Group;
        public string Glyph = "";
        public NetLevel Level = NetLevel.Pending;
        public Func<string> Title = () => "", Value = () => "", Detail = () => "";
        /// <summary>Round trips (ms, NaN = lost) for the little chart, added while measuring. Read under lock.</summary>
        public readonly List<double> Samples = new List<double>();
        public bool Chart;
        /// <summary>When the newest sample came (Anim clock, ms): the chart's last segment grows in from there.</summary>
        public double SampleAt;
        /// <summary>The chart's scale as drawn (ms at the top): follows the samples smoothly.</summary>
        public float Scale;
        /// <summary>A button on the row (the page knows what it does by the id, e.g. open driver updates).</summary>
        public Func<string> ActionText;
        /// <summary>How often samples come (ms): the chart scrolls by that much between two.</summary>
        public int Interval = 200;
        /// <summary>When <see cref="Scale"/> was last moved (Anim clock).</summary>
        public double ScaleAt;

        public void Add(double ms)
        {
            lock (Samples)
            {
                Samples.Add(ms);
                SampleAt = Anim.Now;
            }
        }

        public bool Problem { get { return Level == NetLevel.Warn || Level == NetLevel.Bad; } }
    }

    /// <summary>Round trips to one host: ms per sample, NaN for a lost one, with when each was taken.</summary>
    class Rtt
    {
        public readonly List<double> All = new List<double>();
        public readonly List<double> At = new List<double>();

        public void Add(double ms, double atSec)
        {
            lock (All) { All.Add(ms); At.Add(atSec); }
        }

        List<double> Ok { get { lock (All) return All.Where(x => !double.IsNaN(x)).ToList(); } }
        public int Count { get { lock (All) return All.Count; } }
        public int Received { get { return Ok.Count; } }
        public double Loss { get { lock (All) return All.Count == 0 ? 0 : All.Count(double.IsNaN) * 100.0 / All.Count; } }
        public double Median { get { return Pct(50); } }
        public double Max { get { var s = Ok; return s.Count == 0 ? double.NaN : s.Max(); } }

        public double Pct(double p)
        {
            var s = Ok;
            if (s.Count == 0) return double.NaN;
            s.Sort();
            int i = (int)Math.Ceiling(p / 100 * s.Count) - 1;
            return s[Math.Max(0, Math.Min(s.Count - 1, i))];
        }

        /// <summary>Jitter as RFC 3550 means it: the average change between neighbouring round trips.</summary>
        public double Jitter
        {
            get
            {
                var s = Ok;
                if (s.Count < 2) return 0;
                double sum = 0;
                for (int i = 1; i < s.Count; i++) sum += Math.Abs(s[i] - s[i - 1]);
                return sum / (s.Count - 1);
            }
        }

        /// <summary>Round trips far above the usual (3× the median and 25 ms more): when they came (s).</summary>
        public List<double> Spikes()
        {
            double med = Median, limit = Math.Max(med * 3, med + 25);
            var list = new List<double>();
            lock (All)
                for (int i = 0; i < All.Count; i++)
                    if (!double.IsNaN(All[i]) && All[i] > limit) list.Add(At[i]);
            return list;
        }
    }

    enum LinkKind { Ethernet, WiFi, Mobile, Other }

    /// <summary>A network adapter as the check sees it.</summary>
    class NetAdapter
    {
        public string Guid = "", Name = "", Description = "";
        /// <summary>Its driver's key under the network class (HKLM, relative), or null.</summary>
        public string ClassKey;
        public LinkKind Kind;
        public bool Virtual;
        public long Speed;
        public int Index, Mtu;
        public IPAddress Gateway;
        public List<IPAddress> Dns = new List<IPAddress>();
        public bool StaticDns;
        public bool GlobalV6;
        public DateTime DriverDate;
        public string DriverVersion = "", Provider = "";
        public NetworkInterface Nic;

        public bool IsIntelI225 { get { return Regex.IsMatch(Description, @"\bI22[56]\b", RegexOptions.IgnoreCase); } }
    }

    /// <summary>The Wi-Fi connection as the WLAN service reports it.</summary>
    class WifiInfo
    {
        public string Ssid = "";
        public byte[] Bssid = new byte[0];
        public int Phy, Signal, Rssi = int.MinValue, Channel;
        public long RxKbps, TxKbps;
        public int FreqMhz;
        /// <summary>Other networks heard: (frequency MHz, RSSI dBm, SSID, BSSID).</summary>
        public readonly List<Tuple<int, int, string, string>> Around = new List<Tuple<int, int, string, string>>();

        public double Band { get { return FreqMhz == 0 ? 0 : FreqMhz < 3000 ? 2.4 : FreqMhz < 5925 ? 5 : 6; } }

        public static int ChannelOf(int mhz)
        {
            if (mhz == 2484) return 14;
            if (mhz < 3000) return (mhz - 2407) / 5;
            if (mhz < 5925) return (mhz - 5000) / 5;
            return (mhz - 5950) / 5;
        }

        public string Generation
        {
            get
            {
                switch (Phy)
                {
                    case 7: return "Wi-Fi 4 (802.11n)";
                    case 8: return "Wi-Fi 5 (802.11ac)";
                    case 10: return Band == 6 ? "Wi-Fi 6E (802.11ax)" : "Wi-Fi 6 (802.11ax)";
                    case 11: return "Wi-Fi 7 (802.11be)";
                    case 4: return "802.11a";
                    case 5: return "802.11b";
                    case 6: return "802.11g";
                    default: return "";
                }
            }
        }

        /// <summary>RSSI, or the signal quality turned back into dBm (0 % = -100, 100 % = -50, linear).</summary>
        public int Dbm { get { return Rssi != int.MinValue && Rssi < 0 ? Rssi : -100 + Signal / 2; } }
    }

    /// <summary>The Wi-Fi API of Windows (wlanapi.dll): read only, nothing is changed here.</summary>
    static class Wlan
    {
        [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr valueType);
        [DllImport("wlanapi.dll")] static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, bool securityEnabled, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

        const int OpCurrentConnection = 7, OpChannel = 8, OpRssi = 0x10000102;

        static string Mac(IntPtr p, int at)
        {
            var b = new byte[6];
            Marshal.Copy(IntPtr.Add(p, at), b, 0, 6);
            return BitConverter.ToString(b);
        }

        static string Ssid(IntPtr p, int at)
        {
            int len = Math.Min(32, Marshal.ReadInt32(p, at));
            var b = new byte[Math.Max(0, len)];
            if (len > 0) Marshal.Copy(IntPtr.Add(p, at + 4), b, 0, len);
            return Encoding.UTF8.GetString(b);
        }

        /// <summary>The connection on this adapter (its GUID), or null: no Wi-Fi service, not connected.</summary>
        public static WifiInfo Read(string adapterGuid)
        {
            Guid want;
            if (!Guid.TryParse(adapterGuid, out want)) return null;
            IntPtr h;
            uint ver;
            try { if (WlanOpenHandle(2, IntPtr.Zero, out ver, out h) != 0) return null; }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            try
            {
                IntPtr list;
                if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return null;
                bool found = false;
                try
                {
                    int n = Marshal.ReadInt32(list, 0);
                    for (int i = 0; i < n; i++)
                    {
                        // WLAN_INTERFACE_INFO: GUID, WCHAR[256], state = 532 bytes, after two DWORDs.
                        var g = (Guid)Marshal.PtrToStructure(IntPtr.Add(list, 8 + i * 532), typeof(Guid));
                        if (g == want) found = true;
                    }
                }
                finally { WlanFreeMemory(list); }
                if (!found) return null;

                int size;
                IntPtr data;
                if (WlanQueryInterface(h, ref want, OpCurrentConnection, IntPtr.Zero, out size, out data, IntPtr.Zero) != 0) return null;
                var w = new WifiInfo();
                try
                {
                    // WLAN_CONNECTION_ATTRIBUTES: state, mode, WCHAR[256] profile, then WLAN_ASSOCIATION_ATTRIBUTES at 520.
                    if (Marshal.ReadInt32(data, 0) != 1 /* connected */) return null;
                    w.Ssid = Ssid(data, 520);
                    w.Bssid = new byte[6];
                    Marshal.Copy(IntPtr.Add(data, 560), w.Bssid, 0, 6);
                    w.Phy = Marshal.ReadInt32(data, 568);
                    w.Signal = Marshal.ReadInt32(data, 576);
                    w.RxKbps = (uint)Marshal.ReadInt32(data, 580);
                    w.TxKbps = (uint)Marshal.ReadInt32(data, 584);
                }
                finally { WlanFreeMemory(data); }

                if (WlanQueryInterface(h, ref want, OpRssi, IntPtr.Zero, out size, out data, IntPtr.Zero) == 0)
                {
                    try { w.Rssi = Marshal.ReadInt32(data); } finally { WlanFreeMemory(data); }
                }
                if (WlanQueryInterface(h, ref want, OpChannel, IntPtr.Zero, out size, out data, IntPtr.Zero) == 0)
                {
                    try { w.Channel = Marshal.ReadInt32(data); } finally { WlanFreeMemory(data); }
                }

                // The networks heard on the last scan (no new scan is started): our own frequency and the neighbours.
                IntPtr bss;
                if (WlanGetNetworkBssList(h, ref want, IntPtr.Zero, 3 /* any */, false, IntPtr.Zero, out bss) == 0)
                {
                    try
                    {
                        string me = BitConverter.ToString(w.Bssid);
                        int n = Marshal.ReadInt32(bss, 4);
                        for (int i = 0; i < n && i < 512; i++)
                        {
                            // WLAN_BSS_ENTRY, 360 bytes: SSID @0, BSSID @40, RSSI @56, centre frequency (kHz) @92.
                            int at = 8 + i * 360;
                            string mac = Mac(bss, at + 40);
                            int rssi = Marshal.ReadInt32(bss, at + 56);
                            int mhz = (int)((uint)Marshal.ReadInt32(bss, at + 92) / 1000);
                            if (mac == me) { w.FreqMhz = mhz; if (w.Rssi == int.MinValue) w.Rssi = rssi; }
                            else w.Around.Add(Tuple.Create(mhz, rssi, Ssid(bss, at), mac));
                        }
                    }
                    finally { WlanFreeMemory(bss); }
                }
                if (w.FreqMhz == 0 && w.Channel > 0)
                    w.FreqMhz = w.Channel <= 14 ? (w.Channel == 14 ? 2484 : 2407 + 5 * w.Channel) : 5000 + 5 * w.Channel;
                if (w.Channel == 0 && w.FreqMhz > 0) w.Channel = WifiInfo.ChannelOf(w.FreqMhz);
                return w;
            }
            catch { return null; }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
        }
    }

    /// <summary>Asks a host for a round trip: an ICMP echo, or a TCP handshake where pings are blocked.</summary>
    class Prober
    {
        public readonly IPAddress Ip;
        public readonly bool Tcp;
        public readonly int Port;
        readonly Ping ping;
        static readonly byte[] Payload = Encoding.ASCII.GetBytes("DriveUP network check....");

        public Prober(IPAddress ip, bool tcp, int port = 443)
        {
            Ip = ip;
            Tcp = tcp;
            Port = port;
            if (!tcp) ping = new Ping();
        }

        public double Once(int timeout = 1000)
        {
            if (!Tcp)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    var r = ping.Send(Ip, timeout, Payload, new PingOptions(64, false));
                    double ms = sw.Elapsed.TotalMilliseconds;
                    if (r.Status != IPStatus.Success) return double.NaN;
                    // The reply's own figure is in whole ms (0 on a LAN); the clock around it is finer, and its own
                    // cost (a fraction of a ms) never makes it more than the reply says plus 1.
                    return Math.Min(ms, r.RoundtripTime + 1);
                }
                catch { return double.NaN; }
            }
            return Handshake(Ip, Port, timeout);
        }

        /// <summary>How long a TCP connection takes to open (one round trip), or NaN.</summary>
        public static double Handshake(IPAddress ip, int port, int timeout)
        {
            try
            {
                using (var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
                {
                    s.NoDelay = true;
                    var sw = Stopwatch.StartNew();
                    var ar = s.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeout)) return double.NaN;
                    s.EndConnect(ar);
                    double ms = sw.Elapsed.TotalMilliseconds;
                    try { s.LingerState = new LingerOption(true, 0); } catch { }
                    return ms;
                }
            }
            catch { return double.NaN; }
        }

        /// <summary>Whether ICMP gets through to the host (2 tries).</summary>
        public static bool Echoes(IPAddress ip)
        {
            var p = new Prober(ip, false);
            return !double.IsNaN(p.Once(800)) || !double.IsNaN(p.Once(800));
        }

        public string How { get { return Tcp ? "TCP " + Port : "ICMP"; } }

        public void Dispose() { if (ping != null) ping.Dispose(); }
    }

    /// <summary>A DNS server and how fast it answers.</summary>
    class DnsResult
    {
        public IPAddress Server;
        public string Name = "";
        public readonly List<double> Ms = new List<double>();
        public int Failed;
        public double Median
        {
            get
            {
                var s = Ms.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
                return s.Count == 0 ? double.NaN : s[s.Count / 2];
            }
        }
        public bool Works { get { return Ms.Count(x => !double.IsNaN(x)) > Failed; } }
    }

    static class DnsProbe
    {
        static readonly Random Rnd = new Random();

        /// <summary>One A query over UDP; the answer's time in ms, or NaN (no answer, refused, server failure).</summary>
        public static double Query(IPAddress server, string name, int timeout = 1500)
        {
            var q = new List<byte>();
            int id;
            lock (Rnd) id = Rnd.Next(1, 65535);
            q.AddRange(new byte[] { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 });
            foreach (var label in name.Split('.'))
            {
                var b = Encoding.ASCII.GetBytes(label);
                q.Add((byte)b.Length);
                q.AddRange(b);
            }
            q.AddRange(new byte[] { 0, 0, 1, 0, 1 });
            try
            {
                using (var u = new UdpClient(server.AddressFamily))
                {
                    u.Client.ReceiveTimeout = timeout;
                    u.Connect(server, 53);
                    var sw = Stopwatch.StartNew();
                    u.Send(q.ToArray(), q.Count);
                    while (sw.ElapsedMilliseconds < timeout)
                    {
                        var ep = new IPEndPoint(IPAddress.Any, 0);
                        byte[] r;
                        try { r = u.Receive(ref ep); }
                        catch (SocketException) { return double.NaN; }
                        if (r.Length < 12 || r[0] != (byte)(id >> 8) || r[1] != (byte)id) continue;
                        int rcode = r[3] & 0x0F;
                        // NOERROR or NXDOMAIN are answers; SERVFAIL / REFUSED are not.
                        return rcode == 0 || rcode == 3 ? sw.Elapsed.TotalMilliseconds : double.NaN;
                    }
                }
            }
            catch { }
            return double.NaN;
        }
    }

    /// <summary>
    /// "Check network": what the connection is, how it behaves idle and under load, how fast names resolve,
    /// and which Windows / adapter settings stand in its way. Reads and measures only — fixes are offered
    /// separately (<see cref="NetFix"/>) and applied only when the user ticks and applies them.
    /// </summary>
    class NetCheck
    {
        public readonly Machine Pc;
        public volatile bool Cancel;
        /// <summary>A new line of the report (called on the worker thread) and an update of one.</summary>
        public Action<NetFinding> Added, Updated;
        public Action<string> Progress;
        /// <summary>Two more minutes of quiet pinging: catches drops and Wi-Fi's periodic spikes.</summary>
        public bool Long;

        public NetAdapter Main, Physical;
        public readonly List<NetAdapter> Adapters = new List<NetAdapter>();
        public WifiInfo Wifi;
        public Rtt Gateway, Internet, LoadDown, LoadUp, Quiet;
        public Prober InternetProbe;
        public double DownMbps = double.NaN, UpMbps = double.NaN, BloatDown = double.NaN, BloatUp = double.NaN;
        public DnsResult SystemDns;
        public readonly List<DnsResult> PublicDns = new List<DnsResult>();
        public bool V6Broken, Online, Metered, Vpn;
        public int? Ipv6Components;
        public string AutoTuning = "";
        public bool RssOff;
        public readonly Dictionary<string, string> NicValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<Tuple<string, string>> Optimizers = new List<Tuple<string, string>>();
        public double Retransmit = double.NaN;
        public long RxErrors, RxPackets;
        public readonly List<NetFinding> Findings = new List<NetFinding>();
        public int PathMtu;

        public NetCheck(Machine pc) { Pc = pc; }

        static NetCheck()
        {
            // csc builds the exe without a target-framework mark, so .NET would offer old TLS only; 4 streams to one host.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            if (ServicePointManager.DefaultConnectionLimit < 16) ServicePointManager.DefaultConnectionLimit = 16;
        }

        static readonly IPAddress[] Anycast = { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("9.9.9.9") };

        static string Ms(double v)
        {
            if (double.IsNaN(v)) return "—";
            return (v < 10 ? v.ToString("0.#", CultureInfo.CurrentCulture) : Math.Round(v).ToString(CultureInfo.CurrentCulture)) + L.T(" мс", " ms");
        }

        static string Mbps(double v)
        {
            if (double.IsNaN(v)) return "—";
            return (v < 10 ? v.ToString("0.#", CultureInfo.CurrentCulture) : Math.Round(v).ToString(CultureInfo.CurrentCulture)) + L.T(" Мбит/с", " Mbit/s");
        }

        static string Pct(double v) { return (v < 1 && v > 0 ? v.ToString("0.#", CultureInfo.CurrentCulture) : Math.Round(v).ToString(CultureInfo.CurrentCulture)) + " %"; }

        public static string LinkText(long bps)
        {
            if (bps <= 0) return "";
            if (bps >= 1000000000) return (bps / 1e9).ToString("0.#", CultureInfo.CurrentCulture) + L.T(" Гбит/с", " Gbit/s");
            return (bps / 1000000).ToString(CultureInfo.CurrentCulture) + L.T(" Мбит/с", " Mbit/s");
        }

        NetFinding Add(string id, TuneCat group, string glyph, Func<string> title)
        {
            var f = new NetFinding { Id = id, Group = group, Glyph = glyph, Title = title, Value = () => L.T("измеряю…", "measuring…") };
            lock (Findings) Findings.Add(f);
            if (Added != null) Added(f);
            return f;
        }

        void Done(NetFinding f, NetLevel level, Func<string> value, Func<string> detail)
        {
            f.Level = level;
            f.Value = value;
            f.Detail = detail ?? (() => "");
            if (Updated != null) Updated(f);
        }

        void Say(string text) { if (Progress != null) Progress(text); }

        /// <summary>The whole check, in order; each step adds its lines as it finishes. Call off the UI thread.</summary>
        public void Run()
        {
            Say(L.T("Смотрю подключение…", "Looking at the connection…"));
            ReadAdapters();
            ReadConnection();
            if (Cancel) return;
            Say(L.T("Меряю задержку до роутера и до интернета…", "Measuring the delay to the router and to the internet…"));
            MeasureLatency();
            if (Cancel) return;
            Say(L.T("Проверяю DNS, IPv6 и MTU…", "Checking DNS, IPv6 and MTU…"));
            CheckDns();
            if (Cancel) return;
            CheckPath();
            if (Cancel) return;
            CheckServices();
            if (Cancel) return;
            MeasureLoad();
            if (Cancel) return;
            if (Long)
            {
                Say(L.T("Долгий замер: слежу за задержкой 2 минуты…", "Long check: watching the delay for 2 minutes…"));
                MeasureQuiet();
                if (Cancel) return;
            }
            Say(L.T("Смотрю настройки Windows и драйвер…", "Looking at Windows settings and the driver…"));
            ReadSettings();
        }

        // ---------- the connection ----------

        [DllImport("iphlpapi.dll")]
        static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

        static readonly Regex VirtualName = new Regex(@"VPN|TAP-|Tunnel|WireGuard|Wintun|Tailscale|ZeroTier|OpenVPN|AnyConnect|Fortinet|GlobalProtect|Hyper-V|vEthernet|VirtualBox|VMware|Virtual|Npcap|Loopback|Pseudo|WAN Miniport|Teredo|isatap|Bluetooth",
            RegexOptions.IgnoreCase);

        const string NetClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

        /// <summary>The adapter's key under the network class, by its GUID.</summary>
        public static string ClassKeyOf(string guid)
        {
            foreach (var sub in Reg.SubKeyNames(true, NetClass))
            {
                if (sub.Length != 4) continue;
                try
                {
                    var id = Reg.Get(true, NetClass + "\\" + sub, "NetCfgInstanceId") as string;
                    if (id != null && id.Equals(guid, StringComparison.OrdinalIgnoreCase)) return NetClass + "\\" + sub;
                }
                catch { }
            }
            return null;
        }

        void ReadAdapters()
        {
            uint best = 0;
            try { GetBestInterface(BitConverter.ToUInt32(Anycast[0].GetAddressBytes(), 0), out best); } catch { }
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { all = new NetworkInterface[0]; }
            foreach (var nic in all)
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var a = new NetAdapter { Guid = nic.Id, Name = nic.Name, Description = nic.Description, Nic = nic };
                try { a.Speed = nic.Speed; } catch { }
                switch (nic.NetworkInterfaceType)
                {
                    case NetworkInterfaceType.Wireless80211: a.Kind = LinkKind.WiFi; break;
                    case NetworkInterfaceType.Ethernet:
                    case NetworkInterfaceType.GigabitEthernet:
                    case NetworkInterfaceType.FastEthernetT:
                    case NetworkInterfaceType.FastEthernetFx:
                    case NetworkInterfaceType.Ethernet3Megabit: a.Kind = LinkKind.Ethernet; break;
                    case NetworkInterfaceType.Wwanpp:
                    case NetworkInterfaceType.Wwanpp2: a.Kind = LinkKind.Mobile; break;
                    default: a.Kind = LinkKind.Other; break;
                }
                a.Virtual = a.Kind == LinkKind.Other || VirtualName.IsMatch(a.Description) || VirtualName.IsMatch(a.Name);
                if (a.Kind == LinkKind.WiFi) a.Virtual = false;
                try
                {
                    var ip = nic.GetIPProperties();
                    var v4 = ip.GetIPv4Properties();
                    if (v4 != null) { a.Index = v4.Index; a.Mtu = v4.Mtu; }
                    a.Gateway = ip.GatewayAddresses.Select(g => g.Address)
                        .FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any));
                    a.Dns = ip.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Take(3).ToList();
                    a.GlobalV6 = ip.UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                        !u.Address.IsIPv6LinkLocal && !u.Address.IsIPv6SiteLocal && (u.Address.GetAddressBytes()[0] & 0xE0) == 0x20);
                }
                catch { }
                try
                {
                    var ns = Reg.Get(true, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + a.Guid, "NameServer") as string;
                    a.StaticDns = !string.IsNullOrWhiteSpace(ns);
                }
                catch { }
                a.ClassKey = ClassKeyOf(a.Guid);
                if (a.ClassKey != null)
                {
                    a.Provider = Reg.Get(true, a.ClassKey, "ProviderName") as string ?? "";
                    a.DriverVersion = Reg.Get(true, a.ClassKey, "DriverVersion") as string ?? "";
                    DateTime d;
                    var ds = Reg.Get(true, a.ClassKey, "DriverDate") as string;
                    if (ds != null && DateTime.TryParseExact(ds, new[] { "M-d-yyyy", "MM-dd-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) a.DriverDate = d;
                }
                Adapters.Add(a);
                if (a.Index == best && best != 0) Main = a;
            }
            if (Main == null) Main = Adapters.FirstOrDefault(a => a.Gateway != null && !a.Virtual) ?? Adapters.FirstOrDefault(a => a.Gateway != null);
            Vpn = Main != null && Main.Virtual;
            // Through a VPN the real link is still the adapter that reaches the router.
            Physical = Main != null && !Main.Virtual ? Main
                : Adapters.Where(a => !a.Virtual && a.Gateway != null).OrderBy(a => a.Kind == LinkKind.Ethernet ? 0 : 1).FirstOrDefault();
            if (Physical != null && Physical.Kind == LinkKind.WiFi) Wifi = Wlan.Read(Physical.Guid);
            Metered = NetCost.Metered();
        }

        void ReadConnection()
        {
            var link = Add("link", TuneCat.NetLink, "\uE839", () => L.T("Подключение", "Connection"));
            var p = Physical;
            // An adapter that got no address from the router gives itself 169.254.x.x.
            var noAddress = Adapters.FirstOrDefault(a => !a.Virtual && a.Gateway == null && a.Nic != null && Apipa(a.Nic));
            if (p == null && noAddress != null)
            {
                Done(link, NetLevel.Bad, () => L.T("роутер не выдал адрес", "no address from the router") + " · " + noAddress.Description,
                    () => L.T("Связь с роутером есть, но адрес он не выдал (169.254.x.x). Перезагрузите роутер; если не поможет — проверьте, не задан ли адрес вручную.",
                              "There is a link to the router, but it gave no address (169.254.x.x). Restart the router; if that does not help, check for a manually set address."));
                return;
            }
            if (p == null)
            {
                Done(link, NetLevel.Bad, () => L.T("нет активного подключения", "no active connection"),
                    () => L.T("Ни один сетевой адаптер не подключён к сети с роутером. Проверьте кабель или Wi-Fi.",
                              "No network adapter is connected to a network with a router. Check the cable or Wi-Fi."));
                return;
            }
            link.Glyph = p.Kind == LinkKind.WiFi ? "\uE701" : p.Kind == LinkKind.Mobile ? "\uEC3B" : "\uE839";
            string kind = p.Kind == LinkKind.WiFi ? "Wi-Fi" : p.Kind == LinkKind.Mobile ? L.T("мобильная сеть", "mobile network") : L.T("кабель (Ethernet)", "cable (Ethernet)");
            Func<string> value = () => kind + (p.Speed > 0 && p.Kind == LinkKind.Ethernet ? " · " + LinkText(p.Speed) : "") + " · " + p.Description;
            if (p.Kind == LinkKind.Ethernet && p.Speed > 0 && p.Speed < 1000000000L)
            {
                bool fast = Regex.IsMatch(p.Description, @"Gigabit|GbE|2\.5G|5G|10G|I21[019]|I22[56]|I219|RTL81(1[18]|25|26)|Killer E[23]|AQtion|Marvell|E3[01]00", RegexOptions.IgnoreCase);
                Done(link, p.Speed <= 10000000 ? NetLevel.Bad : NetLevel.Warn, value, () => fast
                    ? L.T("Карта гигабитная, а связь установилась на ", "The card is gigabit, but the link came up at ") + LinkText(p.Speed) +
                      L.T(" — обычно виноват кабель (нужен Cat5e или лучше, все 8 жил), разъём или порт роутера. Если в настройках карты скорость задана вручную, DriveUP вернёт «Авто».",
                          " — usually the cable (Cat5e or better, all 8 wires), a plug or the router port. If the card's speed is set by hand, DriveUP sets it back to Auto.")
                    : L.T("Связь установилась на ", "The link came up at ") + LinkText(p.Speed) +
                      L.T(": если тариф быстрее, проверьте кабель и порт роутера.", ": if your plan is faster, check the cable and the router port."));
            }
            else Done(link, NetLevel.Good, value, () => p.Kind == LinkKind.WiFi
                ? L.T("Беспроводное подключение: его качество — ниже", "Wireless connection: its quality is below")
                : L.T("Проводное подключение — самое стабильное", "A wired connection — the most stable kind"));

            // Wired and Wi-Fi both up, yet the traffic goes over Wi-Fi.
            var wired = Adapters.FirstOrDefault(a => a.Kind == LinkKind.Ethernet && !a.Virtual && a.Gateway != null);
            if (p.Kind == LinkKind.WiFi && wired != null)
            {
                var both = Add("both", TuneCat.NetLink, "\uE839", () => L.T("Кабель подключён, но интернет идёт по Wi-Fi", "Cable connected, but the traffic goes over Wi-Fi"));
                Done(both, NetLevel.Warn, () => wired.Description,
                    () => L.T("Windows выбрала Wi-Fi: обычно кабель подключён к сети без интернета. Отключите Wi-Fi, если кабель ведёт к тому же роутеру.",
                              "Windows chose Wi-Fi: the cable usually leads to a network without internet. Turn Wi-Fi off if the cable goes to the same router."));
            }

            if (Vpn)
            {
                var vpn = Add("vpn", TuneCat.NetLink, "\uE72E", () => L.T("Трафик идёт через VPN", "Traffic goes through a VPN"));
                Done(vpn, NetLevel.Info, () => Main.Description,
                    () => L.T("Задержка и скорость ниже измерены вместе с VPN: без него они могут быть лучше. DNS и сетевые настройки при VPN DriveUP не меняет.",
                              "The delay and speed below include the VPN: without it they can be better. With a VPN DriveUP does not change DNS or network settings."));
            }

            if (Wifi != null) ReadWifi();

            // Errors on the adapter since it came up: a bad cable, plug or driver.
            try
            {
                var st = p.Nic.GetIPv4Statistics();
                RxPackets = st.UnicastPacketsReceived + st.NonUnicastPacketsReceived;
                RxErrors = st.IncomingPacketsWithErrors + st.OutgoingPacketsWithErrors;
            }
            catch { }
            try
            {
                var tcp = IPGlobalProperties.GetIPGlobalProperties().GetTcpIPv4Statistics();
                if (tcp.SegmentsSent > 2000) Retransmit = tcp.SegmentsResent * 100.0 / tcp.SegmentsSent;
            }
            catch { }
            var err = Add("errors", TuneCat.NetLink, "\uE9D9", () => L.T("Ошибки передачи", "Transmission errors"));
            double errPct = RxPackets > 0 ? RxErrors * 100.0 / RxPackets : 0;
            NetLevel lv = NetLevel.Good;
            if ((RxErrors > 50 && errPct > 0.1) || Retransmit > 5) lv = NetLevel.Bad;
            else if ((RxErrors > 20 && errPct > 0.01) || Retransmit > 2) lv = NetLevel.Warn;
            Done(err, lv, () => L.T("битых пакетов: ", "bad packets: ") + RxErrors.ToString("N0", CultureInfo.CurrentCulture) +
                    (double.IsNaN(Retransmit) ? "" : L.T(" · повторы TCP: ", " · TCP resends: ") + Pct(Retransmit)),
                () => lv == NetLevel.Good
                    ? L.T("Пакеты доходят целыми, повторных отправок мало", "Packets arrive intact, few resends")
                    : p.Kind == LinkKind.WiFi
                        ? L.T("Много повторов: слабый сигнал или помехи. Подойдите ближе к роутеру или перейдите на 5 ГГц.", "Many resends: a weak signal or interference. Move closer to the router or switch to 5 GHz.")
                        : L.T("Пакеты бьются: проверьте кабель и разъёмы, обновите драйвер сетевой карты.", "Packets get damaged: check the cable and plugs, update the network driver."));
        }

        static bool Apipa(NetworkInterface nic)
        {
            try
            {
                return nic.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork &&
                    u.Address.GetAddressBytes()[0] == 169 && u.Address.GetAddressBytes()[1] == 254);
            }
            catch { return false; }
        }

        void ReadWifi()
        {
            var w = Wifi;
            int dbm = w.Dbm;
            var sig = Add("wifi", TuneCat.NetLink, "\uE701", () => L.T("Сигнал Wi-Fi", "Wi-Fi signal"));
            string band = w.Band == 2.4 ? L.T("2,4 ГГц", "2.4 GHz") : w.Band == 5 ? L.T("5 ГГц", "5 GHz") : w.Band == 6 ? L.T("6 ГГц", "6 GHz") : "";
            NetLevel lv = dbm >= -67 ? NetLevel.Good : dbm >= -72 ? NetLevel.Info : dbm >= -80 ? NetLevel.Warn : NetLevel.Bad;
            Done(sig, lv,
                () => w.Signal + " % (" + dbm + L.T(" дБм)", " dBm)") + (band.Length > 0 ? " · " + band : "") + (w.Channel > 0 ? L.T(" · канал ", " · channel ") + w.Channel : "") +
                      (w.Generation.Length > 0 ? " · " + w.Generation : "") + (w.RxKbps > 0 ? " · " + LinkText(Math.Max(w.RxKbps, w.TxKbps) * 1000) : ""),
                () =>
                {
                    if (lv == NetLevel.Good) return L.T("Сильный сигнал: хватит и для игр, и для звонков", "A strong signal: enough for games and calls");
                    if (lv == NetLevel.Info) return L.T("Сигнал нормальный, но для игр и звонков лучше ближе к роутеру (от −67 дБм)", "The signal is fine, but games and calls prefer being closer to the router (−67 dBm or better)");
                    return L.T("Слабый сигнал: отсюда задержка и обрывы. Подойдите ближе к роутеру, уберите преграды или подключите кабель.",
                               "A weak signal: it causes delay and drops. Move closer to the router, remove obstacles or plug in a cable.");
                });

            // On 2.4 GHz while the same network is heard on 5 / 6 GHz.
            if (w.Band == 2.4)
            {
                var faster = w.Around.Where(n => n.Item3 == w.Ssid && n.Item3.Length > 0 && n.Item1 >= 5000 && n.Item2 >= -70).OrderByDescending(n => n.Item2).FirstOrDefault();
                var b = Add("band", TuneCat.NetLink, "\uE701", () => L.T("Диапазон Wi-Fi", "Wi-Fi band"));
                if (faster != null)
                    Done(b, NetLevel.Warn, () => L.T("2,4 ГГц, хотя эта же сеть есть на 5 ГГц (", "2.4 GHz, though this network is also on 5 GHz (") + faster.Item2 + L.T(" дБм)", " dBm)"),
                        () => L.T("2,4 ГГц медленнее и шумнее. Если роутер раздаёт одну сеть на оба диапазона, разделите их (разные имена) и подключитесь к 5 ГГц.",
                                  "2.4 GHz is slower and noisier. If the router uses one name for both bands, split them (different names) and join the 5 GHz one."));
                else
                    Done(b, NetLevel.Info, () => L.T("2,4 ГГц", "2.4 GHz"),
                        () => L.T("2,4 ГГц дальнобойнее, но медленнее и шумнее. Если роутер умеет 5 ГГц, для игр и видео лучше он.",
                                  "2.4 GHz reaches further but is slower and noisier. If the router has 5 GHz, it is better for games and video."));
            }

            // Neighbours on the same (or, at 2.4 GHz, an overlapping) channel.
            if (w.Channel > 0 && w.FreqMhz > 0)
            {
                int near = w.Around.Count(n => n.Item2 >= -82 && n.Item3 != w.Ssid &&
                    (w.Band == 2.4 ? n.Item1 < 3000 && Math.Abs(WifiInfo.ChannelOf(n.Item1) - w.Channel) <= 4 : Math.Abs(n.Item1 - w.FreqMhz) < 10));
                var ch = Add("channel", TuneCat.NetLink, "\uE704", () => L.T("Соседние сети на канале", "Neighbour networks on the channel"));
                NetLevel lv2 = near >= (w.Band == 2.4 ? 8 : 5) ? NetLevel.Warn : NetLevel.Good;
                Done(ch, lv2, () => L.Count(near, "сеть", "сети", "сетей", "network", "networks") + L.T(" рядом на канале ", " near channel ") + w.Channel,
                    () => lv2 == NetLevel.Good
                        ? L.T("Эфир свободный — помех от соседей мало", "The air is quiet — little interference from neighbours")
                        : L.T("Канал забит соседями: в настройках роутера выберите другой канал (на 2,4 ГГц — 1, 6 или 11) или перейдите на 5 ГГц.",
                              "The channel is crowded: pick another channel in the router (1, 6 or 11 on 2.4 GHz) or switch to 5 GHz."));
            }
        }

        // ---------- delay ----------

        /// <summary>Samples one host every interval until the time is up (or cancelled).</summary>
        void Sample(Prober p, Rtt into, NetFinding f, double seconds, int intervalMs, Func<bool> stop = null)
        {
            var sw = Stopwatch.StartNew();
            while (!Cancel && sw.Elapsed.TotalSeconds < seconds && (stop == null || !stop()))
            {
                double t0 = sw.Elapsed.TotalMilliseconds;
                double v = p.Once(1000);
                into.Add(v, t0 / 1000);
                if (f != null) f.Add(v);
                int wait = (int)(intervalMs - (sw.Elapsed.TotalMilliseconds - t0));
                if (wait > 0) Thread.Sleep(wait);
            }
        }

        void MeasureLatency()
        {
            var gw = Physical != null ? Physical.Gateway : null;
            NetFinding fg = null, fi;
            Prober pg = null;
            Gateway = new Rtt();
            if (gw != null)
            {
                fg = Add("gateway", TuneCat.NetLatency, Physical.Kind == LinkKind.WiFi ? "\uE701" : "\uE839", () => L.T("Задержка до роутера", "Delay to the router"));
                fg.Chart = true;
                pg = new Prober(gw, false);
            }
            fi = Add("internet", TuneCat.NetLatency, "\uE774", () => L.T("Задержка до интернета", "Delay to the internet"));
            fi.Chart = true;

            // A host that answers pings; where ICMP is blocked, the time a TCP connection takes to open (one round trip).
            foreach (var ip in Anycast)
                if (Prober.Echoes(ip)) { InternetProbe = new Prober(ip, false); break; }
            if (InternetProbe == null)
                foreach (var ip in Anycast)
                    if (!double.IsNaN(Prober.Handshake(ip, 443, 1500))) { InternetProbe = new Prober(ip, true); break; }
            Online = InternetProbe != null;

            Internet = new Rtt();
            const double Secs = 12;
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            Thread tg = null;
            if (pg != null)
            {
                tg = new Thread(() => Sample(pg, Gateway, fg, Secs, 200)) { IsBackground = true, Name = "net gw", Priority = ThreadPriority.AboveNormal };
                tg.Start();
            }
            if (Online) Sample(InternetProbe, Internet, fi, Secs, 200);
            if (tg != null) tg.Join();
            Thread.CurrentThread.Priority = ThreadPriority.Normal;

            if (fg != null)
            {
                bool wifi = Physical.Kind == LinkKind.WiFi;
                if (Gateway.Received == 0)
                    Done(fg, NetLevel.Info, () => L.T("роутер не отвечает на ping", "the router does not answer pings"),
                        () => L.T("Некоторые роутеры не отвечают на ping — это не ошибка, если интернет работает", "Some routers ignore pings — not a problem if the internet works"));
                else
                {
                    double med = Gateway.Median, jit = Gateway.Jitter, p95 = Gateway.Pct(95), loss = Gateway.Loss;
                    NetLevel lv = NetLevel.Good;
                    if (loss >= 3 || med > (wifi ? 40 : 10) || p95 > (wifi ? 150 : 30)) lv = NetLevel.Bad;
                    else if (loss >= 1 || med > (wifi ? 12 : 3) || jit > (wifi ? 10 : 2) || p95 > (wifi ? 60 : 10)) lv = NetLevel.Warn;
                    Done(fg, lv, () => Ms(med) + L.T(" · разброс ", " · jitter ") + Ms(jit) + L.T(" · потери ", " · loss ") + Pct(loss),
                        () => lv == NetLevel.Good
                            ? L.T("Домашняя сеть отвечает быстро и ровно", "The home network answers fast and steadily")
                            : wifi
                                ? L.T("Задержка скачет уже до роутера — дело в Wi-Fi: сигнал, помехи или энергосбережение адаптера",
                                      "The delay jumps already on the way to the router — it is the Wi-Fi: signal, interference or the adapter's power saving")
                                : L.T("Задержка до роутера по кабелю должна быть меньше 1–2 мс и без потерь: проверьте кабель, порт роутера и драйвер карты",
                                      "Over a cable the router should answer in under 1–2 ms without loss: check the cable, the router port and the network driver"));
                }
            }

            if (!Online)
            {
                Done(fi, NetLevel.Bad, () => L.T("интернет недоступен", "no internet"),
                    () => L.T("Ни 1.1.1.1, ни 8.8.8.8, ни 9.9.9.9 не отвечают. Проверьте подключение роутера к провайдеру.",
                              "Neither 1.1.1.1, 8.8.8.8 nor 9.9.9.9 answers. Check the router's connection to the provider."));
                return;
            }
            double m = Internet.Median, j = Internet.Jitter, l = Internet.Loss, p = Internet.Pct(95);
            int spikes = Internet.Spikes().Count;
            NetLevel li = NetLevel.Good;
            if (l >= 3 || m > 150 || j > 30) li = NetLevel.Bad;
            else if (l >= 1 || m > 80 || j > 12 || spikes * 100.0 / Math.Max(1, Internet.Count) > 4) li = NetLevel.Warn;
            string via = InternetProbe.Ip + (InternetProbe.Tcp ? L.T(", через TCP: ping закрыт", ", over TCP: pings are blocked") : "");
            Done(fi, li, () => Ms(m) + L.T(" · разброс ", " · jitter ") + Ms(j) + L.T(" · потери ", " · loss ") + Pct(l) + L.T(" · пик ", " · peak ") + Ms(Internet.Max),
                () =>
                {
                    string s = "(" + via + ") ";
                    if (li == NetLevel.Good) return s + L.T("Ровная задержка без потерь — для игр и звонков отлично", "A steady delay without loss — great for games and calls");
                    bool local = Gateway.Received > 0 && (Gateway.Loss >= 1 || Gateway.Jitter > (Physical.Kind == LinkKind.WiFi ? 10 : 2));
                    if (local) return s + L.T("Проблема уже в домашней сети (см. задержку до роутера)", "The problem is already in the home network (see the router delay)");
                    return s + L.T("До роутера всё ровно, а дальше — потери или скачки: это канал провайдера или загрузка сети другими устройствами",
                                   "Steady up to the router, losses or jumps beyond it: the provider's line, or other devices loading the network");
                });
        }

        // ---------- DNS, IPv6, MTU ----------

        static readonly string[] Names = { "www.google.com", "www.microsoft.com", "www.youtube.com", "www.wikipedia.org", "www.cloudflare.com", "steamcommunity.com", "github.com", "www.apple.com" };

        static DnsResult AskAll(IPAddress server, string name)
        {
            var r = new DnsResult { Server = server, Name = name };
            foreach (var n in Names)
            {
                double ms = DnsProbe.Query(server, n);
                if (double.IsNaN(ms)) r.Failed++;
                r.Ms.Add(ms);
            }
            return r;
        }

        void CheckDns()
        {
            var main = Main ?? Physical;
            if (main == null) return;
            var f = Add("dns", TuneCat.NetDns, "\uE8B9", () => L.T("DNS вашей сети", "Your network's DNS"));
            // The servers Windows asks: the internet adapter's (a VPN may have none of its own), else the real link's.
            var servers = main.Dns.Take(2).ToList();
            if (servers.Count == 0 && Physical != null) servers = Physical.Dns.Take(2).ToList();
            if (servers.Count == 0) servers = Adapters.SelectMany(a => a.Dns).Distinct().Take(2).ToList();
            if (servers.Count == 0)
            {
                Done(f, NetLevel.Bad, () => L.T("DNS-сервер не задан", "no DNS server set"),
                    () => L.T("Без DNS сайты не открываются по именам. Перезапустите роутер или задайте DNS вручную.", "Without DNS, sites do not open by name. Restart the router or set a DNS server."));
                return;
            }
            var results = new List<DnsResult>();
            var publics = new[] { Tuple.Create("1.1.1.1", "Cloudflare"), Tuple.Create("8.8.8.8", "Google"), Tuple.Create("9.9.9.9", "Quad9") };
            var threads = new List<Thread>();
            foreach (var s in servers)
            {
                var ip = s;
                threads.Add(new Thread(() => { var r = AskAll(ip, ""); lock (results) results.Add(r); }) { IsBackground = true });
            }
            foreach (var pub in publics)
            {
                var p = pub;
                threads.Add(new Thread(() => { var r = AskAll(IPAddress.Parse(p.Item1), p.Item2); lock (PublicDns) PublicDns.Add(r); }) { IsBackground = true });
            }
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();
            SystemDns = results.Where(r => r.Works).OrderBy(r => r.Median).FirstOrDefault() ?? results.FirstOrDefault();
            if (SystemDns == null) return;
            double med = SystemDns.Median;
            int failed = results.Sum(r => r.Failed), asked = results.Sum(r => r.Ms.Count);
            NetLevel lv = !SystemDns.Works ? NetLevel.Bad : failed * 100 / Math.Max(1, asked) >= 20 ? NetLevel.Warn : med > 150 ? NetLevel.Warn : NetLevel.Good;
            Done(f, lv, () => string.Join(", ", servers.Select(s => s.ToString())) + " · " + (SystemDns.Works ? Ms(med) : L.T("не отвечает", "no answer")) +
                              (failed > 0 ? L.T(" · без ответа: ", " · unanswered: ") + failed + L.T(" из ", " of ") + asked : ""),
                () => lv == NetLevel.Good
                    ? L.T("Имена сайтов находятся быстро", "Site names resolve quickly")
                    : L.T("DNS отвечает медленно или не всегда: сайты долго начинают открываться", "DNS answers slowly or not always: sites take long to start loading"));

            var best = PublicDns.Where(r => r.Works).OrderBy(r => r.Median).FirstOrDefault();
            var pf = Add("dnspub", TuneCat.NetDns, "\uE774", () => L.T("Публичные DNS для сравнения", "Public DNS for comparison"));
            if (best == null)
                Done(pf, NetLevel.Info, () => L.T("не отвечают из этой сети", "do not answer from this network"),
                    () => L.T("Сеть пропускает DNS только к своему серверу — так бывает в офисах и у некоторых провайдеров", "The network only lets DNS go to its own server — usual at work and with some providers"));
            else
                Done(pf, NetLevel.Info, () => string.Join(" · ", PublicDns.OrderBy(r => r.Works ? r.Median : 9999).Select(r => r.Name + " " + (r.Works ? Ms(r.Median) : "—"))),
                    () => SlowDns
                        ? L.T("Быстрее вашего DNS — можно переключиться (ниже, в исправлениях)", "Faster than your DNS — you can switch (below, among the fixes)")
                        : L.T("Ваш DNS не медленнее — менять незачем", "Your DNS is no slower — no reason to change it"));
        }

        /// <summary>The fastest public resolver that answers.</summary>
        public DnsResult BestPublic { get { return PublicDns.Where(r => r.Works).OrderBy(r => r.Median).FirstOrDefault(); } }

        /// <summary>The network's DNS fails, or a public one is clearly faster (half the time and 20 ms less).</summary>
        public bool SlowDns
        {
            get
            {
                var b = BestPublic;
                if (b == null || SystemDns == null) return false;
                if (!SystemDns.Works || SystemDns.Failed * 4 >= SystemDns.Ms.Count) return true;
                return SystemDns.Median > b.Median * 2 && SystemDns.Median - b.Median > 20;
            }
        }

        void CheckPath()
        {
            if (!Online) return;
            // IPv6: an address from the provider that does not reach anywhere makes programs wait before falling back.
            try { Ipv6Components = Reg.Dword(true, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents"); } catch { }
            var main = Main ?? Physical;
            var f6 = Add("ipv6", TuneCat.NetPath, "\uE774", () => "IPv6");
            if (Ipv6Components.HasValue && (Ipv6Components.Value & 0xFF) == 0xFF)
                Done(f6, NetLevel.Warn, () => L.T("отключён в реестре", "disabled in the registry"),
                    () => L.T("Обычно это след «твикера». Microsoft не рекомендует отключать IPv6: части Windows на него рассчитывают", "Usually left by a \"tweaker\". Microsoft advises against disabling IPv6: parts of Windows rely on it"));
            else if (main == null || !main.GlobalV6)
                Done(f6, NetLevel.Info, () => L.T("провайдер не выдал адрес IPv6", "no IPv6 address from the provider"), () => L.T("Работает IPv4 — это нормально", "IPv4 is used — that is normal"));
            else
            {
                bool works = new[] { "2606:4700:4700::1111", "2001:4860:4860::8888" }.Any(a => !double.IsNaN(Prober.Handshake(IPAddress.Parse(a), 443, 1500)));
                V6Broken = !works;
                Done(f6, works ? NetLevel.Good : NetLevel.Warn, () => works ? L.T("работает", "works") : L.T("адрес есть, но интернет по IPv6 не работает", "there is an address, but the IPv6 internet does not work"),
                    () => works ? L.T("Сайты открываются и по IPv6", "Sites open over IPv6 too")
                        : L.T("Программы сначала пробуют IPv6 и теряют на этом время. Windows можно велеть предпочитать IPv4 (ниже), а провайдеру — сообщить.",
                              "Programs try IPv6 first and lose time on it. Windows can be told to prefer IPv4 (below); tell your provider too."));
            }

            // The largest packet that passes without being split (only where pings are answered).
            if (InternetProbe == null || InternetProbe.Tcp) return;
            int lo = 1200, hi = 1472;
            using (var ping = new Ping())
            {
                Func<int, bool> passes = size =>
                {
                    try { return ping.Send(InternetProbe.Ip, 800, new byte[size], new PingOptions(64, true)).Status == IPStatus.Success; }
                    catch { return false; }
                };
                if (!passes(lo)) return;
                if (passes(hi)) lo = hi;
                else
                    while (hi - lo > 1 && !Cancel)
                    {
                        int mid = (lo + hi) / 2;
                        if (passes(mid)) lo = mid; else hi = mid;
                    }
            }
            PathMtu = lo + 28;
            var fm = Add("mtu", TuneCat.NetPath, "\uE9D2", () => L.T("Размер пакета (MTU)", "Packet size (MTU)"));
            Done(fm, PathMtu >= 1400 ? NetLevel.Good : NetLevel.Info, () => PathMtu.ToString(CultureInfo.InvariantCulture) + L.T(" байт", " bytes"),
                () => PathMtu >= 1500 ? L.T("Полный размер — пакеты не дробятся", "Full size — packets are not split")
                    : PathMtu >= 1480 ? L.T("Чуть меньше 1500 — так у PPPoE и некоторых провайдеров, это нормально", "A bit under 1500 — usual with PPPoE and some providers")
                    : L.T("Заметно меньше 1500: обычно из-за VPN или туннеля. Windows подстраивается сама", "Well under 1500: usually a VPN or tunnel. Windows adapts by itself"));
        }

        static readonly string[][] Services =
        {
            new[] { "Google", "www.google.com" }, new[] { "YouTube", "www.youtube.com" }, new[] { "Microsoft", "www.microsoft.com" },
            new[] { "Steam", "store.steampowered.com" }, new[] { "Discord", "discord.com" }, new[] { "GitHub", "github.com" },
        };

        /// <summary>How long a connection to popular services takes to open (the name found through Windows, as a browser would).</summary>
        void CheckServices()
        {
            if (!Online) return;
            var f = Add("services", TuneCat.NetPath, "\uE909", () => L.T("Популярные сервисы", "Popular services"));
            var times = new double[Services.Length];
            var threads = new List<Thread>();
            for (int i = 0; i < Services.Length; i++)
            {
                int k = i;
                times[k] = double.NaN;
                var t = new Thread(() =>
                {
                    double best = double.NaN;
                    try
                    {
                        var ip = Dns.GetHostAddresses(Services[k][1]).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                        if (ip != null)
                            for (int n = 0; n < 3; n++)
                            {
                                double ms = Prober.Handshake(ip, 443, 2500);
                                if (!double.IsNaN(ms) && (double.IsNaN(best) || ms < best)) best = ms;
                            }
                    }
                    catch { }
                    times[k] = best;
                }) { IsBackground = true };
                threads.Add(t);
                t.Start();
            }
            foreach (var t in threads) t.Join(12000);
            int down = times.Count(double.IsNaN);
            Done(f, down == Services.Length ? NetLevel.Bad : NetLevel.Info,
                () => string.Join(" · ", Services.Select((s, i) => s[0] + " " + (double.IsNaN(times[i]) ? L.T("нет ответа", "no answer") : Ms(times[i])))),
                () => down == 0 ? L.T("Время, за которое открывается соединение: чем меньше, тем быстрее начинают грузиться страницы и игры",
                                      "How long a connection takes to open: the less, the sooner pages and games start loading")
                    : L.T("«Нет ответа» — сервис недоступен из этой сети (блокировка, фильтр или сбой у провайдера); остальные работают",
                          "“No answer” — the service cannot be reached from this network (a block, a filter or a provider fault); the others work"));
        }

        // ---------- speed and delay under load ----------

        const string DownUrl = "https://speed.cloudflare.com/__down?bytes=", UpUrl = "https://speed.cloudflare.com/__up";

        void MeasureLoad()
        {
            if (!Online) return;
            if (Metered)
            {
                var fm = Add("speed", TuneCat.NetSpeed, "\uE896", () => L.T("Скорость и задержка под нагрузкой", "Speed and delay under load"));
                Done(fm, NetLevel.Info, () => L.T("пропущено: подключение лимитное", "skipped: the connection is metered"),
                    () => L.T("Замер скачал бы сотни мегабайт — на лимитном подключении DriveUP его не делает", "The test would download hundreds of megabytes — DriveUP skips it on a metered connection"));
                return;
            }
            Say(L.T("Скачиваю для замера скорости (до 1 ГБ) и слежу за задержкой…", "Downloading to measure speed (up to 1 GB) and watching the delay…"));
            var fd = Add("down", TuneCat.NetSpeed, "\uE896", () => L.T("Скачивание", "Download"));
            var fl = Add("bloat", TuneCat.NetSpeed, "\uE916", () => L.T("Задержка во время скачивания", "Delay while downloading"));
            fl.Chart = true;
            LoadDown = new Rtt();
            DownMbps = Load(true, fd, fl, LoadDown, 9, 1000L << 20);
            if (Cancel) return;
            // Without a load (the test failed) the delay "under load" means nothing.
            BloatDown = double.IsNaN(DownMbps) ? double.NaN : AddedDelay(LoadDown);
            Report(fd, fl, DownMbps, BloatDown, true);

            Say(L.T("Отправляю для замера отдачи (до 300 МБ)…", "Uploading to measure upload (up to 300 MB)…"));
            var fu = Add("up", TuneCat.NetSpeed, "\uE898", () => L.T("Отдача", "Upload"));
            var flu = Add("bloatup", TuneCat.NetSpeed, "\uE916", () => L.T("Задержка во время отдачи", "Delay while uploading"));
            flu.Chart = true;
            LoadUp = new Rtt();
            UpMbps = Load(false, fu, flu, LoadUp, 7, 300L << 20);
            if (Cancel) return;
            BloatUp = double.IsNaN(UpMbps) ? double.NaN : AddedDelay(LoadUp);
            Report(fu, flu, UpMbps, BloatUp, false);
        }

        /// <summary>How much the delay grows under load (median, ms), against the quiet one.</summary>
        double AddedDelay(Rtt loaded)
        {
            if (loaded.Received < 5 || Internet.Received == 0) return double.NaN;
            return Math.Max(0, loaded.Median - Internet.Median);
        }

        /// <summary>The bufferbloat grade (added delay under load), as Waveform's test grades it.</summary>
        public static string Grade(double added)
        {
            if (double.IsNaN(added)) return "";
            return added < 5 ? "A+" : added < 30 ? "A" : added < 60 ? "B" : added < 200 ? "C" : added < 400 ? "D" : "F";
        }

        void Report(NetFinding speed, NetFinding bloat, double mbps, double added, bool down)
        {
            Done(speed, double.IsNaN(mbps) ? NetLevel.Warn : mbps < (down ? 10 : 2) ? NetLevel.Warn : NetLevel.Info, () => Mbps(mbps),
                () =>
                {
                    if (double.IsNaN(mbps)) return L.T("Замер не удался: сервер speed.cloudflare.com не ответил", "The test failed: speed.cloudflare.com did not answer");
                    string s = L.T("Замер через speed.cloudflare.com, 4 потока", "Measured via speed.cloudflare.com, 4 streams");
                    if (Wifi != null && Wifi.RxKbps > 0 && down && mbps > Wifi.RxKbps / 1000.0 * 0.6)
                        s += L.T(". Упирается в скорость Wi-Fi (", ". Limited by the Wi-Fi link (") + LinkText(Wifi.RxKbps * 1000) + ")";
                    return s;
                });
            string g = Grade(added);
            var r = down ? LoadDown : LoadUp;
            // Loaded, and the answers stopped coming: the worst case of all, not "no figure".
            bool drowned = g == "" && !double.IsNaN(mbps) && r.Count >= 5 && r.Received < 5;
            NetLevel lv = drowned ? NetLevel.Bad : g == "" ? NetLevel.Info : g == "A+" || g == "A" ? NetLevel.Good : g == "B" ? NetLevel.Info : g == "C" ? NetLevel.Warn : NetLevel.Bad;
            if (drowned) { if (down) BloatDown = 1000; else BloatUp = 1000; }
            Done(bloat, lv, () => drowned ? L.T("ответы пропадают (оценка F)", "answers get lost (grade F)") : g == "" ? "—" : "+" + Ms(added) + L.T(" (оценка ", " (grade ") + g + ")" + L.T(" · потери ", " · loss ") + Pct(r.Loss),
                () =>
                {
                    if (g == "" && !drowned) return L.T("Замер скорости не удался — сравнить не с чем", "The speed test failed — nothing to compare with");
                    if (lv == NetLevel.Good) return L.T("Когда канал занят, задержка почти не растёт — игры и звонки не страдают от загрузок", "With the line busy the delay barely grows — downloads do not hurt games and calls");
                    if (lv == NetLevel.Info) return down ? L.T("Задержка немного растёт, пока что-то качается", "The delay grows a little while something downloads")
                                                         : L.T("Задержка немного растёт, пока что-то отправляется", "The delay grows a little while something uploads");
                    return L.T("Пока что-то качается, задержка сильно растёт (bufferbloat). Лечится в роутере: включите SQM / Smart Queue / QoS (fq_codel или cake) с лимитом ~90 % скорости тарифа. В Windows можно ограничить фоновые загрузки обновлений.",
                               "While something downloads the delay grows a lot (bufferbloat). The cure is in the router: turn on SQM / Smart Queue / QoS (fq_codel or cake) at ~90 % of your plan. Windows can cap background update downloads.");
                });
        }

        /// <summary>
        /// Loads the line with 4 streams for the given time (or until the cap) while pinging; returns Mbit/s from
        /// the moment it got going (the first 1.5 s are left out), or NaN.
        /// </summary>
        double Load(bool down, NetFinding speedRow, NetFinding chart, Rtt into, double seconds, long cap)
        {
            long bytes = 0, counted = 0;
            var sw = Stopwatch.StartNew();
            double from = 1.5, fromBytes = -1;
            bool stop = false;
            var reqs = new List<HttpWebRequest>();
            var threads = new List<Thread>();
            var payload = new byte[64 * 1024];
            new Random().NextBytes(payload);
            for (int i = 0; i < 4; i++)
            {
                var t = new Thread(() =>
                {
                    while (!stop && !Cancel)
                    {
                        HttpWebRequest req = null;
                        try
                        {
                            // The test server gives at most ~99 MB per request: requests follow one another until time is up.
                            long size = down ? 50000000L : 25000000L;
                            req = (HttpWebRequest)WebRequest.Create(down ? DownUrl + size : UpUrl);
                            req.UserAgent = "DriveUP network check";
                            req.Timeout = 15000;
                            req.ReadWriteTimeout = 15000;
                            req.AllowAutoRedirect = false;
                            req.ServicePoint.Expect100Continue = false;
                            lock (reqs) reqs.Add(req);
                            var buf = new byte[64 * 1024];
                            if (down)
                            {
                                using (var resp = req.GetResponse())
                                using (var s = resp.GetResponseStream())
                                {
                                    int n;
                                    while (!stop && !Cancel && (n = s.Read(buf, 0, buf.Length)) > 0) Interlocked.Add(ref bytes, n);
                                }
                            }
                            else
                            {
                                req.Method = "POST";
                                req.ContentType = "application/octet-stream";
                                req.ContentLength = size;
                                req.AllowWriteStreamBuffering = false;
                                req.SendChunked = false;
                                using (var s = req.GetRequestStream())
                                {
                                    long left = size;
                                    while (!stop && !Cancel && left > 0)
                                    {
                                        int n = (int)Math.Min(payload.Length, left);
                                        s.Write(payload, 0, n);
                                        left -= n;
                                        Interlocked.Add(ref bytes, n);
                                    }
                                }
                                if (!stop && !Cancel) using (req.GetResponse()) { }
                            }
                        }
                        catch { if (!stop) Thread.Sleep(200); }
                        finally { if (req != null) lock (reqs) reqs.Remove(req); }
                    }
                }) { IsBackground = true, Name = "net load", Priority = ThreadPriority.BelowNormal };   // TLS at full speed: the window stays smooth
                threads.Add(t);
                t.Start();
            }
            // Ping while loaded; the speed row shows the figure as it goes.
            // Above the load threads: a busy processor must not add its own delay to the figures.
            var pinger = new Thread(() => Sample(InternetProbe, into, chart, seconds + 1, 200, () => stop)) { IsBackground = true, Name = "net load ping", Priority = ThreadPriority.AboveNormal };
            pinger.Start();
            double mbps = double.NaN;
            while (!Cancel && sw.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(250);
                long b = Interlocked.Read(ref bytes);
                double t = sw.Elapsed.TotalSeconds;
                if (fromBytes < 0 && t >= from) { fromBytes = b; from = t; }
                if (fromBytes >= 0 && t - from > 0.5)
                {
                    mbps = (b - fromBytes) * 8 / 1e6 / (t - from);
                    double show = mbps;
                    speedRow.Value = () => Mbps(show) + L.T(" — измеряю…", " — measuring…");
                    if (Updated != null) Updated(speedRow);
                }
                if (b >= cap) break;
            }
            counted = Interlocked.Read(ref bytes);
            // A fast line hit the cap before the warm-up was over: the whole run, start included.
            if (double.IsNaN(mbps) && counted > 0 && !Cancel) mbps = counted * 8 / 1e6 / Math.Max(0.25, sw.Elapsed.TotalSeconds);
            stop = true;
            lock (reqs) foreach (var r in reqs.ToList()) try { r.Abort(); } catch { }
            foreach (var t in threads) t.Join(3000);
            pinger.Join(2000);
            return counted < 256 * 1024 ? double.NaN : mbps;
        }

        void MeasureQuiet()
        {
            if (!Online) return;
            var gw = Physical != null ? Physical.Gateway : null;
            var f = Add("quiet", TuneCat.NetLatency, "\uE916", () => L.T("Стабильность за 2 минуты", "Stability over 2 minutes"));
            f.Chart = true;
            f.Interval = 250;
            Quiet = new Rtt();
            // To the router over Wi-Fi (that is where scans hurt), otherwise to the internet.
            var p = Physical != null && Physical.Kind == LinkKind.WiFi && gw != null && Gateway.Received > 0 ? new Prober(gw, false) : InternetProbe;
            Sample(p, Quiet, f, 120, 250);
            if (Cancel) return;
            var spikes = Quiet.Spikes();
            // Spikes at a steady interval (Wi-Fi background scans come every ~60 s, some drivers 20–30 s).
            double period = 0;
            if (spikes.Count >= 3)
            {
                var gaps = new List<double>();
                for (int i = 1; i < spikes.Count; i++) if (spikes[i] - spikes[i - 1] > 3) gaps.Add(spikes[i] - spikes[i - 1]);
                if (gaps.Count >= 2)
                {
                    double avg = gaps.Average(), dev = Math.Sqrt(gaps.Average(g => (g - avg) * (g - avg)));
                    if (dev < avg * 0.2) period = avg;
                }
            }
            double loss = Quiet.Loss;
            NetLevel lv = loss >= 2 ? NetLevel.Bad : (loss >= 0.5 || period > 0 || spikes.Count > 6) ? NetLevel.Warn : NetLevel.Good;
            Done(f, lv, () => Ms(Quiet.Median) + L.T(" · всплесков: ", " · spikes: ") + spikes.Count + L.T(" · потери ", " · loss ") + Pct(loss) +
                              (period > 0 ? L.T(" · каждые ~", " · every ~") + Math.Round(period) + L.T(" с", " s") : ""),
                () =>
                {
                    if (lv == NetLevel.Good) return L.T("За 2 минуты связь не проседала", "The connection did not dip over 2 minutes");
                    if (period > 0 && Physical.Kind == LinkKind.WiFi)
                        return L.T("Регулярные всплески — это Wi-Fi ищет другие сети в фоне. Кабель убирает их полностью; ещё помогает закрыть программы, которые просят список сетей (определение местоположения, менеджеры Wi-Fi).",
                                   "Regular spikes are Wi-Fi scanning for other networks in the background. A cable removes them; closing apps that ask for the network list (location, Wi-Fi managers) helps too.");
                    return L.T("Связь иногда проседает или теряет пакеты: так бывает при слабом сигнале, загрузке сети другими устройствами или проблемах у провайдера",
                               "The connection dips or loses packets now and then: a weak signal, other devices loading the network or the provider");
                });
        }

        // ---------- Windows and the adapter ----------

        /// <summary>Power-saving keywords of network drivers: the standard one first, then the makers' own.</summary>
        public static readonly string[] SavingKeywords = { "*EEE", "EEE", "AdvancedEEE", "EnableGreenEthernet", "GigaLite", "PowerSavingMode", "EEELinkAdvertisement" };

        void ReadSettings()
        {
            var p = Physical;
            if (p != null && p.ClassKey != null)
                foreach (var kw in SavingKeywords.Concat(new[] { "*SpeedDuplex", "*RSS", "*InterruptModeration" }))
                {
                    var v = Reg.Get(true, p.ClassKey, kw) as string;
                    if (v != null) NicValues[kw] = v;
                }

            if (p != null && p.Kind == LinkKind.Ethernet)
            {
                var eee = SavingKeywords.Where(k => NicValues.ContainsKey(k) && NicValues[k] != "0").ToList();
                var f = Add("eee", TuneCat.NetSystem, "\uE83E", () => L.T("Энергосбережение Ethernet (EEE)", "Ethernet power saving (EEE)"));
                bool wiredTrouble = WiredTrouble;
                Done(f, eee.Count == 0 ? NetLevel.Good : wiredTrouble || p.IsIntelI225 ? NetLevel.Warn : NetLevel.Info,
                    () => eee.Count == 0 ? L.T("выключено", "off") : L.T("включено", "on") + " (" + string.Join(", ", eee) + ")",
                    () => eee.Count == 0 ? L.T("Карта не засыпает между пакетами", "The card does not doze between packets")
                        : p.IsIntelI225 ? L.T("На Intel I225/I226 оно вызывает обрывы и подвисания — Intel советует его выключить", "On Intel I225/I226 it causes drops and stutter — Intel advises turning it off")
                        : wiredTrouble ? L.T("При обрывах и потерях по кабелю его выключение — проверенное средство", "With drops and loss over a cable, turning it off is a proven fix")
                        : L.T("На исправной сети задержку не добавляет — трогать незачем", "On a healthy network it adds no delay — no need to touch it"));
                string sd;
                if (NicValues.TryGetValue("*SpeedDuplex", out sd) && sd != "0")
                {
                    var fs = Add("duplex", TuneCat.NetSystem, "\uE839", () => L.T("Скорость и дуплекс карты", "Card speed and duplex"));
                    Done(fs, NetLevel.Warn, () => L.T("задано вручную", "set by hand") + " (" + sd + ")",
                        () => L.T("Если вторая сторона в «Авто», возможен полудуплекс с потерями и скорость ниже гигабита. Почти всегда правильно «Авто».",
                                  "If the other side is on Auto this can end in half duplex with loss and less than gigabit. Auto is almost always right."));
                }
            }

            if (p != null && p.Kind == LinkKind.WiFi)
            {
                var f = Add("wifipower", TuneCat.NetSystem, "\uE83E", () => L.T("Энергосбережение Wi-Fi от сети", "Wi-Fi power saving on mains"));
                uint ac = 99;
                try { ac = PowerApi.Read(PowerApi.Active, NetFix.SubWireless, NetFix.WirelessSaving, true); } catch { }
                if (ac == 99)
                    Done(f, NetLevel.Info, () => L.T("настройки нет в схеме питания", "not in the power plan"),
                        () => L.T("На ноутбуках с современным режимом ожидания Windows управляет этим сама", "On laptops with modern standby Windows manages it itself"));
                else
                    Done(f, ac == 0 ? NetLevel.Good : WifiTrouble ? NetLevel.Warn : NetLevel.Info,
                        () => ac == 0 ? L.T("максимальная производительность", "maximum performance") : ac == 3 ? L.T("максимальное энергосбережение", "maximum power saving") : L.T("энергосбережение", "power saving"),
                        () => ac == 0 ? L.T("Адаптер не дремлет, пока компьютер от сети", "The adapter does not doze while on mains")
                            : L.T("Адаптер дремлет между пакетами — это может давать скачки задержки. От сети экономить незачем.", "The adapter dozes between packets — this can make the delay jump. On mains there is nothing to save."));
            }

            // TCP receive window tuning and RSS: off only where a "tweaker" turned them off.
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT AutoTuningLevelLocal FROM MSFT_NetTCPSetting WHERE SettingName='Internet'"))
                    foreach (ManagementObject o in s.Get())
                        using (o) AutoTuning = NetFix.TuningName(Convert.ToInt32(o["AutoTuningLevelLocal"], CultureInfo.InvariantCulture));
            }
            catch { }
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT ReceiveSideScaling FROM MSFT_NetOffloadGlobalSetting"))
                    foreach (ManagementObject o in s.Get())
                        using (o) RssOff = Convert.ToInt32(o["ReceiveSideScaling"], CultureInfo.InvariantCulture) == 0;
            }
            catch { }
            string rssNic;
            bool nicRssOff = NicValues.TryGetValue("*RSS", out rssNic) && rssNic == "0";
            var ft = Add("tcp", TuneCat.NetSystem, "\uE713", () => L.T("Настройки TCP Windows", "Windows TCP settings"));
            bool tcpOk = (AutoTuning == "" || AutoTuning == "normal") && !RssOff && !nicRssOff;
            Done(ft, tcpOk ? NetLevel.Good : NetLevel.Warn,
                () => L.T("автонастройка окна: ", "window auto-tuning: ") + (AutoTuning == "" ? "?" : AutoTuning) + " · RSS: " + (RssOff || nicRssOff ? L.T("выключен", "off") : L.T("включён", "on")),
                () => tcpOk ? L.T("Как задумано в Windows — так и должно быть", "As Windows intends — as it should be")
                    : L.T("Изменено «ускорителем»: без автонастройки скорость на быстрых линиях падает в разы, без RSS — нагружается одно ядро. Вернуть как в Windows — ниже.",
                          "Changed by a \"booster\": without auto-tuning fast lines lose most of their speed, without RSS one core does all the work. Back to Windows defaults — below."));

            // "Network boosters" that shape traffic.
            foreach (var svc in NetFix.FindOptimizers()) Optimizers.Add(svc);
            if (Optimizers.Count > 0)
            {
                var fo = Add("optimizers", TuneCat.NetSystem, "\uE7BA", () => L.T("Программы-«ускорители» сети", "Network \"booster\" programs"));
                Done(fo, NetLevel.Warn, () => string.Join(", ", Optimizers.Select(o => o.Item2)),
                    () => L.T("Они перехватывают трафик ради «приоритетов» и известны тем, что режут скорость и добавляют задержку (Dell SmartByte, Killer)",
                              "They intercept traffic for \"priorities\" and are known for cutting speed and adding delay (Dell SmartByte, Killer)"));
            }

            // Proxy for the person at the desktop.
            try
            {
                const string Inet = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
                if (Reg.Dword(false, Inet, "ProxyEnable") == 1)
                {
                    string server = Reg.Get(false, Inet, "ProxyServer") as string ?? "";
                    var fp = Add("proxy", TuneCat.NetSystem, "\uE774", () => L.T("Прокси-сервер", "Proxy server"));
                    bool alive = ProxyAlive(server);
                    Done(fp, alive ? NetLevel.Info : NetLevel.Bad, () => server,
                        () => alive ? L.T("Браузеры ходят в интернет через прокси — задержка и скорость зависят от него", "Browsers go online through the proxy — delay and speed depend on it")
                            : L.T("Прокси не отвечает: браузеры не откроют сайты. Если вы его не включали, выключите: Параметры → Сеть и Интернет → Прокси.",
                                  "The proxy does not answer: browsers will not open sites. If you did not set it, turn it off: Settings → Network & internet → Proxy."));
                }
            }
            catch { }

            // The driver.
            if (p != null && p.DriverDate > DateTime.MinValue)
            {
                double years = (DateTime.Now - p.DriverDate).TotalDays / 365.25;
                bool inbox = p.Provider.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
                var fd = Add("driver", TuneCat.NetSystem, "\uE7F8", () => L.T("Драйвер сетевой карты", "Network driver"));
                NetLevel lv = years > 3 ? NetLevel.Warn : inbox && !p.Virtual ? NetLevel.Info : NetLevel.Good;
                Done(fd, lv, () => p.Provider + " " + p.DriverVersion + " · " + p.DriverDate.ToString("d", CultureInfo.CurrentCulture),
                    () => lv == NetLevel.Good ? L.T("Свежий драйвер", "A recent driver")
                        : inbox ? L.T("Стоит стандартный драйвер Windows: у производителя карты он обычно стабильнее и быстрее", "Windows' generic driver is in use: the maker's own is usually more stable and faster")
                        : L.T("Драйверу больше 3 лет: новые версии часто исправляют обрывы и задержки (так Intel лечила I225/I226)", "The driver is over 3 years old: newer ones often fix drops and delay (that is how Intel fixed I225/I226)"));
                if (lv != NetLevel.Good)
                {
                    fd.ActionText = () => L.T("Обновления", "Updates");   // the page opens the Updates tab
                }
            }
        }

        static bool ProxyAlive(string server)
        {
            // "host:port" or "http=host:port;https=…"
            string first = server.Split(';')[0];
            int eq = first.IndexOf('=');
            if (eq >= 0) first = first.Substring(eq + 1);
            int colon = first.LastIndexOf(':');
            int port;
            if (colon <= 0 || !int.TryParse(first.Substring(colon + 1), out port)) return true;
            try
            {
                var ips = Dns.GetHostAddresses(first.Substring(0, colon));
                return ips.Any(ip => !double.IsNaN(Prober.Handshake(ip, port, 1500)));
            }
            catch { return false; }
        }

        /// <summary>Signs of a wired link in trouble: loss or jitter to the router, broken packets.</summary>
        public bool WiredTrouble
        {
            get
            {
                if (Physical == null || Physical.Kind != LinkKind.Ethernet) return false;
                return (Gateway != null && Gateway.Received > 0 && (Gateway.Loss >= 1 || Gateway.Jitter > 2 || Gateway.Pct(95) > 10)) ||
                       (RxErrors > 20 && RxPackets > 0 && RxErrors * 100.0 / RxPackets > 0.01);
            }
        }

        /// <summary>Wi-Fi that jumps or loses packets on the way to the router.</summary>
        public bool WifiTrouble
        {
            get
            {
                if (Physical == null || Physical.Kind != LinkKind.WiFi) return false;
                if (Gateway != null && Gateway.Received > 0 && (Gateway.Loss >= 1 || Gateway.Jitter > 10 || Gateway.Pct(95) > 60)) return true;
                return Quiet != null && Quiet.Received > 0 && (Quiet.Loss >= 0.5 || Quiet.Spikes().Count > 6);
            }
        }

        /// <summary>The delay grows a lot under load (grade C or worse).</summary>
        public bool Bloated
        {
            get { return (!double.IsNaN(BloatDown) && BloatDown >= 60) || (!double.IsNaN(BloatUp) && BloatUp >= 60); }
        }
    }

    /// <summary>Whether Windows counts the connection as metered (INetworkCostManager).</summary>
    static class NetCost
    {
        [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface INetworkCostManager
        {
            void GetCost(out uint cost, IntPtr destIPAddr);
        }

        public static bool Metered()
        {
            try
            {
                var t = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
                var mgr = (INetworkCostManager)Activator.CreateInstance(t);
                try
                {
                    uint cost;
                    mgr.GetCost(out cost, IntPtr.Zero);
                    // NLM_CONNECTION_COST_UNRESTRICTED = 1; FIXED / VARIABLE / over the limit / roaming are metered.
                    return (cost & 0x1) == 0 && cost != 0;
                }
                finally { Marshal.ReleaseComObject(mgr); }
            }
            catch { return false; }
        }
    }
}
