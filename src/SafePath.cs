using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace DriveUp
{
    /// <summary>
    /// File system helpers for an app that runs as administrator: tells real links (junctions,
    /// symbolic links) from other reparse points (OneDrive / cloud files, deduplication), and keeps
    /// driver files in folders only administrators and SYSTEM can change between "added" and "installed".
    /// </summary>
    static class SafePath
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public uint Attributes;
            public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint SizeHigh, SizeLow;
            public uint Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileW(string path, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll")]
        static extern bool FindClose(IntPtr handle);

        /// <summary>
        /// A junction or symbolic link: a reparse point that names another place ("name surrogate").
        /// OneDrive folders and files are reparse points too, but not links: they are fine.
        /// </summary>
        public static bool IsLink(string path)
        {
            FileAttributes attr;
            try { attr = File.GetAttributes(path); }
            catch { return true; }
            if ((attr & FileAttributes.ReparsePoint) == 0) return false;
            WIN32_FIND_DATA data;
            IntPtr h = FindFirstFileW(path.TrimEnd('\\', '/'), out data);
            if (h == new IntPtr(-1)) return true;   // unknown: treat as a link
            FindClose(h);
            return (data.Reserved0 & 0x20000000) != 0;   // IsReparseTagNameSurrogate
        }

        /// <summary>
        /// Staging folders left by an earlier run (closed mid-download, power cut) are removed at start:
        /// only DriveUP-* under %SystemRoot%\Temp, older than half a day, never through a link, and only those
        /// DriveUP itself made (see <see cref="Ours"/>).
        /// </summary>
        public static void CleanStale()
        {
            if (!Elevated) return;
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
                foreach (var dir in Directory.GetDirectories(root, "DriveUP-*"))
                {
                    try
                    {
                        if (IsLink(dir) || DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) < TimeSpan.FromHours(12) || !Ours(dir)) continue;
                        Directory.Delete(dir, true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Made by <see cref="NewStaging"/>: owned by Administrators or SYSTEM, with a protected list that lets
        /// nobody else in. Any user may create a "DriveUP-x" folder in %SystemRoot%\Temp and, owning it, turn it into
        /// a link half-way through a recursive delete (deleting files anywhere as administrator); such a folder
        /// is not owned by an administrator group and is left alone. Ours cannot be changed by anyone else.
        /// </summary>
        static bool Ours(string dir)
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var sec = Directory.GetAccessControl(dir, AccessControlSections.Owner | AccessControlSections.Access);
            var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null || (owner != admins && owner != system)) return false;
            if (!sec.AreAccessRulesProtected) return false;
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                var sid = rule.IdentityReference as SecurityIdentifier;
                if (rule.AccessControlType == AccessControlType.Allow && sid != admins && sid != system) return false;
            }
            return true;
        }

        /// <summary>
        /// A new empty folder that only administrators and SYSTEM may change, under %SystemRoot%\Temp
        /// (a folder ordinary users cannot replace). Random name, refused if it already exists.
        /// </summary>
        public static string NewStaging()
        {
            if (!Elevated)
            {
                // Not elevated (DriveUP.dev.exe, tests): nothing to protect from, and %SystemRoot%\Temp is not ours.
                string dev = Path.Combine(Path.GetTempPath(), "DriveUP", Guid.NewGuid().ToString("N").Substring(0, 12));
                Directory.CreateDirectory(dev);
                return dev;
            }
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            for (int attempt = 0; attempt < 5; attempt++)
            {
                string dir = Path.Combine(root, "DriveUP-" + Guid.NewGuid().ToString("N").Substring(0, 16));
                if (Directory.Exists(dir) || File.Exists(dir)) continue;
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(true, false);
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                // Owned by the group (also as the built-in Administrator account): how CleanStale knows it is ours.
                sec.SetOwner(admins);
                foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                    sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                // Created with these rights in one step: there is no moment when anyone else could write.
                try { Directory.CreateDirectory(dir, sec); }
                catch (Exception)
                {
                    if (Directory.Exists(dir) || File.Exists(dir)) continue;
                    // A token that may not name the group as owner: the default owner (then left for Windows to clean up).
                    var plain = new DirectorySecurity(); plain.SetSecurityDescriptorBinaryForm(sec.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                    Directory.CreateDirectory(dir, plain);
                }
                if (IsLink(dir)) throw new IOException(L.T("временная папка подменена ссылкой", "the temporary folder was replaced by a link"));
                return dir;
            }
            throw new IOException(L.T("не удалось создать временную папку", "could not create a temporary folder"));
        }

        /// <summary>
        /// Copies a folder (links skipped) into a new protected staging folder; null when it is larger
        /// than the limits (then the caller checks the .inf hash instead of copying).
        /// </summary>
        public static string StageCopy(string from, long maxBytes, int maxFiles)
        {
            long bytes = 0;
            int files = 0;
            if (!Measure(from, ref bytes, ref files, maxBytes, maxFiles, 0)) return null;
            string to = NewStaging();
            Copy(from, to, 0);
            return to;
        }

        static bool Measure(string dir, ref long bytes, ref int files, long maxBytes, int maxFiles, int depth)
        {
            if (depth > 16) return false;
            foreach (var f in new DirectoryInfo(dir).GetFiles())
            {
                bytes += f.Length;
                if (++files > maxFiles || bytes > maxBytes) return false;
            }
            foreach (var d in Directory.GetDirectories(dir))
                if (!IsLink(d) && !Measure(d, ref bytes, ref files, maxBytes, maxFiles, depth + 1)) return false;
            return true;
        }

        static void Copy(string from, string to, int depth)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from))
                if (!IsLink(f)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), false);
            if (depth >= 16) return;
            foreach (var d in Directory.GetDirectories(from))
                if (!IsLink(d)) Copy(d, Path.Combine(to, Path.GetFileName(d)), depth + 1);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES { public int Length; public IntPtr Descriptor; public bool Inherit; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateDirectoryW(string path, ref SECURITY_ATTRIBUTES sa);

        /// <summary>
        /// Creates a folder that did not exist, with these permissions from its first moment; false if something
        /// with that name is already there (Directory.CreateDirectory would quietly reuse a folder someone else made).
        /// </summary>
        public static bool CreateNew(string path, DirectorySecurity sec)
        {
            byte[] sd = sec.GetSecurityDescriptorBinaryForm();
            var pin = GCHandle.Alloc(sd, GCHandleType.Pinned);
            try
            {
                var sa = new SECURITY_ATTRIBUTES { Length = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), Descriptor = pin.AddrOfPinnedObject() };
                if (CreateDirectoryW(path, ref sa)) return true;
                int err = Marshal.GetLastWin32Error();
                if (err == 183 /* ERROR_ALREADY_EXISTS */) return false;
                throw new System.ComponentModel.Win32Exception(err);
            }
            finally { pin.Free(); }
        }

        static bool? elevated;

        public static bool Elevated
        {
            get
            {
                if (elevated == null)
                    using (var id = WindowsIdentity.GetCurrent())
                        elevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                return elevated.Value;
            }
        }

        public static string Sha256(string file)
        {
            using (var s = File.OpenRead(file))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "");
        }
    }
}
