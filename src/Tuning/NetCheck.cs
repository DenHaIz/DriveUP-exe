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
        /// <summary>The details panel: what it means in full, how it was measured (with the norms), and what to do.</summary>
        public Func<string> More, How;
        public readonly List<NetAdvice> Advice = new List<NetAdvice>();
        /// <summary>A page the row's button opens (instead of a tab of DriveUP).</summary>
        public string Url;

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

    /// <summary>
    /// One way to deal with a finding: what to do and why; a command an administrator can run (copied from the
    /// details panel), a page to open, or a mark that DriveUP itself can do it (among the fixes below the report).
    /// </summary>
    class NetAdvice
    {
        readonly string ru, en, ruText, enText;
        public string Command, Url;
        public bool Auto;

        public NetAdvice(string ru, string en, string ruText, string enText)
        {
            this.ru = ru;
            this.en = en;
            this.ruText = ruText;
            this.enText = enText;
        }

        public string Title { get { return L.T(ru, en); } }
        public string Text { get { return L.T(ruText, enText); } }
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
        /// <summary>DOT11_AUTH_ALGORITHM and DOT11_CIPHER_ALGORITHM of the connection (0 = unknown / open).</summary>
        public int Auth, Cipher;
        public bool Secured;

        /// <summary>WEP or TKIP: 802.11n and newer fall back to 54 Mbit/s with them.</summary>
        public bool WeakCipher { get { return Cipher == 1 || Cipher == 2 || Cipher == 5 || Cipher == 0x101; } }

        public string Security
        {
            get
            {
                if (!Secured) return L.T("без пароля", "open");
                string a;
                switch (Auth)
                {
                    case 3: case 4: a = "WPA"; break;
                    case 6: case 7: a = "WPA2"; break;
                    case 8: case 9: case 11: a = "WPA3"; break;
                    case 10: a = "OWE"; break;
                    case 1: case 2: a = "WEP"; break;
                    default: a = "?"; break;
                }
                string c;
                switch (Cipher)
                {
                    // DOT11_CIPHER_ALGORITHM: WEP40 1, TKIP 2, CCMP 4, WEP104 5, GCMP 8, GCMP-256 9, CCMP-256 0x0A, WEP 0x101.
                    case 1: case 5: case 0x101: c = "WEP"; break;
                    case 2: c = "TKIP"; break;
                    case 4: case 0x0A: c = "AES"; break;
                    case 8: case 9: c = "GCMP"; break;
                    default: c = ""; break;
                }
                return c.Length > 0 && c != a ? a + " (" + c + ")" : a;
            }
        }
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
                    // WLAN_SECURITY_ATTRIBUTES right after: enabled, 802.1X, auth algorithm, cipher.
                    w.Secured = Marshal.ReadInt32(data, 588) != 0;
                    w.Auth = Marshal.ReadInt32(data, 596);
                    w.Cipher = Marshal.ReadInt32(data, 600);
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
        /// <summary>Names nobody asked before (made up under big zones): the server has to look them up itself.</summary>
        public readonly List<double> Cold = new List<double>();
        public double ColdMedian
        {
            get
            {
                var s = Cold.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
                return s.Count == 0 ? double.NaN : s[s.Count / 2];
            }
        }
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
        /// <summary>
        /// Under load, also TCP handshakes to the same host (where pings are used): routers answer pings last when busy,
        /// so lost pings alone do not prove that real traffic is lost.
        /// </summary>
        public Rtt LoadDownTcp, LoadUpTcp;
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
        /// <summary>TCP segments sent again while uploading (% of those sent then): loss on the way out.</summary>
        public double Retransmit = double.NaN;
        /// <summary>The adapter's own counters: since it came up, and during the speed test.</summary>
        public NicCounters NicStart, NicLoadFrom, NicLoadTo;
        public readonly List<NetFinding> Findings = new List<NetFinding>();
        public int PathMtu;
        /// <summary>Reconnections of the real link over the last 7 days (not counting start-up and sleep), and when.</summary>
        public readonly List<DateTime> Drops = new List<DateTime>();
        public int IpConflicts;
        public bool? FullDuplex;
        public string Congestion = "";
        /// <summary>Tcpip parameters a "tweaker" left that hurt: (name, value).</summary>
        public readonly List<Tuple<string, int>> TcpLeftovers = new List<Tuple<string, int>>();
        /// <summary>Network services of Windows that were disabled: (service, its usual start type).</summary>
        public readonly List<Tuple<string, string>> DeadServices = new List<Tuple<string, string>>();
        public bool CgNat, DoubleNat;
        public bool JumboBroken, UsbAdapter, DiscardsUnderLoad, RxAtMax;
        NetFinding BloatDownRow, BloatUpRow;
        public string Colo = "";
        /// <summary>A newer driver for the card that DriveUP found (catalog, maker's site, Windows Update), or null.</summary>
        public string NewerDriver;
        public bool DriverLookedUp;
        /// <summary>TCP while the check only pinged: each program's sends and resends, and Windows' totals.</summary>
        public List<TcpShare> IdleShares = new List<TcpShare>();
        public long IdleSent, IdleResent;
        /// <summary>DriveUP's own upload: segments and resends (per connection where Windows allows, else the totals).</summary>
        public long UpSent, UpResent;
        /// <summary>How the per-connection counting went (diagnostics for tests).</summary>
        public string TcpDiag = "";

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

        /// <summary>The details panel of a row: the full story, how it was measured, and the ways out.</summary>
        static void Explain(NetFinding f, Func<string> more, Func<string> how, params NetAdvice[] advice)
        {
            f.More = more;
            f.How = how;
            f.Advice.Clear();
            f.Advice.AddRange(advice.Where(a => a != null));
        }

        static NetAdvice Tip(string ru, string en, string ruText, string enText, string command = null, string url = null, bool auto = false)
        {
            return new NetAdvice(ru, en, ruText, enText) { Command = command, Url = url, Auto = auto };
        }

        /// <summary>A grade letter as a rank (A+ = 0 … F = 5), and back: the worse of two grades wins.</summary>
        static readonly string[] Grades = { "A+", "A", "B", "C", "D", "F" };
        static string Worse(string a, string b) { return Array.IndexOf(Grades, a) >= Array.IndexOf(Grades, b) ? a : b; }

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
            NicStart = NicCounters.Read(p.Nic);
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT FullDuplex FROM MSFT_NetAdapter WHERE DeviceID='" + p.Guid.Replace("'", "") + "'"))
                    foreach (ManagementObject o in s.Get())
                        using (o) { var v = o["FullDuplex"]; if (v is bool) FullDuplex = (bool)v; }
            }
            catch { }
            bool half = p.Kind == LinkKind.Ethernet && FullDuplex == false;
            Func<string> value = () => kind + (p.Speed > 0 && p.Kind == LinkKind.Ethernet ? " · " + LinkText(p.Speed) : "") +
                                       (half ? L.T(" · полудуплекс", " · half duplex") : "") + " · " + p.Description;
            Explain(link,
                () => L.T("Через какой адаптер компьютер выходит в интернет и на какой скорости он договорился с роутером или коммутатором. " +
                          "Скорость соединения — потолок для всего остального: на 100 Мбит/с порте гигабитный тариф не получить.",
                          "Which adapter the computer goes online through and the speed it agreed with the router or switch. " +
                          "The link speed caps everything else: a gigabit plan does not fit through a 100 Mbit/s port."),
                () => L.T("Адаптер — тот, через который Windows шлёт пакеты к 1.1.1.1 (GetBestInterface); при VPN — реальный адаптер под ним. " +
                          "Скорость и дуплекс — со слов драйвера (MSFT_NetAdapter). Норма по кабелю: 1 Гбит/с и выше, полный дуплекс.",
                          "The adapter is the one Windows sends packets to 1.1.1.1 through (GetBestInterface); with a VPN, the real adapter under it. " +
                          "Speed and duplex are as the driver reports them (MSFT_NetAdapter). Normal over a cable: 1 Gbit/s or more, full duplex."),
                Tip("Посмотреть, что видит Windows", "See what Windows sees",
                    "Скорость, дуплекс и состояние всех адаптеров:", "Speed, duplex and state of every adapter:",
                    "Get-NetAdapter | Format-Table Name, InterfaceDescription, Status, LinkSpeed, FullDuplex -AutoSize"));
            if (half)
            {
                Done(link, NetLevel.Bad, value, () => L.T("Полудуплекс: карта и роутер передают по очереди — потери и низкая скорость. Почти всегда скорость задана вручную на одной из сторон.",
                                                          "Half duplex: the card and the router take turns — loss and low speed. Almost always the speed is set by hand on one side."));
                link.Advice.Insert(0, Tip("Вернуть автосогласование", "Back to auto-negotiation",
                    "Скорость и дуплекс должны быть «Авто» и на карте, и на порте роутера/коммутатора. Если на карте задано вручную — DriveUP вернёт «Авто» (ниже, в исправлениях).",
                    "Speed and duplex must be Auto both on the card and on the router/switch port. If the card has it set by hand, DriveUP sets Auto back (below, among the fixes).",
                    null, null, true));
                link.Advice.Insert(1, Tip("Проверить кабель", "Check the cable",
                    "Если везде «Авто», а дуплекс всё равно половинный — кабель или разъём: попробуйте другой кабель (Cat5e/Cat6) и другой порт.",
                    "If both are on Auto and duplex is still half, it is the cable or a plug: try another cable (Cat5e/Cat6) and another port."));
            }
            else if (p.Kind == LinkKind.Ethernet && p.Speed > 0 && p.Speed < 1000000000L)
            {
                bool fast = Regex.IsMatch(p.Description, @"Gigabit|GbE|2\.5G|5G|10G|I21[019]|I22[56]|I219|RTL81(1[18]|25|26)|Killer E[23]|AQtion|Marvell|E3[01]00", RegexOptions.IgnoreCase);
                Done(link, p.Speed <= 10000000 ? NetLevel.Bad : NetLevel.Warn, value, () => fast
                    ? L.T("Карта гигабитная, а связь установилась на ", "The card is gigabit, but the link came up at ") + LinkText(p.Speed) +
                      L.T(" — обычно виноват кабель (нужен Cat5e или лучше, все 8 жил), разъём или порт роутера. Если в настройках карты скорость задана вручную, DriveUP вернёт «Авто».",
                          " — usually the cable (Cat5e or better, all 8 wires), a plug or the router port. If the card's speed is set by hand, DriveUP sets it back to Auto.")
                    : L.T("Связь установилась на ", "The link came up at ") + LinkText(p.Speed) +
                      L.T(": если тариф быстрее, проверьте кабель и порт роутера.", ": if your plan is faster, check the cable and the router port."));
                link.Advice.Insert(0, Tip("Кабель и разъёмы", "Cable and plugs",
                    "100 Мбит/с вместо гигабита — классический признак того, что из 4 пар кабеля работают 2: перебит кабель, плохо обжат разъём (жилы 4-5 и 7-8) или порт старый. Замените патч-корд на Cat5e/Cat6 и включите в другой порт роутера.",
                    "100 Mbit/s instead of gigabit is the classic sign that only 2 of the cable's 4 pairs work: a damaged cable, a badly crimped plug (wires 4-5 and 7-8) or an old port. Replace the patch cord with Cat5e/Cat6 and use another router port."));
                link.Advice.Insert(1, Tip("Скорость карты — «Авто»", "Card speed on Auto",
                    "Если в свойствах карты скорость задана вручную, DriveUP вернёт «Авто» (ниже, в исправлениях). Порт коммутатора тоже должен быть в «Авто».",
                    "If the card's speed is set by hand, DriveUP sets Auto back (below, among the fixes). The switch port must be on Auto as well.",
                    null, null, true));
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
                Explain(both,
                    () => L.T("Оба подключения активны, но маршрут в интернет идёт через Wi-Fi. Windows выбирает адаптер по метрике маршрута: у проводного она обычно меньше, поэтому так бывает, когда у кабеля нет выхода в интернет или метрики заданы вручную.",
                              "Both connections are up, but the route to the internet goes over Wi-Fi. Windows picks the adapter by route metric; the wired one is normally preferred, so this happens when the cable has no way to the internet or the metrics were set by hand."),
                    () => L.T("Маршрут к 1.1.1.1 (GetBestInterface) и шлюзы адаптеров.", "The route to 1.1.1.1 (GetBestInterface) and the adapters' gateways."),
                    Tip("Сравнить метрики", "Compare the metrics", "Меньшая метрика выигрывает. «Автоматическая метрика» обычно ставит кабель первым:", "The lower metric wins. \"Automatic metric\" normally puts the cable first:",
                        "Get-NetIPInterface -AddressFamily IPv4 | Sort-Object InterfaceMetric | Format-Table InterfaceAlias, InterfaceMetric, AutomaticMetric, ConnectionState"),
                    Tip("Вернуть автоматическую метрику", "Back to the automatic metric", "Если метрику кто-то задал вручную:", "If someone set the metric by hand:",
                        "Set-NetIPInterface -InterfaceAlias '" + wired.Name.Replace("'", "''") + "' -AutomaticMetric Enabled"));
            }

            if (Vpn)
            {
                var vpn = Add("vpn", TuneCat.NetLink, "\uE72E", () => L.T("Трафик идёт через VPN", "Traffic goes through a VPN"));
                Done(vpn, NetLevel.Info, () => Main.Description,
                    () => L.T("Задержка и скорость ниже измерены вместе с VPN: без него они могут быть лучше. DNS и сетевые настройки при VPN DriveUP не меняет.",
                              "The delay and speed below include the VPN: without it they can be better. With a VPN DriveUP does not change DNS or network settings."));
                Explain(vpn,
                    () => L.T("Маршрут в интернет идёт через виртуальный адаптер VPN. Всё, что измерено ниже (задержка, скорость, DNS), — вместе с VPN: шифрование, путь до его сервера и его загрузка входят в цифры. Чтобы понять, виновата ли сама сеть, повторите проверку с выключенным VPN.",
                              "The route to the internet goes through the VPN's virtual adapter. Everything below (delay, speed, DNS) includes the VPN: encryption, the way to its server and its load are in the figures. To tell whether the network itself is at fault, check again with the VPN off."),
                    () => L.T("Адаптер маршрута к 1.1.1.1 — виртуальный (WireGuard, Wintun, TAP, OpenVPN и т. п.).", "The adapter of the route to 1.1.1.1 is virtual (WireGuard, Wintun, TAP, OpenVPN and the like)."),
                    Tip("Сравнить без VPN", "Compare without the VPN", "Отключите VPN и нажмите «Проверить снова»: разница в задержке и скорости — цена VPN.",
                        "Turn the VPN off and press Check again: the difference in delay and speed is what the VPN costs."));
            }

            if (Wifi != null) ReadWifi();
            ReadDrops(p);
        }

        /// <summary>Reconnections over the last week, from Windows' own log.</summary>
        void ReadDrops(NetAdapter p)
        {
            try { Drops.AddRange(NetEvents.Reconnects(p.Guid, 7)); } catch { }
            try { IpConflicts = NetEvents.IpConflicts(7); } catch { }
            var f = Add("drops", TuneCat.NetLink, "\uE9D9", () => L.T("Обрывы связи за неделю", "Connection drops this week"));
            int n = Drops.Count;
            // A laptop also joins other networks (home, office, a phone): those count too, so it takes more to worry.
            int warnAt = Pc.Laptop ? 5 : 3, badAt = Pc.Laptop ? 15 : 10;
            NetLevel lv = n >= badAt ? NetLevel.Bad : n >= warnAt ? NetLevel.Warn : n > 0 ? NetLevel.Info : NetLevel.Good;
            if (IpConflicts > 0 && lv < NetLevel.Warn) lv = NetLevel.Warn;
            bool wifi = p.Kind == LinkKind.WiFi;
            Done(f, lv,
                () => (n == 0 ? L.T("не было", "none") : L.Count(n, "раз", "раза", "раз", "time", "times") + L.T(" · последний ", " · last ") + Drops[n - 1].ToString("g", CultureInfo.CurrentCulture)) +
                      (IpConflicts > 0 ? L.T(" · конфликт IP-адреса: ", " · IP address conflict: ") + IpConflicts : ""),
                () =>
                {
                    if (IpConflicts > 0) return L.T("Другое устройство в сети занимает тот же IP-адрес — связь будет пропадать, пока это не исправить", "Another device on the network has the same IP address — the connection will keep dropping until that is fixed");
                    if (n == 0) return L.T("Подключение не пропадало (включения и сон не в счёт)", "The connection did not drop (start-ups and sleep do not count)");
                    if (n < warnAt) return Pc.Laptop
                        ? L.T("Несколько переподключений — так бывает при переходе между сетями или перезагрузке роутера", "A few reconnections — usual when moving between networks or when the router restarts")
                        : L.T("Пару раз переподключалось — так бывает, если кабель вынимали или роутер перезагружался", "It reconnected a couple of times — usual if the cable was pulled or the router restarted");
                    return wifi ? L.T("Wi-Fi регулярно отваливается: слабый сигнал, энергосбережение адаптера или драйвер", "Wi-Fi keeps dropping: a weak signal, the adapter's power saving or the driver")
                                : L.T("Кабельное подключение регулярно пропадает: кабель, разъём, энергосбережение карты или драйвер", "The wired link keeps dropping: the cable, a plug, the card's power saving or the driver");
                });
            var tips = new List<NetAdvice>();
            if (IpConflicts > 0)
                tips.Add(Tip("Убрать конфликт адресов", "Clear the address conflict",
                    "Обычно виноват адрес, заданный вручную на другом устройстве (принтер, камера, второй роутер) из диапазона, который раздаёт роутер. Задайте таким устройствам адреса вне диапазона DHCP или зарезервируйте их на роутере.",
                    "Usually a device with a hand-set address (a printer, a camera, a second router) inside the range the router hands out. Give such devices addresses outside the DHCP range or reserve them in the router.",
                    "Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Tcpip'; Id=4199} -MaxEvents 5 | Format-List TimeCreated, Message"));
            if (n >= warnAt && !wifi)
            {
                tips.Add(Tip("Кабель, разъёмы, порт", "Cable, plugs, port",
                    "Чаще всего обрывы по кабелю — физика: перегнутый или пережатый кабель, разболтанный разъём, плохой порт. Замените патч-корд и включите в другой порт роутера или коммутатора.",
                    "Most wired drops are physical: a kinked or pinched cable, a loose plug, a bad port. Replace the patch cord and use another port on the router or switch."));
                tips.Add(Tip("Энергосбережение карты", "The card's power saving",
                    "Energy-Efficient Ethernet / «Зелёный Ethernet» и разрешение Windows отключать карту ради экономии энергии — известная причина обрывов у Realtek и Intel I225/I226. DriveUP выключит их (ниже, в исправлениях).",
                    "Energy-Efficient Ethernet / Green Ethernet and Windows being allowed to turn the card off to save power are a known cause of drops on Realtek and Intel I225/I226. DriveUP turns them off (below, among the fixes).",
                    null, null, true));
            }
            if (n >= warnAt && wifi)
            {
                tips.Add(Tip("Отчёт Windows о Wi-Fi", "Windows' Wi-Fi report",
                    "Windows сама пишет подробный отчёт о каждом отключении Wi-Fi за 3 дня, с причиной (роуминг, сигнал, драйвер). Откройте созданный HTML-файл:",
                    "Windows writes a detailed report of every Wi-Fi disconnection over 3 days, with the reason (roaming, signal, driver). Open the HTML file it makes:",
                    "netsh wlan show wlanreport"));
                tips.Add(Tip("Энергосбережение адаптера", "The adapter's power saving",
                    "От сети адаптер не должен дремать между пакетами — DriveUP переключит схему питания Wi-Fi на максимальную производительность (ниже, в исправлениях).",
                    "On mains the adapter should not doze between packets — DriveUP sets the Wi-Fi power plan to maximum performance (below, among the fixes).",
                    null, null, true));
            }
            if (n >= warnAt)
                tips.Add(Tip("Драйвер", "The driver", "Новые драйверы часто лечат именно обрывы. Строка «Драйвер сетевой карты» ниже скажет, нашёл ли DriveUP версию новее.",
                    "New drivers often fix exactly this. The \"Network driver\" row below says whether DriveUP found a newer version."));
            tips.Add(Tip("Посмотреть сами события", "See the events themselves",
                "Каждое новое подключение адаптера — событие 4001 (состояние 0) в журнале NetworkProfile:",
                "Every new connection of the adapter is event 4001 (state 0) in the NetworkProfile log:",
                "Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-NetworkProfile/Operational'; Id=4001; StartTime=(Get-Date).AddDays(-7)} | Format-Table TimeCreated, Message -Wrap"));
            Explain(f,
                () => L.T("Сколько раз за последние 7 дней подключение пропадало и появлялось снова. Включения, перезагрузки, выход из сна и из современного режима ожидания не считаются — при них адаптер подключается заново всегда. " +
                          "Короткий обрыв рвёт звонки, игровые сессии и загрузки, даже если интернет потом «сам вернулся».",
                          "How many times over the last 7 days the connection went away and came back. Start-ups, restarts, waking from sleep and from modern standby do not count — the adapter always connects anew then. " +
                          "A short drop breaks calls, game sessions and downloads, even if the internet \"came back by itself\"."),
                () => L.T("Журнал Microsoft-Windows-NetworkProfile/Operational: событие 4001 (адаптер опознаёт сеть) для этого адаптера; события в первые 4 минуты после загрузки (Kernel-General 12), выхода из сна (Kernel-Power 107, Power-Troubleshooter 1) и ожидания (Kernel-Power 507) пропускаются. " +
                          "Конфликты адресов — Tcpip 4199 в журнале «Система». Норма: 0. Кабель, вынутый руками, и переход между сетями Wi-Fi тоже попадут в счёт.",
                          "The Microsoft-Windows-NetworkProfile/Operational log: event 4001 (the adapter identifies its network) for this adapter; events in the first 4 minutes after start-up (Kernel-General 12), resume (Kernel-Power 107, Power-Troubleshooter 1) and standby (Kernel-Power 507) are left out. " +
                          "Address conflicts are Tcpip 4199 in the System log. Normal: 0. A cable pulled by hand and moving between Wi-Fi networks count too."),
                tips.ToArray());
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
            var tips = new List<NetAdvice>();
            if (lv != NetLevel.Good)
            {
                tips.Add(Tip("Ближе к роутеру или без преград", "Closer to the router, or without obstacles",
                    "Каждая несущая стена отнимает 10–20 дБ, зеркала, аквариумы и металлические шкафы — ещё больше. Роутер лучше ставить высоко и в центре квартиры, а не в углу или в шкафу.",
                    "Every load-bearing wall takes 10–20 dB; mirrors, aquariums and metal cabinets more. A router works best high up and in the middle of the home, not in a corner or a cupboard."));
                tips.Add(Tip("Mesh или точка доступа по кабелю", "Mesh or a wired access point",
                    "Если до роутера далеко, повторитель «по воздуху» режет скорость вдвое. Лучше mesh-система с проводной связью между узлами или вторая точка доступа по кабелю.",
                    "If the router is far away, a wireless repeater halves the speed. A mesh system with wired backhaul, or a second access point on a cable, is better."));
            }
            tips.Add(Tip("Подробности соединения", "Connection details",
                "Сигнал, канал, скорость приёма и передачи, тип шифрования — как их видит Windows:", "Signal, channel, receive and transmit rates, security — as Windows sees them:",
                "netsh wlan show interfaces"));
            Explain(sig,
                () => L.T("Уровень сигнала от роутера в месте, где стоит компьютер. Для веб-сёрфинга хватает −75 дБм, для видеозвонков и игр нужно −67 дБм и сильнее: при слабом сигнале адаптер снижает скорость и повторяет пакеты, отсюда скачки задержки. " +
                          "Скорость соединения — то, о чём адаптер договорился с роутером: реальная скорость обычно вдвое ниже.",
                          "How strong the router's signal is where the computer stands. Browsing is fine at −75 dBm; video calls and games want −67 dBm or stronger: with a weak signal the adapter slows down and repeats packets, and the delay jumps. " +
                          "The link rate is what the adapter agreed with the router: the real speed is usually half of it."),
                () => L.T("WLAN API Windows (WlanQueryInterface): RSSI, качество сигнала, частота, стандарт, скорость соединения; соседние сети — из последнего сканирования (новое не запускается). " +
                          "Нормы: от −67 дБм — отлично, до −72 — нормально, до −80 — слабо, ниже — плохо.",
                          "Windows' WLAN API (WlanQueryInterface): RSSI, signal quality, frequency, standard, link rate; neighbour networks from the last scan (no new scan is started). " +
                          "Norms: −67 dBm or better — great, down to −72 — fine, down to −80 — weak, below — poor."),
                tips.ToArray());

            // A cipher that holds the whole link back, or none at all.
            if (w.WeakCipher || !w.Secured)
            {
                var fs = Add("wifisec", TuneCat.NetLink, "\uE72E", () => L.T("Защита Wi-Fi", "Wi-Fi security"));
                bool weak = w.WeakCipher;
                Done(fs, weak ? NetLevel.Warn : NetLevel.Info, () => w.Security,
                    () => weak ? L.T("Устаревшее шифрование (WEP/TKIP) ограничивает Wi-Fi 54 Мбит/с — даже на Wi-Fi 5/6", "Outdated encryption (WEP/TKIP) caps Wi-Fi at 54 Mbit/s — even on Wi-Fi 5/6")
                               : L.T("Сеть без пароля: трафик видят все рядом", "An open network: everyone nearby sees the traffic"));
                Explain(fs,
                    () => weak ? L.T("Стандарты 802.11n и новее (Wi-Fi 4–7) не разрешают высокие скорости с шифрованием WEP и TKIP: адаптер и роутер откатываются к 802.11g, 54 Мбит/с. Кроме того, WEP и TKIP давно взломаны.",
                                     "802.11n and newer (Wi-Fi 4–7) do not allow high rates with WEP or TKIP: the adapter and router fall back to 802.11g, 54 Mbit/s. WEP and TKIP are long broken, too.")
                               : L.T("Открытая сеть не шифрует трафик. На скорость это не влияет, но любой рядом может его читать.", "An open network does not encrypt traffic. Speed is not affected, but anyone nearby can read it."),
                    () => L.T("Атрибуты безопасности текущего соединения из WLAN API (алгоритм проверки подлинности и шифр).", "The current connection's security attributes from the WLAN API (authentication algorithm and cipher)."),
                    Tip("Включить WPA2-AES или WPA3", "Turn on WPA2-AES or WPA3",
                        "В настройках Wi-Fi роутера выберите «WPA2-PSK (AES)» или «WPA2/WPA3» и шифр AES (CCMP), без TKIP. Старые устройства, которые умеют только TKIP, лучше заменить или вынести в отдельную гостевую сеть.",
                        "In the router's Wi-Fi settings choose \"WPA2-PSK (AES)\" or \"WPA2/WPA3\" and the AES (CCMP) cipher, without TKIP. Old devices that only do TKIP are best replaced or moved to a separate guest network."));
            }

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
                Explain(b,
                    () => L.T("На 2,4 ГГц всего 3 непересекающихся канала, туда же светят соседи, Bluetooth и микроволновки. 5 ГГц даёт в разы больше скорости и меньше помех, но хуже проходит через стены; 6 ГГц (Wi-Fi 6E/7) — ещё свободнее.",
                              "2.4 GHz has only 3 non-overlapping channels, shared with neighbours, Bluetooth and microwave ovens. 5 GHz gives several times the speed and less interference but goes through walls worse; 6 GHz (Wi-Fi 6E/7) is freer still."),
                    () => L.T("Частота текущего соединения и сети с тем же именем из последнего сканирования.", "The current connection's frequency and networks with the same name from the last scan."),
                    Tip("Разделить диапазоны", "Split the bands",
                        "Если роутер раздаёт одну сеть на оба диапазона («band steering»), а ноутбук упорно сидит на 2,4 ГГц — дайте сетям 5 ГГц и 2,4 ГГц разные имена и подключайтесь к 5 ГГц.",
                        "If the router uses one name for both bands (band steering) and the laptop keeps sitting on 2.4 GHz, give the 5 GHz and 2.4 GHz networks different names and join the 5 GHz one."),
                    Tip("Предпочитаемый диапазон в драйвере", "Preferred band in the driver",
                        "У адаптеров Intel, Realtek и MediaTek в свойствах есть «Предпочитаемый диапазон» (Preferred Band) — выберите 5 ГГц:",
                        "Intel, Realtek and MediaTek adapters have a \"Preferred Band\" property — choose 5 GHz:",
                        "Get-NetAdapterAdvancedProperty -Name '" + Physical.Name.Replace("'", "''") + "' | Where-Object DisplayName -Match 'Band|Диапазон' | Format-Table DisplayName, DisplayValue, ValidDisplayValues"));
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
                Explain(ch,
                    () => L.T("Сети на одном канале делят эфир: пока передаёт соседний роутер, ваш ждёт. Отсюда скачки задержки вечером, когда соседи дома.",
                              "Networks on one channel share the air: while a neighbour's router talks, yours waits. Hence the delay jumps in the evening, when the neighbours are home."),
                    () => L.T("Сети слышнее −82 дБм с другим именем: на 2,4 ГГц — в пределах 4 каналов (они перекрываются), на 5/6 ГГц — на той же частоте. Заметно, если их 8+ на 2,4 ГГц или 5+ на 5 ГГц.",
                              "Networks louder than −82 dBm with another name: on 2.4 GHz within 4 channels (they overlap), on 5/6 GHz on the same frequency. It matters at 8+ on 2.4 GHz or 5+ on 5 GHz."),
                    Tip("Список сетей вокруг", "The networks around", "Все сети с каналами и уровнями сигнала:", "Every network with its channel and signal level:",
                        "netsh wlan show networks mode=bssid"),
                    Tip("Сменить канал", "Change the channel",
                        "В настройках роутера выберите канал, где меньше сетей: на 2,4 ГГц только 1, 6 или 11; на 5 ГГц — любой свободный (каналы 36–48 есть у всех устройств). «Авто» часто выбирает плохо.",
                        "In the router pick the channel with the fewest networks: on 2.4 GHz only 1, 6 or 11; on 5 GHz any free one (36–48 work with every device). \"Auto\" often picks badly."));
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
            // Meanwhile: which programs send TCP segments again (the network is quiet, only they talk).
            var watch = new TcpWatch();
            var t0 = TcpTotals();
            bool watching = true;
            var tw = new Thread(() =>
            {
                watch.Start();
                for (int i = 0; watching && !Cancel && i < 40; i++) { Thread.Sleep(1000); watch.Poll(); }
            }) { IsBackground = true, Name = "net tcp", Priority = ThreadPriority.BelowNormal };
            tw.Start();
            if (Online) Sample(InternetProbe, Internet, fi, Secs, 200);
            if (tg != null) tg.Join();
            Thread.CurrentThread.Priority = ThreadPriority.Normal;
            watching = false;
            tw.Join(3000);
            IdleShares = watch.Works ? watch.Stop() : new List<TcpShare>();
            TcpDiag = "idle: works=" + watch.Works + " err=" + watch.LastError + " programs=" + IdleShares.Count;
            var t1 = TcpTotals();
            if (t0 != null && t1 != null) { IdleSent = t1.Item1 - t0.Item1; IdleResent = t1.Item2 - t0.Item2; }

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

            if (fg != null)
            {
                bool wifi = Physical.Kind == LinkKind.WiFi;
                var tips = new List<NetAdvice>();
                if (fg.Problem && wifi)
                {
                    tips.Add(Tip("Сигнал и помехи", "Signal and interference", "Задержка до роутера по Wi-Fi растёт от слабого сигнала и занятого канала — см. строки «Сигнал Wi-Fi» и «Соседние сети» выше.",
                        "The delay to the router over Wi-Fi grows with a weak signal and a busy channel — see \"Wi-Fi signal\" and \"Neighbour networks\" above."));
                    tips.Add(Tip("Энергосбережение адаптера", "The adapter's power saving", "Адаптер, который дремлет между пакетами, отвечает рывками. От сети DriveUP это выключит (ниже, в исправлениях).",
                        "An adapter that dozes between packets answers in bursts. On mains DriveUP turns that off (below, among the fixes).", null, null, true));
                    tips.Add(Tip("Кабель", "A cable", "Для игр и звонков кабель убирает все эти скачки сразу.", "For games and calls a cable removes all of these jumps at once."));
                }
                else if (fg.Problem)
                {
                    tips.Add(Tip("Кабель, порт, коммутатор", "Cable, port, switch", "По кабелю роутер отвечает за доли миллисекунды и без потерь. Иначе — плохой кабель или порт, перегруженный коммутатор между компьютером и роутером, или сам роутер занят (торрент, скачивание на другом устройстве).",
                        "Over a cable the router answers in a fraction of a millisecond without loss. If not — a bad cable or port, a busy switch between the computer and the router, or the router itself is loaded (torrents, a download on another device)."));
                    tips.Add(Tip("Энергосбережение карты", "The card's power saving", "Energy-Efficient Ethernet добавляет пробуждение карты к каждой паузе. При потерях и скачках DriveUP выключит его (ниже, в исправлениях).",
                        "Energy-Efficient Ethernet adds a wake-up to every pause. With loss and jumps DriveUP turns it off (below, among the fixes).", null, null, true));
                }
                tips.Add(Tip("Повторить замер самому", "Measure it yourself", "100 пингов до роутера: смотрите на потери и разброс (Максимальное − Минимальное):",
                    "100 pings to the router: look at the loss and the spread (Maximum − Minimum):", "ping -n 100 " + gw));
                Explain(fg,
                    () => L.T("Время ответа роутера — то, что добавляет сама домашняя сеть. Всё, что сверх этого, добавляет провайдер и дальний путь. " +
                              "Если задержка скачет уже здесь, менять DNS или «ускорять» интернет бесполезно: проблема в кабеле, Wi-Fi или роутере.",
                              "How fast the router answers — what the home network itself adds. Everything above that comes from the provider and the long way. " +
                              "If the delay jumps already here, changing DNS or \"boosting\" the internet is pointless: the trouble is the cable, the Wi-Fi or the router."),
                    () => L.T("ICMP echo к шлюзу каждые 200 мс в течение 12 с (около 60 замеров), с повышенным приоритетом. Разброс — среднее изменение между соседними замерами (RFC 3550). " +
                              "Нормы по кабелю: медиана до 3 мс, разброс до 2 мс, 95-й процентиль до 10 мс, потерь нет. По Wi-Fi: до 12 мс, 10 мс, 60 мс, потери меньше 1 %.",
                              "An ICMP echo to the gateway every 200 ms for 12 s (about 60 samples), at a raised priority. Jitter is the mean change between neighbouring samples (RFC 3550). " +
                              "Norms over a cable: median up to 3 ms, jitter up to 2 ms, 95th percentile up to 10 ms, no loss. Over Wi-Fi: up to 12 ms, 10 ms, 60 ms, loss under 1 %."),
                    tips.ToArray());
            }

            if (!Online)
            {
                Explain(fi,
                    () => L.T("Ни один из публичных серверов (1.1.1.1, 8.8.8.8, 9.9.9.9) не ответил ни на ping, ни на попытку соединения по порту 443.", "None of the public servers (1.1.1.1, 8.8.8.8, 9.9.9.9) answered either a ping or a connection attempt on port 443."),
                    () => L.T("ICMP, затем TCP 443 к трём адресам.", "ICMP, then TCP 443 to three addresses."),
                    Tip("Роутер и провайдер", "The router and the provider", "Перезагрузите роутер; если индикатор интернета на нём не горит — проблема у провайдера.", "Restart the router; if its internet light is off, the trouble is at the provider."),
                    Tip("Сбросить сетевые настройки Windows", "Reset Windows' network settings", "Если с другими устройствами интернет есть, а с этим нет — сбросьте стек TCP/IP и Winsock (нужна перезагрузка):",
                        "If other devices are online and this one is not, reset the TCP/IP stack and Winsock (needs a restart):", "netsh winsock reset; netsh int ip reset; ipconfig /flushdns"));
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
            var itips = new List<NetAdvice>();
            if (li != NetLevel.Good)
            {
                itips.Add(Tip("Долгий замер", "The long check", "Включите «Долгий замер» и проверьте снова: 2 минуты покажут, постоянны ли потери и есть ли регулярные всплески.",
                    "Turn on \"Long check\" and check again: 2 minutes show whether the loss is steady and whether spikes come at regular times."));
                itips.Add(Tip("Найти, где теряется", "Find where it is lost", "pathping 100 секунд опрашивает каждый узел маршрута и показывает, с какого начинаются потери. Потери только на промежуточном узле — не страшно (роутеры отвечают на ping неохотно); страшно, если они тянутся до конца.",
                    "pathping probes every hop of the route for 100 seconds and shows where the loss begins. Loss on a middle hop only is harmless (routers answer pings reluctantly); loss that carries on to the end is real.",
                    "pathping -n " + InternetProbe.Ip));
                itips.Add(Tip("К провайдеру — с цифрами", "To the provider — with figures", "Если до роутера всё ровно, а дальше нет, — сохраните отчёт pathping и время замеров: с ними провайдер не отделается советом «перезагрузите роутер».",
                    "If it is steady up to the router and not beyond, keep the pathping report and the times: with them the provider cannot get away with \"restart your router\"."));
            }
            itips.Add(Tip("Повторить замер самому", "Measure it yourself", "100 пингов до того же адреса:", "100 pings to the same address:", "ping -n 100 " + InternetProbe.Ip));
            Explain(fi,
                () => L.T("Задержка до ближайшего узла крупной сети (Cloudflare, Google или Quad9) — почти то же, что до игровых серверов и сервисов в вашем городе. " +
                          "Для игр важна не только медиана, но и разброс и пики: ровные 40 мс лучше, чем 15 мс со скачками до 200. Потери даже 1–2 % заметны в играх и звонках.",
                          "The delay to the nearest node of a large network (Cloudflare, Google or Quad9) — nearly the same as to game servers and services in your city. " +
                          "For games the spread and peaks matter as much as the median: a steady 40 ms beats 15 ms with jumps to 200. Even 1–2 % loss shows in games and calls."),
                () => L.T("ICMP echo каждые 200 мс в течение 12 с; если ping закрыт — время установки TCP-соединения на порт 443 (один круг). Всплеск — замер больше 3 медиан и на 25 мс. " +
                          "Нормы: потери меньше 1 %, медиана до 80 мс, разброс до 12 мс, всплесков не больше 4 %. Плохо: потери от 3 %, медиана больше 150 мс или разброс больше 30 мс.",
                          "An ICMP echo every 200 ms for 12 s; where pings are blocked, the time a TCP connection to port 443 takes to open (one round trip). A spike is a sample over 3 medians and 25 ms more. " +
                          "Norms: loss under 1 %, median up to 80 ms, jitter up to 12 ms, at most 4 % spikes. Poor: loss from 3 %, median over 150 ms or jitter over 30 ms."),
                itips.ToArray());
        }

        /// <summary>Windows' own count of TCP segments sent and sent again (IPv4 + IPv6), or null.</summary>
        static Tuple<long, long> TcpTotals()
        {
            try
            {
                var g = IPGlobalProperties.GetIPGlobalProperties();
                var a = g.GetTcpIPv4Statistics();
                long sent = a.SegmentsSent, resent = a.SegmentsResent;
                try { var b = g.GetTcpIPv6Statistics(); sent += b.SegmentsSent; resent += b.SegmentsResent; } catch { }
                return Tuple.Create(sent, resent);
            }
            catch { return null; }
        }

        // ---------- DNS, IPv6, MTU ----------

        static readonly string[] Names = { "www.google.com", "www.microsoft.com", "www.youtube.com", "www.wikipedia.org", "www.cloudflare.com", "steamcommunity.com", "github.com", "www.apple.com" };

        // Zones without DNSSEC: a made-up name under them cannot be answered from a cached "no such names" proof.
        static readonly string[] ColdZones = { "google.com", "microsoft.com", "amazon.com", "youtube.com" };

        static DnsResult AskAll(IPAddress server, string name)
        {
            var r = new DnsResult { Server = server, Name = name };
            foreach (var n in Names)
            {
                double ms = DnsProbe.Query(server, n);
                if (double.IsNaN(ms)) r.Failed++;
                r.Ms.Add(ms);
            }
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            foreach (var z in ColdZones)
                r.Cold.Add(DnsProbe.Query(server, "dup" + rnd.Next(100000, 999999) + rnd.Next(1000, 9999) + "." + z, 2500));
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
            double med = SystemDns.Median, cold = SystemDns.ColdMedian;
            int failed = results.Sum(r => r.Failed), asked = results.Sum(r => r.Ms.Count);
            bool slowCold = !double.IsNaN(cold) && cold > 400;
            NetLevel lv = !SystemDns.Works ? NetLevel.Bad : failed * 100 / Math.Max(1, asked) >= 20 ? NetLevel.Warn : med > 150 || slowCold ? NetLevel.Warn : NetLevel.Good;
            Done(f, lv, () => string.Join(", ", servers.Select(s => s.ToString())) + " · " + (SystemDns.Works ? Ms(med) : L.T("не отвечает", "no answer")) +
                              (SystemDns.Works && !double.IsNaN(cold) ? L.T(" · новые имена ", " · new names ") + Ms(cold) : "") +
                              (failed > 0 ? L.T(" · без ответа: ", " · unanswered: ") + failed + L.T(" из ", " of ") + asked : ""),
                () => lv == NetLevel.Good
                    ? L.T("Имена сайтов находятся быстро", "Site names resolve quickly")
                    : slowCold && med <= 150 && SystemDns.Works
                        ? L.T("Знакомые имена — быстро, а новые сервер ищет долго: каждый новый сайт начинает грузиться с задержкой", "Known names are quick, but the server looks new ones up slowly: every new site starts loading late")
                        : L.T("DNS отвечает медленно или не всегда: сайты долго начинают открываться", "DNS answers slowly or not always: sites take long to start loading"));
            var dtips = new List<NetAdvice>();
            if (Pc.Managed || InternalDns)
                dtips.Add(Tip(Pc.Managed ? "Сеть организации: DNS не менять" : "Свой DNS-сервер сети: не менять", Pc.Managed ? "An organisation's network: keep its DNS" : "The network's own DNS server: keep it",
                    "DNS сети — собственный сервер (домен организации, VPN, домашний Pi-hole / AdGuard): он знает внутренние имена, публичный DNS — нет, поэтому DriveUP его не меняет. Если новые имена ищутся медленно — это настройка самого сервера: пересылка (forwarders) на быстрый вышестоящий DNS, доступность корневых серверов, кеш и нагрузка.",
                    "The network's DNS is a server of its own (an organisation's domain, a VPN, a home Pi-hole / AdGuard): it knows internal names a public DNS does not, so DriveUP leaves it alone. If new names resolve slowly, it is that server's setup: forwarders to a fast upstream DNS, reaching the root servers, the cache and the load.",
                    "Get-DnsServerForwarder; Get-DnsServerRecursion   # Windows DNS server"));
            else if (SlowDns)
                dtips.Add(Tip("Перейти на быстрый публичный DNS", "Switch to a fast public DNS",
                    "DriveUP предложит самый быстрый из проверенных (ниже, в исправлениях); вернуть — одной кнопкой на вкладке «Изменено».",
                    "DriveUP offers the fastest of those tested (below, among the fixes); it goes back with one button under Changed.", null, null, true));
            dtips.Add(Tip("Сбросить кеш DNS", "Flush the DNS cache", "Если сайт переехал или открывается «старая» версия:", "If a site moved or an old copy opens:", "ipconfig /flushdns"));
            dtips.Add(Tip("Сравнить серверы самому", "Compare servers yourself", "Время ответа конкретного сервера на конкретное имя:", "How long a given server takes for a given name:",
                "Measure-Command { Resolve-DnsName example.com -Server " + servers[0] + " -DnsOnly -QuickTimeout }"));
            Explain(f,
                () => L.T("Прежде чем открыть сайт или подключиться к игре, компьютер спрашивает у DNS-сервера адрес по имени. Пока ответа нет, страница не начинает грузиться. " +
                          "Знакомые серверу имена отдаются из кеша за единицы миллисекунд; новые он ищет сам, обходя серверы имён по цепочке, — это и отличает хороший DNS от медленного.",
                          "Before opening a site or joining a game the computer asks a DNS server for the address behind the name. Until the answer comes, nothing loads. " +
                          "Names the server already knows come from its cache in milliseconds; new ones it looks up itself, walking the chain of name servers — that is what tells a good DNS from a slow one."),
                () => L.T("Прямые UDP-запросы к серверам Windows: 8 популярных имён (из кеша) и 4 выдуманных имени в зонах без DNSSEC (google.com, microsoft.com, amazon.com, youtube.com) — их сервер ищет заново. " +
                          "Нормы: знакомые до 50 мс (хорошо) / 150 мс (терпимо), новые до 150 / 400 мс, без отказов.",
                          "Direct UDP queries to Windows' servers: 8 popular names (cached) and 4 made-up names in zones without DNSSEC (google.com, microsoft.com, amazon.com, youtube.com) — the server looks those up anew. " +
                          "Norms: known names up to 50 ms (good) / 150 ms (bearable), new ones up to 150 / 400 ms, no failures."),
                dtips.ToArray());

            var best = PublicDns.Where(r => r.Works).OrderBy(r => r.Median).FirstOrDefault();
            var pf = Add("dnspub", TuneCat.NetDns, "\uE774", () => L.T("Публичные DNS для сравнения", "Public DNS for comparison"));
            if (best == null)
                Done(pf, NetLevel.Info, () => L.T("не отвечают из этой сети", "do not answer from this network"),
                    () => L.T("Сеть пропускает DNS только к своему серверу — так бывает в офисах и у некоторых провайдеров", "The network only lets DNS go to its own server — usual at work and with some providers"));
            else
                Done(pf, NetLevel.Info, () => string.Join(" · ", PublicDns.OrderBy(r => r.Works ? r.Median : 9999).Select(r => r.Name + " " + (r.Works ? Ms(r.Median) : "—"))),
                    () => SlowDns
                        ? (Pc.Managed || InternalDns ? L.T("Быстрее, но ваш DNS — внутренний сервер сети, его не заменить — см. подробности", "Faster, but your DNS is the network's own server and is not replaced — see the details")
                                      : L.T("Быстрее вашего DNS — можно переключиться (ниже, в исправлениях)", "Faster than your DNS — you can switch (below, among the fixes)"))
                        : L.T("Ваш DNS не медленнее — менять незачем", "Your DNS is no slower — no reason to change it"));
            Explain(pf,
                () => L.T("Те же запросы к Cloudflare (1.1.1.1), Google (8.8.8.8) и Quad9 (9.9.9.9) — чтобы было с чем сравнить. Публичный DNS имеет смысл, только если он заметно быстрее или ваш отказывает: «самый быстрый DNS» сам по себе интернет не ускоряет.",
                          "The same queries to Cloudflare (1.1.1.1), Google (8.8.8.8) and Quad9 (9.9.9.9) — something to compare with. A public DNS makes sense only if it is clearly faster or yours fails: \"the fastest DNS\" by itself does not speed the internet up."),
                () => L.T("Первое число — знакомые имена, в скобках — новые. Переключиться предлагается, если ваш DNS отказывает или публичный быстрее вдвое и на 20 мс (знакомые) либо вдвое и на 150 мс (новые); на компьютерах организации и при VPN — никогда.",
                          "The first figure is known names, in brackets new ones. Switching is offered if your DNS fails, or a public one is twice as fast and 20 ms quicker (known) or twice and 150 ms (new); never on an organisation's computer or with a VPN."));
            if (best != null)
                pf.Value = () => string.Join(" · ", PublicDns.OrderBy(r => r.Works ? r.Median : 9999).Select(r => r.Name + " " + (r.Works ? Ms(r.Median) + (double.IsNaN(r.ColdMedian) ? "" : " (" + Ms(r.ColdMedian) + ")") : "—")));
            if (Updated != null) Updated(pf);
        }

        /// <summary>
        /// The network's DNS is a server of its own: a private address that is not the router (an office, a VPN, a home
        /// server like Pi-hole). Replacing it with a public one would lose the names only it knows.
        /// </summary>
        public bool InternalDns
        {
            get
            {
                if (SystemDns == null || SystemDns.Server == null) return false;
                var gw = Physical != null ? Physical.Gateway : null;
                return IsPrivate(SystemDns.Server) && gw != null && !SystemDns.Server.Equals(gw);
            }
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
                if (SystemDns.Median > b.Median * 2 && SystemDns.Median - b.Median > 20) return true;
                double mine = SystemDns.ColdMedian, theirs = b.ColdMedian;
                return !double.IsNaN(mine) && !double.IsNaN(theirs) && mine > theirs * 2 && mine - theirs > 150;
            }
        }

        void CheckPath()
        {
            if (!Online) return;
            // IPv6: an address from the provider that does not reach anywhere makes programs wait before falling back.
            try { Ipv6Components = Reg.Dword(true, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents"); } catch { }
            var main = Main ?? Physical;
            var f6 = Add("ipv6", TuneCat.NetPath, "\uE774", () => "IPv6");
            bool off = Ipv6Components.HasValue && (Ipv6Components.Value & 0xFF) == 0xFF;
            if (off)
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
            Explain(f6,
                () => L.T("Если провайдер выдал адрес IPv6, программы сначала пробуют его. Когда IPv6 «есть, но не работает», каждое новое соединение ждёт, пока попытка не сорвётся, и только потом идёт по IPv4 — сайты открываются с задержкой в секунды. " +
                          "Полностью отключать IPv6 в реестре Microsoft не рекомендует: на нём держатся части Windows; правильнее велеть предпочитать IPv4.",
                          "If the provider gave an IPv6 address, programs try it first. When IPv6 is \"there but broken\", every new connection waits for the attempt to fail and only then goes over IPv4 — sites open seconds late. " +
                          "Microsoft advises against switching IPv6 off in the registry: parts of Windows rely on it; telling Windows to prefer IPv4 is the right way."),
                () => L.T("Глобальный адрес IPv6 на адаптере; соединение по TCP 443 к 2606:4700:4700::1111 и 2001:4860:4860::8888; значение DisabledComponents в Tcpip6\\Parameters.",
                          "A global IPv6 address on the adapter; a TCP 443 connection to 2606:4700:4700::1111 and 2001:4860:4860::8888; DisabledComponents under Tcpip6\\Parameters."),
                V6Broken || off ? Tip("Предпочитать IPv4 вместо отключения", "Prefer IPv4 instead of disabling",
                    "Значение 0x20 — рекомендация Microsoft: IPv6 остаётся, но в интернет программы идут по IPv4. DriveUP сделает это (ниже, в исправлениях), нужна перезагрузка.",
                    "The value 0x20 is Microsoft's advice: IPv6 stays, but programs go online over IPv4. DriveUP does it (below, among the fixes); a restart is needed.",
                    null, "https://learn.microsoft.com/troubleshoot/windows-server/networking/configure-ipv6-in-windows", true) : null,
                V6Broken ? Tip("Роутер и провайдер", "The router and the provider", "IPv6 без связи — обычно сбой настроек IPv6 на роутере (режим, делегирование префикса) или у провайдера. Сообщите им — тогда предпочтение IPv4 можно будет вернуть.",
                    "IPv6 without connectivity is usually a broken IPv6 setup in the router (mode, prefix delegation) or at the provider. Tell them — then the IPv4 preference can go back.") : null,
                Tip("Проверить самому", "Check it yourself", "Соединение по IPv6 до Cloudflare:", "A connection over IPv6 to Cloudflare:", "Test-NetConnection 2606:4700:4700::1111 -Port 443"));

            CheckRoute();

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
            Explain(fm,
                () => L.T("Самый большой пакет, который проходит до интернета целиком. Меньше 1500 бывает из-за PPPoE (1492), VPN и туннелей — Windows узнаёт это сама (Path MTU Discovery) и подстраивается. " +
                          "Вручную занижать MTU «для игр» незачем: это миф, пакеты игр маленькие.",
                          "The largest packet that reaches the internet in one piece. Under 1500 comes from PPPoE (1492), VPNs and tunnels — Windows finds that out itself (Path MTU Discovery) and adapts. " +
                          "Lowering the MTU by hand \"for games\" is a myth: game packets are small."),
                () => L.T("Ping с запретом фрагментации (DF) к " + InternetProbe.Ip + ", поиск размера делением пополам между 1200 и 1472 байтами данных (+28 байт заголовков).",
                          "Pings with Don't Fragment set to " + InternetProbe.Ip + ", a halving search between 1200 and 1472 bytes of payload (+28 bytes of headers)."),
                Tip("Проверить самому", "Check it yourself", "Пакет 1472 + 28 = 1500 байт без дробления; если «Требуется фрагментация» — уменьшайте:",
                    "A 1472 + 28 = 1500 byte packet without fragmenting; if it says \"needs to be fragmented\", go lower:", "ping -f -l 1472 " + InternetProbe.Ip));
        }

        static bool IsPrivate(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168));
        }

        static bool IsCgNat(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] < 128;
        }

        /// <summary>
        /// The first hops on the way out (ICMP with a growing TTL): how many private networks lie between this computer
        /// and the internet — the provider's shared "grey" address (CGNAT, 100.64/10) or a second router (double NAT).
        /// Both are invisible in everyday browsing but break incoming connections: hosting a game, P2P, remote access.
        /// </summary>
        void CheckRoute()
        {
            if (InternetProbe == null || InternetProbe.Tcp || Vpn || Physical == null || Physical.Gateway == null) return;
            var hops = new List<Tuple<int, IPAddress, double>>();
            using (var ping = new Ping())
                for (int ttl = 1; ttl <= 8 && !Cancel; ttl++)
                {
                    IPAddress at = null;
                    double best = double.NaN;
                    bool reached = false;
                    for (int k = 0; k < 3; k++)
                    {
                        try
                        {
                            var sw = Stopwatch.StartNew();
                            var r = ping.Send(InternetProbe.Ip, 1000, new byte[32], new PingOptions(ttl, true));
                            double ms = sw.Elapsed.TotalMilliseconds;
                            if (r.Status == IPStatus.TtlExpired || r.Status == IPStatus.Success)
                            {
                                at = r.Address;
                                if (double.IsNaN(best) || ms < best) best = ms;
                                if (r.Status == IPStatus.Success) reached = true;
                            }
                        }
                        catch { }
                    }
                    hops.Add(Tuple.Create(ttl, at, best));
                    if (reached) break;
                }
            if (hops.Count == 0 || hops[0].Item2 == null) return;
            // Hops before the first public address.
            var inner = hops.TakeWhile(h => h.Item2 == null || IsPrivate(h.Item2) || IsCgNat(h.Item2)).Where(h => h.Item2 != null).ToList();
            CgNat = inner.Any(h => IsCgNat(h.Item2));
            int privateHops = inner.Count(h => IsPrivate(h.Item2));
            DoubleNat = !CgNat && privateHops >= 2;
            var firstPublic = hops.FirstOrDefault(h => h.Item2 != null && !IsPrivate(h.Item2) && !IsCgNat(h.Item2));
            var f = Add("route", TuneCat.NetPath, "\uE707", () => L.T("Маршрут и NAT", "Route and NAT"));
            string path = string.Join(" → ", hops.Take(Math.Min(hops.Count, (firstPublic != null ? firstPublic.Item1 : hops.Count) + 1))
                .Select(h => h.Item2 == null ? "*" : h.Item2.ToString()));
            bool office = Pc.Managed && DoubleNat;
            NetLevel lv = office ? NetLevel.Good : CgNat || DoubleNat ? NetLevel.Info : NetLevel.Good;
            Done(f, lv,
                () => (CgNat ? L.T("серый адрес провайдера (CGNAT)", "provider's shared address (CGNAT)")
                       : DoubleNat ? L.T("частных сетей до интернета: ", "private networks before the internet: ") + privateHops
                       : L.T("один роутер до интернета", "one router before the internet")) +
                      (firstPublic != null && !double.IsNaN(firstPublic.Item3) ? L.T(" · до провайдера ", " · to the provider ") + Ms(firstPublic.Item3) : ""),
                () => CgNat ? L.T("Вы выходите в интернет через общий адрес провайдера: входящие соединения (хост в играх, торренты, удалённый доступ) не работают", "You go online through the provider's shared address: incoming connections (game hosting, torrents, remote access) do not work")
                    : office ? L.T("Сеть организации: до интернета несколько внутренних узлов — для офиса это обычно", "An organisation's network: several internal hops before the internet — usual for an office")
                    : DoubleNat ? L.T("Между компьютером и интернетом больше одной частной сети: два роутера подряд или сеть организации", "There is more than one private network between the computer and the internet: two routers in a row, or an organisation's network")
                    : L.T("Обычная схема: компьютер → роутер → провайдер", "The usual layout: computer → router → provider"));
            var tips = new List<NetAdvice>();
            if (CgNat)
                tips.Add(Tip("Попросить белый IP", "Ask for a public IP", "У провайдера обычно можно заказать «белый» (публичный) IP-адрес — часто бесплатно или за небольшую плату. Без него не помогут ни проброс портов, ни UPnP.",
                    "Providers usually offer a public (\"white\") IP address — often free or cheap. Without it neither port forwarding nor UPnP help."));
            if (DoubleNat && !Pc.Managed)
                tips.Add(Tip("Один роутер вместо двух", "One router instead of two", "Если перед вашим роутером стоит роутер провайдера (оптический терминал, модем), переведите его в режим моста (bridge) или ваш — в режим точки доступа. Тогда NAT будет один, и игры перестанут жаловаться на «строгий NAT».",
                    "If the provider's router (an optical terminal, a modem) sits in front of yours, put it into bridge mode, or yours into access-point mode. Then there is one NAT and games stop complaining about \"strict NAT\"."));
            tips.Add(Tip("Весь маршрут", "The whole route", "Все узлы до " + InternetProbe.Ip + " с задержкой до каждого:", "Every hop to " + InternetProbe.Ip + " with the delay to each:", "tracert -d " + InternetProbe.Ip));
            Explain(f,
                () => L.T("Маршрут: ", "Route: ") + path + ". " +
                      L.T("Сколько частных сетей между компьютером и интернетом. Один роутер — норма. Серый адрес провайдера (100.64.0.0/10) или второй роутер в цепочке на скорость не влияют, но входящие соединения через них не проходят: " +
                          "хост в играх, «строгий NAT» в консолях и играх, торренты, удалённый доступ к домашнему компьютеру.",
                          "How many private networks lie between the computer and the internet. One router is normal. The provider's shared address (100.64.0.0/10) or a second router in the chain do not affect speed, but incoming connections do not get through: " +
                          "game hosting, \"strict NAT\" in consoles and games, torrents, remote access to a home computer."),
                () => L.T("ICMP с растущим TTL (1–8) к " + InternetProbe.Ip + ", по 3 попытки на узел. Частные адреса — 10/8, 172.16/12, 192.168/16; CGNAT — 100.64/10. Время — лучшее из трёх, до первого публичного узла (это обычно провайдер).",
                          "ICMP with a growing TTL (1–8) to " + InternetProbe.Ip + ", 3 tries per hop. Private addresses are 10/8, 172.16/12, 192.168/16; CGNAT is 100.64/10. The time is the best of three, to the first public hop (usually the provider)."),
                tips.ToArray());
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
            Explain(f,
                () => L.T("Сколько занимает открытие соединения с популярными сервисами — как у браузера: имя через DNS Windows, затем TCP-соединение на порт 443. Это первое, что ждёт пользователь при открытии сайта или запуске игры.",
                          "How long opening a connection to popular services takes — as a browser does it: the name through Windows' DNS, then a TCP connection to port 443. It is the first wait when a site opens or a game starts."),
                () => L.T("Лучшее из 3 соединений с каждым; без ответа за 2,5 с — «нет ответа». Обычно 5–50 мс; сотни — далёкий сервер или перегруженный канал.",
                          "The best of 3 connections to each; nothing within 2.5 s is \"no answer\". Usually 5–50 ms; hundreds mean a distant server or a busy line."),
                Tip("Проверить сервис самому", "Check a service yourself", "DNS и соединение до конкретного сайта:", "DNS and a connection to a given site:",
                    "Test-NetConnection www.youtube.com -Port 443 -InformationLevel Detailed"));
        }

        // ---------- speed and delay under load ----------

        const string DownUrl = "https://speed.cloudflare.com/__down?bytes=", UpUrl = "https://speed.cloudflare.com/__up", TraceUrl = "https://speed.cloudflare.com/cdn-cgi/trace";
        /// <summary>DriveUP's own upload connections, as Windows counted them.</summary>
        public List<TcpShare> UpShares = new List<TcpShare>();
        /// <summary>The loaded delay's 95th percentile over the quiet median (ms).</summary>
        public double PeakDown = double.NaN, PeakUp = double.NaN;
        public string GradeDown = "", GradeUp = "";
        /// <summary>The last HTTP status the speed test server gave instead of data (429: too many tests), 0 = none.</summary>
        public volatile int LoadStatus;

        /// <summary>Which Cloudflare site serves this connection (IATA code and country), for the speed rows.</summary>
        void ReadColo()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(TraceUrl);
                req.UserAgent = "DriveUP network check";
                req.Timeout = 5000;
                req.ReadWriteTimeout = 5000;
                using (var resp = req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream()))
                {
                    string colo = "", loc = "";
                    foreach (var line in r.ReadToEnd().Split('\n'))
                    {
                        if (line.StartsWith("colo=")) colo = line.Substring(5).Trim();
                        else if (line.StartsWith("loc=")) loc = line.Substring(4).Trim();
                    }
                    if (Regex.IsMatch(colo, "^[A-Z]{3}$")) Colo = colo + (Regex.IsMatch(loc, "^[A-Z]{2}$") ? ", " + loc : "");
                }
            }
            catch { }
        }

        void MeasureLoad()
        {
            if (!Online) return;
            if (Metered)
            {
                var fm = Add("speed", TuneCat.NetSpeed, "\uE896", () => L.T("Скорость и задержка под нагрузкой", "Speed and delay under load"));
                Done(fm, NetLevel.Info, () => L.T("пропущено: подключение лимитное", "skipped: the connection is metered"),
                    () => L.T("Замер скачал бы сотни мегабайт — на лимитном подключении DriveUP его не делает", "The test would download hundreds of megabytes — DriveUP skips it on a metered connection"));
                Explain(fm,
                    () => L.T("Windows считает это подключение лимитным (мобильный интернет, раздача с телефона или так задано в параметрах сети). Замер скорости скачал бы до 1 ГБ и отправил до 300 МБ.",
                              "Windows counts this connection as metered (mobile data, a phone hotspot, or set so in the network settings). The speed test would download up to 1 GB and upload up to 300 MB."),
                    () => L.T("INetworkCostManager: стоимость подключения не «без ограничений».", "INetworkCostManager: the connection cost is not \"unrestricted\"."),
                    Tip("Если подключение на самом деле безлимитное", "If the connection is actually unlimited",
                        "Параметры → Сеть и Интернет → свойства подключения → «Лимитное подключение» — выключите и проверьте снова.",
                        "Settings → Network & internet → the connection's properties → \"Metered connection\" — turn it off and check again.",
                        null, "ms-settings:network-status"));
                return;
            }
            ReadColo();
            Say(L.T("Скачиваю для замера скорости (до 1 ГБ) и слежу за задержкой…", "Downloading to measure speed (up to 1 GB) and watching the delay…"));
            var fd = Add("down", TuneCat.NetSpeed, "\uE896", () => L.T("Скачивание", "Download"));
            var fl = Add("bloat", TuneCat.NetSpeed, "\uE916", () => L.T("Задержка во время скачивания", "Delay while downloading"));
            fl.Chart = true;
            BloatDownRow = fl;
            LoadDown = new Rtt();
            LoadDownTcp = new Rtt();
            var nic = Physical != null ? Physical.Nic : null;
            NicLoadFrom = NicCounters.Read(nic);
            DownMbps = Load(true, fd, fl, LoadDown, 9, 1000L << 20, null);
            if (Cancel) return;
            // Without a load (the test failed) the delay "under load" means nothing.
            BloatDown = double.IsNaN(DownMbps) ? double.NaN : AddedDelay(LoadDown);
            PeakDown = double.IsNaN(DownMbps) ? double.NaN : PeakDelay(LoadDown);
            Report(fd, fl, DownMbps, true);

            Say(L.T("Отправляю для замера отдачи (до 300 МБ)…", "Uploading to measure upload (up to 300 MB)…"));
            var fu = Add("up", TuneCat.NetSpeed, "\uE898", () => L.T("Отдача", "Upload"));
            var flu = Add("bloatup", TuneCat.NetSpeed, "\uE916", () => L.T("Задержка во время отдачи", "Delay while uploading"));
            flu.Chart = true;
            BloatUpRow = flu;
            LoadUp = new Rtt();
            LoadUpTcp = new Rtt();
            var watch = new TcpWatch();
            var t0 = TcpTotals();
            UpMbps = Load(false, fu, flu, LoadUp, 7, 300L << 20, watch);
            var t1 = TcpTotals();
            NicLoadTo = NicCounters.Read(nic);
            if (Cancel) return;
            BloatUp = double.IsNaN(UpMbps) ? double.NaN : AddedDelay(LoadUp);
            PeakUp = double.IsNaN(UpMbps) ? double.NaN : PeakDelay(LoadUp);
            Report(fu, flu, UpMbps, false);

            // Resends of DriveUP's own upload: per connection where Windows lets us count, else everything sent meanwhile.
            int me;
            using (var self = Process.GetCurrentProcess()) me = self.Id;
            var own = UpShares.Where(s => s.Pid == me).ToList();
            bool perConn = own.Sum(s => s.Sent) > 500;
            if (perConn) { UpSent = own.Sum(s => s.Sent); UpResent = own.Sum(s => s.Resent); }
            else if (t0 != null && t1 != null) { UpSent = t1.Item1 - t0.Item1; UpResent = t1.Item2 - t0.Item2; }
            if (!double.IsNaN(UpMbps) && UpSent > 1000) ReportResends(perConn);
            ReportNic();
        }

        /// <summary>How much the delay grows under load (median, ms), against the quiet one.</summary>
        double AddedDelay(Rtt loaded)
        {
            if (loaded.Received < 5 || Internet.Received == 0) return double.NaN;
            return Math.Max(0, loaded.Median - Internet.Median);
        }

        /// <summary>The loaded delay's 95th percentile over the quiet median: the spikes a game or a call feels.</summary>
        double PeakDelay(Rtt loaded)
        {
            if (loaded.Received < 5 || Internet.Received == 0) return double.NaN;
            return Math.Max(0, loaded.Pct(95) - Internet.Median);
        }

        /// <summary>The bufferbloat grade by the added delay alone, as Waveform's test grades it.</summary>
        public static string Grade(double added)
        {
            if (double.IsNaN(added)) return "";
            return added < 5 ? "A+" : added < 30 ? "A" : added < 60 ? "B" : added < 200 ? "C" : added < 400 ? "D" : "F";
        }

        /// <summary>
        /// The grade DriveUP gives: the added delay (Waveform's scale), held down by the spikes (95th percentile) and by
        /// the answers lost while the line was busy — a line that drops packets under load is no A+, however low its median.
        /// </summary>
        public static string Grade(double added, double peak, double loss)
        {
            string g = Grade(added);
            if (g == "") return "";
            if (!double.IsNaN(peak)) g = Worse(g, peak >= 400 ? "D" : peak >= 200 ? "C" : peak >= 100 ? "B" : "A+");
            g = Worse(g, loss >= 10 ? "D" : loss >= 3 ? "C" : loss >= 1 ? "B" : "A+");
            return g;
        }

        void Report(NetFinding speed, NetFinding bloat, double mbps, bool down)
        {
            double link = Physical != null && Physical.Kind == LinkKind.Ethernet && Physical.Speed > 0 ? Physical.Speed / 1e6 : double.NaN;
            bool portBound = !double.IsNaN(link) && !double.IsNaN(mbps) && link < 1000 && mbps > link * 0.85;
            bool wifiBound = Wifi != null && Wifi.RxKbps > 0 && !double.IsNaN(mbps) && mbps > Math.Max(Wifi.RxKbps, Wifi.TxKbps) / 1000.0 * 0.6;
            Done(speed, double.IsNaN(mbps) ? (LoadStatus == 429 ? NetLevel.Info : NetLevel.Warn) : mbps < (down ? 10 : 2) || portBound ? NetLevel.Warn : NetLevel.Info, () => Mbps(mbps),
                () =>
                {
                    if (double.IsNaN(mbps) && LoadStatus == 429)
                        return L.T("Сервер замера ограничил частые проверки с вашего адреса — повторите через 5–10 минут", "The test server limits frequent tests from your address — try again in 5–10 minutes");
                    if (double.IsNaN(mbps)) return L.T("Замер не удался: сервер speed.cloudflare.com не ответил", "The test failed: speed.cloudflare.com did not answer");
                    string s = L.T("Замер через speed.cloudflare.com", "Measured via speed.cloudflare.com") + (Colo.Length > 0 ? " (" + Colo + ")" : "") + L.T(", 4 потока", ", 4 streams");
                    if (portBound) s += L.T(". Упирается в скорость порта ", ". Limited by the port speed, ") + LinkText(Physical.Speed);
                    else if (wifiBound) s += L.T(". Упирается в скорость Wi-Fi (", ". Limited by the Wi-Fi link (") + LinkText(Math.Max(Wifi.RxKbps, Wifi.TxKbps) * 1000) + ")";
                    return s;
                });
            var stips = new List<NetAdvice>();
            if (portBound)
                stips.Add(Tip("Порт 100 Мбит/с — потолок", "A 100 Mbit/s port is the ceiling", "Скорость упёрлась в соединение карты с роутером, а не в тариф. См. строку «Подключение»: кабель Cat5e/Cat6 со всеми 4 парами и гигабитный порт.",
                    "The speed hit the card's link to the router, not the plan. See the \"Connection\" row: a Cat5e/Cat6 cable with all 4 pairs and a gigabit port."));
            if (wifiBound)
                stips.Add(Tip("Скорость Wi-Fi — потолок", "The Wi-Fi link is the ceiling", "Реальная скорость Wi-Fi — около половины скорости соединения. Ближе к роутеру, 5/6 ГГц, более новый стандарт или кабель поднимут её.",
                    "Real Wi-Fi throughput is about half the link rate. Closer to the router, 5/6 GHz, a newer standard or a cable raise it."));
            stips.Add(Tip("Сравнить с тарифом", "Compare with the plan", "Скорость ниже тарифа вечером и нормальная ночью — загрузка сети провайдера; ниже всегда — роутер, Wi-Fi или тариф. Во время замера другие устройства тоже должны молчать.",
                "Slower than the plan in the evening and fine at night is the provider's network load; always slower — the router, Wi-Fi or the plan. Other devices should be quiet during the test."));
            stips.Add(Tip("Перепроверить в браузере", "Re-test in a browser", "Тот же сервер, с графиками и задержкой под нагрузкой:", "The same server, with graphs and loaded latency:", null, "https://speed.cloudflare.com/"));
            Explain(speed,
                () => down ? L.T("Сколько данных в секунду компьютер получает из интернета, когда качает в несколько потоков, как браузер или игровой клиент.", "How much data per second the computer gets from the internet when it downloads in several streams, as a browser or a game client does.")
                           : L.T("Сколько данных в секунду компьютер отправляет: видеозвонки, стримы, облако, отправка файлов.", "How much data per second the computer sends: video calls, streams, cloud sync, sending files."),
                () => L.T("4 параллельных HTTPS-потока к speed.cloudflare.com" + (Colo.Length > 0 ? " (площадка " + Colo + ")" : "") + (down ? ", 9 с или до 1 ГБ" : ", 7 с или до 300 МБ") + "; первые 1,5 с (разгон TCP) не считаются. Ниже " + (down ? "10" : "2") + " Мбит/с — замечание.",
                          "4 parallel HTTPS streams to speed.cloudflare.com" + (Colo.Length > 0 ? " (site " + Colo + ")" : "") + (down ? ", 9 s or up to 1 GB" : ", 7 s or up to 300 MB") + "; the first 1.5 s (TCP ramp-up) do not count. Under " + (down ? "10" : "2") + " Mbit/s is flagged."),
                stips.ToArray());

            var r = down ? LoadDown : LoadUp;
            double added = down ? BloatDown : BloatUp, peak = down ? PeakDown : PeakUp, loss = r.Loss;
            // Lost pings while TCP gets through: the router answers pings last when busy — the real loss is the TCP one.
            var rt = down ? LoadDownTcp : LoadUpTcp;
            double pingLoss = loss;
            bool pingOnly = rt != null && rt.Count >= 10 && loss >= 1 && rt.Loss < loss / 2;
            if (pingOnly) loss = rt.Loss;
            string g = Grade(added, peak, loss);
            // Loaded, and the answers stopped coming: the worst case of all, not "no figure".
            bool drowned = g == "" && !double.IsNaN(mbps) && r.Count >= 5 && r.Received < 5;
            if (drowned) { g = "F"; if (down) BloatDown = 1000; else BloatUp = 1000; }
            if (down) GradeDown = g; else GradeUp = g;
            string byDelay = Grade(added);
            bool lossLed = !drowned && g != "" && Array.IndexOf(Grades, g) > Array.IndexOf(Grades, byDelay) && loss >= 1;
            NetLevel lv = g == "" ? NetLevel.Info : g == "A+" || g == "A" ? NetLevel.Good : g == "B" ? NetLevel.Info : g == "C" ? NetLevel.Warn : NetLevel.Bad;
            Done(bloat, lv,
                () => drowned ? L.T("ответы пропадают (оценка F)", "answers get lost (grade F)") : g == "" ? "—"
                    : "+" + Ms(added) + (!double.IsNaN(peak) && peak >= 20 ? L.T(" · пики +", " · peaks +") + Ms(peak) : "") + L.T(" · потери ", " · loss ") + Pct(loss) +
                      (pingOnly ? L.T(" (ping: ", " (ping: ") + Pct(pingLoss) + ")" : "") + L.T(" · оценка ", " · grade ") + g,
                () =>
                {
                    if (g == "" && !drowned) return L.T("Замер скорости не удался — сравнить не с чем", "The speed test failed — nothing to compare with");
                    if (drowned) return L.T("Пока канал занят, ответы перестают приходить совсем: игры и звонки обрываются во время загрузок", "With the line busy, answers stop coming at all: games and calls break during downloads");
                    if (lossLed) return L.T("Задержка почти не растёт, но пока канал занят, пакеты теряются — для игр и звонков это хуже лишних миллисекунд", "The delay barely grows, but packets get lost while the line is busy — worse for games and calls than extra milliseconds");
                    if (pingOnly && lv == NetLevel.Good) return L.T("Под нагрузкой роутер отвечает на ping в последнюю очередь, но настоящий трафик (TCP) доходит без потерь — всё в порядке", "Under load the router answers pings last, but real traffic (TCP) gets through without loss — all fine");
                    if (lv == NetLevel.Good) return L.T("Когда канал занят, задержка почти не растёт и пакеты не теряются — игры и звонки не страдают от загрузок", "With the line busy the delay barely grows and nothing is lost — downloads do not hurt games and calls");
                    if (lv == NetLevel.Info) return down ? L.T("Задержка немного растёт, пока что-то качается", "The delay grows a little while something downloads")
                                                         : L.T("Задержка немного растёт, пока что-то отправляется", "The delay grows a little while something uploads");
                    return L.T("Пока что-то качается, задержка сильно растёт (bufferbloat). Лечится в роутере: SQM / Smart Queue / QoS (fq_codel или cake) с лимитом ~90 % скорости тарифа.",
                               "While something downloads the delay grows a lot (bufferbloat). The cure is in the router: SQM / Smart Queue / QoS (fq_codel or cake) at ~90 % of your plan.");
                });
            var tips = new List<NetAdvice>();
            if (lv == NetLevel.Warn || lv == NetLevel.Bad)
            {
                if (!lossLed)
                    tips.Add(Tip("SQM в роутере — главное средство", "SQM in the router — the real cure",
                        "Включите в роутере умную очередь: SQM (OpenWrt, fq_codel или cake), Smart Queue (Ubiquiti), Adaptive QoS/Bandwidth (ASUS, TP-Link, Keenetic — «IntelliQoS»). Лимит — 85–95 % реальной скорости из этого замера, отдельно для скачивания и отдачи. Задержка под нагрузкой падает до единиц миллисекунд.",
                        "Turn on a smart queue in the router: SQM (OpenWrt, fq_codel or cake), Smart Queue (Ubiquiti), Adaptive QoS / Bandwidth control (ASUS, TP-Link, Keenetic \"IntelliQoS\"). Set it to 85–95 % of the real speed from this test, separately for download and upload. The loaded delay drops to a few milliseconds."));
                if (lossLed)
                {
                    tips.Add(Tip("Потери под нагрузкой", "Loss under load",
                        "Когда очередь переполняется, роутер или провайдер отбрасывает пакеты. Если по кабелю до роутера потерь нет (см. «Задержка до роутера»), это ограничитель скорости у провайдера: SQM на роутере с лимитом чуть ниже тарифа убирает и эти потери. По Wi-Fi — сначала сигнал.",
                        "When the queue overflows, the router or the provider drops packets. If there is no loss to the router over a cable (see \"Delay to the router\"), it is the provider's rate limiter: SQM in the router set slightly under the plan removes this loss too. Over Wi-Fi — the signal first."));
                }
                tips.Add(Tip("Фоновые загрузки Windows", "Windows' background downloads",
                    "Обновления Windows и Store качаются в фоне на всю ширину канала. DriveUP ограничит их 30 % канала (ниже, в исправлениях; на Windows Home — Параметры → Центр обновления → Оптимизация доставки).",
                    "Windows and Store updates download in the background at full width. DriveUP caps them at 30 % of the line (below, among the fixes; on Windows Home — Settings → Windows Update → Delivery Optimization).",
                    null, null, true));
                tips.Add(Tip("Другие устройства и торренты", "Other devices and torrents", "Тот же эффект дают облака, торренты и видео на других устройствах сети. Лимит скорости в торрент-клиенте (80 % канала) заметно спасает игры.",
                    "Cloud sync, torrents and video on other devices have the same effect. A speed cap in the torrent client (80 % of the line) helps games a lot."));
            }
            tips.Add(Tip("Тест bufferbloat Waveform", "Waveform's bufferbloat test", "Независимый замер с той же шкалой оценок:", "An independent test with the same grading scale:", null, "https://www.waveform.com/tools/bufferbloat"));
            Explain(bloat,
                () => L.T("Насколько вырастает задержка, пока канал загружен (кто-то качает, стримит или синхронизирует облако). Буферы роутера и провайдера наполняются, и каждый пакет игры или звонка стоит в очереди — это называется bufferbloat. " +
                          "Плохой канал выдаёт себя ещё и потерями: переполненная очередь отбрасывает пакеты. Именно это, а не «пинг в простое», чувствуется в играх, когда дома кто-то смотрит видео.",
                          "How much the delay grows while the line is busy (someone downloads, streams or syncs a cloud). The router's and the provider's buffers fill up, and every game or call packet waits in the queue — that is bufferbloat. " +
                          "A poor line also gives itself away by loss: an overflowing queue drops packets. That, not the idle ping, is what you feel in games when someone at home watches video."),
                () => L.T("Пинг каждые 200 мс к " + (InternetProbe != null ? InternetProbe.Ip.ToString() : "1.1.1.1") + " во время замера скорости, параллельно — TCP-соединение к нему же каждые 400 мс: роутеры под нагрузкой отвечают на ping в последнюю очередь, поэтому если TCP доходит, а пинги теряются, потерями считается TCP. Прирост — медиана под нагрузкой минус медиана в простое; пики — 95-й процентиль минус медиана в простое. " +
                          "Оценка по приросту, как у Waveform: A+ < 5 мс, A < 30, B < 60, C < 200, D < 400, иначе F. DriveUP понижает её за пики (≥ 100 мс — не выше B, ≥ 200 — C, ≥ 400 — D) и за потери (≥ 1 % — не выше B, ≥ 3 % — C, ≥ 10 % — D).",
                          "A ping every 200 ms to " + (InternetProbe != null ? InternetProbe.Ip.ToString() : "1.1.1.1") + " during the speed test, and a TCP connection to it every 400 ms: routers answer pings last when busy, so if TCP gets through while pings are lost, the TCP loss counts. Added delay is the loaded median minus the quiet median; peaks are the 95th percentile minus the quiet median. " +
                          "The grade by added delay, as Waveform's: A+ < 5 ms, A < 30, B < 60, C < 200, D < 400, else F. DriveUP lowers it for peaks (≥ 100 ms — at best B, ≥ 200 — C, ≥ 400 — D) and for loss (≥ 1 % — at best B, ≥ 3 % — C, ≥ 10 % — D)."),
                tips.ToArray());
        }

        /// <summary>TCP resends of DriveUP's own upload: loss on the way out, whatever else runs on the PC.</summary>
        void ReportResends(bool perConn)
        {
            Retransmit = UpResent * 100.0 / UpSent;
            double pct = Retransmit;
            var f = Add("retrans", TuneCat.NetSpeed, "\uE898", () => L.T("Повторные отправки при отдаче", "Resends while uploading"));
            NetLevel lv = pct >= 10 ? NetLevel.Bad : pct >= 3 ? NetLevel.Warn : pct >= 1 ? NetLevel.Info : NetLevel.Good;
            bool wifi = Physical != null && Physical.Kind == LinkKind.WiFi;
            Done(f, lv,
                () => Pct(pct) + " (" + UpResent.ToString("N0", CultureInfo.CurrentCulture) + L.T(" из ", " of ") + UpSent.ToString("N0", CultureInfo.CurrentCulture) + L.T(" сегментов)", " segments)"),
                () => lv == NetLevel.Good ? L.T("Отдача идёт чисто — пакеты по пути почти не теряются", "The upload goes clean — hardly anything is lost on the way")
                    : lv == NetLevel.Info ? L.T("Немного повторов — для загруженного канала это нормально", "A few resends — normal for a busy line")
                    : wifi ? L.T("Пакеты теряются на пути наружу: слабый сигнал или помехи Wi-Fi, перегруженный роутер или ограничение провайдера", "Packets get lost on the way out: weak or noisy Wi-Fi, an overloaded router or the provider's limiter")
                           : L.T("Пакеты теряются на пути наружу: кабель или порт, перегруженный роутер или ограничение провайдера", "Packets get lost on the way out: the cable or a port, an overloaded router or the provider's limiter"));
            var tips = new List<NetAdvice>();
            if (lv >= NetLevel.Warn)
            {
                tips.Add(Tip("Где теряется", "Where it is lost", "Сравните с «Задержкой до роутера»: потери уже там — домашняя сеть (кабель, Wi-Fi, роутер); там чисто — путь за роутером. SQM на роутере убирает потери от переполнения очереди.",
                    "Compare with \"Delay to the router\": loss already there means the home network (cable, Wi-Fi, router); clean there means beyond the router. SQM in the router removes loss from queue overflow."));
                if (!wifi)
                    tips.Add(Tip("Энергосбережение карты", "The card's power saving", "При потерях по кабелю DriveUP выключит Energy-Efficient Ethernet (ниже, в исправлениях).",
                        "With loss over a cable DriveUP turns Energy-Efficient Ethernet off (below, among the fixes).", null, null, true));
            }
            tips.Add(Tip("Счётчики Windows", "Windows' counters", "Общие счётчики TCP с момента загрузки (в них попадают все программы и все неудачные соединения — поэтому DriveUP мерит отдельно):",
                "TCP totals since start-up (they include every program and every failed connection — which is why DriveUP measures on its own):",
                "netstat -s -p tcp"));
            Explain(f,
                () => L.T("Когда пакет теряется по пути, TCP отправляет его ещё раз. Немного повторов при полной загрузке канала — норма: так TCP нащупывает его ширину. Много — значит, пакеты теряются: на Wi-Fi, в кабеле, в роутере или у провайдера; скорость падает, а задержка скачет. " +
                          "Общий счётчик Windows «с загрузки» для этого не годится: в него попадают все программы, прокси и VPN, недоступные серверы — он легко показывает 20 % на исправной сети.",
                          "When a packet is lost on the way, TCP sends it again. A few resends on a fully loaded line are normal: that is how TCP finds its width. Many mean packets get lost — on Wi-Fi, in the cable, the router or at the provider; speed falls and delay jumps. " +
                          "Windows' \"since start-up\" total is no good for this: it includes every program, proxy and VPN and unreachable servers — it easily shows 20 % on a healthy network."),
                () => (perConn
                        ? L.T("Статистика TCP по соединениям (ESTATS: PktsRetrans / SegsOut) только для соединений самого DriveUP во время замера отдачи — другие программы не влияют. ",
                              "Per-connection TCP statistics (ESTATS: PktsRetrans / SegsOut) of DriveUP's own connections only, during the upload test — other programs do not count. ")
                        : L.T("Счётчики TCP Windows за время замера отдачи (по соединениям считать не дали права — могли попасть и другие программы). ",
                              "Windows' TCP counters over the upload test (no rights to count per connection — other programs may be included). ")) +
                      L.T("Нормы: до 1 % — чисто, до 3 % — терпимо, от 3 % — потери, от 10 % — плохо.", "Norms: up to 1 % — clean, up to 3 % — bearable, from 3 % — loss, from 10 % — poor."),
                tips.ToArray());
        }

        /// <summary>The adapter's own error and discard counters: during the speed test, and since it came up.</summary>
        void ReportNic()
        {
            var d = NicCounters.Delta(NicLoadFrom, NicLoadTo);
            var now = NicLoadTo ?? NicCounters.Read(Physical != null ? Physical.Nic : null);
            if (d == null || now == null || d.Packets < 1000) return;
            var f = Add("nicerr", TuneCat.NetSpeed, "\uE839", () => L.T("Ошибки и отброшенные пакеты карты", "Card errors and dropped packets"));
            // Wi-Fi drivers count interference and frames for other stations as errors and drops: only a lot of them matters there.
            bool wifiNic = Physical.Kind == LinkKind.WiFi;
            bool errBad = !wifiNic && d.ErrorPct >= 0.1 && d.Errors >= 20;
            bool errWarn = wifiNic ? d.ErrorPct >= 1 && d.Errors >= 100 : d.Errors >= 10 && d.ErrorPct >= 0.01;
            // A few drops at hundreds of megabits are recovered by TCP unnoticed (a Realtek PCIe GbE: 0.13 % at 800 Mbit/s, no TCP loss):
            // worth a remark from 0.5 %, a mention from 0.1 %.
            bool discWarn = wifiNic ? d.DiscardPct >= 1 && d.InDiscards >= 200 : d.DiscardPct >= 0.5 && d.InDiscards >= 200;
            bool discSome = !discWarn && d.DiscardPct >= 0.1 && d.InDiscards >= 50;
            bool discOld = now.DiscardPct >= (wifiNic ? 1 : 0.1) && now.InDiscards >= 1000;
            NetLevel lv = errBad ? NetLevel.Bad : errWarn || discWarn ? NetLevel.Warn : discSome || discOld || now.ErrorPct >= 0.01 && now.Errors >= 50 ? NetLevel.Info : NetLevel.Good;
            DiscardsUnderLoad = discWarn;
            bool wifi = Physical.Kind == LinkKind.WiFi;
            int rxNow = 0, rxMax = 0;
            if (Physical.ClassKey != null)
            {
                int.TryParse(Reg.Get(true, Physical.ClassKey, "*ReceiveBuffers") as string ?? Reg.Get(true, Physical.ClassKey + @"\Ndi\params\*ReceiveBuffers", "default") as string ?? "", out rxNow);
                int.TryParse(Reg.Get(true, Physical.ClassKey + @"\Ndi\params\*ReceiveBuffers", "max") as string ?? "", out rxMax);
            }
            RxAtMax = rxMax > 0 && rxNow >= rxMax;
            // The loss under load was the card's own: say so on the delay rows measured before.
            if (discWarn)
                foreach (var row in new[] { BloatDownRow, BloatUpRow })
                    if (row != null && row.Problem)
                    {
                        row.Detail = () => L.T("Пакеты теряет сама сетевая карта: под нагрузкой она не успевает их принимать (см. «Ошибки и отброшенные пакеты карты»)", "The network card itself drops the packets: under load it cannot keep up (see \"Card errors and dropped packets\")");
                        if (Updated != null) Updated(row);
                    }
            Done(f, lv,
                () => L.T("за замер: ошибок ", "during the test: errors ") + d.Errors.ToString("N0", CultureInfo.CurrentCulture) + L.T(", отброшено ", ", dropped ") + d.InDiscards.ToString("N0", CultureInfo.CurrentCulture) +
                      L.T(" из ", " of ") + d.Packets.ToString("N0", CultureInfo.CurrentCulture) +
                      L.T(" · с подключения: ошибок ", " · since connected: errors ") + now.Errors.ToString("N0", CultureInfo.CurrentCulture) + L.T(", отброшено ", ", dropped ") + now.InDiscards.ToString("N0", CultureInfo.CurrentCulture) +
                      (now.InDiscards > 0 ? " (" + Pct(now.DiscardPct) + ")" : ""),
                () => lv == NetLevel.Good ? L.T("Карта принимает и отправляет пакеты без ошибок и не теряет их", "The card sends and receives without errors and drops nothing")
                    : errWarn || errBad ? (wifi ? L.T("Пакеты приходят битыми: помехи или слабый сигнал Wi-Fi", "Packets arrive damaged: Wi-Fi interference or a weak signal")
                                                : L.T("Пакеты приходят битыми (ошибки CRC): почти всегда кабель, разъём или порт", "Packets arrive damaged (CRC errors): almost always the cable, a plug or the port"))
                    : discWarn ? L.T("Под нагрузкой карта не успевает принимать — пакеты отбрасываются: мало буферов приёма или драйвер", "Under load the card cannot keep up and drops packets: too few receive buffers, or the driver")
                    : discSome ? L.T("Под нагрузкой карта изредка отбрасывает пакеты — TCP их переотправляет, на скорость почти не влияет", "Under load the card now and then drops a packet — TCP sends it again, speed is barely affected")
                    : L.T("Карта отбрасывала часть входящих пакетов раньше — под нагрузкой сейчас не отбрасывает. Часто это пакеты чужих протоколов и VLAN, их отбрасывают нарочно", "The card dropped some incoming packets earlier — not under load now. Often those are packets of foreign protocols and VLANs, dropped on purpose"));
            var tips = new List<NetAdvice>();
            if (errWarn || errBad)
                tips.Add(wifi ? Tip("Сигнал и канал", "Signal and channel", "Битые кадры Wi-Fi — помехи: см. «Сигнал Wi-Fi» и «Соседние сети».", "Damaged Wi-Fi frames are interference: see \"Wi-Fi signal\" and \"Neighbour networks\".")
                              : Tip("Заменить кабель", "Replace the cable", "Ошибки CRC — это повреждённые по пути кадры. Замените патч-корд (Cat5e/Cat6, не плоский и не самодельный), попробуйте другой порт роутера или коммутатора.",
                                  "CRC errors are frames damaged on the way. Replace the patch cord (Cat5e/Cat6, not flat, not home-made) and try another port on the router or switch."));
            if (discWarn && !RxAtMax && rxMax > 0)
                tips.Add(Tip("Больше буферов приёма", "More receive buffers", "Карта отбрасывает пакеты, когда ей негде их держать. DriveUP поднимет «Буферы приёма» с " + rxNow + " до максимума драйвера " + rxMax + " (ниже, в исправлениях; карта перезапустится на пару секунд).",
                    "The card drops packets when it has nowhere to keep them. DriveUP raises \"Receive Buffers\" from " + rxNow + " to the driver's maximum " + rxMax + " (below, among the fixes; the card restarts for a couple of seconds).", null, null, true));
            if (discWarn && RxAtMax)
            {
                tips.Add(Tip("Буферы уже на максимуме — нужен драйвер", "Buffers are at the maximum — the driver it is",
                    "«Буферы приёма» уже " + rxNow + " — больше этот драйвер не разрешает. Отбрасывание под нагрузкой обычно лечит новый драйвер карты (см. строку «Драйвер сетевой карты»: если DriveUP нашёл версию новее — ставьте её).",
                    "\"Receive Buffers\" are already " + rxNow + " — this driver allows no more. Dropping under load is usually cured by a new card driver (see the \"Network driver\" row: if DriveUP found a newer version, install it)."));
                tips.Add(Tip("Модерация прерываний — «Вкл»", "Interrupt moderation on", "Если её кто-то выключил «для игр», карта обрабатывает каждый пакет отдельным прерыванием и на сотнях мегабит не успевает. Должно быть «Вкл» (как в Windows):",
                    "If someone turned it off \"for games\", the card raises an interrupt per packet and cannot keep up at hundreds of megabits. It should be On (as Windows ships it):",
                    "Get-NetAdapterAdvancedProperty -Name '" + Physical.Name.Replace("'", "''") + "' -RegistryKeyword '*InterruptModeration','*ReceiveBuffers','*RSS' | Format-Table DisplayName, DisplayValue"));
            }
            tips.Add(Tip("Счётчики карты", "The card's counters", "Все счётчики драйвера с момента подключения:", "Every driver counter since the link came up:",
                "Get-NetAdapterStatistics -Name '" + Physical.Name.Replace("'", "''") + "' | Format-List ReceivedPacketErrors, ReceivedDiscardedPackets, OutboundPacketErrors, OutboundDiscardedPackets, ReceivedUnicastPackets"));
            Explain(f,
                () => L.T("Ошибки — кадры, которые пришли повреждёнными (не сошлась контрольная сумма): по кабелю это почти всегда физика — кабель, разъём, порт. " +
                          "Отброшенные — целые пакеты, которые карта или драйвер не приняли: не хватило буферов под нагрузкой или пакет не для этого компьютера (чужой протокол, VLAN). Важны те, что появляются под нагрузкой.",
                          "Errors are frames that arrived damaged (the checksum did not match): over a cable it is almost always physical — the cable, a plug, a port. " +
                          "Dropped are whole packets the card or driver did not take: no buffers left under load, or a packet not meant for this computer (a foreign protocol, a VLAN). The ones that appear under load matter."),
                () => L.T("Счётчики адаптера (GetIfEntry2: InErrors, OutErrors, InDiscards) до и после замера скорости и с момента подключения. Нормы: ошибок под нагрузкой меньше 0,01 %, отброшенных меньше 0,1 %.",
                          "The adapter's counters (GetIfEntry2: InErrors, OutErrors, InDiscards) before and after the speed test and since the link came up. Norms: errors under load below 0.01 %, drops below 0.1 %."),
                tips.ToArray());
        }

        /// <summary>
        /// Loads the line with 4 streams for the given time (or until the cap) while pinging; returns Mbit/s from
        /// the moment it got going (the first 1.5 s are left out), or NaN. With a watch, DriveUP's own connections are
        /// counted per connection (read before they are closed).
        /// </summary>
        double Load(bool down, NetFinding speedRow, NetFinding chart, Rtt into, double seconds, long cap, TcpWatch watch)
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
                        catch (WebException we)
                        {
                            // 429: the test server limits repeated tests from one address for a while.
                            var hr = we.Response as HttpWebResponse;
                            if (hr != null) LoadStatus = (int)hr.StatusCode;
                            if (!stop) Thread.Sleep(LoadStatus == 429 ? 1000 : 200);
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
            Thread shaker = null;
            var tcpInto = down ? LoadDownTcp : LoadUpTcp;
            if (!InternetProbe.Tcp && tcpInto != null)
            {
                var tp = new Prober(InternetProbe.Ip, true, 443);
                shaker = new Thread(() => Sample(tp, tcpInto, null, seconds + 1, 400, () => stop)) { IsBackground = true, Name = "net load tcp", Priority = ThreadPriority.AboveNormal };
                shaker.Start();
            }
            pinger.Start();
            if (watch != null) watch.Start();
            double mbps = double.NaN;
            while (!Cancel && sw.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(250);
                if (watch != null) watch.Poll();
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
            // Read before the connections close: a closed one has no statistics left.
            if (watch != null) { UpShares = watch.Works ? watch.Stop() : new List<TcpShare>(); TcpDiag += "; up: works=" + watch.Works + " err=" + watch.LastError + " programs=" + UpShares.Count; }
            stop = true;
            lock (reqs) foreach (var r in reqs.ToList()) try { r.Abort(); } catch { }
            foreach (var t in threads) t.Join(3000);
            pinger.Join(2000);
            if (shaker != null) shaker.Join(2000);
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
            Explain(f,
                () => L.T("Две минуты наблюдения в покое ловят то, что 12 секунд не видят: редкие потери и всплески по расписанию. Регулярные всплески по Wi-Fi — фоновое сканирование сетей (каждые 30–120 с); случайные — помехи, другие устройства или провайдер.",
                          "Two quiet minutes catch what 12 seconds miss: rare loss and spikes on a schedule. Regular spikes over Wi-Fi are background network scans (every 30–120 s); random ones are interference, other devices or the provider."),
                () => L.T("Пинг каждые 250 мс в течение 120 с — до роутера по Wi-Fi, иначе до интернета. Всплеск — больше 3 медиан и на 25 мс; период — если промежутки между всплесками отличаются меньше чем на 20 %. Норма: потери меньше 0,5 %, не больше 6 всплесков.",
                          "A ping every 250 ms for 120 s — to the router over Wi-Fi, else to the internet. A spike is over 3 medians and 25 ms more; a period is found when the gaps between spikes differ by under 20 %. Normal: loss under 0.5 %, at most 6 spikes."),
                period > 0 && Physical.Kind == LinkKind.WiFi
                    ? Tip("Фоновое сканирование Wi-Fi", "Background Wi-Fi scans", "Его запускают службы определения местоположения и программы, которые просят список сетей. Выключите «Определение местоположения» (Параметры → Конфиденциальность) или закройте такие программы; для игр — кабель.",
                        "Location services and apps that ask for the network list start it. Turn off Location (Settings → Privacy) or close such apps; for games — a cable.", null, "ms-settings:privacy-location") : null,
                Tip("Длинный пинг самому", "A long ping yourself", "Непрерывный пинг; остановить — Ctrl+C, затем смотрите на потери:", "A continuous ping; stop with Ctrl+C and look at the loss:",
                    "ping -t " + (p == InternetProbe ? InternetProbe.Ip.ToString() : Physical.Gateway.ToString())));
        }

        // ---------- Windows and the adapter ----------

        /// <summary>Power-saving keywords of network drivers: the standard one first, then the makers' own.</summary>
        public static readonly string[] SavingKeywords = { "*EEE", "EEE", "AdvancedEEE", "EnableGreenEthernet", "GigaLite", "PowerSavingMode", "EEELinkAdvertisement", "AutoDisableGigabit" };

        /// <summary>Tcpip parameters that still work on Windows 10/11 and only do harm when a "tweaker" set them.</summary>
        public static readonly string[] HarmfulTcp = { "EnablePMTUDiscovery", "DisableTaskOffload", "DefaultTTL" };

        /// <summary>Network services of Windows and the start type they come with.</summary>
        static readonly string[][] NetServices =
        {
            new[] { "Dhcp", "auto" }, new[] { "Dnscache", "auto" }, new[] { "nsi", "auto" }, new[] { "netprofm", "manual" },
            new[] { "NlaSvc", "manual" }, new[] { "WlanSvc", "auto" }, new[] { "WinHttpAutoProxySvc", "manual" },
        };

        public static string CongestionName(int v)
        {
            switch (v)
            {
                case 0: return "default";
                case 1: return "newreno";
                case 2: return "ctcp";
                case 3: return "dctcp";
                case 4: return "ledbat";
                case 5: return "cubic";
                case 6: return "bbr2";
                default: return "#" + v;
            }
        }

        void ReadSettings()
        {
            var p = Physical;
            if (p != null && p.ClassKey != null)
                foreach (var kw in SavingKeywords.Concat(new[] { "*SpeedDuplex", "*RSS", "*InterruptModeration", "*JumboPacket", "*SelectiveSuspend", "*ReceiveBuffers", "PnPCapabilities", "DeviceInstanceID" }))
                {
                    var v = Reg.Get(true, p.ClassKey, kw);
                    if (v != null) NicValues[kw] = Convert.ToString(v, CultureInfo.InvariantCulture);
                }
            string instance;
            UsbAdapter = NicValues.TryGetValue("DeviceInstanceID", out instance) && instance.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);

            if (p != null && p.Kind == LinkKind.Ethernet) ReadEthernet(p);
            if (p != null && p.Kind == LinkKind.WiFi) ReadWifiPower();
            if (p != null) ReadAdapterPower(p);
            ReadTcp();
            ReadPrograms();
            ReadServices();
            ReadProxyAndHosts();
            if (p != null) ReadDriver(p);
        }

        void ReadEthernet(NetAdapter p)
        {
            var eee = SavingKeywords.Where(k => NicValues.ContainsKey(k) && NicValues[k] != "0").ToList();
            var f = Add("eee", TuneCat.NetSystem, "\uE83E", () => L.T("Энергосбережение Ethernet (EEE)", "Ethernet power saving (EEE)"));
            bool wiredTrouble = WiredTrouble;
            Done(f, eee.Count == 0 ? NetLevel.Good : wiredTrouble || p.IsIntelI225 ? NetLevel.Warn : NetLevel.Info,
                () => eee.Count == 0 ? L.T("выключено", "off") : L.T("включено", "on") + " (" + string.Join(", ", eee) + ")",
                () => eee.Count == 0 ? L.T("Карта не засыпает между пакетами", "The card does not doze between packets")
                    : p.IsIntelI225 ? L.T("На Intel I225/I226 оно вызывает обрывы и подвисания — Intel советует его выключить", "On Intel I225/I226 it causes drops and stutter — Intel advises turning it off")
                    : wiredTrouble ? L.T("При обрывах и потерях по кабелю его выключение — проверенное средство", "With drops and loss over a cable, turning it off is a proven fix")
                    : L.T("Связь ровная и без обрывов — значит, не мешает; трогать незачем", "The link is steady and does not drop — so it does no harm; no need to touch it"));
            Explain(f,
                () => L.T("Energy-Efficient Ethernet (IEEE 802.3az) и фирменные режимы («Зелёный Ethernet», Power Saving Mode, GigaLite) усыпляют карту в паузах между пакетами. На исправной сети это незаметно. " +
                          "Но с частью коммутаторов и роутеров карта просыпается с запозданием или теряет связь — у Realtek это классическая причина обрывов, у Intel I225/I226 Intel сама советует EEE выключать.",
                          "Energy-Efficient Ethernet (IEEE 802.3az) and the makers' own modes (Green Ethernet, Power Saving Mode, GigaLite) put the card to sleep between packets. On a healthy network you do not notice. " +
                          "But with some switches and routers the card wakes late or loses the link — a classic cause of drops on Realtek, and on Intel I225/I226 Intel itself advises turning EEE off."),
                () => L.T("Дополнительные свойства драйвера (раздел карты в классе сетевых адаптеров): ", "The driver's advanced properties (the card's key in the network adapter class): ") + string.Join(", ", SavingKeywords) +
                      L.T(". Выключить предлагается только при обрывах, потерях или ошибках по кабелю, а на I225/I226 — всегда.", ". Turning off is offered only with drops, loss or errors on the cable, and always on I225/I226."),
                eee.Count > 0 && (wiredTrouble || p.IsIntelI225)
                    ? Tip("Выключить энергосбережение карты", "Turn the card's power saving off", "DriveUP выключит все такие режимы драйвера (ниже, в исправлениях) и один раз перезапустит карту — связь пропадёт на пару секунд.",
                        "DriveUP turns all such driver modes off (below, among the fixes) and restarts the card once — the connection drops for a couple of seconds.", null, null, true) : null,
                Tip("Посмотреть или поменять вручную", "See or change by hand", "Все режимы энергосбережения карты и их значения:", "All the card's power-saving modes and their values:",
                    "Get-NetAdapterAdvancedProperty -Name '" + p.Name.Replace("'", "''") + "' | Where-Object RegistryKeyword -Match 'EEE|Green|Saving|GigaLite|AutoDisable' | Format-Table DisplayName, DisplayValue, RegistryKeyword"),
                p.IsIntelI225 ? Tip("Intel о I225/I226", "Intel on I225/I226", "Рекомендация Intel по обрывам этих карт:", "Intel's advice on drops with these cards:", null, "https://www.intel.com/content/www/us/en/support/articles/000091005.html") : null);

            string sd;
            if (NicValues.TryGetValue("*SpeedDuplex", out sd) && sd != "0")
            {
                var fs = Add("duplex", TuneCat.NetSystem, "\uE839", () => L.T("Скорость и дуплекс карты", "Card speed and duplex"));
                Done(fs, NetLevel.Warn, () => L.T("задано вручную", "set by hand") + " (" + sd + ")",
                    () => L.T("Если вторая сторона в «Авто», возможен полудуплекс с потерями и скорость ниже гигабита. Почти всегда правильно «Авто».",
                              "If the other side is on Auto this can end in half duplex with loss and less than gigabit. Auto is almost always right."));
                Explain(fs,
                    () => L.T("Когда на одной стороне скорость задана вручную, автосогласование не работает, и вторая сторона по стандарту выбирает полудуплекс. Итог — коллизии, потери и скорость ниже возможной. " +
                              "Ручная скорость нужна только для старого оборудования, которое не умеет договариваться.",
                              "When one side has its speed set by hand, auto-negotiation does not happen and the other side falls back to half duplex as the standard says. The result is collisions, loss and less speed than possible. " +
                              "A fixed speed is only for old equipment that cannot negotiate."),
                    () => L.T("Свойство драйвера *SpeedDuplex: 0 — автосогласование.", "The driver property *SpeedDuplex: 0 is auto-negotiation."),
                    Tip("Вернуть «Авто»", "Back to Auto", "DriveUP вернёт автосогласование (ниже, в исправлениях); порт коммутатора тоже должен быть в «Авто».",
                        "DriveUP sets auto-negotiation back (below, among the fixes); the switch port must be on Auto as well.", null, null, true));
            }

            // Jumbo frames that the router does not take: large packets on the home network are lost.
            int jumbo;
            if (NicValues.ContainsKey("*JumboPacket") && int.TryParse(NicValues["*JumboPacket"], out jumbo) && jumbo > 1514 && p.Gateway != null)
            {
                bool big = false, small = false;
                try
                {
                    using (var ping = new Ping())
                    {
                        small = ping.Send(p.Gateway, 800, new byte[1472], new PingOptions(64, true)).Status == IPStatus.Success;
                        big = ping.Send(p.Gateway, 800, new byte[Math.Min(jumbo, 9000) - 64], new PingOptions(64, true)).Status == IPStatus.Success;
                    }
                }
                catch { }
                JumboBroken = small && !big;
                var fj = Add("jumbo", TuneCat.NetSystem, "\uE9D2", () => L.T("Большие кадры (Jumbo)", "Jumbo frames"));
                Done(fj, JumboBroken ? NetLevel.Warn : NetLevel.Info, () => jumbo + L.T(" байт", " bytes"),
                    () => JumboBroken ? L.T("Роутер не принимает большие кадры: крупные пакеты в домашней сети теряются", "The router does not take jumbo frames: large packets on the home network are lost")
                                      : L.T("Включены, и роутер их принимает. Для интернета пользы нет — только для NAS в той же сети", "On, and the router takes them. No use for the internet — only for a NAS on the same network"));
                Explain(fj,
                    () => L.T("Jumbo-кадры (больше 1500 байт) ускоряют только передачу внутри локальной сети, и только если их поддерживает каждое устройство на пути. Интернет их не пропускает. " +
                              "Если роутер или коммутатор их не умеет, большие пакеты молча теряются — подвисают копирование файлов, общие папки и принтеры.",
                              "Jumbo frames (over 1500 bytes) only speed up transfers inside the local network, and only if every device on the way supports them. The internet does not carry them. " +
                              "If the router or switch does not, large packets are silently lost — file copies, shared folders and printers hang."),
                    () => L.T("Свойство *JumboPacket; ping роутера с запретом фрагментации: 1500 байт и почти полный кадр.", "The *JumboPacket property; pings to the router with Don't Fragment: 1500 bytes and a nearly full frame."),
                    JumboBroken ? Tip("Вернуть 1514", "Back to 1514", "DriveUP вернёт стандартный размер кадра (ниже, в исправлениях).", "DriveUP sets the standard frame size back (below, among the fixes).", null, null, true) : null);
            }
        }

        void ReadWifiPower()
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
            Explain(f,
                () => L.T("Режим энергосбережения беспроводного адаптера в схеме питания: в экономичных режимах адаптер засыпает между пакетами и просыпается по сигналу роутера — задержка растёт и скачет, особенно в играх и звонках. От батареи это оправдано, от сети — нет.",
                          "The wireless adapter's power-saving mode in the power plan: in saving modes the adapter sleeps between packets and wakes on the router's beacon — the delay grows and jumps, especially in games and calls. On battery it is worth it, on mains it is not."),
                () => L.T("Схема питания, раздел «Параметры адаптера беспроводной сети», значение от сети. На компьютерах с современным режимом ожидания (S0) этой настройки нет — Windows решает сама.",
                          "The power plan, \"Wireless Adapter Settings\", the mains value. Computers with modern standby (S0) do not have it — Windows decides itself."),
                ac != 0 && ac != 99 ? Tip("Максимальная производительность от сети", "Maximum performance on mains", "DriveUP переключит значение «от сети» (от батареи останется как было) — ниже, в исправлениях.",
                    "DriveUP switches the mains value (battery stays as it was) — below, among the fixes.", null, null, true) : null,
                Tip("Посмотреть в схеме питания", "See it in the power plan", "Текущие значения от сети и от батареи:", "The current mains and battery values:",
                    "powercfg /query SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1"));
        }

        /// <summary>Windows may switch the card off to save power (Device Manager → Power Management); USB adapters may also be suspended.</summary>
        void ReadAdapterPower(NetAdapter p)
        {
            string ss;
            bool suspend = UsbAdapter && NicValues.TryGetValue("*SelectiveSuspend", out ss) && ss != "0";
            string pnp;
            int caps;
            bool mayOff = !(NicValues.TryGetValue("PnPCapabilities", out pnp) && int.TryParse(pnp, out caps) && (caps & 0x18) == 0x18);
            bool trouble = DropTrouble || (p.Kind == LinkKind.WiFi ? WifiTrouble : WiredTrouble);
            if (!suspend && !(mayOff && trouble)) return;
            var f = Add("nicpower", TuneCat.NetSystem, "\uE83E", () => L.T("Отключение адаптера ради экономии", "Adapter turned off to save power"));
            Done(f, trouble ? NetLevel.Warn : NetLevel.Info,
                () => (suspend ? L.T("USB-адаптер усыпляется (Selective Suspend)", "the USB adapter is suspended (Selective Suspend)") : "") +
                      (suspend && mayOff ? " · " : "") + (mayOff ? L.T("Windows может отключать адаптер", "Windows may turn the adapter off") : ""),
                () => trouble ? L.T("При обрывах это первое, что стоит выключить", "With drops this is the first thing to turn off")
                              : L.T("Обрывов нет, но USB-адаптеры после засыпания иногда не просыпаются", "No drops, but USB adapters sometimes do not wake up after suspending"));
            Explain(f,
                () => L.T("Windows может отключать сетевой адаптер для экономии энергии (галочка «Разрешить отключение этого устройства» в диспетчере устройств), а USB-адаптеры — ещё и усыплять между передачами. " +
                          "Иногда адаптер не просыпается вовремя: связь пропадает до переподключения, а после сна адаптер «исчезает».",
                          "Windows may turn a network adapter off to save power (the \"Allow the computer to turn off this device\" box in Device Manager), and suspend USB adapters between transfers as well. " +
                          "Sometimes the adapter does not wake in time: the link is gone until it reconnects, and after sleep the adapter \"disappears\"."),
                () => L.T("Значение PnPCapabilities в разделе драйвера (24 — отключать запрещено) и свойство *SelectiveSuspend у USB-адаптеров. Предлагается при обрывах или для USB-адаптера.",
                          "PnPCapabilities in the driver's key (24 — turning off not allowed) and *SelectiveSuspend on USB adapters. Offered with drops, or for a USB adapter."),
                Tip("Запретить отключение", "Do not allow turning it off", "DriveUP снимет разрешение и выключит усыпление USB (ниже, в исправлениях); карта перезапустится.",
                    "DriveUP removes the permission and turns USB suspend off (below, among the fixes); the card restarts.", null, null, true),
                Tip("Вручную", "By hand", "Диспетчер устройств → сетевой адаптер → «Управление электропитанием» → снять «Разрешить отключение этого устройства для экономии энергии».",
                    "Device Manager → the network adapter → Power Management → clear \"Allow the computer to turn off this device to save power\".", "devmgmt.msc"));
        }

        void ReadTcp()
        {
            // TCP receive window tuning, RSS and the congestion control: as Windows ships them, unless a "tweaker" changed them.
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT AutoTuningLevelLocal, CongestionProvider FROM MSFT_NetTCPSetting WHERE SettingName='Internet'"))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                        {
                            AutoTuning = NetFix.TuningName(Convert.ToInt32(o["AutoTuningLevelLocal"], CultureInfo.InvariantCulture));
                            if (o["CongestionProvider"] != null) Congestion = CongestionName(Convert.ToInt32(o["CongestionProvider"], CultureInfo.InvariantCulture));
                        }
            }
            catch { }
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT ReceiveSideScaling FROM MSFT_NetOffloadGlobalSetting"))
                    foreach (ManagementObject o in s.Get())
                        using (o) RssOff = Convert.ToInt32(o["ReceiveSideScaling"], CultureInfo.InvariantCulture) == 0;
            }
            catch { }
            const string Tcp = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
            foreach (var name in HarmfulTcp)
            {
                int? v = null;
                try { v = Reg.Dword(true, Tcp, name); } catch { }
                if (!v.HasValue) continue;
                bool bad = name == "EnablePMTUDiscovery" ? v == 0 : name == "DisableTaskOffload" ? v == 1 : v < 32;
                if (bad) TcpLeftovers.Add(Tuple.Create(name, v.Value));
            }
            string rssNic;
            bool nicRssOff = NicValues.TryGetValue("*RSS", out rssNic) && rssNic == "0";
            bool congOk = Congestion == "" || Congestion == "cubic" || Congestion == "default";
            var ft = Add("tcp", TuneCat.NetSystem, "\uE713", () => L.T("Настройки TCP Windows", "Windows TCP settings"));
            bool tcpOk = (AutoTuning == "" || AutoTuning == "normal") && !RssOff && !nicRssOff && congOk && TcpLeftovers.Count == 0;
            Done(ft, tcpOk ? NetLevel.Good : NetLevel.Warn,
                () => L.T("автонастройка окна: ", "window auto-tuning: ") + (AutoTuning == "" ? "?" : AutoTuning) + " · RSS: " + (RssOff || nicRssOff ? L.T("выключен", "off") : L.T("включён", "on")) +
                      (Congestion.Length > 0 ? L.T(" · управление перегрузкой: ", " · congestion control: ") + Congestion.ToUpperInvariant() : "") +
                      (TcpLeftovers.Count > 0 ? " · " + string.Join(", ", TcpLeftovers.Select(t => t.Item1 + "=" + t.Item2)) : ""),
                () => tcpOk ? L.T("Как задумано в Windows — так и должно быть", "As Windows intends — as it should be")
                    : L.T("Изменено «ускорителем» или твикером: такие правки не ускоряют, а режут скорость или ломают соединения. Вернуть как в Windows — ниже.",
                          "Changed by a \"booster\" or a tweak: such changes do not speed anything up, they cut speed or break connections. Back to Windows defaults — below."));
            var tips = new List<NetAdvice>();
            if (AutoTuning != "" && AutoTuning != "normal")
                tips.Add(Tip("Автонастройка окна — normal", "Window auto-tuning to normal", "Без неё окно приёма TCP ограничено 64 КБ: на быстрых и дальних соединениях скорость падает в разы (100 Мбит/с при задержке 50 мс требуют окна ~600 КБ). DriveUP вернёт normal.",
                    "Without it the TCP receive window is capped at 64 KB: fast and distant connections lose most of their speed (100 Mbit/s at 50 ms needs a ~600 KB window). DriveUP sets normal back.", "netsh int tcp set global autotuninglevel=normal", null, true));
            if (RssOff || nicRssOff)
                tips.Add(Tip("Включить RSS", "Turn RSS on", "Receive Side Scaling распределяет приём пакетов по ядрам; без него всё делает одно ядро, и на гигабите оно не успевает. DriveUP включит.",
                    "Receive Side Scaling spreads packet receiving over the cores; without it one core does it all and cannot keep up at gigabit speed. DriveUP turns it on.", "netsh int tcp set global rss=enabled", null, true));
            if (!congOk)
                tips.Add(Tip("Вернуть CUBIC", "Back to CUBIC", Congestion == "bbr2"
                        ? "BBR2 в Windows 11 остаётся экспериментальным: после его включения известны зависания соединений (особенно к localhost — WSL, Docker, среды разработки, лаунчеры). Microsoft по умолчанию использует CUBIC. DriveUP вернёт CUBIC."
                        : "Windows 10/11 по умолчанию используют CUBIC; CTCP, NewReno и прочие советуют старые «оптимизаторы» времён Windows 7 — на современных линиях они медленнее. DriveUP вернёт CUBIC.",
                    Congestion == "bbr2"
                        ? "BBR2 is still experimental in Windows 11: connections are known to hang after enabling it (especially to localhost — WSL, Docker, dev tools, launchers). Microsoft uses CUBIC by default. DriveUP sets CUBIC back."
                        : "Windows 10/11 use CUBIC by default; CTCP, NewReno and the rest come from Windows 7-era \"optimizers\" — on modern lines they are slower. DriveUP sets CUBIC back.",
                    "netsh int tcp set supplemental template=internet congestionprovider=cubic", null, true));
            foreach (var t in TcpLeftovers)
                tips.Add(Tip("Убрать " + t.Item1 + "=" + t.Item2, "Remove " + t.Item1 + "=" + t.Item2,
                    t.Item1 == "EnablePMTUDiscovery" ? "Без поиска MTU пути Windows шлёт в интернет пакеты по 576 байт — больше накладных расходов, медленнее. DriveUP удалит значение (после перезагрузки)."
                    : t.Item1 == "DisableTaskOffload" ? "Выключает аппаратную разгрузку карты: контрольные суммы и нарезку пакетов считает процессор — выше нагрузка и задержка. DriveUP удалит значение (после перезагрузки)."
                    : "Слишком малый TTL: пакеты не доживают до дальних серверов. DriveUP удалит значение (после перезагрузки).",
                    t.Item1 == "EnablePMTUDiscovery" ? "Without path MTU discovery Windows sends 576-byte packets to the internet — more overhead, slower. DriveUP deletes the value (after a restart)."
                    : t.Item1 == "DisableTaskOffload" ? "Turns the card's hardware offload off: the processor computes checksums and splits packets — more load and delay. DriveUP deletes the value (after a restart)."
                    : "Too small a TTL: packets do not live long enough to reach distant servers. DriveUP deletes the value (after a restart).", null, null, true));
            tips.Add(Tip("Посмотреть всё сразу", "See it all at once", "Глобальные параметры TCP и шаблон для интернета:", "The global TCP parameters and the internet template:",
                "netsh int tcp show global; Get-NetTCPSetting -SettingName Internet | Format-List CongestionProvider, AutoTuningLevelLocal, EcnCapability"));
            Explain(ft,
                () => L.T("Windows 10/11 настраивают TCP сами, под каждое соединение, и делают это лучше ручных правок. «Ускорители» и твики из старых гайдов (отключение автонастройки, CTCP, TcpAckFrequency, NetworkThrottlingIndex, отключение Nagle) скорость интернета не поднимают; часть из них режет её в разы. " +
                          "DriveUP проверяет только те настройки, вред которых измерим, и предлагает вернуть их как в Windows — ничего «экстремального» он не включает.",
                          "Windows 10/11 tune TCP themselves, for each connection, and do it better than manual tweaks. \"Boosters\" and tweaks from old guides (auto-tuning off, CTCP, TcpAckFrequency, NetworkThrottlingIndex, Nagle off) do not raise internet speed; some cut it several times over. " +
                          "DriveUP checks only the settings whose harm is measurable and offers to put them back as Windows has them — it does not turn on anything \"extreme\"."),
                () => L.T("MSFT_NetTCPSetting (шаблон Internet): автонастройка окна и управление перегрузкой; MSFT_NetOffloadGlobalSetting и свойство карты *RSS; значения EnablePMTUDiscovery, DisableTaskOffload, DefaultTTL в Tcpip\\Parameters. " +
                          "Норма: normal, RSS включён, CUBIC, значений в реестре нет.",
                          "MSFT_NetTCPSetting (the Internet template): window auto-tuning and congestion control; MSFT_NetOffloadGlobalSetting and the card's *RSS; EnablePMTUDiscovery, DisableTaskOffload, DefaultTTL under Tcpip\\Parameters. " +
                          "Normal: normal, RSS on, CUBIC, no registry values."),
                tips.ToArray());
        }

        /// <summary>Programs whose own connections keep resending, and filters that sit in the middle of all traffic.</summary>
        void ReadPrograms()
        {
            int me;
            using (var self = Process.GetCurrentProcess()) me = self.Id;
            var heavy = IdleShares.Where(s => s.Pid != me && ((s.Resent >= 30 && s.Pct >= 10) || s.Attempts >= 10)).OrderByDescending(s => s.Resent + s.Attempts * 4L).Take(3).ToList();
            if (heavy.Count > 0)
            {
                var top = heavy[0];
                string path = "";
                try { using (var pr = Process.GetProcessById(top.Pid)) path = pr.MainModule.FileName; } catch { }
                var f = Add("tcpapps", TuneCat.NetSystem, "\uE7BA", () => L.T("Программы с потерями TCP", "Programs losing TCP packets"));
                bool warn = heavy.Sum(s => s.Resent) >= 200 || heavy.Sum(s => s.Attempts) >= 20;
                Done(f, warn ? NetLevel.Warn : NetLevel.Info,
                    () => string.Join(" · ", heavy.Select(s => s.Process + ": " +
                        string.Join(", ", new[]
                        {
                            s.Attempts > 0 ? L.Count(s.Attempts, "зависшая попытка соединения", "зависшие попытки соединения", "зависших попыток соединения", "hanging connection attempt", "hanging connection attempts") : null,
                            s.Resent > 0 ? Pct(s.Pct) + L.T(" повторов в ", " resent on ") + L.Count(s.Connections, "соединении", "соединениях", "соединениях", "connection", "connections") : null,
                            s.Resent == 0 && s.Connections >= 50 ? L.Count(s.Connections, "соединение", "соединения", "соединений", "connection", "connections") + L.T(" за 12 с", " in 12 s") : null,
                        }.Where(x => x != null)))),
                    () => L.T("Повторы даёт не сеть, а эта программа: её соединения теряют пакеты по пути (блокировки, недоступные серверы, прокси/VPN). Отсюда и большой процент повторов в счётчиках Windows",
                              "The resends come from this program, not the network: its connections lose packets on the way (blocking, unreachable servers, a proxy/VPN). That is where the big resend share in Windows' counters comes from"));
                Explain(f,
                    () => L.T("Пока DriveUP мерил задержку, сеть была почти свободна, а эта программа раз за разом пыталась подключиться к серверам, которые не отвечают (каждая попытка — повторные пакеты SYN), или переотправляла данные в уже открытых соединениях. Так ведут себя соединения к заблокированным или перегруженным серверам, прокси, VPN-клиенты и обход блокировок. " +
                              "Другим программам это почти не мешает, но именно отсюда «повторы TCP 20 %» в общих счётчиках Windows; немного занимает канал и процессор.",
                              "While DriveUP measured the delay the network was nearly idle, yet this program kept trying to reach servers that do not answer (every attempt is repeated SYN packets) or resent data on open connections. That is typical of connections to blocked or overloaded servers, proxies, VPN clients and blocking workarounds. " +
                              "Other programs barely suffer, but that is where the \"20 % TCP resends\" in Windows' totals come from; it takes a little of the line and the processor."),
                    () => L.T("За 12 с замера задержки: таблица TCP-соединений раз в секунду (попытки в состоянии SYN_SENT — по программам) и статистика TCP по соединениям (ESTATS: повторы в открытых соединениях). Показаны программы с 10+ зависшими попытками или 30+ повторами (больше 10 %). Всего по Windows за это время: ",
                              "Over the 12 s delay test: the TCP table once a second (attempts in SYN_SENT, per program) and per-connection TCP statistics (ESTATS: resends on open connections). Shown are programs with 10+ hanging attempts or 30+ resends (over 10 %). Windows' total over that time: ") +
                          (IdleSent > 0 ? Pct(IdleResent * 100.0 / IdleSent) + " (" + IdleResent.ToString("N0", CultureInfo.CurrentCulture) + L.T(" из ", " of ") + IdleSent.ToString("N0", CultureInfo.CurrentCulture) + ")" : "—") +
                          (path.Length > 0 ? L.T(". Файл: ", ". File: ") + path : ""),
                    Tip("Нужна ли программа", "Is the program needed", "Если это забытый прокси, VPN или «ускоритель» — закройте его или уберите из автозагрузки и проверьте сеть снова.",
                        "If it is a forgotten proxy, VPN or \"booster\", close it or remove it from start-up and check the network again."),
                    Tip("Куда она подключается", "Where it connects", "Соединения программы по адресам:", "The program's connections by address:",
                        "Get-NetTCPConnection -OwningProcess " + top.Pid + " | Group-Object RemoteAddress, State | Sort-Object Count -Descending | Select-Object -First 20 Count, Name"));
            }

            var cept = Interceptors.Running();
            if (cept.Count > 0)
            {
                var f = Add("intercept", TuneCat.NetSystem, "\uE72E", () => L.T("Перехват трафика", "Traffic interception"));
                Done(f, NetLevel.Info, () => string.Join(", ", cept),
                    () => L.T("Программа обхода блокировок пропускает весь трафик через себя: задержка и скорость измерены вместе с ней", "A blocking workaround passes all traffic through itself: the delay and speed were measured with it"));
                Explain(f,
                    () => L.T("Драйвер WinDivert и программы вроде zapret / GoodbyeDPI перехватывают пакеты и подменяют их, чтобы обойти блокировки. Это добавляет обработку каждому пакету, а некоторые их режимы нарочно шлют «фальшивые» и повторные пакеты — растут повторы TCP. " +
                              "Если сеть ведёт себя странно, проверьте её с остановленным перехватом.",
                              "The WinDivert driver and programs like zapret / GoodbyeDPI intercept and alter packets to get around blocking. That adds processing to every packet, and some of their modes deliberately send fake and repeated packets — TCP resends go up. " +
                              "If the network acts strangely, check it with the interception stopped."),
                    () => L.T("Запущенный драйвер WinDivert (Win32_SystemDriver) и процессы winws, goodbyedpi, byedpi, ciadpi.", "A running WinDivert driver (Win32_SystemDriver) and the winws, goodbyedpi, byedpi, ciadpi processes."),
                    Tip("Сравнить без перехвата", "Compare without it", "Остановите программу (или её службу) и нажмите «Проверить снова».", "Stop the program (or its service) and press Check again."));
            }

            // "Network boosters" that shape traffic.
            foreach (var svc in NetFix.FindOptimizers()) Optimizers.Add(svc);
            if (Optimizers.Count > 0)
            {
                var fo = Add("optimizers", TuneCat.NetSystem, "\uE7BA", () => L.T("Программы-«ускорители» сети", "Network \"booster\" programs"));
                Done(fo, NetLevel.Warn, () => string.Join(", ", Optimizers.Select(o => o.Item2)),
                    () => L.T("Они перехватывают трафик ради «приоритетов» и известны тем, что режут скорость и добавляют задержку (Dell SmartByte, Killer)",
                              "They intercept traffic for \"priorities\" and are known for cutting speed and adding delay (Dell SmartByte, Killer)"));
                Explain(fo,
                    () => L.T("Dell SmartByte, Killer Prioritization Engine / Smart AP, Rivet, cFosSpeed, ASUS GameFirst пропускают трафик через свой фильтр, чтобы «приоритизировать» игры. На деле они известны падением скорости в разы (особенно SmartByte на гигабите) и лишней задержкой; производители сами советуют их отключать при проблемах.",
                              "Dell SmartByte, Killer Prioritization Engine / Smart AP, Rivet, cFosSpeed and ASUS GameFirst pass traffic through their own filter to \"prioritise\" games. In practice they are known for cutting speed several times over (SmartByte at gigabit especially) and adding delay; the makers themselves advise turning them off when there are problems."),
                    () => L.T("Службы с такими именами, не отключённые.", "Services with such names that are not disabled."),
                    Tip("Отключить службы", "Turn the services off", "DriveUP отключит их службы (ниже, в исправлениях); вернуть — на вкладке «Изменено».", "DriveUP disables their services (below, among the fixes); they go back under Changed.", null, null, true));
            }
        }

        void ReadServices()
        {
            bool wifi = Adapters.Any(a => a.Kind == LinkKind.WiFi) || Physical != null && Physical.Kind == LinkKind.WiFi;
            foreach (var s in NetServices)
            {
                if (s[0] == "WlanSvc" && !wifi) continue;
                int? start = null;
                try { start = Reg.Dword(true, @"SYSTEM\CurrentControlSet\Services\" + s[0], "Start"); } catch { }
                if (start == 4) DeadServices.Add(Tuple.Create(s[0], s[1]));
            }
            if (DeadServices.Count == 0) return;
            var f = Add("netsvc", TuneCat.NetSystem, "\uE912", () => L.T("Сетевые службы Windows", "Windows network services"));
            Done(f, NetLevel.Bad, () => L.T("отключены: ", "disabled: ") + string.Join(", ", DeadServices.Select(d => d.Item1)),
                () => L.T("Без этих служб Windows не получает адрес, не кеширует DNS или не видит интернет — их отключают «оптимизаторы»", "Without these services Windows gets no address, caches no DNS or does not see the internet — \"optimizers\" turn them off"));
            Explain(f,
                () => L.T("DHCP-клиент получает адрес от роутера, DNS-клиент кеширует имена, NSI и «Служба списка сетей» определяют сеть и доступ в интернет, WLAN AutoConfig управляет Wi-Fi, WinHTTP — автонастройкой прокси. " +
                          "Их отключают сборки и «оптимизаторы» ради экономии памяти — в итоге подключение «без доступа к интернету», медленные сайты или не работает Wi-Fi.",
                          "The DHCP client gets the address from the router, the DNS client caches names, NSI and Network List detect the network and internet access, WLAN AutoConfig runs Wi-Fi, WinHTTP does proxy auto-config. " +
                          "Custom builds and \"optimizers\" disable them to save memory — and the result is \"no internet access\", slow sites or no Wi-Fi."),
                () => L.T("Тип запуска служб Dhcp, Dnscache, nsi, netprofm, NlaSvc, WlanSvc, WinHttpAutoProxySvc (4 — отключена).", "Start type of Dhcp, Dnscache, nsi, netprofm, NlaSvc, WlanSvc, WinHttpAutoProxySvc (4 — disabled)."),
                Tip("Вернуть как в Windows", "Back as in Windows", "DriveUP вернёт обычный тип запуска и запустит нужные (ниже, в исправлениях).", "DriveUP restores the usual start type and starts the ones that should run (below, among the fixes).", null, null, true),
                Tip("Проверить самому", "Check yourself", "Состояние и тип запуска:", "State and start type:",
                    "Get-Service Dhcp, Dnscache, nsi, netprofm, NlaSvc, WlanSvc, WinHttpAutoProxySvc -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType"));
        }

        void ReadProxyAndHosts()
        {
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
                    Explain(fp,
                        () => L.T("В настройках Windows для этого пользователя включён прокси-сервер: браузеры и многие программы ходят в интернет через него. Его ставят VPN-клиенты, «ускорители», антивирусы и, увы, вредоносные программы — если вы его не включали, это повод проверить компьютер.",
                                  "A proxy server is turned on in Windows' settings for this user: browsers and many programs go online through it. VPN clients, \"boosters\", antivirus and, sadly, malware set it — if you did not turn it on, it is a reason to check the computer."),
                        () => L.T("ProxyEnable / ProxyServer в разделе Internet Settings пользователя; проверка — соединение TCP с адресом прокси.", "ProxyEnable / ProxyServer under the user's Internet Settings; checked by a TCP connection to the proxy's address."),
                        Tip("Выключить", "Turn it off", "Параметры → Сеть и Интернет → Прокси → «Использовать прокси-сервер» — выкл.", "Settings → Network & internet → Proxy → \"Use a proxy server\" — off.", null, "ms-settings:network-proxy"),
                        Tip("Прокси для служб Windows", "The proxy for Windows services", "Отдельный прокси WinHTTP (им пользуется Центр обновления):", "The separate WinHTTP proxy (Windows Update uses it):", "netsh winhttp show proxy"));
                }
            }
            catch { }

            // The hosts file: names sent elsewhere, or a list so big that every lookup slows down.
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
                if (!File.Exists(path)) return;
                var fi = new FileInfo(path);
                var watched = new HashSet<string>(Names.Concat(Services.Select(s => s[1])).Concat(new[] { "speed.cloudflare.com", "windowsupdate.microsoft.com", "update.microsoft.com" }), StringComparer.OrdinalIgnoreCase);
                int entries = 0;
                var hijacked = new List<string>();
                if (fi.Length < 64L << 20)
                    foreach (var raw in File.ReadLines(path))
                    {
                        string line = raw;
                        int hash = line.IndexOf('#');
                        if (hash >= 0) line = line.Substring(0, hash);
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) continue;
                        entries += parts.Length - 1;
                        foreach (var n in parts.Skip(1)) if (watched.Contains(n) && hijacked.Count < 5) hijacked.Add(n);
                    }
                bool huge = fi.Length > 2L << 20 || entries > 20000;
                if (hijacked.Count == 0 && !huge) return;
                var fh = Add("hosts", TuneCat.NetSystem, "\uE8A5", () => L.T("Файл hosts", "The hosts file"));
                Done(fh, hijacked.Count > 0 ? NetLevel.Warn : NetLevel.Info,
                    () => hijacked.Count > 0 ? L.T("перенаправлены: ", "redirected: ") + string.Join(", ", hijacked) : L.Count(entries, "запись", "записи", "записей", "entry", "entries") + " · " + L.Size(fi.Length),
                    () => hijacked.Count > 0 ? L.T("Популярные сайты в hosts ведут на другие адреса: они не откроются или откроются подменённые", "Popular sites are pointed elsewhere in hosts: they will not open, or fakes will")
                                             : L.T("Огромный список блокировки: служба DNS-клиента обрабатывает его при каждом запуске и при запросах — сайты могут открываться медленнее", "A huge block list: the DNS client reads it at every start and with lookups — sites may open slower"));
                Explain(fh,
                    () => L.T("Файл hosts задаёт адреса для имён в обход DNS. Его используют блокировщики рекламы и «анти-шпионы», но и вредоносные программы — чтобы подменить сайты или отрезать обновления.",
                              "The hosts file gives addresses for names, bypassing DNS. Ad blockers and \"anti-spy\" tools use it, and so does malware — to fake sites or cut off updates."),
                    () => L.T("Записи файла " + path + "; проверяются имена, которые меряет DNS-тест, популярные сервисы и сервер замера скорости.", "Entries of " + path + "; checked are the names the DNS test uses, popular services and the speed test server."),
                    Tip("Открыть файл", "Open the file", "Откройте блокнот от имени администратора и удалите лишние строки:", "Open Notepad as administrator and remove the unwanted lines:", "notepad " + path));
            }
            catch { }
        }

        /// <summary>The card's driver: old by date is no problem by itself — only a newer one that DriveUP can actually find is.</summary>
        void ReadDriver(NetAdapter p)
        {
            if (p.DriverDate <= DateTime.MinValue) return;
            double years = (DateTime.Now - p.DriverDate).TotalDays / 365.25;
            bool inbox = p.Provider.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
            var fd = Add("driver", TuneCat.NetSystem, "\uE7F8", () => L.T("Драйвер сетевой карты", "Network driver"));
            Say(L.T("Ищу драйвер новее для сетевой карты…", "Looking for a newer network driver…"));
            string instance;
            NicValues.TryGetValue("DeviceInstanceID", out instance);
            Device dev = null;
            string newer = null, newerDate = "";
            try
            {
                // The Updates tab may already know (Windows Update and the makers' sites).
                var known = Inventory.Devices.ToList().FirstOrDefault(d => instance != null && d.InstanceId.Equals(instance, StringComparison.OrdinalIgnoreCase));
                if (known != null && known.Update != null) newer = known.Update.VersionText;
                else if (known != null && known.Vendor != null) newer = known.Vendor.VersionText;
                dev = known ?? (instance == null ? null : Devices.Load(null).FirstOrDefault(d => d.InstanceId.Equals(instance, StringComparison.OrdinalIgnoreCase)));
                if (newer == null && dev != null && !Cancel)
                {
                    var hit = CatalogSource.Lookup(dev, false, DateTime.UtcNow.AddSeconds(20));
                    if (hit != null) { newer = hit.Version; newerDate = hit.Date.HasValue ? hit.Date.Value.ToString("d", CultureInfo.CurrentCulture) : ""; }
                }
                DriverLookedUp = dev != null;
            }
            catch { }
            NewerDriver = newer;
            string page = null;
            try
            {
                // The PC maker's page for this model needs the model: read it if the Updates tab has not yet.
                var pc = Inventory.Pc != null && Inventory.Pc.Manufacturer.Length > 0 ? Inventory.Pc : PcInfo.Read();
                if (dev != null) page = VendorCheck.PageFor(dev, pc);
            }
            catch { }
            NetLevel lv = newer != null ? NetLevel.Warn : inbox && !p.Virtual && years > 1 ? NetLevel.Info : NetLevel.Good;
            Done(fd, lv,
                () => p.Provider + " " + p.DriverVersion + " · " + p.DriverDate.ToString("d", CultureInfo.CurrentCulture) +
                      (newer != null ? L.T(" · есть новее: ", " · newer: ") + newer + (newerDate.Length > 0 ? " (" + newerDate + ")" : "") : ""),
                () => newer != null ? L.T("Есть драйвер новее — он может исправить обрывы и задержки. Установить — на вкладке «Обновления»", "A newer driver exists — it may fix drops and delay. Install it on the Updates tab")
                    : inbox ? L.T("Стоит стандартный драйвер Windows: у производителя карты он обычно стабильнее и быстрее", "Windows' generic driver is in use: the maker's own is usually more stable and faster")
                    : !DriverLookedUp ? L.T("Новее DriveUP проверить не смог — посмотрите на сайте производителя", "DriveUP could not check for a newer one — see the maker's site")
                    : years > 3 ? L.T("Новее в каталоге Microsoft нет — для этой карты это последняя версия, которую раздаёт Windows. Старый, но не проблема, если сеть работает ровно", "Nothing newer in Microsoft's catalog — the latest Windows offers for this card. Old, but no problem if the network is steady")
                    : L.T("Новее в каталоге Microsoft нет", "Nothing newer in Microsoft's catalog"));
            if (newer != null) fd.ActionText = () => L.T("Обновления", "Updates");   // the page opens the Updates tab
            else if (page != null && (lv != NetLevel.Good || years > 3)) { fd.Url = page; fd.ActionText = () => L.T("Сайт", "Website"); }
            Explain(fd,
                () => L.T("Драйвер — посредник между картой и Windows; в новых версиях производители чинят обрывы, зависания после сна, проблемы с энергосбережением и скоростью. " +
                          "Сам по себе возраст драйвера — не проблема: если новее нет нигде, а сеть работает ровно, менять нечего. Поэтому DriveUP отмечает драйвер, только если нашёл версию новее.",
                          "The driver sits between the card and Windows; newer versions fix drops, hangs after sleep, power-saving and speed problems. " +
                          "Age alone is no problem: if there is nothing newer and the network is steady, there is nothing to change. So DriveUP flags the driver only when it found a newer version."),
                () => L.T("Версия и дата — из раздела драйвера. Поиск новее: результаты вкладки «Обновления» (Windows Update и сайты производителей), затем каталог Центра обновления Microsoft по точному ИД оборудования и вашей версии Windows. " +
                          "Сайты Realtek, MSI, Gigabyte и других без открытого каталога автоматически не проверяются.",
                          "Version and date come from the driver's key. Looking for a newer one: the Updates tab's results (Windows Update and makers' sites), then the Microsoft Update Catalog by exact hardware ID and your Windows version. " +
                          "Realtek, MSI, Gigabyte and other sites without an open catalog are not checked automatically."),
                newer != null ? Tip("Установить из DriveUP", "Install from DriveUP", "Вкладка «Обновления» → проверить → драйвер сетевой карты. Перед установкой DriveUP создаёт точку восстановления.", "The Updates tab → check → the network driver. DriveUP makes a restore point before installing.", null, null, true) : null,
                page != null ? Tip("Сайт производителя", "The maker's site", "Для ноутбуков и готовых ПК надёжнее драйвер с сайта производителя компьютера; для собранных — с сайта производителя чипа.",
                    "For laptops and brand PCs the computer maker's driver is the safer choice; for self-built ones, the chip maker's.", null, page) : null,
                Tip("Сведения о драйвере", "Driver details", "Версия, дата и поставщик:", "Version, date and provider:",
                    "Get-NetAdapter -Name '" + p.Name.Replace("'", "''") + "' | Format-List DriverDescription, DriverVersion, DriverDate, DriverProvider, PnPDeviceID"));
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

        /// <summary>The link dropped often enough this week to count (a laptop moves between networks: more for it).</summary>
        public bool DropTrouble { get { return Drops.Count >= (Pc != null && Pc.Laptop ? 5 : 3); } }

        /// <summary>Signs of a wired link in trouble: loss or jitter to the router, broken packets.</summary>
        public bool WiredTrouble
        {
            get
            {
                if (Physical == null || Physical.Kind != LinkKind.Ethernet) return false;
                if (DropTrouble) return true;
                if (Gateway != null && Gateway.Received > 0 && (Gateway.Loss >= 1 || Gateway.Jitter > 2 || Gateway.Pct(95) > 10)) return true;
                var d = NicCounters.Delta(NicLoadFrom, NicLoadTo);
                return d != null && d.Errors >= 10 && d.ErrorPct >= 0.01;
            }
        }

        /// <summary>Wi-Fi that jumps or loses packets on the way to the router.</summary>
        public bool WifiTrouble
        {
            get
            {
                if (Physical == null || Physical.Kind != LinkKind.WiFi) return false;
                if (DropTrouble) return true;
                if (Gateway != null && Gateway.Received > 0 && (Gateway.Loss >= 1 || Gateway.Jitter > 10 || Gateway.Pct(95) > 60)) return true;
                return Quiet != null && Quiet.Received > 0 && (Quiet.Loss >= 0.5 || Quiet.Spikes().Count > 6);
            }
        }

        /// <summary>The delay grows a lot under load (grade C or worse).</summary>
        public bool Bloated
        {
            get
            {
                return (!double.IsNaN(BloatDown) && BloatDown >= 60) || (!double.IsNaN(BloatUp) && BloatUp >= 60) ||
                       (!double.IsNaN(PeakDown) && PeakDown >= 200) || (!double.IsNaN(PeakUp) && PeakUp >= 200);
            }
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
