using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace DriveUp
{
    /// <summary>An adapter's own packet counters at one moment (all protocols, as the driver counts them).</summary>
    class NicCounters
    {
        public long InPackets, OutPackets, InErrors, OutErrors, InDiscards, OutDiscards;

        public static NicCounters Read(NetworkInterface nic)
        {
            if (nic == null) return null;
            try
            {
                var s = nic.GetIPStatistics();
                return new NicCounters
                {
                    InPackets = s.UnicastPacketsReceived + s.NonUnicastPacketsReceived,
                    OutPackets = s.UnicastPacketsSent + s.NonUnicastPacketsSent,
                    InErrors = s.IncomingPacketsWithErrors,
                    OutErrors = s.OutgoingPacketsWithErrors,
                    InDiscards = s.IncomingPacketsDiscarded,
                    OutDiscards = s.OutgoingPacketsDiscarded,
                };
            }
            catch { return null; }
        }

        public static NicCounters Delta(NicCounters a, NicCounters b)
        {
            if (a == null || b == null) return null;
            return new NicCounters
            {
                InPackets = Math.Max(0, b.InPackets - a.InPackets), OutPackets = Math.Max(0, b.OutPackets - a.OutPackets),
                InErrors = Math.Max(0, b.InErrors - a.InErrors), OutErrors = Math.Max(0, b.OutErrors - a.OutErrors),
                InDiscards = Math.Max(0, b.InDiscards - a.InDiscards), OutDiscards = Math.Max(0, b.OutDiscards - a.OutDiscards),
            };
        }

        public long Errors { get { return InErrors + OutErrors; } }
        public long Packets { get { return InPackets + OutPackets; } }
        public double ErrorPct { get { return Packets == 0 ? 0 : Errors * 100.0 / Packets; } }
        public double DiscardPct { get { return InPackets == 0 ? 0 : InDiscards * 100.0 / (InPackets + InDiscards); } }
    }

    /// <summary>TCP segments sent and sent again by one program, over a stretch of time.</summary>
    class TcpShare
    {
        public int Pid;
        public string Process = "";
        public long Sent, Resent;
        public int Connections;
        /// <summary>Connection attempts seen hanging unanswered (SYN sent, no reply yet): each is sent again and again.</summary>
        public int Attempts;
        public readonly HashSet<string> Hosts = new HashSet<string>();
        public double Pct { get { return Sent == 0 ? 0 : Resent * 100.0 / Sent; } }
    }

    /// <summary>
    /// Per-connection TCP statistics of Windows (ESTATS, the same counters Resource Monitor's network view rests on):
    /// collection is switched on for the IPv4 connections open now (and those that open while <see cref="Poll"/> is
    /// called), and after a while each program's segments and resends are summed. Needs administrator rights;
    /// collection is switched off again at the end.
    /// </summary>
    class TcpWatch
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MIB_TCPROW { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort; }

        [DllImport("iphlpapi.dll")]
        static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);
        [DllImport("iphlpapi.dll")]
        static extern int SetPerTcpConnectionEStats(ref MIB_TCPROW row, int type, byte[] rw, int rwVersion, int rwSize, int offset);
        [DllImport("iphlpapi.dll")]
        static extern int GetPerTcpConnectionEStats(ref MIB_TCPROW row, int type, IntPtr rw, int rwVersion, int rwSize,
            IntPtr ros, int rosVersion, int rosSize, IntPtr rod, int rodVersion, int rodSize);

        const int EstatsData = 1, EstatsPath = 3, Established = 5;

        class Conn { public MIB_TCPROW Row; public int Pid; }

        readonly Dictionary<string, Conn> watched = new Dictionary<string, Conn>();
        /// <summary>Connection attempts hanging in SYN_SENT, by program (each attempt counted once).</summary>
        readonly Dictionary<string, int> attempts = new Dictionary<string, int>();
        public bool Works = true;
        /// <summary>The last error Windows gave (diagnostics), 0 = none.</summary>
        public int LastError;
        int tried;

        // Sizes Windows checks exactly: TCP_ESTATS_DATA_ROD_v0 (6 × ULONG64, 5 × ULONG, ULONG64 at 72, ULONG, ULONG64 at 88)
        // and TCP_ESTATS_PATH_ROD_v0 (40 × ULONG).
        const int RodData = 96, RodPath = 160;

        static string Key(MIB_TCPROW r) { return r.LocalAddr + ":" + r.LocalPort + ">" + r.RemoteAddr + ":" + r.RemotePort; }

        const int SynSent = 3;

        static List<Conn> Table(bool hanging = false)
        {
            var list = new List<Conn>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2 /* AF_INET */, 4 /* TCP_TABLE_OWNER_PID_CONNECTIONS */, 0);
            if (size <= 0) return list;
            IntPtr buf = Marshal.AllocHGlobal(size + 4096);
            try
            {
                size += 4096;
                if (GetExtendedTcpTable(buf, ref size, false, 2, 4, 0) != 0) return list;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid (6 DWORDs).
                    IntPtr p = IntPtr.Add(buf, 4 + i * 24);
                    var r = new MIB_TCPROW
                    {
                        State = (uint)Marshal.ReadInt32(p, 0), LocalAddr = (uint)Marshal.ReadInt32(p, 4), LocalPort = (uint)Marshal.ReadInt32(p, 8),
                        RemoteAddr = (uint)Marshal.ReadInt32(p, 12), RemotePort = (uint)Marshal.ReadInt32(p, 16),
                    };
                    if (r.State != (hanging ? (uint)SynSent : (uint)Established) || (r.RemoteAddr & 0xFF) == 127) continue;
                    list.Add(new Conn { Row = r, Pid = Marshal.ReadInt32(p, 20) });
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }

        int Enable(ref MIB_TCPROW row, bool on)
        {
            var rw = new[] { (byte)(on ? 1 : 0) };
            int a = SetPerTcpConnectionEStats(ref row, EstatsData, rw, 0, 1, 0);
            int b = SetPerTcpConnectionEStats(ref row, EstatsPath, rw, 0, 1, 0);
            return a != 0 ? a : b;
        }

        /// <summary>Starts watching the connections open now.</summary>
        public void Start()
        {
            Poll();
        }

        /// <summary>Starts watching the connections that opened since the last call.</summary>
        public void Poll()
        {
            try
            {
                // Attempts that hang: no statistics needed (nor rights) — the state says it all.
                foreach (var c in Table(true))
                {
                    string k = Key(c.Row);
                    if (!attempts.ContainsKey(k)) attempts[k] = c.Pid;
                }
            }
            catch { }
            if (!Works) return;
            try
            {
                foreach (var c in Table())
                {
                    string k = Key(c.Row);
                    if (watched.ContainsKey(k)) continue;
                    tried++;
                    int err = Enable(ref c.Row, true);
                    // A connection may close in between; only "access denied" on every try means no rights at all.
                    if (err != 0) { LastError = err; if (err == 5 && watched.Count == 0 && tried >= 3) { Works = false; return; } continue; }
                    watched[k] = c;
                }
            }
            catch { Works = false; }
        }

        /// <summary>Reads each watched connection (closed ones are gone and left out), switches collection off, sums per program.</summary>
        public List<TcpShare> Stop()
        {
            var by = new Dictionary<int, TcpShare>();
            IntPtr data = Marshal.AllocHGlobal(RodData), path = Marshal.AllocHGlobal(RodPath);
            try
            {
                foreach (var c in watched.Values)
                {
                    var row = c.Row;
                    try
                    {
                        int e1 = GetPerTcpConnectionEStats(ref row, EstatsData, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, data, 0, RodData);
                        int e2 = e1 != 0 ? e1 : GetPerTcpConnectionEStats(ref row, EstatsPath, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, path, 0, RodPath);
                        if (e2 != 0) { LastError = e2; continue; }
                        // TCP_ESTATS_DATA_ROD_v0: DataBytesOut, DataSegsOut, DataBytesIn, DataSegsIn, SegsOut (ULONG64 each).
                        long segsOut = Marshal.ReadInt64(data, 32);
                        // TCP_ESTATS_PATH_ROD_v0: FastRetran, Timeouts, SubsequentTimeouts, CurTimeoutCount, AbruptTimeouts, PktsRetrans.
                        long resent = (uint)Marshal.ReadInt32(path, 20);
                        TcpShare s;
                        if (!by.TryGetValue(c.Pid, out s)) by[c.Pid] = s = new TcpShare { Pid = c.Pid };
                        s.Sent += segsOut;
                        s.Resent += resent;
                        s.Connections++;
                        s.Hosts.Add(new IPAddress(row.RemoteAddr).ToString());
                    }
                    finally { Enable(ref row, false); }
                }
            }
            catch { }
            finally
            {
                Marshal.FreeHGlobal(data);
                Marshal.FreeHGlobal(path);
                watched.Clear();
            }
            foreach (var a in attempts)
            {
                TcpShare s;
                if (!by.TryGetValue(a.Value, out s)) by[a.Value] = s = new TcpShare { Pid = a.Value };
                s.Attempts++;
            }
            attempts.Clear();
            foreach (var s in by.Values)
            {
                try { using (var p = System.Diagnostics.Process.GetProcessById(s.Pid)) s.Process = p.ProcessName; }
                catch { s.Process = s.Pid == 4 ? "System" : "PID " + s.Pid; }
            }
            return by.Values.OrderByDescending(s => s.Resent + s.Attempts * 4L).ToList();
        }
    }

    /// <summary>What Windows' logs say about the link over the last days.</summary>
    static class NetEvents
    {
        static IEnumerable<EventRecord> Read(string log, string xpath)
        {
            EventLogReader r;
            try { r = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath)); }
            catch { yield break; }
            using (r)
                for (int i = 0; i < 5000; i++)
                {
                    EventRecord e;
                    try { e = r.ReadEvent(); }
                    catch { yield break; }
                    if (e == null) yield break;
                    using (e) yield return e;
                }
        }

        /// <summary>
        /// When the adapter (its interface GUID) connected anew over the last days, leaving out the connections that
        /// come with switching on, restarting, waking up and modern standby — those are not drops.
        /// </summary>
        public static List<DateTime> Reconnects(string guid, int days)
        {
            var result = new List<DateTime>();
            if (string.IsNullOrEmpty(guid)) return result;
            long ms = days * 86400000L;
            var starts = new List<DateTime>();
            // Start-up (Kernel-General 12), resume (Kernel-Power 107, Power-Troubleshooter 1), modern standby exit (Kernel-Power 507).
            foreach (var e in Read("System", "*[System[(EventID=12 or EventID=107 or EventID=507 or EventID=1) and TimeCreated[timediff(@SystemTime) <= " + (ms + 3600000) + "]]]"))
            {
                string prov = e.ProviderName ?? "";
                bool wake = (e.Id == 12 && prov == "Microsoft-Windows-Kernel-General") || ((e.Id == 107 || e.Id == 507) && prov == "Microsoft-Windows-Kernel-Power") ||
                            (e.Id == 1 && prov == "Microsoft-Windows-Power-Troubleshooter");
                if (wake && e.TimeCreated.HasValue) starts.Add(e.TimeCreated.Value);
            }
            // NetworkProfile 4001 with state 0 ("identifying"): the interface has just come up and looks for its network.
            foreach (var e in Read("Microsoft-Windows-NetworkProfile/Operational", "*[System[EventID=4001 and TimeCreated[timediff(@SystemTime) <= " + ms + "]]]"))
            {
                if (!e.TimeCreated.HasValue || e.Properties.Count < 2) continue;
                string g = Convert.ToString(e.Properties[0].Value) ?? "";
                if (!g.Trim('{', '}').Equals(guid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)) continue;
                if (Convert.ToString(e.Properties[1].Value) != "0") continue;
                var t = e.TimeCreated.Value;
                if (starts.Any(s => t >= s.AddSeconds(-30) && t <= s.AddMinutes(4))) continue;
                result.Add(t);
            }
            result.Sort();
            return result;
        }

        /// <summary>"Another device on the network has this IP address" (Tcpip 4199) over the last days.</summary>
        public static int IpConflicts(int days)
        {
            return Read("System", "*[System[Provider[@Name='Tcpip'] and EventID=4199 and TimeCreated[timediff(@SystemTime) <= " + days * 86400000L + "]]]").Count();
        }
    }

    /// <summary>Programs that sit in the middle of all traffic (packet filters for getting around blocks, "game boosters").</summary>
    static class Interceptors
    {
        static readonly string[] Programs = { "winws", "goodbyedpi", "byedpi", "ciadpi", "zapret" };

        /// <summary>Names of what is running now: a packet-diverting driver and the program that drives it.</summary>
        public static List<string> Running()
        {
            var list = new List<string>();
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_SystemDriver WHERE State='Running' AND (Name LIKE 'WinDivert%' OR Name='Monkey')"))
                    foreach (System.Management.ManagementObject o in s.Get())
                        using (o) { list.Add("WinDivert"); break; }
            }
            catch { }
            foreach (var p in Programs)
                try
                {
                    var procs = Process.GetProcessesByName(p);
                    if (procs.Length > 0) list.Add(p + ".exe");
                    foreach (var x in procs) x.Dispose();
                }
                catch { }
            return list;
        }
    }
}
