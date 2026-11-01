// ---------------------------------------------------------------------------
//  Part of the 86 RuntimePack sources: install-state detection (DetectResult, Detect)
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
    // ======================= 安装状态检测 =======================

    internal class DetectResult
    {
        public bool Installed;
        public bool Applicable;
        public string Text;

        public DetectResult(bool installed, bool applicable, string text)
        {
            Installed = installed;
            Applicable = applicable;
            Text = text;
        }
    }

    internal static class Detect
    {
        public static DetectResult Run(Pkg p)
        {
            try
            {
                return RunCore(p);
            }
            catch
            {
                return new DetectResult(false, true, "检测异常");
            }
        }

        private static DetectResult RunCore(Pkg p)
        {
            if (p.X64Only && !Environment.Is64BitOperatingSystem)
                return new DetectResult(false, false, "不适用（32 位系统）");

            switch (p.DetectKind)
            {
                case "vcfile":   return SystemFile(p.DetectArg);
                case "vcredist": return VcRedist(p.DetectArg);
                case "vc14":     return Vc14(p.DetectArg);
                case "net35":    return Net35();
                case "net48":    return Net48();
                case "sharedfx": return SharedFx(p.DetectArg);
                case "webview2": return WebView2();
                case "java":     return Java(p.DetectArg);
                case "xna":      return Xna(p.DetectArg);
                case "vjsharp":  return VJSharp();
                case "vsto":     return Vsto();
                case "dllrepair": return DllRepair();
            }
            return new DetectResult(false, true, "未知");
        }

        /// <summary>
        /// 系统运行库 DLL 体检：逐项检查 System32 / SysWOW64 里清单中的 DLL 是否齐全。
        /// 检测阶段只比对文件是否存在（快），修复阶段才会去 WinSxS 找副本并补齐。
        /// </summary>
        private static DetectResult DllRepair()
        {
            List<DllIssue> issues = Sys.ScanRuntimeDlls(false);
            int total = Sys.TotalChecks;

            if (issues.Count == 0)
                return new DetectResult(true, true, "全部正常（体检 " + total + " 项）");

            return new DetectResult(false, true, "缺失 " + issues.Count + " 项（体检 " + total + " 项）");
        }

        /// <summary>按分组给出缺失统计文字，例如 "DirectX 组件 12，VC++ 运行库 8"。</summary>
        internal static string GroupSummary(List<DllIssue> issues)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string g in DllCatalog.Groups())
            {
                int n = 0;
                foreach (DllIssue it in issues) if (it.Group == g) n++;
                if (n == 0) continue;
                if (sb.Length > 0) sb.Append("，");
                sb.Append(g).Append(" ").Append(n);
            }
            return sb.ToString();
        }

        /// <summary>检查系统目录中是否存在运行库关键 DLL。参数："x86|msvcr80.dll" / "both|d3dx9_43.dll"</summary>
        private static DetectResult SystemFile(string arg)
        {
            string[] parts = arg.Split('|');
            string arch = parts[0];
            string file = parts[1];

            bool found = false;
            if (arch == "x86" || arch == "both")
                found = File.Exists(Path.Combine(ArchDir(false), file));
            if (!found && (arch == "x64" || arch == "both"))
            {
                if (!Environment.Is64BitOperatingSystem && arch == "x64") return new DetectResult(false, false, "不适用（32 位系统）");
                found = File.Exists(Path.Combine(ArchDir(true), file));
            }
            return new DetectResult(found, true, found ? "已安装" : "未安装");
        }

        /// <summary>64 位系统上 x64 运行库在 System32、x86 运行库在 SysWOW64。</summary>
        private static string ArchDir(bool x64)
        {
            return Sys.DirFor(x64);
        }

        /// <summary>
        /// VC++ 2005 / 2008：在 Win10 / Win11 上，官方安装包只把运行库注册为 WinSxS
        /// 并行程序集（x86_/amd64_microsoft.vc80.crt_* 等），并不会复制到
        /// System32 / SysWOW64。只查系统目录会漏判，必须两处都查。
        /// 参数："2005|x86" / "2008|x64"
        /// </summary>
        private static DetectResult VcRedist(string arg)
        {
            string[] parts = arg.Split('|');
            string year = parts[0];
            bool x64 = parts[1] == "x64";

            if (x64 && !Environment.Is64BitOperatingSystem)
                return new DetectResult(false, false, "不适用（32 位系统）");

            string dll = (year == "2005") ? "msvcr80.dll" : "msvcr90.dll";
            if (File.Exists(Path.Combine(ArchDir(x64), dll)))
                return new DetectResult(true, true, "已安装");

            string tag = (year == "2005") ? "vc80" : "vc90";
            string prefix = (x64 ? "amd64" : "x86") + "_microsoft." + tag + ".crt_";
            if (SxsAssemblyExists(prefix))
                return new DetectResult(true, true, "已安装");

            return new DetectResult(false, true, "未安装");
        }

        /// <summary>WinSxS 目录只枚举一次并缓存，避免逐项重复扫描上万个文件夹。</summary>
        private static bool SxsAssemblyExists(string prefix)
        {
            foreach (string dir in Sys.SxsDirs())
            {
                string name = Path.GetFileName(dir);
                if (name.Length >= prefix.Length
                    && string.Compare(name, 0, prefix, 0, prefix.Length, true) == 0)
                    return true;
            }
            return false;
        }

        /// <summary>VC++ 2015-2022 专用注册表项，带有精确版本号。</summary>
        private static DetectResult Vc14(string arch)
        {
            string[] paths = new string[]
            {
                @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\" + arch,
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + arch
            };
            foreach (string path in paths)
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k == null) continue;
                    object inst = k.GetValue("Installed");
                    if (inst != null && Convert.ToInt32(inst) == 1)
                    {
                        string v = k.GetValue("Version") as string;
                        if (string.IsNullOrEmpty(v)) v = "已安装";
                        return new DetectResult(true, true, v);
                    }
                }
            }
            return new DetectResult(false, true, "未安装");
        }

        private static DetectResult Net35()
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5"))
            {
                if (k != null)
                {
                    object v = k.GetValue("Install");
                    if (v != null && Convert.ToInt32(v) == 1)
                        return new DetectResult(true, true, "已启用");
                }
            }
            return new DetectResult(false, true, "未启用");
        }

        private static DetectResult Net48()
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
            {
                if (k != null)
                {
                    object rv = k.GetValue("Release");
                    if (rv != null)
                    {
                        int r = Convert.ToInt32(rv);
                        string name = ReleaseName(r);
                        if (r >= 528040) return new DetectResult(true, true, "已安装 " + name);
                        return new DetectResult(false, true, "当前 " + name + "，低于 4.8");
                    }
                }
            }
            return new DetectResult(false, true, "未安装");
        }

        private static string ReleaseName(int r)
        {
            if (r >= 533320) return "4.8.1 或更高";
            if (r >= 528040) return "4.8";
            if (r >= 461808) return "4.7.2";
            if (r >= 461308) return "4.7.1";
            if (r >= 460798) return "4.7";
            if (r >= 394802) return "4.6.2";
            if (r >= 394254) return "4.6.1";
            if (r >= 393295) return "4.6";
            if (r >= 379893) return "4.5.2";
            if (r >= 378675) return "4.5.1";
            if (r >= 378389) return "4.5";
            return "4.x";
        }

        /// <summary>.NET (Core) 共享框架，参数："Microsoft.WindowsDesktop.App|8" 或 "...|8|x86"</summary>
        private static DetectResult SharedFx(string arg)
        {
            string[] parts = arg.Split('|');
            string fx = parts[0];
            string major = parts[1];
            bool x86 = parts.Length > 2 && parts[2] == "x86";

            string root = x86
                ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            string dir = Path.Combine(root, "dotnet", "shared", fx);
            if (!Directory.Exists(dir)) return new DetectResult(false, true, "未安装");

            string best = null;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string n = Path.GetFileName(sub);
                if (n.StartsWith(major + "."))
                    if (best == null || string.CompareOrdinal(n, best) > 0) best = n;
            }
            if (best != null) return new DetectResult(true, true, "已安装 " + best);
            return new DetectResult(false, true, "未安装");
        }

        private static DetectResult WebView2()
        {
            const string guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
            string[] paths = new string[]
            {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + guid,
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + guid
            };
            foreach (string path in paths)
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k == null) continue;
                    object pv = k.GetValue("pv");
                    if (pv != null && pv.ToString() != "0.0.0.0")
                        return new DetectResult(true, true, "已安装 " + pv);
                }
            }
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + guid))
            {
                if (k != null)
                {
                    object pv = k.GetValue("pv");
                    if (pv != null && pv.ToString() != "0.0.0.0")
                        return new DetectResult(true, true, "已安装 " + pv);
                }
            }
            return new DetectResult(false, true, "未安装");
        }

        /// <summary>扫描 Temurin / Oracle 等厂商的注册表，匹配主版本号。</summary>
        private static DetectResult Java(string major)
        {
            string[] roots = new string[]
            {
                @"SOFTWARE\Eclipse Adoptium\JRE",
                @"SOFTWARE\WOW6432Node\Eclipse Adoptium\JRE",
                @"SOFTWARE\JavaSoft\Java Runtime Environment",
                @"SOFTWARE\WOW6432Node\JavaSoft\Java Runtime Environment",
                @"SOFTWARE\JavaSoft\JRE",
                @"SOFTWARE\WOW6432Node\JavaSoft\JRE"
            };
            foreach (string root in roots)
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(root))
                {
                    if (k == null) continue;
                    foreach (string ver in k.GetSubKeyNames())
                        if (MajorOf(ver) == major)
                            return new DetectResult(true, true, "已安装 " + ver);
                }
            }
            return new DetectResult(false, true, "未安装");
        }

        private static string MajorOf(string ver)
        {
            if (ver.StartsWith("1.8")) return "8";
            string[] p = ver.Split('.');
            if (p.Length >= 2 && p[0] == "1") return p[1];
            return p.Length >= 1 ? p[0] : ver;
        }

        /// <summary>XNA Framework：官方判据是 HKLM\Software\Microsoft\XNA\Framework\vX.Y\NativeLibraryPath。
        /// 它是 32 位组件，64 位系统上落在 WOW6432Node，两个视图都要查。</summary>
        private static DetectResult Xna(string ver)
        {
            string[] roots = new string[]
            {
                @"SOFTWARE\Microsoft\XNA\Framework\v" + ver,
                @"SOFTWARE\WOW6432Node\Microsoft\XNA\Framework\v" + ver
            };
            foreach (string root in roots)
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(root))
                {
                    if (k == null) continue;
                    object v = k.GetValue("NativeLibraryPath");
                    if (v != null && v.ToString().Length > 0)
                        return new DetectResult(true, true, "已安装");
                }
            }
            return new DetectResult(false, true, "未安装");
        }

        /// <summary>Visual J# 2.0：把类库 vjslib.dll 安装进 .NET Framework 2.0 目录，以此判定。</summary>
        private static DetectResult VJSharp()
        {
            string net = Path.Combine(Sys.Windows, "Microsoft.NET");
            string[] files = new string[]
            {
                Path.Combine(net, "Framework", "v2.0.50727", "vjslib.dll"),
                Path.Combine(net, "Framework64", "v2.0.50727", "vjslib.dll")
            };
            foreach (string f in files)
                if (File.Exists(f)) return new DetectResult(true, true, "已安装");
            return new DetectResult(false, true, "未安装");
        }

        /// <summary>VSTO 4.0 运行时：注册表 HKLM\SOFTWARE\Microsoft\VSTO Runtime Setup\v4（或 v4R）。</summary>
        private static DetectResult Vsto()
        {
            string[] roots = new string[]
            {
                @"SOFTWARE\Microsoft\VSTO Runtime Setup\v4",
                @"SOFTWARE\Microsoft\VSTO Runtime Setup\v4R",
                @"SOFTWARE\WOW6432Node\Microsoft\VSTO Runtime Setup\v4",
                @"SOFTWARE\WOW6432Node\Microsoft\VSTO Runtime Setup\v4R"
            };
            foreach (string root in roots)
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(root))
                {
                    if (k == null) continue;
                    object v = k.GetValue("Version");
                    if (v != null) return new DetectResult(true, true, "已安装 " + v);
                }
            }
            return new DetectResult(false, true, "未安装");
        }
    }

}
