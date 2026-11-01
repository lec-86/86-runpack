// ---------------------------------------------------------------------------
//  Part of the 86 RuntimePack sources: system dir / WinSxS helpers (Sys, DllIssue)
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RunTimePack
{
    // ======================= 系统目录 / WinSxS 工具 =======================
    //  说明：64 位 Windows 上 System32 存放 64 位文件、SysWOW64 存放 32 位文件；
    //  32 位 Windows 上只有一个 System32，存放 32 位文件。

    internal static class Sys
    {
        public static string Windows
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); }
        }
        public static string System32 { get { return Path.Combine(Windows, "System32"); } }
        public static string SysWow64 { get { return Path.Combine(Windows, "SysWOW64"); } }
        public static string WinSxS   { get { return Path.Combine(Windows, "WinSxS"); } }

        /// <summary>取某架构运行库应当存放的系统目录。</summary>
        public static string DirFor(bool x64)
        {
            if (x64) return System32;
            return Environment.Is64BitOperatingSystem ? SysWow64 : System32;
        }

        private static string[] sxsDirs;

        /// <summary>WinSxS 子目录只枚举一次并缓存，避免反复扫描上万个文件夹。</summary>
        public static string[] SxsDirs()
        {
            if (sxsDirs == null)
            {
                try
                {
                    sxsDirs = Directory.Exists(WinSxS) ? Directory.GetDirectories(WinSxS) : new string[0];
                }
                catch { sxsDirs = new string[0]; }
            }
            return sxsDirs;
        }

        /// <summary>
        /// 在 WinSxS 中查找指定名称、指定架构的 DLL，返回版本最高的那一份。
        /// WinSxS 里有上万个目录，逐个全量扫描太慢，所以先用文件名推导出的关键词过滤目录名
        /// （msvcr80.dll → vc80，d3dcompiler_43.dll → d3dcompiler），只扫描相关目录；
        /// 命中后还要读 PE 头确认位数，避免把 32 位文件复制到 64 位目录。
        /// </summary>
        public static string FindInSxs(string fileName, bool x64)
        {
            string archPrefix = x64 ? "amd64_" : "x86_";
            string[] hints = SxsHints(fileName);
            string[] dirs = SxsDirs();
            string best = null, bestDir = null;

            for (int pass = 0; pass < 2; pass++)
            {
                foreach (string dir in dirs)
                {
                    string n = Path.GetFileName(dir);
                    if ((pass == 0) != n.StartsWith(archPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ContainsAny(n, hints)) continue;

                    string f = Path.Combine(dir, fileName);
                    if (!File.Exists(f)) continue;
                    if (!MachineMatches(f, x64)) continue;

                    // WinSxS 目录名里嵌了版本号，字典序最大即为最新
                    if (best == null || string.CompareOrdinal(n, bestDir) > 0)
                    {
                        best = f;
                        bestDir = n;
                    }
                }
                if (best != null) return best;
            }
            return null;
        }

        private static bool ContainsAny(string text, string[] hints)
        {
            foreach (string h in hints)
                if (text.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>由 DLL 文件名推导 WinSxS 目录名里可能出现的关键词。</summary>
        private static string[] SxsHints(string fileName)
        {
            string b = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
            List<string> hints = new List<string>();
            hints.Add(b);

            int us = b.IndexOf('_');
            if (us > 0)
            {
                string head = b.Substring(0, us);                       // d3dx9_43 -> d3dx9 ，xinput1_4 -> xinput1
                hints.Add(head);
                char[] digits = new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };
                string trimmed = head.TrimEnd(digits);                  // xinput1 -> xinput
                if (trimmed.Length >= 4) hints.Add(trimmed);
            }

            // CRT / MFC 家族：msvcr80 → vc80，mfc140u → vc140，vcruntime140 → vc140
            string[] family = new string[]
            {
                "msvcr", "msvcp", "mfc", "mfcm", "atl", "vcruntime", "concrt", "vccorlib"
            };
            foreach (string f in family)
            {
                if (!b.StartsWith(f)) continue;
                string num = "";
                foreach (char c in b)
                {
                    if (c >= '0' && c <= '9') num += c;
                    else if (num.Length > 0) break;
                }
                if (num.Length > 0) hints.Add("vc" + num);
                break;
            }
            return hints.ToArray();
        }

        /// <summary>读取 PE 头 Machine 字段，判断该映像是否为 32 位（0x014C）或 64 位（0x8664）。</summary>
        public static bool MachineMatches(string file, bool x64)
        {
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (fs.Length < 0x40) return false;

                    fs.Position = 0x3C;
                    int pe = br.ReadInt32();
                    if (pe <= 0 || pe + 6 > fs.Length) return false;

                    fs.Position = pe;
                    byte[] sig = br.ReadBytes(4);
                    if (sig[0] != 0x50 || sig[1] != 0x45) return false;   // "PE"

                    ushort machine = br.ReadUInt16();
                    return x64 ? machine == 0x8664 : machine == 0x014C;
                }
            }
            catch { return false; }
        }

        // ---------- 运行库 DLL 体检 ----------

        /// <summary>体检总项数：清单条目数 × 架构数（64 位系统上每种架构各查一遍）。
        /// 仅 64 位版本的文件（X64Only）不参与 32 位体检，故单独扣除。</summary>
        public static int TotalChecks
        {
            get
            {
                bool os64 = Environment.Is64BitOperatingSystem;
                int n = 0;
                foreach (DllCatalog.Item item in DllCatalog.Items)
                {
                    if (!item.X64Only) n++;   // 32 位副本
                    if (os64) n++;            // 64 位副本
                }
                return n;
            }
        }

        /// <summary>
        /// 扫描系统目录，返回缺失的 DLL 列表。
        /// findSource 为 true 时（修复流程）额外去 WinSxS 找同架构副本；检测流程传 false 以保证启动速度。
        /// </summary>
        public static List<DllIssue> ScanRuntimeDlls(bool findSource)
        {
            List<DllIssue> list = new List<DllIssue>();
            bool os64 = Environment.Is64BitOperatingSystem;

            foreach (DllCatalog.Item item in DllCatalog.Items)
            {
                // 32 位副本（X64Only 的文件只有 64 位版本，32 位系统目录里本来就不该有）
                if (!item.X64Only && !File.Exists(Path.Combine(DirFor(false), item.Name)))
                    list.Add(new DllIssue(item, false, findSource ? FindInSxs(item.Name, false) : null));

                // 64 位副本（仅 64 位系统才有）
                if (os64 && !File.Exists(Path.Combine(DirFor(true), item.Name)))
                    list.Add(new DllIssue(item, true, findSource ? FindInSxs(item.Name, true) : null));
            }
            return list;
        }
    }

    /// <summary>
    /// 一个缺失的运行库 DLL。
    /// Source 为空表示本机 WinSxS 里也没有同架构副本，需要安装 Owner 对应的运行库来补齐。
    /// </summary>
    internal class DllIssue
    {
        public string Name;
        public string Group;
        public string Owner;
        public bool X64;
        public string Source;

        public DllIssue(DllCatalog.Item item, bool x64, string source)
        {
            Name = item.Name;
            Group = item.Group;
            Owner = item.Owner;
            X64 = x64;
            Source = source;
        }

        public string ArchText { get { return X64 ? "64 位" : "32 位"; } }
    }

}
