// ============================================================================
//  86 运行库
//  ---------------------------------------------------------------------------
//  一个零依赖的 Windows 原生程序（面向 Windows 10 / 11）：列出全部主流
//  C++ / .NET 运行库及其它常用运行库，自动检测本机安装状态，支持勾选后一键
//  静默安装。安装包首次运行从微软（及 Java/OpenAL/Vulkan 官方）服务器下载，
//  保存在程序同目录的 packages 文件夹内，之后可离线复用。
//
//  编译： csc.exe /target:winexe /codepage:65001 /win32icon:app.ico ...
//  自检： 86运行库.exe /selftest [报告文件]
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RunTimePack
{
    // ======================= 数据模型 =======================

    internal enum InstallKind
    {
        Exe,        // 普通 exe，带静默参数直接运行
        Msi,        // 走 msiexec 安装
        Dism,       // Windows 功能（.NET Framework 3.5）
        DirectX,    // 先自解压，再运行 DXSETUP.exe /silent
        Zip,        // 解压后运行包内 exe
        DllRepair   // 体检并补齐系统目录中缺失的运行库 DLL（从本机 WinSxS 取文件）
    }

    internal class Pkg
    {
        public string Category;
        public string Name;
        public string Note;          // 版本 / 说明
        public string Url;
        public string FileName;      // 本地保存名（也用于离线包匹配）
        public InstallKind Kind;
        public string Args;          // 静默安装参数
        public string DetectKind;
        public string DetectArg;
        public long SizeHint;        // 大致体积，仅用于展示
        public bool Recommended;     // 是否属于“推荐一键安装”集合
        public bool X64Only;         // 仅 64 位系统适用

        public string InnerExe;      // Zip 类型：压缩包内要运行的 exe
        public string InnerArgs;

        // 运行时状态
        public ListViewItem Item;
        public bool Installed;
        public bool Applicable = true;
        public bool Selected;            // 勾选状态以这里为准（不能直接读 ListViewItem.Checked，见 UpdateTabTitles 的说明）
        public string StatusText = "等待检测";

        public Pkg(string category, string name, string note, string url, string fileName,
                   InstallKind kind, string args, string detectKind, string detectArg,
                   long sizeHint, bool recommended, bool x64Only)
        {
            Category = category;
            Name = name;
            Note = note;
            Url = url;
            FileName = fileName;
            Kind = kind;
            Args = args;
            DetectKind = detectKind;
            DetectArg = detectArg;
            SizeHint = sizeHint;
            Recommended = recommended;
            X64Only = x64Only;
        }

        public string SizeText
        {
            get
            {
                if (SizeHint <= 0) return "体积未知";
                if (SizeHint >= 1024L * 1024L)
                    return string.Format("{0:0.#} MB", SizeHint / (1024.0 * 1024.0));
                return string.Format("{0:0.#} KB", SizeHint / 1024.0);
            }
        }

        /// <summary>
        /// "修复模式"参数：当运行库本身已安装、但系统目录里少了某个 DLL 时，
        /// 重复 /install 会被安装器判定为"已安装"而什么都不做（文件仍然缺失），
        /// 必须改用 /repair 让 Windows Installer 还原缺失组件。
        /// 仅对使用 /install 静默参数的安装器（VC++ 2012 / 2013 / v14）自动推导。
        /// </summary>
        public string RepairArgs
        {
            get
            {
                if (Kind != InstallKind.Exe) return null;
                if (Args == null) return null;
                if (Args.IndexOf("/install", StringComparison.OrdinalIgnoreCase) < 0) return null;
                return Args.Replace("/install", "/repair");
            }
        }
    }

    // ======================= 运行库清单 =======================
    //  所有下载地址均为官方地址，且均已逐一验证可访问。
    internal static class Catalog
    {
        public const string CAT_VC     = "Microsoft Visual C++ 运行库";
        public const string CAT_NET    = ".NET 运行库";
        public const string CAT_GAME   = "游戏 & 经典运行库";
        public const string CAT_OTHER  = "其他常用运行库";
        public const string CAT_REPAIR = "系统组件修复";

        private const string DM = "https://download.microsoft.com/download/";

        public static List<Pkg> Build()
        {
            List<Pkg> list = new List<Pkg>();
            const InstallKind E = InstallKind.Exe;

            // ---------- Visual C++ ----------
            // 2005/2008 的安装包是自解压程序，需把 msi 再交给 msiexec 才能完全静默
            const string OLD_ARGS = "/q:a /c:\"msiexec /i vcredist.msi /qn /norestart\"";

            list.Add(new Pkg(CAT_VC, "Visual C++ 2005 SP1 (x86)", "8.0.61001", DM + "8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x86.exe", "vcredist_2005_x86.exe", E, OLD_ARGS, "vcredist", "2005|x86", 2710520, true, false));
            list.Add(new Pkg(CAT_VC, "Visual C++ 2005 SP1 (x64)", "8.0.61001", DM + "8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x64.exe", "vcredist_2005_x64.exe", E, OLD_ARGS, "vcredist", "2005|x64", 3179000, true, true));
            list.Add(new Pkg(CAT_VC, "Visual C++ 2008 SP1 (x86)", "9.0.30729.5677", DM + "5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x86.exe", "vcredist_2008_x86.exe", E, OLD_ARGS, "vcredist", "2008|x86", 4483040, true, false));
            list.Add(new Pkg(CAT_VC, "Visual C++ 2008 SP1 (x64)", "9.0.30729.5677", DM + "5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x64.exe", "vcredist_2008_x64.exe", E, OLD_ARGS, "vcredist", "2008|x64", 5211080, true, true));
            list.Add(new Pkg(CAT_VC, "Visual C++ 2010 SP1 (x86)", "10.0.40219.325", DM + "1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x86.exe", "vcredist_2010_x86.exe", E, "/q /norestart", "vcfile", "x86|msvcr100.dll", 8993744, true, false));
            list.Add(new Pkg(CAT_VC, "Visual C++ 2010 SP1 (x64)", "10.0.40219.325", DM + "1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe", "vcredist_2010_x64.exe", E, "/q /norestart", "vcfile", "x64|msvcr100.dll", 10277328, true, true));
            list.Add(new Pkg(CAT_VC, "Visual C++ 2012 Update 4 (x86)", "11.0.61030.0", DM + "1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x86.exe", "vcredist_2012_x86.exe", E, "/install /quiet /norestart", "vcfile", "x86|msvcr110.dll", 6554576, true, false));
            list.Add(new Pkg(CAT_VC, "Visual C++ 2012 Update 4 (x64)", "11.0.61030.0", DM + "1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe", "vcredist_2012_x64.exe", E, "/install /quiet /norestart", "vcfile", "x64|msvcr110.dll", 7186992, true, true));
            list.Add(new Pkg(CAT_VC, "Visual C++ 2013 (x86)", "12.0.40664.0", "https://aka.ms/highdpimfc2013x86enu", "vcredist_2013_x86.exe", E, "/install /quiet /norestart", "vcfile", "x86|msvcr120.dll", 6510136, true, false));
            list.Add(new Pkg(CAT_VC, "Visual C++ 2013 (x64)", "12.0.40664.0", "https://aka.ms/highdpimfc2013x64enu", "vcredist_2013_x64.exe", E, "/install /quiet /norestart", "vcfile", "x64|msvcr120.dll", 7200744, true, true));
            list.Add(new Pkg(CAT_VC, "Visual C++ 2015-2026 (x86)", "v14 最新版（VS2017-2026 通用）", "https://aka.ms/vs/17/release/vc_redist.x86.exe", "vcredist_2015-2022_x86.exe", E, "/install /quiet /norestart", "vc14", "x86", 13953392, true, false));
            list.Add(new Pkg(CAT_VC, "Visual C++ 2015-2026 (x64)", "v14 最新版（VS2017-2026 通用）", "https://aka.ms/vs/17/release/vc_redist.x64.exe", "vcredist_2015-2022_x64.exe", E, "/install /quiet /norestart", "vc14", "x64", 25635768, true, true));

            // ---------- .NET ----------
            list.Add(new Pkg(CAT_NET, ".NET Framework 3.5 (含 2.0/3.0)", "系统功能，走 DISM 启用", "", "", InstallKind.Dism, "", "net35", "", 0, true, false));
            list.Add(new Pkg(CAT_NET, ".NET Framework 4.8", "4.8.0.0", "https://go.microsoft.com/fwlink/?linkid=2088631", "ndp48_x86_x64.exe", E, "/q /norestart", "net48", "", 121346568, true, false));

            list.Add(new Pkg(CAT_NET, ".NET Desktop Runtime 6.0 (x64)", "6.0 LTS", "https://aka.ms/dotnet/6.0/windowsdesktop-runtime-win-x64.exe", "windowsdesktop-runtime-6.0-x64.exe", E, "/install /quiet /norestart", "sharedfx", "Microsoft.WindowsDesktop.App|6", 57380656, false, true));
            list.Add(new Pkg(CAT_NET, ".NET Desktop Runtime 7.0 (x64)", "7.0", "https://aka.ms/dotnet/7.0/windowsdesktop-runtime-win-x64.exe", "windowsdesktop-runtime-7.0-x64.exe", E, "/install /quiet /norestart", "sharedfx", "Microsoft.WindowsDesktop.App|7", 58011208, false, true));
            list.Add(new Pkg(CAT_NET, ".NET Desktop Runtime 8.0 (x64)", "8.0 LTS", "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe", "windowsdesktop-runtime-8.0-x64.exe", E, "/install /quiet /norestart", "sharedfx", "Microsoft.WindowsDesktop.App|8", 58715896, true, true));
            list.Add(new Pkg(CAT_NET, ".NET Desktop Runtime 8.0 (x86)", "8.0 LTS", "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x86.exe", "windowsdesktop-runtime-8.0-x86.exe", E, "/install /quiet /norestart", "sharedfx", "Microsoft.WindowsDesktop.App|8|x86", 53761616, false, false));
            list.Add(new Pkg(CAT_NET, ".NET Desktop Runtime 9.0 (x64)", "9.0", "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe", "windowsdesktop-runtime-9.0-x64.exe", E, "/install /quiet /norestart", "sharedfx", "Microsoft.WindowsDesktop.App|9", 60851384, false, true));
            list.Add(new Pkg(CAT_NET, ".NET Desktop Runtime 10.0 (x64)", "10.0 LTS", "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe", "windowsdesktop-runtime-10.0-x64.exe", E, "/install /quiet /norestart", "sharedfx", "Microsoft.WindowsDesktop.App|10", 60032984, false, true));
            list.Add(new Pkg(CAT_NET, "ASP.NET Core Runtime 8.0 (x64)", "8.0 LTS", "https://aka.ms/dotnet/8.0/aspnetcore-runtime-win-x64.exe", "aspnetcore-runtime-8.0-x64.exe", E, "/install /quiet /norestart", "sharedfx", "Microsoft.AspNetCore.App|8", 10505248, false, true));
            list.Add(new Pkg(CAT_NET, "ASP.NET Core Runtime 9.0 (x64)", "9.0", "https://aka.ms/dotnet/9.0/aspnetcore-runtime-win-x64.exe", "aspnetcore-runtime-9.0-x64.exe", E, "/install /quiet /norestart", "sharedfx", "Microsoft.AspNetCore.App|9", 10679120, false, true));

            // ---------- 游戏 & 经典运行库 ----------
            // XNA / J# 均为 32 位运行库，安装包本身同时带 x86/x64 组件，故不分架构列条目
            list.Add(new Pkg(CAT_GAME, "XNA Framework 3.1", "老游戏常用（XNA 3.1 游戏）",
                DM + "5/9/1/5912526c-b950-4662-99b6-119a83e60e5c/xnafx31_redist.msi",
                "xnafx31_redist.msi", InstallKind.Msi, "/qn /norestart", "xna", "3.1", 7672320, false, false));

            list.Add(new Pkg(CAT_GAME, "XNA Framework 4.0 Refresh", "老游戏常用（星露谷等）",
                DM + "5/3/A/53A804C8-EC78-43CD-A0F0-2FB4D45603D3/xnafx40_redist.msi",
                "xnafx40_redist.msi", InstallKind.Msi, "/qn /norestart", "xna", "4.0", 7059456, false, false));

            list.Add(new Pkg(CAT_GAME, "Visual J# 2.0 运行库", "J# 编写的老程序 / 老游戏",
                DM + "9/a/0/9a01eb1e-fe80-41af-a3f8-ea41220918f7/vjredist.exe",
                "vjredist.exe", E, "/q", "vjsharp", "", 3798912, false, false));

            // ---------- 其他常用运行库 ----------
            list.Add(new Pkg(CAT_OTHER, "DirectX 9.0c 运行库", "2010 年 6 月版", DM + "8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe", "directx_Jun2010_redist.exe", InstallKind.DirectX, "", "vcfile", "both|d3dx9_43.dll", 100275120, true, false));
            list.Add(new Pkg(CAT_OTHER, "Microsoft Edge WebView2 Runtime", "常青版引导程序", "https://go.microsoft.com/fwlink/p/?LinkId=2124703", "MicrosoftEdgeWebview2Setup.exe", E, "/silent /install", "webview2", "", 1850576, true, false));

            Pkg jre8 = new Pkg(CAT_OTHER, "Java 运行环境 JRE 8 (Eclipse Temurin)", "8 (x64)", "https://api.adoptium.net/v3/installer/latest/8/ga/windows/x64/jre/hotspot/normal/eclipse", "temurin-jre8-x64.msi", InstallKind.Msi, "/qn /norestart", "java", "8", 32010240, false, true);
            list.Add(jre8);

            Pkg jre21 = new Pkg(CAT_OTHER, "Java 运行环境 JRE 21 (Eclipse Temurin)", "21 LTS", "https://api.adoptium.net/v3/installer/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse", "temurin-jre21-x64.msi", InstallKind.Msi, "/qn /norestart", "java", "21", 34963456, false, true);
            list.Add(jre21);

            Pkg openal = new Pkg(CAT_OTHER, "OpenAL 1.1 音频运行库", "老游戏常用", "https://www.openal.org/downloads/oalinst.zip", "oalinst.zip", InstallKind.Zip, "", "vcfile", "both|OpenAL32.dll", 590434, false, false);
            openal.InnerExe = "oalinst.exe";
            openal.InnerArgs = "/s";
            list.Add(openal);

            list.Add(new Pkg(CAT_OTHER, "Vulkan Runtime 运行库", "图形 API 运行时", "https://sdk.lunarg.com/sdk/download/latest/windows/vulkan-runtime.exe", "vulkan-runtime.exe", E, "/S", "vcfile", "both|vulkan-1.dll", 25699768, false, false));

            list.Add(new Pkg(CAT_OTHER, "Visual Studio 2010 Tools for Office Runtime", "VSTO 4.0，Office 插件依赖",
                DM + "8/6/4/8641e164-7796-4b34-81c7-30d24a5bd533/vstor_redist.exe",
                "vstor_redist.exe", E, "/quiet /norestart", "vsto", "", 41649696, false, false));

            // ---------- 系统组件修复 ----------
            list.Add(new Pkg(CAT_REPAIR, "补充 / 修复系统运行库 DLL（含 DirectX）",
                "体检 " + DllCatalog.Items.Count + " 种 DLL（DirectX / VC++ / MFC / 系统组件），共 "
                + Sys.TotalChecks + " 项，缺失即就地补齐或自动安装归属运行库",
                "", "", InstallKind.DllRepair, "", "dllrepair", "", 0, false, false));

            return list;
        }
    }

    // ======================= 运行库 DLL 体检清单 =======================
    //  对照 DirectX 修复工具的做法，把"常见缺失的运行库 DLL"整理成一张清单，
    //  逐项体检系统目录（System32 / SysWOW64），缺失的就地补齐。
    //
    //  DirectX 部分的 92 个名称，是把微软官方 directx_Jun2010_redist.exe 解包后
    //  逐个 CAB 展开、按实际内容清点出来的，不是凭印象编的。
    //  每一项都带有"归属运行库"：本机找不到现成副本时，就自动下载并静默安装它。

    internal static class DllCatalog
    {
        public const string G_DX  = "DirectX 组件";
        public const string G_CRT = "VC++ 运行库";
        public const string G_MFC = "MFC / ATL";
        public const string G_SYS = "系统组件";

        internal class Item
        {
            public string Name;    // DLL 文件名（小写）
            public string Group;   // 分组，仅用于统计展示
            public string Owner;   // 归属运行库代号，见 OwnerFile()
            public bool X64Only;   // 只有 64 位版本，32 位副本永不存在，不参与 x86 体检

            public Item(string name, string group, string owner)
            {
                Name = name;
                Group = group;
                Owner = owner;
            }
        }

        public static readonly List<Item> Items = Build();

        private static List<Item> Build()
        {
            List<Item> l = new List<Item>();

            // ---- DirectX 9.0c 组件（2010 年 6 月版，共 92 个）----
            Range(l, G_DX, "directx", "d3dcompiler_", 33, 43);
            Range(l, G_DX, "directx", "d3dcsx_", 42, 43);
            l.Add(new Item("d3dx10.dll", G_DX, "directx"));
            Range(l, G_DX, "directx", "d3dx10_", 33, 43);
            Range(l, G_DX, "directx", "d3dx11_", 42, 43);
            Range(l, G_DX, "directx", "d3dx9_", 24, 43);
            Range(l, G_DX, "directx", "x3daudio1_", 0, 7);
            Range(l, G_DX, "directx", "xactengine2_", 0, 10);
            Range(l, G_DX, "directx", "xactengine3_", 0, 7);
            Range(l, G_DX, "directx", "xapofx1_", 0, 5);
            Range(l, G_DX, "directx", "xaudio2_", 0, 7);
            Range(l, G_DX, "directx", "xinput1_", 1, 3);
            l.Add(new Item("xinput9_1_0.dll", G_DX, "directx"));

            // ---- VC++ 运行库 ----
            l.Add(new Item("msvcr80.dll", G_CRT, "2005"));
            l.Add(new Item("msvcp80.dll", G_CRT, "2005"));
            l.Add(new Item("msvcr90.dll", G_CRT, "2008"));
            l.Add(new Item("msvcp90.dll", G_CRT, "2008"));
            l.Add(new Item("msvcr100.dll", G_CRT, "2010"));
            l.Add(new Item("msvcp100.dll", G_CRT, "2010"));
            l.Add(new Item("msvcr110.dll", G_CRT, "2012"));
            l.Add(new Item("msvcp110.dll", G_CRT, "2012"));
            l.Add(new Item("msvcr120.dll", G_CRT, "2013"));
            l.Add(new Item("msvcp120.dll", G_CRT, "2013"));
            string[] v14 = new string[]
            {
                "vcruntime140.dll",
                "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll",
                "msvcp140_codecvt_ids.dll", "concrt140.dll", "vccorlib140.dll"
            };
            foreach (string n in v14) l.Add(new Item(n, G_CRT, "vc14"));

            //  实测（解包官方 vcredist_2015-2022_x86.exe 的 Burn 容器，读两个 MSI 的 File 表）：
            //    x86 Minimum Runtime 11 个文件里**没有** vcruntime140_1.dll；
            //    x64 / arm64 Minimum Runtime 才有该文件。
            //  即微软从未发布 32 位版 vcruntime140_1.dll，官方任何安装包都补不出它，
            //  与 atl140.dll 同属"永远补不上"的假缺口，故标记为仅 64 位、不参与 x86 体检。
            Item vcr1401 = new Item("vcruntime140_1.dll", G_CRT, "vc14");
            vcr1401.X64Only = true;
            l.Add(vcr1401);

            // ---- MFC（VC++ 2015-2026 起随 vc_redist 一起安装）----
            //  注意：ATL 自 v14 起是纯头文件，不再分发 atl140.dll，
            //  连完整安装的机器上也没有该文件，故不列入清单（否则会永远报"缺失"）。
            string[] mfc = new string[]
            {
                "mfc140.dll", "mfc140u.dll", "mfcm140.dll", "mfcm140u.dll"
            };
            foreach (string n in mfc) l.Add(new Item(n, G_MFC, "vc14"));

            // ---- 系统自带组件（正常情况下一定存在，缺失时从 WinSxS 取）----
            l.Add(new Item("msvcrt.dll", G_SYS, null));
            l.Add(new Item("msvcirt.dll", G_SYS, null));

            return l;
        }

        private static void Range(List<Item> l, string group, string owner, string prefix, int from, int to)
        {
            for (int i = from; i <= to; i++) l.Add(new Item(prefix + i + ".dll", group, owner));
        }

        /// <summary>归属运行库代号 → 运行库清单里的安装包文件名（离线包名）。</summary>
        public static string OwnerFile(string owner, bool x64)
        {
            switch (owner)
            {
                case "directx": return "directx_Jun2010_redist.exe";
                case "2005":    return x64 ? "vcredist_2005_x64.exe" : "vcredist_2005_x86.exe";
                case "2008":    return x64 ? "vcredist_2008_x64.exe" : "vcredist_2008_x86.exe";
                case "2010":    return x64 ? "vcredist_2010_x64.exe" : "vcredist_2010_x86.exe";
                case "2012":    return x64 ? "vcredist_2012_x64.exe" : "vcredist_2012_x86.exe";
                case "2013":    return x64 ? "vcredist_2013_x64.exe" : "vcredist_2013_x86.exe";
                case "vc14":    return x64 ? "vcredist_2015-2022_x64.exe" : "vcredist_2015-2022_x86.exe";
            }
            return null;
        }

        /// <summary>按分组统计。</summary>
        public static List<string> Groups()
        {
            List<string> g = new List<string>();
            foreach (Item i in Items) if (!g.Contains(i.Group)) g.Add(i.Group);
            return g;
        }
    }

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

    // ======================= 下载与进程工具 =======================

    internal static class Util
    {
        public static string WorkDir;

        /// <summary>安装包存放目录：优先程序同目录的 packages（便于离线复用），不可写时退回临时目录。</summary>
        public static string ResolveWorkDir()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                string cand = Path.Combine(exeDir, "packages");
                Directory.CreateDirectory(cand);
                // 实际写一个文件验证权限
                string probe = Path.Combine(cand, ".writetest");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return cand;
            }
            catch
            {
                string tmp = Path.Combine(Path.GetTempPath(), "86运行库");
                Directory.CreateDirectory(tmp);
                return tmp;
            }
        }

        public static void Download(string url, string dest, Action<int> onPercent, Func<bool> cancelled)
        {
            // 网络抖动是下载型工具的常见故障，最多自动重试 3 次
            Exception last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (cancelled != null && cancelled()) throw new OperationCanceledException("用户取消");
                try
                {
                    DownloadOnce(url, dest, onPercent, cancelled);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 3) Thread.Sleep(1500);
                }
            }
            throw new Exception("下载失败（已自动重试 3 次）："
                + (last == null ? "未知错误" : last.Message), last);
        }

        private static void DownloadOnce(string url, string dest, Action<int> onPercent, Func<bool> cancelled)
        {
            try
            {
                // .NET Framework 4.x 默认可能不含 TLS 1.2，必须显式打开，否则 aka.ms / builds.dotnet 会握手失败
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls | (SecurityProtocolType)768 | (SecurityProtocolType)3072;
            }
            catch { }

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            req.AllowAutoRedirect = true;
            req.Timeout = 60000;
            req.ReadWriteTimeout = 180000;

            string part = dest + ".part";
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    long total = resp.ContentLength;
                    using (Stream rs = resp.GetResponseStream())
                    using (FileStream fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                    {
                        byte[] buf = new byte[65536];
                        long got = 0;
                        int lastPct = -1;
                        int n;
                        while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (cancelled != null && cancelled()) throw new OperationCanceledException("用户取消");
                            fs.Write(buf, 0, n);
                            got += n;
                            if (total > 0)
                            {
                                int pct = (int)(got * 100L / total);
                                if (pct != lastPct)
                                {
                                    lastPct = pct;
                                    if (onPercent != null) onPercent(pct);
                                }
                            }
                        }
                    }
                }
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(part, dest);
            }
            catch
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                throw;
            }
        }

        /// <summary>启动进程并等待结束，返回退出代码。</summary>
        public static int RunExe(string file, string args)
        {
            return RunExe(file, args, null);
        }

        /// <summary>
        /// 可取消的等待版本：cancelCheck 返回 true 时结束子进程并抛 OperationCanceledException。
        /// 用于 DISM / sfc 这类可能占用十几分钟的长命令，保证界面上的"取消"真的有用。
        /// </summary>
        public static int RunExe(string file, string args, Func<bool> cancelCheck)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = file;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            try
            {
                string wd = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(wd) && Directory.Exists(wd)) psi.WorkingDirectory = wd;
            }
            catch { }

            Process p = Process.Start(psi);
            if (p == null) throw new Exception("无法启动进程：" + file);
            using (p)
            {
                if (cancelCheck == null)
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
                while (!p.WaitForExit(300))
                {
                    if (cancelCheck())
                    {
                        try { p.Kill(); } catch { }
                        throw new OperationCanceledException();
                    }
                }
                return p.ExitCode;
            }
        }
    }

    // ======================= 主界面 =======================

    public class MainForm : Form
    {
        private TabControl tabs;
        private readonly List<string> catOrder = new List<string>();
        private readonly Dictionary<string, TabPage> pages = new Dictionary<string, TabPage>();
        private readonly Dictionary<string, ListView> lists = new Dictionary<string, ListView>();

        private TextBox log;
        private ProgressBar bar;
        private Label lblStatus;
        private ToolTip tip;
        private CheckBox chkAllPages;
        private Button btnInstall, btnAll, btnNone, btnInvert, btnMissing, btnRecommended, btnRescan, btnOpenDir;
        /// <summary>安装期间要临时禁用的工具栏按钮（防止用户误以为"点了没反应"）。</summary>
        private Button[] toolbarBtns;
        /// <summary>批量勾选（全选/反选等）进行中：此时不逐行写点击日志，避免刷屏。</summary>
        private bool batchToggling;

        private List<Pkg> pkgs = new List<Pkg>();
        private string workDir;

        private volatile bool busy;
        private volatile bool cancelFlag;
        private bool needReboot;
        private bool firstDetection = true;   // 只有首次检测才自动套用“推荐”勾选，避免覆盖用户的手动选择

        // ---- 权限解耦：界面永远是普通权限，安装交给另起的提权工作进程 ----
        /// <summary>本实例是不是提权的工作进程（无界面，只负责执行安装）。</summary>
        private bool workerMode;
        /// <summary>工作进程模式下的任务文件（内含安装目录与要安装的条目序号）。</summary>
        private string workerTaskFile;
        private string workerStatusFile;     // 工作进程把日志/进度追加到这个文件
        private string workerCancelFile;     // 界面把取消请求写成这个文件的出现
        private Process workerProc;          // 界面侧持有的提权进程
        private System.Windows.Forms.Timer workerTimer;
        private long workerTailPos;          // 已读取到的状态文件字节位置
        private string workerTailPending = ""; // 没读完的半行
        private bool workerDone;             // 是否已经收到 DONE 行
        private int workerExitTicks;          // 工作进程已退出、但还没读到 DONE 行的轮询计数
        private bool elevatedInstall;        // 本次安装是否由提权工作进程执行

        /// <summary>
        /// 取消标记。工作进程模式下改为轮询“取消文件”是否出现，
        /// 这样界面（普通权限）也能中断提权进程里的长任务。
        /// </summary>
        private bool cancelRequested
        {
            get
            {
                if (!cancelFlag && workerMode && workerCancelFile != null)
                {
                    try { if (File.Exists(workerCancelFile)) cancelFlag = true; }
                    catch { }
                }
                return cancelFlag;
            }
            set { cancelFlag = value; }
        }

        public MainForm()
        {
            Text = "86运行库";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 720);
            MinimumSize = new Size(880, 600);
            Font = PickFont();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            // 标题栏 / 任务栏图标：.NET 窗口默认不会自动使用 exe 里内嵌的图标，必须显式设置
            try { Icon = LoadAppIcon(); } catch { }

            workDir = Util.WorkDir;

            BuildUi();
            BuildList();
            UpdateTabTitles();

            Shown += delegate
            {
                FitToWorkingArea();
                LayoutColumns();
                Activate();
                int dpiPct;
                using (Graphics g = CreateGraphics()) dpiPct = (int)(g.DpiX * 100 / 96);
                Rectangle wa0 = Screen.FromControl(this).WorkingArea;
                Log("程序文件：" + SafeExeStamp());
                Log("当前权限：" + (IsElevated() ? "管理员（安装无需再次授权）"
                                                  : "标准用户（界面免授权，开始安装时请求一次管理员权限）"));
                Log("窗口 " + Width + "×" + Height + "，屏幕可用区 " + wa0.Width + "×" + wa0.Height
                    + "，缩放 " + dpiPct + "%");
                RunDetection();
            };
            FormClosing += OnFormClosing;
        }

        /// <summary>
        /// 工作进程模式：不建任何界面，只按任务文件执行安装。
        /// 由界面进程通过 runas 以管理员身份拉起，因此界面本身可以一直是普通权限。
        /// </summary>
        public MainForm(string taskFile)
        {
            workerMode = true;
            workerTaskFile = taskFile;
            workerStatusFile = TaskFile.StatusPath(taskFile);
            workerCancelFile = TaskFile.CancelPath(taskFile);
            Util.WorkDir = TaskFile.ReadValue(taskFile, "workdir");
            if (string.IsNullOrEmpty(Util.WorkDir)) Util.WorkDir = Util.ResolveWorkDir();
            workDir = Util.WorkDir;
            pkgs = Catalog.Build();
        }

        /// <summary>工作进程入口：解析任务文件里的条目序号并执行安装，结束返回退出码。</summary>
        public int RunHeadless()
        {
            List<Pkg> sel = new List<Pkg>();
            foreach (int i in TaskFile.ReadIndices(workerTaskFile))
                if (i >= 0 && i < pkgs.Count) sel.Add(pkgs[i]);

            Log("工作进程已启动（" + (IsElevated() ? "管理员权限" : "普通权限") + "），共 " + sel.Count + " 个任务。");
            Log("安装包目录：" + workDir);
            InstallWorker(sel);
            return 0;
        }

        private static string SafeExeStamp()
        {
            try
            {
                return Application.ExecutablePath + "（生成于 "
                       + File.GetLastWriteTime(Application.ExecutablePath).ToString("yyyy-MM-dd HH:mm") + "）";
            }
            catch { return Application.ExecutablePath; }
        }

        /// <summary>当前进程是否已有管理员权限。</summary>
        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>
        /// 把窗口收进屏幕可用区：小屏（例如 1366×768、任务栏占掉一截）或 125% 缩放时，
        /// 默认的 1000×720 客户区可能比可用区还高，底部"开始安装"按钮会被任务栏挡住，
        /// 点上去就像"没反应"。这里按可用区收缩并重新居中。
        /// </summary>
        private void FitToWorkingArea()
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int w = Math.Min(Width, wa.Width - 16);
            int h = Math.Min(Height, wa.Height - 16);
            if (w < 640) w = Math.Min(wa.Width, 640);
            if (h < 480) h = Math.Min(wa.Height, 480);

            Size min = MinimumSize;
            if (w < min.Width || h < min.Height)
                MinimumSize = new Size(Math.Min(min.Width, w), Math.Min(min.Height, h));

            if (Width != w || Height != h)
            {
                Size = new Size(w, h);
                Location = new Point(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2);
            }
        }

        /// <summary>
        /// 取回程序图标：优先用编译时内嵌的 ico 资源（多尺寸，标题栏和任务栏都清晰），
        /// 取不到再退回从 exe 文件本身提取。
        /// </summary>
        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                Stream s = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico");
                if (s != null)
                {
                    using (s) { return new System.Drawing.Icon(s, new Size(32, 32)); }
                }
            }
            catch { }
            return System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }

        // ---------- 界面搭建 ----------

        private static Font PickFont()
        {
            string[] names = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei" };
            foreach (string n in names)
            {
                try
                {
                    Font f = new Font(n, 9F);
                    if (string.Compare(f.Name, n, true) == 0) return f;
                    f.Dispose();
                }
                catch { }
            }
            return SystemFonts.MessageBoxFont;
        }

        private Button MakeButton(string text, int width, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = width;
            b.Height = 28;
            b.Margin = new Padding(0, 0, 6, 0);
            b.Click += onClick;
            return b;
        }

        private void BuildUi()
        {
            // ---- 顶部工具栏 ----
            FlowLayoutPanel toolbar = new FlowLayoutPanel();
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = 46;
            toolbar.Padding = new Padding(8, 9, 8, 4);
            toolbar.WrapContents = false;

            // 按钮文字带上快捷键：万一鼠标点击被系统/其他窗口拦截，键盘也能操作。
            btnAll = MakeButton("全选(&A)", 78, delegate { SetAll(true); });
            btnNone = MakeButton("全不选(&N)", 88, delegate { SetAll(false); });
            btnInvert = MakeButton("反选(&I)", 78, delegate { InvertAll(); });
            btnMissing = MakeButton("仅选未安装(&U)", 120, delegate { SelectMissing(); });
            btnRecommended = MakeButton("仅选推荐(&R)", 106, delegate { ApplyRecommended(false); });

            chkAllPages = new CheckBox();
            chkAllPages.Text = "作用于所有页";
            chkAllPages.AutoSize = true;
            chkAllPages.Margin = new Padding(12, 6, 12, 0);

            tip = new ToolTip();
            tip.SetToolTip(chkAllPages, "不勾选时，左边的选择按钮只作用于当前分页；勾选后作用于全部分页。");
            tip.SetToolTip(btnAll, "选中当前分页里所有适用于本机（或所有分页）的运行库");

            btnRescan = MakeButton("重新检测(&D)", 106, delegate { RunDetection(); });
            btnOpenDir = MakeButton("打开安装包目录(&O)", 148, delegate { OpenWorkDir(); });

            toolbarBtns = new Button[] { btnAll, btnNone, btnInvert, btnMissing, btnRecommended, btnRescan };

            toolbar.Controls.Add(btnAll);
            toolbar.Controls.Add(btnNone);
            toolbar.Controls.Add(btnInvert);
            toolbar.Controls.Add(btnMissing);
            toolbar.Controls.Add(btnRecommended);
            toolbar.Controls.Add(chkAllPages);
            toolbar.Controls.Add(btnRescan);
            toolbar.Controls.Add(btnOpenDir);

            // ---- 运行库列表：按分类分成多页，便于按类型挑选 ----
            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(14, 5);
            tabs.SelectedIndexChanged += delegate { OnTabChanged(); };

            // ---- 日志 ----
            log = new TextBox();
            log.Dock = DockStyle.Bottom;
            log.Height = 150;
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.BackColor = Color.FromArgb(28, 30, 34);
            log.ForeColor = Color.Gainsboro;
            log.Font = new Font("Consolas", 9F);
            log.WordWrap = false;

            // ---- 底部操作栏 ----
            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 64;
            bottom.Padding = new Padding(8, 6, 8, 8);
            bottom.ColumnCount = 3;
            bottom.RowCount = 1;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230F));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            btnInstall = new Button();
            btnInstall.Text = "开始安装选中的运行库";
            btnInstall.Dock = DockStyle.Fill;
            btnInstall.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            btnInstall.Click += delegate
            {
                if (busy) { Log("▶ 点击：取消安装"); RequestCancel(); return; }
                Log("▶ 点击：开始安装选中的运行库");
                StartInstall();
            };
            // 键盘兜底：回车=开始安装，F5=重新检测。鼠标输入被系统拦截时仍可操作。
            AcceptButton = btnInstall;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F5) { e.Handled = true; RunDetection(); }
            };

            bar = new ProgressBar();
            bar.Height = 22;
            bar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            bar.Minimum = 0;
            bar.Maximum = 100;

            lblStatus = new Label();
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.TextAlign = ContentAlignment.MiddleRight;
            lblStatus.Text = "正在准备…";
            lblStatus.AutoEllipsis = true;

            bottom.Controls.Add(btnInstall, 0, 0);
            bottom.Controls.Add(bar, 1, 0);
            bottom.Controls.Add(lblStatus, 2, 0);

            // 顺序很重要：Fill 先加，停靠条后加
            Controls.Add(tabs);
            Controls.Add(log);
            Controls.Add(bottom);
            Controls.Add(toolbar);
        }

        /// <summary>第二列括号内显示的内容：需要下载的显示体积，不需要下载的直说。</summary>
        private static string DownloadHint(Pkg p)
        {
            if (p.Kind == InstallKind.DllRepair) return "体检后自动补齐，不额外下载清单";
            if (p.Kind == InstallKind.Dism) return "系统功能，无需下载";
            return p.SizeText;
        }

        private static string TooltipFor(Pkg p)
        {
            if (p.Kind == InstallKind.DllRepair)
                return "逐项体检 System32 / SysWOW64 里 " + DllCatalog.Items.Count + " 种（双架构共 " + Sys.TotalChecks
                     + " 项）常用运行库 DLL：能就地补齐的直接从本机系统组件复制原版文件；\n"
                     + "本机没有副本的，会自动下载并静默安装它所属的微软官方运行库（DirectX 9.0c 或对应 Visual C++）。";
            if (p.Kind == InstallKind.Dism)
                return ".NET Framework 3.5 是 Windows 的“按需功能”，安装时按下面的顺序自动降级尝试：\n"
                     + "  1) 本程序同级目录下 sxs\\ 或 sources\\sxs\\ 里的安装介质源（完全离线，最可靠）\n"
                     + "  2) 本程序同级目录下的 Microsoft-Windows-NetFx3-OnDemand-Package*.cab（VLSC 按需功能介质）\n"
                     + "  3) 从 Windows 更新在线下载\n"
                     + "若第一次失败，程序会自动把内置修复方案运行到本机，并重启安装任务重试：\n"
                     + "  · 恢复并启动被禁用的 Windows 更新相关服务（wuauserv / BITS / CryptSvc 等）\n"
                     + "  · 解除组策略对 Windows 更新的拦截（内网 WSUS 环境常见，表现为 0x800F0954）\n"
                     + "  · 重置 Windows 更新组件缓存（SoftwareDistribution / catroot2，原目录只改名备份）\n"
                     + "  · 错误码指向组件损坏时，再自动执行 DISM RestoreHealth 与 sfc /scannow\n"
                     + "两三轮回合后仍失败，才会给出该错误码对应的中文解决办法。";
            return "通过 Windows 系统功能启用";
        }

        private ListView MakeListView()
        {
            ListView lv = new ListView();
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.CheckBoxes = true;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            lv.ShowItemToolTips = true;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.Columns.Add("运行库", 320);
            lv.Columns.Add("版本 / 说明", 320);
            lv.Columns.Add("本机状态", 240);
            lv.ItemCheck += OnItemCheck;
            lv.ItemChecked += OnItemChecked;
            lv.Resize += delegate { LayoutColumns(lv); };
            return lv;
        }

        private void LayoutColumns()
        {
            foreach (string c in catOrder) LayoutColumns(lists[c]);
        }

        private static void LayoutColumns(ListView lv)
        {
            int w = lv.ClientSize.Width - 4;
            if (w < 400) return;
            lv.Columns[0].Width = (int)(w * 0.38);
            lv.Columns[1].Width = (int)(w * 0.34);
            lv.Columns[2].Width = w - lv.Columns[0].Width - lv.Columns[1].Width;
        }

        private void BuildList()
        {
            pkgs = Catalog.Build();

            foreach (Pkg p in pkgs)
            {
                if (!lists.ContainsKey(p.Category))
                {
                    TabPage page = new TabPage(p.Category);
                    page.Padding = new Padding(4);
                    page.UseVisualStyleBackColor = true;

                    ListView lv = MakeListView();
                    page.Controls.Add(lv);
                    tabs.TabPages.Add(page);

                    lists[p.Category] = lv;
                    pages[p.Category] = page;
                    catOrder.Add(p.Category);
                }

                ListViewItem it = new ListViewItem(p.Name);
                it.SubItems.Add(p.Note + "　(" + DownloadHint(p) + ")");
                it.SubItems.Add(p.StatusText);
                it.ToolTipText = p.Url.Length > 0 ? p.Url : TooltipFor(p);
                it.Tag = p;
                p.Item = it;
                lists[p.Category].Items.Add(it);
            }
        }

        // ---------- 分页与选择范围 ----------

        private string CurrentCategory()
        {
            if (tabs.SelectedTab == null) return null;
            foreach (string c in catOrder)
                if (pages[c] == tabs.SelectedTab) return c;
            return null;
        }

        /// <summary>当前操作范围：默认只有当前分页，勾选“作用于所有页”后是全部。</summary>
        private List<Pkg> Scope()
        {
            List<Pkg> r = new List<Pkg>();
            if (chkAllPages.Checked)
            {
                r.AddRange(pkgs);
                return r;
            }
            string cat = CurrentCategory();
            if (cat == null) return r;
            foreach (Pkg p in pkgs)
                if (p.Category == cat) r.Add(p);
            return r;
        }

        /// <summary>范围的中文说法，用于给用户明确的反馈。</summary>
        private string ScopeName()
        {
            if (chkAllPages.Checked) return "所有页";
            string cat = CurrentCategory();
            return cat == null ? "当前页" : "当前页「" + cat + "」";
        }

        private void OnTabChanged()
        {
            LayoutColumns();
            string cat = CurrentCategory();
            if (cat != null) SetStatus("当前页：" + cat + "（选择按钮默认只作用于当前页）");
        }

        /// <summary>不适用于本机的条目不允许勾选。</summary>
        private void OnItemCheck(object sender, ItemCheckEventArgs e)
        {
            ListView lv = sender as ListView;
            if (lv == null || e.Index < 0 || e.Index >= lv.Items.Count) return;
            Pkg p = lv.Items[e.Index].Tag as Pkg;
            if (p == null) return;

            if (busy || !p.Applicable)
            {
                e.NewValue = e.CurrentValue;
                if (!batchToggling && !busy) Log("× 该项当前不可勾选：" + p.Name + "（" + p.StatusText + "）");
                return;
            }

            // 勾选状态只在这里记录：e.NewValue 是纯数值，不依赖控件内部索引，
            // 而 ListViewItem.Checked 在条目尚未真正挂到列表上时读取会抛异常。
            p.Selected = (e.NewValue == CheckState.Checked);
            if (!batchToggling)
                Log((p.Selected ? "▶ 勾选：" : "▶ 取消勾选：") + p.Name);
        }

        private void OnItemChecked(object sender, ItemCheckedEventArgs e)
        {
            UpdateTabTitles();
        }

        /// <summary>
        /// 分页标题上显示勾选进度，例如「.NET 运行库 (已选 3/10)」。
        /// 注意：这里只能读 Pkg.Selected。ListViewItem.Checked 内部会走
        /// GetItemState(Index, ...)，而 WinForms 在批量插入条目 / 延迟创建句柄时，
        /// 条目可能暂时还没真正进入列表（Index 为 -1），读取就会抛
        /// ArgumentOutOfRangeException（这就是之前的崩溃点）。
        /// </summary>
        private void UpdateTabTitles()
        {
            foreach (string c in catOrder)
            {
                int sel = 0, total = 0;
                foreach (Pkg p in pkgs)
                {
                    if (p.Category != c) continue;
                    total++;
                    if (p.Selected) sel++;
                }
                string text = c + " (已选 " + sel + "/" + total + ")";
                if (pages[c].Text != text) pages[c].Text = text;
            }
        }

        // ---------- 线程与 UI 交互辅助 ----------

        private void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }

        /// <summary>同步回到 UI 线程执行（需要拿到用户在弹窗里的选择时用）。</summary>
        private void UiSync(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) Invoke(a);
            else a();
        }

        /// <summary>在 UI 线程上弹一个确认框，返回用户是否点了“确定”。</summary>
        private bool Confirm(string text, string caption, MessageBoxIcon icon)
        {
            if (workerMode)
            {
                // 工作进程没有界面，无法弹窗；按默认方案继续，并把决定写进日志。
                Log("    （工作进程自动确认）" + caption);
                return true;
            }
            bool ok = false;
            UiSync(delegate
            {
                ok = (MessageBox.Show(this, text, caption, MessageBoxButtons.OKCancel, icon) == DialogResult.OK);
            });
            return ok;
        }

        private void Log(string s)
        {
            if (workerMode)
            {
                TaskFile.WriteLine(workerStatusFile,
                    "L\t" + DateTime.Now.ToString("HH:mm:ss") + "  " + s);
                return;
            }
            Ui(delegate
            {
                log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine);
            });
        }

        private void SetStatus(string s)
        {
            if (workerMode)
            {
                TaskFile.WriteLine(workerStatusFile, "S\t" + s);
                return;
            }
            Ui(delegate { lblStatus.Text = s; });
        }

        /// <summary>设置进度：工作进程模式下把百分比写进状态文件，由界面进程读取显示。</summary>
        private void SetBar(int pct)
        {
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            if (workerMode)
            {
                TaskFile.WriteLine(workerStatusFile, "P\t" + pct);
                return;
            }
            Ui(delegate
            {
                bar.Style = ProgressBarStyle.Continuous;
                bar.Value = pct;
            });
        }

        /// <summary>进度条进出“不确定进度”状态。</summary>
        private void SetMarquee(bool on)
        {
            if (workerMode)
            {
                TaskFile.WriteLine(workerStatusFile, "M\t" + (on ? "1" : "0"));
                return;
            }
            Ui(delegate
            {
                bar.Style = on ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            });
        }

        private void UpdateItem(Pkg p)
        {
            Ui(delegate
            {
                if (p.Item == null) return;
                p.Item.SubItems[2].Text = p.StatusText;
                if (!p.Applicable) p.Item.ForeColor = Color.Gray;
                else if (p.Installed) p.Item.ForeColor = Color.FromArgb(0, 122, 0);
                else p.Item.ForeColor = Color.FromArgb(176, 0, 32);
            });
        }

        private static string FmtMb(long bytes)
        {
            return string.Format("{0:0.#} MB", bytes / (1024.0 * 1024.0));
        }

        // ---------- 选择操作 ----------

        private void SetAll(bool value)
        {
            if (busy) return;
            int n = 0;
            batchToggling = true;
            try
            {
                foreach (Pkg p in Scope())
                    if (p.Applicable) { p.Selected = value; p.Item.Checked = value; if (value) n++; }
            }
            finally { batchToggling = false; }
            string txt = ScopeName() + "：已" + (value ? "全选 " : "全部取消 ") + n + " 项";
            SetStatus(txt);
            Log("▶ " + txt);
            UpdateTabTitles();
        }

        private void InvertAll()
        {
            if (busy) return;
            int n = 0;
            batchToggling = true;
            try
            {
                foreach (Pkg p in Scope())
                    if (p.Applicable) { p.Selected = !p.Selected; p.Item.Checked = p.Selected; if (p.Selected) n++; }
            }
            finally { batchToggling = false; }
            string txt = ScopeName() + "：已反选，当前勾选 " + n + " 项";
            SetStatus(txt);
            Log("▶ " + txt);
            UpdateTabTitles();
        }

        private void SelectMissing()
        {
            if (busy) return;
            int n = 0;
            batchToggling = true;
            try
            {
                foreach (Pkg p in Scope())
                {
                    p.Selected = p.Applicable && !p.Installed;
                    p.Item.Checked = p.Selected;
                    if (p.Selected) n++;
                }
            }
            finally { batchToggling = false; }
            string txt = ScopeName() + "：已勾选未安装的运行库 " + n + " 项"
                       + (n == 0 ? "（本机都已安装，无需处理）" : "");
            SetStatus(txt);
            Log("▶ " + txt);
            UpdateTabTitles();
        }

        private void ApplyRecommended(bool forceAll)
        {
            if (busy) return;
            List<Pkg> scope = forceAll ? pkgs : Scope();
            int n = 0;
            batchToggling = true;   // 一次性批量勾选，只写一条汇总日志
            try
            {
                foreach (Pkg p in scope)
                {
                    p.Selected = p.Recommended && p.Applicable && !p.Installed;
                    p.Item.Checked = p.Selected;
                    if (p.Selected) n++;
                }
            }
            finally { batchToggling = false; }
            string txt = (forceAll ? "全部" : ScopeName()) + "：已勾选推荐的未安装运行库 " + n + " 项"
                       + (n == 0 ? "（推荐的运行库本机都已安装）" : "");
            SetStatus(txt);
            if (!forceAll) Log("▶ " + txt);
            UpdateTabTitles();
        }

        private void OpenWorkDir()
        {
            try { Process.Start("explorer.exe", "\"" + workDir + "\""); }
            catch (Exception ex) { MessageBox.Show(this, "无法打开目录：\n" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        // ---------- 检测 ----------

        private void RunDetection()
        {
            if (busy) return;
            btnInstall.Enabled = false;
            SetStatus("正在检测本机已安装的运行库…");
            Log("开始检测本机运行库安装状态…");

            Thread t = new Thread(delegate()
            {
                int installed = 0;
                foreach (Pkg p in pkgs)
                {
                    DetectResult r = Detect.Run(p);
                    p.Installed = r.Installed;
                    p.Applicable = r.Applicable;
                    p.StatusText = r.Text;
                    if (r.Installed) installed++;
                    UpdateItem(p);
                }

                int total = pkgs.Count;
                bool first = firstDetection;
                firstDetection = false;

                Ui(delegate
                {
                    if (first) ApplyRecommended(true);
                    btnInstall.Enabled = true;
                    SetStatus("检测完成：共 " + total + " 项，本机已装 " + installed + " 项");
                });

                if (first)
                    Log("检测完成：共 " + total + " 项，本机已装 " + installed
                        + " 项。已默认勾选推荐的未安装项，可点“仅选推荐”恢复默认勾选。");
                else
                    Log("检测完成：共 " + total + " 项，本机已装 " + installed + " 项。（勾选状态保持不变）");
                Log("");
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------- 安装 ----------

        private void StartInstall()
        {
            if (busy) return;

            List<Pkg> selected = new List<Pkg>();
            foreach (Pkg p in pkgs)
                if (p.Selected && p.Applicable) selected.Add(p);

            if (selected.Count == 0)
            {
                MessageBox.Show(this, "请先勾选需要安装的运行库。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            long totalBytes = 0;
            int needDownload = 0;
            foreach (Pkg p in selected)
            {
                if (p.Url.Length == 0) continue;
                totalBytes += p.SizeHint;
                needDownload++;
            }

            string msg = "即将处理 " + selected.Count + " 个运行库。\n\n"
                       + "· 其中 " + needDownload + " 项需要联网下载，首次共约 " + FmtMb(totalBytes) + "\n"
                       + "· 已下载过的安装包会自动复用，不会重复下载\n"
                       + "· 全程静默安装，不会弹出安装向导\n\n"
                       + "是否开始？";

            if (MessageBox.Show(this, msg, "确认安装", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            busy = true;
            cancelRequested = false;
            needReboot = false;
            // 安装期间把选择类按钮真正置灰（此前只是点击被静默忽略，用户误以为"没反应"），
            // 主按钮变成可用的"取消安装"。
            foreach (Button b in toolbarBtns) b.Enabled = false;
            chkAllPages.Enabled = false;
            btnInstall.Text = "取消安装";
            log.Clear();
            bar.Style = ProgressBarStyle.Continuous;
            bar.Value = 0;

            if (!IsElevated())
            {
                // 界面用普通权限运行：另起一个提权的工作进程去安装，
                // 界面本身不被 UIPI 拦截，鼠标键盘始终可用。
                StartElevatedInstall(selected);
                return;
            }

            // 本来就已经是管理员（例如从管理员命令行启动）：直接在本进程内安装。
            Log("当前已是管理员权限，直接在本进程内安装。");
            Log("");
            elevatedInstall = false;
            Thread t = new Thread(delegate() { InstallWorker(selected); });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// 以管理员身份启动一个没有界面的工作进程执行安装，本界面只负责展示进度与取消。
        /// 这样界面永远保持普通权限，任何来源的鼠标键盘输入都不会被 UIPI 丢弃。
        /// </summary>
        private void StartElevatedInstall(List<Pkg> selected)
        {
            string task = Path.Combine(Path.GetTempPath(),
                "86运行库_task_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".txt");
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("workdir=" + workDir);
                sb.Append("index=");
                bool first = true;
                for (int i = 0; i < pkgs.Count; i++)
                {
                    if (!selected.Contains(pkgs[i])) continue;
                    if (!first) sb.Append(",");
                    sb.Append(i);
                    first = false;
                }
                File.WriteAllText(task, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                FinishBusyUi("无法创建安装任务文件：" + ex.Message);
                return;
            }

            workerTaskFile = task;
            workerStatusFile = TaskFile.StatusPath(task);
            workerCancelFile = TaskFile.CancelPath(task);
            workerTailPos = 0;
            workerTailPending = "";
            workerDone = false;
            workerExitTicks = 0;
            try { File.Delete(workerStatusFile); } catch { }
            try { File.Delete(workerCancelFile); } catch { }

            ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath,
                "--worker \"" + task + "\"");
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            // 仅供自动化自测：不提权直接起工作进程（安装动作会因权限不足失败，但可验证回传链路）
            if (Environment.GetEnvironmentVariable("86_RUNPACK_NOELEVATE") == "1") psi.Verb = null;

            try
            {
                workerProc = Process.Start(psi);
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223)
                    FinishBusyUi("已取消管理员授权，安装没有开始。");
                else
                    FinishBusyUi("无法以管理员身份启动安装进程：" + ex.Message);
                return;
            }
            catch (Exception ex)
            {
                FinishBusyUi("无法启动安装进程：" + ex.Message);
                return;
            }

            elevatedInstall = true;
            Log("已请求管理员权限，正在启动安装进程（界面本身不需要管理员权限）。");
            Log("");
            SetStatus("正在启动安装进程…");

            workerTimer = new System.Windows.Forms.Timer();
            workerTimer.Interval = 250;
            workerTimer.Tick += delegate { PumpWorkerLog(); };
            workerTimer.Start();
        }

        /// <summary>把工作进程写进状态文件的新内容读出来，转成界面上的日志/进度/完成状态。</summary>
        private void PumpWorkerLog()
        {
            if (workerDone) return;

            try
            {
                if (!string.IsNullOrEmpty(workerStatusFile) && File.Exists(workerStatusFile))
                {
                    using (FileStream fs = new FileStream(workerStatusFile, FileMode.Open,
                               FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (fs.Length < workerTailPos) workerTailPos = 0;
                        long avail = fs.Length - workerTailPos;
                        if (avail > 0)
                        {
                            fs.Position = workerTailPos;
                            int len = (int)Math.Min(avail, 4 * 1024 * 1024);
                            byte[] buf = new byte[len];
                            int read = 0;
                            while (read < len)
                            {
                                int n = fs.Read(buf, read, len - read);
                                if (n <= 0) break;
                                read += n;
                            }
                            workerTailPos += read;
                            workerTailPending += Encoding.UTF8.GetString(buf, 0, read);

                            int cut;
                            while (!workerDone && (cut = workerTailPending.IndexOf('\n')) >= 0)
                            {
                                string line = workerTailPending.Substring(0, cut).TrimEnd('\r');
                                workerTailPending = workerTailPending.Substring(cut + 1);
                                if (line.Length > 0) HandleWorkerLine(line);
                            }
                        }
                    }
                }
            }
            catch { }

            if (workerDone || workerProc == null) return;

            // 进程已退出但没等到 DONE 行：再等两个节拍（让最后几行落盘），仍没有就按异常结束处理。
            bool exited;
            try { exited = workerProc.HasExited; } catch { exited = true; }
            if (!exited) { workerExitTicks = 0; return; }
            if (++workerExitTicks < 2) return;

            int code = -1;
            try { code = workerProc.ExitCode; } catch { }
            workerDone = true;
            FinishBusyUi("安装进程意外结束（退出代码 " + code + "），请查看上方日志。");
        }

        private void HandleWorkerLine(string line)
        {
            if (line.Length < 2) return;
            string tag = line.Substring(0, 1);
            string body = line.Length > 2 ? line.Substring(2) : "";

            if (tag == "L") { log.AppendText(body + Environment.NewLine); return; }
            if (tag == "S") { lblStatus.Text = body; return; }
            if (tag == "P")
            {
                int v;
                if (int.TryParse(body, out v))
                {
                    if (v < 0) v = 0;
                    if (v > 100) v = 100;
                    bar.Style = ProgressBarStyle.Continuous;
                    bar.Value = v;
                }
                return;
            }
            if (tag == "M") { bar.Style = body == "1" ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous; return; }
            if (tag == "D")
            {
                workerDone = true;
                FinishElevatedInstall(body.Split('\t'));
                return;
            }
            if (tag == "F")
            {
                workerDone = true;
                FinishBusyUi("安装进程出错：" + body);
            }
        }

        private static int ParseInt(string[] fields, int idx)
        {
            if (fields == null || idx >= fields.Length) return 0;
            int v;
            return int.TryParse(fields[idx], out v) ? v : 0;
        }

        /// <summary>提权安装正常结束：恢复界面、按结果重新检测、给出汇总。</summary>
        private void FinishElevatedInstall(string[] fields)
        {
            int ok = ParseInt(fields, 1);
            int fail = ParseInt(fields, 2);
            int skip = ParseInt(fields, 3);
            if (ParseInt(fields, 4) != 0) needReboot = true;

            RestoreAfterInstall();
            SetStatus("结束：成功 " + ok + " 项，失败 " + fail + " 项" + (needReboot ? "，需重启生效" : ""));
            CleanupTaskFiles();

            // 工作进程改的是系统本身，界面里的状态必须以重新检测为准。
            RunDetection();

            string tip = "安装流程已结束。\n\n成功：" + ok + " 项\n失败：" + fail + " 项";
            if (skip > 0) tip += "\n状态未确认：" + skip + " 项";
            if (needReboot) tip += "\n\n部分组件需要重启计算机才能生效，请尽快重启。";
            MessageBoxIcon icon = (fail > 0 || needReboot) ? MessageBoxIcon.Warning : MessageBoxIcon.Information;
            MessageBox.Show(this, tip, "完成", MessageBoxButtons.OK, icon);
        }

        /// <summary>安装被中断 / 无法开始：恢复界面并提示。</summary>
        private void FinishBusyUi(string message)
        {
            RestoreAfterInstall();
            SetStatus(message);
            Log(message);
            CleanupTaskFiles();
            MessageBox.Show(this, message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void RestoreAfterInstall()
        {
            try
            {
                if (workerTimer != null) { workerTimer.Stop(); workerTimer.Dispose(); workerTimer = null; }
            }
            catch { }
            busy = false;
            elevatedInstall = false;
            foreach (Button b in toolbarBtns) b.Enabled = true;
            chkAllPages.Enabled = true;
            btnInstall.Enabled = true;
            btnInstall.Text = "开始安装选中的运行库";
            bar.Style = ProgressBarStyle.Continuous;
            bar.Value = 0;
        }

        private void CleanupTaskFiles()
        {
            try { if (!string.IsNullOrEmpty(workerTaskFile)) File.Delete(workerTaskFile); } catch { }
            try { if (!string.IsNullOrEmpty(workerStatusFile)) File.Delete(workerStatusFile); } catch { }
            try { if (!string.IsNullOrEmpty(workerCancelFile)) File.Delete(workerCancelFile); } catch { }
            try { if (workerProc != null) workerProc.Dispose(); } catch { }
            workerProc = null;
            workerTaskFile = null;
            workerStatusFile = null;
            workerCancelFile = null;
        }

        private void RequestCancel()
        {
            if (!busy) return;
            if (MessageBox.Show(this,
                    "确定要取消安装吗？\n\n已完成的安装项会保留，未开始的任务不再执行。",
                    "取消安装", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            cancelRequested = true;
            if (elevatedInstall)
            {
                try { File.WriteAllText(workerCancelFile, "cancel", new UTF8Encoding(false)); } catch { }
                SetStatus("正在取消…（等待安装进程中断当前步骤）");
                Log("已发送取消请求，安装进程会中断当前步骤。");
                btnInstall.Enabled = false;
                return;
            }
            btnInstall.Enabled = false;
            SetStatus("正在取消…（等待当前步骤中断）");
            Log("用户请求取消，正在中断当前步骤…");
        }

        private void InstallWorker(List<Pkg> selected)
        {
            int ok = 0, fail = 0, skip = 0;

            try
            {
                Log("安装包目录：" + workDir);
                Log("本次共 " + selected.Count + " 个任务。");
                Log("");

                for (int i = 0; i < selected.Count; i++)
                {
                    Pkg p = selected[i];
                    if (cancelRequested) { Log("已被用户取消，停止后续任务。"); break; }

                    SetStatus("(" + (i + 1) + "/" + selected.Count + ") " + p.Name);
                    Log("[" + (i + 1) + "/" + selected.Count + "] " + p.Name);

                    try
                    {
                        InstallOne(p);

                        DetectResult r = Detect.Run(p);
                        p.Installed = r.Installed;
                        p.StatusText = r.Text;
                        UpdateItem(p);

                        if (p.Kind == InstallKind.DllRepair)
                        {
                            // DLL 体检修复自带明细报告，不走"可能需重启"的通用文案
                            if (r.Installed) { ok++; Log("    修复完成：" + r.Text); }
                            else { skip++; Log("    修复结束，仍有缺口：" + r.Text); }
                        }
                        else if (r.Installed) { ok++; Log("    完成，当前状态：" + r.Text); }
                        else { skip++; Log("    注意：安装程序已结束，但状态仍为 " + r.Text + "（可能需重启后生效）"); }
                    }
                    catch (OperationCanceledException)
                    {
                        Log("    已取消。");
                        break;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        p.StatusText = "失败";
                        UpdateItem(p);
                        Log("    失败：" + ex.Message);
                    }
                    Log("");
                }
            }
            finally
            {
                Log("================= 全部结束 =================");
                Log("成功 " + ok + " 项，失败 " + fail + " 项，未确认 " + skip + " 项。");
                if (needReboot) Log("部分组件需要重启计算机后才能生效。");

                if (workerMode)
                {
                    // 工作进程没有界面：把最终结果写进状态文件，由界面进程读取并展示。
                    TaskFile.WriteLine(workerStatusFile,
                        "D\t" + ok + "\t" + fail + "\t" + skip + "\t" + (needReboot ? "1" : "0"));
                }
                else
                {
                    Ui(delegate
                    {
                        busy = false;
                        foreach (Button b in toolbarBtns) b.Enabled = true;
                        chkAllPages.Enabled = true;
                        btnInstall.Enabled = true;
                        btnInstall.Text = "开始安装选中的运行库";
                        bar.Style = ProgressBarStyle.Continuous;
                        bar.Value = 0;
                        SetStatus("结束：成功 " + ok + " 项，失败 " + fail + " 项" + (needReboot ? "，需重启生效" : ""));
                    });

                    string tip = "安装流程已结束。\n\n成功：" + ok + " 项\n失败：" + fail + " 项";
                    if (skip > 0) tip += "\n状态未确认：" + skip + " 项";
                    if (needReboot) tip += "\n\n部分组件需要重启计算机才能生效，请尽快重启。";

                    MessageBoxIcon icon = (fail > 0 || needReboot) ? MessageBoxIcon.Warning : MessageBoxIcon.Information;
                    Ui(delegate { MessageBox.Show(this, tip, "完成", MessageBoxButtons.OK, icon); });
                }
            }
        }

        private void InstallOne(Pkg p)
        {
            // --- 系统运行库 DLL 补齐修复：不下载，直接扫本机并从 WinSxS 复制 ---
            if (p.Kind == InstallKind.DllRepair)
            {
                RepairRuntimeDlls();
                return;
            }

            // --- .NET Framework 3.5：走 DISM 系统功能 ---
            if (p.Kind == InstallKind.Dism)
            {
                InstallNetFx3(p);
                return;
            }

            // --- 准备安装包（本地已有则直接复用，支持离线）---
            string file = Path.Combine(workDir, p.FileName);
            bool cached = File.Exists(file) && new FileInfo(file).Length > 0;

            if (cached)
            {
                Log("    使用已缓存的安装包：" + p.FileName);
            }
            else
            {
                Log("    下载：" + p.Url);
                SetBar(0);
                Util.Download(p.Url, file,
                    delegate(int pct) { SetBar(pct); },
                    delegate { return cancelRequested; });
                Log("    下载完成：" + FmtMb(new FileInfo(file).Length));
            }

            SetMarquee(true);

            // --- 按类型静默安装 ---
            if (p.Kind == InstallKind.DirectX)
            {
                string dxDir = Path.Combine(workDir, "dx");
                if (Directory.Exists(dxDir)) Directory.Delete(dxDir, true);
                Directory.CreateDirectory(dxDir);

                Log("    正在解压 DirectX 安装文件…");
                // 注意：这是 WExtract 自解压包，加 /Q 会抑制实际解包（实测解出 0 个文件），
                // 只传 /T:<dir> 才能正常展开（实测解出 157 个文件）。
                int rc1 = Util.RunExe(file, "/T:\"" + dxDir + "\"", delegate { return cancelRequested; });
                string setup = Path.Combine(dxDir, "DXSETUP.exe");
                if (!File.Exists(setup))
                    throw new Exception("DirectX 自解压失败，未找到 DXSETUP.exe（返回码 " + rc1 + "）");

                Log("    正在静默安装 DirectX 组件…");
                HandleExitCode(p, Util.RunExe(setup, "/silent", delegate { return cancelRequested; }));
            }
            else if (p.Kind == InstallKind.Zip)
            {
                string dir = Path.Combine(workDir, "unzip");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);

                Log("    正在解压安装包…");
                ZipFile.ExtractToDirectory(file, dir);
                string exe = Path.Combine(dir, p.InnerExe);
                if (!File.Exists(exe)) throw new Exception("压缩包内未找到 " + p.InnerExe);

                Log("    正在执行 " + p.InnerExe + " " + p.InnerArgs);
                HandleExitCode(p, Util.RunExe(exe, p.InnerArgs, delegate { return cancelRequested; }));
            }
            else if (p.Kind == InstallKind.Msi)
            {
                Log("    正在通过 msiexec 静默安装…");
                HandleExitCode(p, Util.RunExe("msiexec.exe", "/i \"" + file + "\" " + p.Args,
                    delegate { return cancelRequested; }));
            }
            else
            {
                Log("    正在静默安装…");
                HandleExitCode(p, Util.RunExe(file, p.Args, delegate { return cancelRequested; }));
            }

            SetBar(0);
        }

        /// <summary>
        /// 系统运行库 DLL 全面体检与补齐（思路参照 DirectX 修复工具）：
        ///   1) 逐项体检 System32 / SysWOW64 里清单中的 DLL；
        ///   2) 能在本机找到同架构原版文件的（主要是系统组件 WinSxS），直接复制补齐；
        ///   3) 剩下的按“归属运行库”去重，经用户确认后自动下载并静默安装官方安装包；
        ///   4) 装完再体检一遍，给出分组统计。
        /// 全程只用微软原版文件与官方安装包，不夹带任何第三方 DLL。
        /// </summary>
        private void RepairRuntimeDlls()
        {
            int total = Sys.TotalChecks;
            Log("    体检清单：" + DllCatalog.Items.Count + " 种 DLL（"
                + string.Join("、", DllCatalog.Groups().ToArray()) + "）");
            Log("    体检目录：" + Sys.DirFor(true) + "（64 位）　" + Sys.DirFor(false) + "（32 位）");
            Log("    共 " + total + " 项，开始逐项检查…");

            List<DllIssue> issues = Sys.ScanRuntimeDlls(true);

            if (issues.Count == 0)
            {
                Log("    体检结果：全部正常，系统运行库 DLL 完整，无需修复。");
                return;
            }

            Log("    体检结果：缺失 " + issues.Count + " 项 —— " + Detect.GroupSummary(issues));
            SetMarquee(true);

            // ---------- 第一步：本机就地补齐 ----------
            int copied = 0, copyFail = 0;
            List<DllIssue> left = new List<DllIssue>();
            foreach (DllIssue it in issues)
            {
                if (it.Source == null) { left.Add(it); continue; }
                try
                {
                    File.Copy(it.Source, Path.Combine(Sys.DirFor(it.X64), it.Name), true);
                    copied++;
                    Log("    [" + it.ArchText + "] " + it.Name + " —— 已从本机系统组件补齐");
                }
                catch (Exception ex)
                {
                    copyFail++;
                    left.Add(it);
                    Log("    [" + it.ArchText + "] " + it.Name + " —— 复制失败：" + ex.Message);
                }
            }
            if (copied > 0) Log("    就地补齐 " + copied + " 项。");

            // ---------- 第二步：按归属运行库自动安装 ----------
            int installedPkgs = 0;
            if (left.Count > 0)
            {
                List<string> files = new List<string>();
                List<DllIssue> noOwner = new List<DllIssue>();
                foreach (DllIssue it in left)
                {
                    string f = DllCatalog.OwnerFile(it.Owner, it.X64);
                    if (f == null) { noOwner.Add(it); continue; }
                    if (!files.Contains(f)) files.Add(f);
                }

                if (files.Count > 0)
                {
                    long bytes = 0;
                    StringBuilder names = new StringBuilder();
                    foreach (string f in files)
                    {
                        Pkg owner = FindPkg(f);
                        names.AppendLine("· " + (owner != null ? owner.Name : f)
                                       + (owner != null ? "（约 " + owner.SizeText + "）" : ""));
                        if (owner != null) bytes += owner.SizeHint;
                    }

                    string msg = "有 " + left.Count + " 个 DLL 在本机找不到现成副本，需要安装下列官方运行库才能补齐：\n\n"
                               + names.ToString()
                               + "\n合计约 " + FmtMb(bytes) + "（已下载过的安装包会直接复用）。\n\n"
                               + "是否现在自动下载并静默安装？";

                    if (Confirm(msg, "自动补齐运行库", MessageBoxIcon.Question))
                    {
                        foreach (string f in files)
                        {
                            Pkg owner = FindPkg(f);
                            if (owner == null) continue;
                            Log("    自动安装：" + owner.Name);
                            try { InstallForDllRepair(owner); installedPkgs++; }
                            catch (Exception ex) { Log("    自动安装失败：" + owner.Name + " —— " + ex.Message); }
                        }
                    }
                    else
                    {
                        Log("    已跳过自动安装。可在上方列表里勾选对应的运行库，再点“开始安装选中的运行库”。");
                    }
                }

                if (noOwner.Count > 0)
                {
                    Log("    另有 " + noOwner.Count + " 项没有对应的可安装运行库，只能靠本机其它副本补齐：");
                    foreach (DllIssue it in noOwner)
                        Log("      [" + it.ArchText + "] " + it.Name);
                }
            }

            // ---------- 第三步：复检 ----------
            List<DllIssue> after = Sys.ScanRuntimeDlls(true);
            int fixedCount = issues.Count - after.Count;
            Log("    修复结束：本次补齐 " + fixedCount + " 项（就地复制 " + copied + " 项、"
                + "自动安装运行库 " + installedPkgs + " 个，复制失败 " + copyFail + " 项）。");

            if (after.Count == 0)
                Log("    复检结果：清单内 " + total + " 项 DLL 现已全部正常。");
            else
                Log("    复检结果：仍有 " + after.Count + " 项缺失 —— " + Detect.GroupSummary(after));
                foreach (DllIssue it in after)
                    Log("      [" + it.ArchText + "] " + it.Name
                        + (it.Source == null ? "（本机与官方运行库均不提供此文件）" : "（本机系统组件里有副本，但复制未成功）"));

            SetBar(0);
        }

        /// <summary>
        /// 启用 .NET Framework 3.5（NetFx3）系统功能。
        ///
        /// 这一步在不同机器上差异极大，也正是"有些电脑装不上"的根源：
        ///   · Win7 自带 .NET 2.0/3.0/3.5，本来就是"已启用"；
        ///   · Win8 / Win8.1 / Win10 早期版本的安装介质里带 sources\sxs，可以完全离线启用；
        ///   · Win10 1809 起和 Win11，NetFx3 改成了"按需功能(FoD)"，微软不再随安装介质分发，
        ///     默认只能让 DISM 去 Windows 更新下载 —— 于是任何一条链路被掐断都会失败：
        ///       0x800F0954  公司/学校的 WSUS 组策略不允许本机连 Windows 更新
        ///       0x800F0906  连不上 Windows 更新（断网、代理、Windows Update 服务被禁用）
        ///       0x800F081F  找不到安装源（既没有网络，也没有 sources\sxs）
        ///       0x800F0950 / 0x80073701  组件存储 WinSxS 损坏
        /// 因此这里做成“体检 → 修复 → 重启重试”的闭环：
        ///   第 1 次：本地安装源 → 本地按需功能包 → Windows 更新；
        ///   第 2 次：自动运行内置修复方案（启用更新服务 / 解除 WSUS 拦截 / 重置更新组件）后重试；
        ///   第 3 次：错误码指向组件损坏时，修复系统组件存储后再重试。
        /// </summary>
        private void InstallNetFx3(Pkg p)
        {
            Log("    说明：.NET Framework 3.5 是 Windows 的按需功能，组件只能来自系统安装源或 Windows 更新。");
            LogNetFx3Environment();

            int rc = TryNetFx3(1);

            // 第一次失败 → 自动把内置修复方案跑到这台电脑上，然后重启安装任务重试
            if (!NetFx3Ok(p, rc))
            {
                Log("");
                Log("    第 1 次安装失败（" + Hex(rc) + "）。正在自动运行内置修复方案…");
                RemediateWindowsUpdate();

                Log("");
                Log("    修复完成，重启安装任务（第 2 次尝试）…");
                rc = TryNetFx3(2);
            }

            // 仍失败，且错误码指向系统组件损坏 → 修好组件存储后再重启一次
            if (!NetFx3Ok(p, rc) && IsComponentStoreDamage(rc))
            {
                Log("");
                Log("    第 2 次仍失败（" + Hex(rc) + "）。正在修复系统组件存储…");
                RepairComponentStore();

                Log("");
                Log("    修复完成，重启安装任务（第 3 次尝试）…");
                rc = TryNetFx3(3);
            }

            if (NetFx3Ok(p, rc)) return;
            throw new Exception(NetFx3Hint(rc));
        }

        /// <summary>判断 NetFx3 是否已经可用：退出码成功，或功能检测显示已启用。</summary>
        private bool NetFx3Ok(Pkg p, int rc)
        {
            if (rc == 0) return true;
            if (rc == 3010) { needReboot = true; return true; }
            try { if (Detect.Run(p).Installed) return true; } catch { }
            return false;
        }

        /// <summary>
        /// 一轮完整尝试：本地安装源 → 本地按需功能包 → Windows 更新，返回最后一次 DISM 的退出码。
        /// </summary>
        private int TryNetFx3(int round)
        {
            string dism = SysTool("dism.exe");

            // ---- 路线 1：本地 sources\sxs —— 完全离线，最可靠 ----
            string src = FindNetFx3Source();
            if (src != null)
            {
                Log("    路线 1：找到本机安装源 " + src + "，离线启用（不访问 Windows 更新）。");
                int r = Util.RunExe(dism, "/Online /Enable-Feature /FeatureName:NetFx3 /All /LimitAccess /Source:\""
                    + src + "\" /NoRestart", delegate { return cancelRequested; });
                Log("    路线 1 返回 " + Hex(r) + "。");
                if (r == 0 || r == 3010) return r;
            }
            else if (round == 1)
            {
                Log("    路线 1：本机没有安装源（既无 sources\\sxs，也无 NetFx3 按需功能包）。");
            }

            // ---- 路线 2：本地按需功能包（Win10 1809+ / Win11 离线方案）----
            string cab = FindNetFx3Cab();
            if (cab != null)
            {
                Log("    路线 2：使用本地按需功能包 " + Path.GetFileName(cab) + "。");
                int r = Util.RunExe(dism, "/Online /Add-Package /PackagePath:\"" + cab + "\" /NoRestart",
                    delegate { return cancelRequested; });
                Log("    路线 2 返回 " + Hex(r) + "。");
                if (r == 0 || r == 3010) return r;
            }

            // ---- 路线 3：由 DISM 从 Windows 更新获取 ----
            Log("    路线 3：从 Windows 更新获取并启用（可能需要几分钟）…");
            int rc = Util.RunExe(dism, "/Online /Enable-Feature /FeatureName:NetFx3 /All /NoRestart",
                delegate { return cancelRequested; });
            Log("    路线 3 返回 " + Hex(rc) + "。");
            return rc;
        }

        /// <summary>与 Windows 更新 / 组件安装相关的服务，体检与修复共用同一份清单。</summary>
        private static readonly string[] WUServices = new string[]
        {
            "wuauserv", "bits", "cryptsvc", "trustedinstaller", "msiserver", "dosvc"
        };

        /// <summary>
        /// 内置修复方案：把“更新服务没打开 / 被组策略禁止连微软服务器 / 更新组件损坏”
        /// 这三类最常见的拦路人就地处理掉，随后由调用方重启安装任务重试。
        /// </summary>
        private void RemediateWindowsUpdate()
        {
            // ---- ① 恢复并启动 Windows 更新相关服务 ----
            Log("      ① 检查并启动 Windows 更新相关服务");
            foreach (string s in WUServices)
            {
                if (cancelRequested) throw new OperationCanceledException();
                try
                {
                    if (RestoreServiceStart(s))
                        Log("        " + s + "：原本被禁用，已恢复为“手动”启动");
                    int st = Util.RunExe(SysTool("sc.exe"), "start " + s, delegate { return cancelRequested; });
                    if (st == 0) Log("        " + s + "：已启动");
                    else if (st == 1056) Log("        " + s + "：已在运行");
                    else Log("        " + s + "：启动返回 " + st + "（不致命，重试时会再确认）");
                }
                catch (Exception ex) { Log("        " + s + "：跳过（" + ex.Message + "）"); }
            }

            // ---- ② 解除“不许连 Windows 更新”的组策略 ----
            Log("      ② 解除组策略对 Windows 更新的拦截（内网 WSUS 环境最常见）");
            ClearPolicyBlocks();

            // ---- ③ 重置 Windows 更新组件缓存 ----
            Log("      ③ 重置 Windows 更新组件缓存");
            ResetUpdateComponents();
        }

        /// <summary>服务若被“禁用”，恢复为“手动”（Start: 4 禁用 → 3 手动）。返回是否改动过。</summary>
        private static bool RestoreServiceStart(string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + name, true))
                {
                    if (k == null) return false;
                    object v = k.GetValue("Start");
                    if (v == null || Convert.ToInt32(v) != 4) return false;
                    k.SetValue("Start", 3, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 解除把更新源锁在内网 WSUS、或禁止访问 Windows 更新的组策略。
        /// 这些键正是 0x800F0954（“策略不允许连 Windows 更新”）的直接原因。
        /// </summary>
        private void ClearPolicyBlocks()
        {
            SetPolicyDword(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "UseWUServer", 0);
            SetPolicyDword(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
                "DoNotConnectToWindowsUpdateInternetLocations", 0);
            SetPolicyDword(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "SetDisableUXWUAccess", 0);
            SetPolicyDword(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\WindowsUpdate",
                "DisableWindowsUpdateAccess", 0);
        }

        private void SetPolicyDword(string sub, string name, int value)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(sub))
                {
                    if (k == null) return;
                    object old = k.GetValue(name);
                    if (old == null || Convert.ToInt32(old) == value) return;
                    k.SetValue(name, value, RegistryValueKind.DWord);
                    Log("        已解除拦截：" + name + "（" + old + " → " + value + "）");
                }
            }
            catch (Exception ex) { Log("        跳过 " + name + "（" + ex.Message + "）"); }
        }

        /// <summary>重置更新组件缓存：停服务 → 把 SoftwareDistribution / catroot2 改名备份 → 再启动。</summary>
        private void ResetUpdateComponents()
        {
            string[] svcs = new string[] { "wuauserv", "bits", "cryptsvc", "msiserver" };
            // /y：有依赖服务时自动确认，避免 net stop 卡在交互提示上把整个流程挂死
            foreach (string s in svcs)
            {
                if (cancelRequested) throw new OperationCanceledException();
                try { Util.RunExe(SysTool("net.exe"), "stop " + s + " /y", delegate { return cancelRequested; }); } catch (OperationCanceledException) { throw; } catch { }
            }

            RenameAsBackup(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "SoftwareDistribution"));
            RenameAsBackup(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "catroot2"));

            foreach (string s in svcs)
            {
                if (cancelRequested) throw new OperationCanceledException();
                try { Util.RunExe(SysTool("sc.exe"), "start " + s, delegate { return cancelRequested; }); } catch (OperationCanceledException) { throw; } catch { }
            }
            Log("        Windows 更新组件缓存已重建（原目录改名备份，未删除）");
        }

        /// <summary>把目录改名为 .bak（可随时改回），失败只记录、不中断流程。</summary>
        private void RenameAsBackup(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                string bak = dir + ".bak";
                if (Directory.Exists(bak)) Directory.Delete(bak, true);
                Directory.Move(dir, bak);
                Log("        已重置：" + dir);
            }
            catch (Exception ex)
            {
                Log("        重置 " + Path.GetFileName(dir) + " 未成功（可能正被占用，不影响继续重试）：" + ex.Message);
            }
        }

        /// <summary>该错误码是否指向系统组件存储（WinSxS）损坏，值得做一次 RestoreHealth。</summary>
        private static bool IsComponentStoreDamage(int rc)
        {
            uint u = (uint)rc;
            return u == 0x800F0950 || u == 0x80073701 || u == 0x800F0922 || u == 0x800F0906;
        }

        /// <summary>修复系统组件存储（0x800F0950 / 0x80073701 等组件损坏错误的根治手段）。</summary>
        private void RepairComponentStore()
        {
            Log("      正在修复系统组件存储（可能需要几分钟，请耐心等待）…");
            int r1 = Util.RunExe(SysTool("dism.exe"), "/Online /Cleanup-Image /RestoreHealth",
                delegate { return cancelRequested; });
            Log("      RestoreHealth 返回 " + Hex(r1) + "。");
            Log("      正在校验系统文件（sfc /scannow，同样可能需要几分钟）…");
            int r2 = Util.RunExe(SysTool("sfc.exe"), "/scannow", delegate { return cancelRequested; });
            Log("      sfc 返回 " + Hex(r2) + "。");
        }

        /// <summary>
        /// 安装前的环境体检：把“更新服务被禁用 / 组策略锁死更新源”直接写进日志，
        /// 这样即便这台电脑第一次就安装失败，用户也能从日志看出原因。
        /// </summary>
        private void LogNetFx3Environment()
        {
            List<string> issues = new List<string>();

            foreach (string s in WUServices)
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Services\" + s))
                    {
                        if (k == null) { issues.Add(s + " 未安装"); continue; }
                        object v = k.GetValue("Start");
                        if (v != null && Convert.ToInt32(v) == 4) issues.Add(s + " 被禁用");
                    }
                }
                catch { }
            }

            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("UseWUServer");
                        if (v != null && Convert.ToInt32(v) == 1) issues.Add("组策略把更新源锁在内网 WSUS");
                    }
                }
            }
            catch { }

            if (issues.Count == 0)
                Log("    体检：Windows 更新相关服务与策略均正常，可直接尝试在线启用。");
            else
                Log("    体检：发现可能阻碍安装的问题 —— " + string.Join("；", issues.ToArray())
                    + "（若首次安装失败，会自动修复后重试）。");
        }

        /// <summary>System32 下的系统工具完整路径。</summary>
        private static string SysTool(string name)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
        }

        /// <summary>把退出码写成 0xXXXXXXXX。</summary>
        private static string Hex(int rc)
        {
            return "0x" + ((uint)rc).ToString("X8");
        }

        /// <summary>
        /// 在常见位置找 Windows 安装介质里的 sources\sxs，且必须确实含 NetFx3 包才算数。
        /// 覆盖：本程序同级目录的 sxs / sources\sxs、各固定磁盘根目录、已挂载的光驱或 ISO。
        /// </summary>
        private string FindNetFx3Source()
        {
            List<string> cand = new List<string>();
            cand.Add(Path.Combine(workDir, "sxs"));
            cand.Add(Path.Combine(workDir, "sources", "sxs"));

            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.CDRom) continue;
                    if (!d.IsReady) continue;
                    cand.Add(Path.Combine(d.RootDirectory.FullName, "sources", "sxs"));
                    cand.Add(Path.Combine(d.RootDirectory.FullName, "sxs"));
                }
            }
            catch { }

            foreach (string dir in cand)
            {
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFiles(dir, "*netfx3*").Length > 0)
                        return dir;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 在本程序同级目录找 NetFx3 的按需功能包，
        /// 例如 Microsoft-Windows-NetFx3-OnDemand-Package~31bf3856ad364e35~amd64~~.cab（VLSC 的 FoD 介质里）。
        /// </summary>
        private string FindNetFx3Cab()
        {
            try
            {
                string[] hits = Directory.GetFiles(workDir, "*netfx3*.cab");
                if (hits.Length > 0) return hits[0];
            }
            catch { }
            return null;
        }

        /// <summary>把 DISM 启用 NetFx3 的返回码翻译成可照着做的中文建议。</summary>
        private static string NetFx3Hint(int rc)
        {
            string hex = "0x" + ((uint)rc).ToString("X8");
            string head = ".NET Framework 3.5 启用失败（" + hex + "）。";

            switch ((uint)rc)
            {
                case 0x800F0954:
                    return head + "这台电脑的策略不允许连 Windows 更新（常见于公司/学校统一管理的 WSUS 环境），"
                         + "而 NetFx3 的安装源又不在本机。\n"
                         + "解决办法（任选其一）：\n"
                         + "  1) 把 Windows 安装介质的 sources\\sxs 整个文件夹复制到本程序同级目录下的 sxs 文件夹，再重试；\n"
                         + "     注意 Win10 1809 之后的 ISO 已不带该文件夹，需要微软 VLSC 的“按需功能(FoD)”介质；\n"
                         + "  2) 交给 IT 管理员：组策略“计算机配置 → 管理模板 → 系统 → 指定可选组件安装和组件修复的设置”"
                         + "改为“已启用”并勾选“直接与 Windows 更新联系”；\n"
                         + "  3) 手动挂载安装介质后执行：\n"
                         + "     dism /Online /Enable-Feature /FeatureName:NetFx3 /All /LimitAccess /Source:X:\\sources\\sxs";

                case 0x800F0906:
                    return head + "本机下载不到安装源。请检查网络/代理是否可用、"
                         + "Windows Update 服务是否被禁用，或改用本地安装源（见下方说明）。\n"
                         + "解决办法：把 Windows 安装介质的 sources\\sxs 复制到本程序同级目录下的 sxs 文件夹后重试；"
                         + "Win10 1809+ / Win11 请使用 VLSC 按需功能(FoD)介质里的 NetFx3 cab。";

                case 0x800F081F:
                    return head + "找不到安装源文件。\n"
                         + "解决办法：把 Windows 安装介质里 sources\\sxs 复制到本程序同级目录下的 sxs 文件夹后重试。";

                case 0x800F0950:
                case 0x80073701:
                case 0x800F0922:
                    return head + "系统组件存储（WinSxS）可能已损坏。\n"
                         + "请先以管理员身份执行下面两条命令，再回来重试：\n"
                         + "  dism /Online /Cleanup-Image /RestoreHealth\n"
                         + "  sfc /scannow";

                case 0x800F0907:
                    return head + "被安全软件或系统策略拦截。请临时关闭第三方安全软件/系统加固工具后重试。";
            }

            return head + "若为 0x800F0906 / 0x800F081F，说明系统缺少安装源；"
                 + "请联网，或把安装介质的 sources\\sxs 放到本程序同级目录后重试。";
        }

        /// <summary>按离线包文件名找到清单里的运行库条目。</summary>
        private Pkg FindPkg(string fileName)
        {
            foreach (Pkg p in pkgs)
                if (string.Equals(p.FileName, fileName, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>
        /// 为"补齐 DLL"安装归属运行库：运行库若已在本机，重复安装不会补回被删掉的 DLL，
        /// 必须走 /repair 让 Windows Installer 还原缺失文件；没装过才走正常安装。
        /// </summary>
        private void InstallForDllRepair(Pkg p)
        {
            bool already = false;
            try { already = Detect.Run(p).Installed; } catch { }

            if (already && p.RepairArgs != null)
            {
                string file = Path.Combine(workDir, p.FileName);
                if (!File.Exists(file) || new FileInfo(file).Length == 0)
                {
                    Log("    下载（供修复使用）：" + p.Url);
                    SetBar(0);
                    Util.Download(p.Url, file,
                        delegate(int pct) { SetBar(pct); },
                        delegate { return cancelRequested; });
                }
                Log("    运行库已在本机，改用修复模式还原缺失文件：" + p.RepairArgs
                    + "（注意：安装包只会还原它自己带有的文件，包内没有的文件仍旧不会出现）");
                SetMarquee(true);
                int rc = Util.RunExe(file, p.RepairArgs, delegate { return cancelRequested; });
                Log("    修复模式返回码 " + rc + "。");
                HandleExitCode(p, rc);
                return;
            }

            InstallOne(p);
        }

        private void HandleExitCode(Pkg p, int rc)
        {
            switch (rc)
            {
                case 0:
                    return;
                case 3010:  // ERROR_SUCCESS_REBOOT_REQUIRED
                case 1641:  // ERROR_SUCCESS_REBOOT_INITIATED
                    needReboot = true;
                    Log("    安装完成，返回码 " + rc + "：需要重启计算机后生效。");
                    return;
                case 1638:  // 已安装更高版本
                    Log("    系统中已存在相同或更高版本，跳过。");
                    return;
                case 1605:  // 产品未安装
                    Log("    系统报告该产品未安装（返回码 1605）。");
                    return;
            }
            throw new Exception("安装程序返回错误代码 " + rc + "（0x" + ((uint)rc).ToString("X8") + "）");
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!busy) return;
            if (MessageBox.Show(this, "正在安装中，确定要退出吗？", "确认退出",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            cancelRequested = true;

            if (elevatedInstall)
            {
                // 界面退出后没人再读状态文件了，直接通知并结束提权进程树。
                try { File.WriteAllText(workerCancelFile, "cancel", new UTF8Encoding(false)); } catch { }
                try
                {
                    if (workerProc != null && !workerProc.HasExited)
                    {
                        ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe",
                            "/PID " + workerProc.Id + " /T /F");
                        psi.UseShellExecute = false;
                        psi.CreateNoWindow = true;
                        Process.Start(psi);
                    }
                }
                catch { }
            }
        }
    }

    // ======================= 程序入口 =======================

    /// <summary>
    /// 任务文件 / 状态文件读写（界面进程 ↔ 提权工作进程之间唯一的通信通道）。
    /// 任务文件由界面写、工作进程读；状态文件由工作进程追加、界面轮询读取。
    /// </summary>
    internal static class TaskFile
    {
        private static readonly object gate = new object();

        public static string StatusPath(string task) { return task + ".status"; }
        public static string CancelPath(string task) { return task + ".cancel"; }

        /// <summary>追加一行。文件被界面删除或正在读取时都不能影响安装，所以异常一律吞掉。</summary>
        public static void WriteLine(string path, string line)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                lock (gate)
                {
                    using (FileStream fs = new FileStream(path, FileMode.Append,
                               FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamWriter w = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        w.WriteLine(line);
                        w.Flush();
                    }
                }
            }
            catch { }
        }

        /// <summary>读取任务文件里 "key=value" 形式的某一项。</summary>
        public static string ReadValue(string taskFile, string key)
        {
            try
            {
                foreach (string raw in File.ReadAllLines(taskFile, Encoding.UTF8))
                {
                    string s = raw.Trim();
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    if (string.Equals(s.Substring(0, eq), key, StringComparison.OrdinalIgnoreCase))
                        return s.Substring(eq + 1);
                }
            }
            catch { }
            return null;
        }

        /// <summary>读取任务文件里的条目序号列表。</summary>
        public static List<int> ReadIndices(string taskFile)
        {
            List<int> r = new List<int>();
            string v = ReadValue(taskFile, "index");
            if (string.IsNullOrEmpty(v)) return r;
            foreach (string part in v.Split(','))
            {
                int i;
                if (int.TryParse(part.Trim(), out i)) r.Add(i);
            }
            return r;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 自检模式：不显示界面，仅输出检测结果，便于部署前核对
            if (args.Length > 0 && string.Equals(args[0], "/selftest", StringComparison.OrdinalIgnoreCase))
            {
                string outFile = args.Length > 1
                    ? args[1]
                    : Path.Combine(Path.GetTempPath(), "86运行库_selftest.txt");
                SelfTest(outFile);
                return;
            }

            // 工作进程模式：由界面进程用 runas 以管理员身份拉起，没有界面，只执行安装。
            if (args.Length >= 2 && string.Equals(args[0], "--worker", StringComparison.OrdinalIgnoreCase))
            {
                string taskFile = args[1];
                int rc = 0;
                try
                {
                    Util.WorkDir = Util.ResolveWorkDir();
                    MainForm w = new MainForm(taskFile);
                    rc = w.RunHeadless();
                }
                catch (Exception ex)
                {
                    try
                    {
                        TaskFile.WriteLine(TaskFile.StatusPath(taskFile),
                            "F\t" + (ex == null ? "未知错误" : ex.Message));
                    }
                    catch { }
                    rc = 1;
                }
                Environment.Exit(rc);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 兜底：界面线程上的任何未捕获异常都转成中文提示 + 错误报告文件，
            // 而不是弹出 .NET 那个看不懂的 JIT 崩溃对话框。
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                ReportCrash(e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                ReportCrash(e.ExceptionObject as Exception);
            };

            try
            {
                Util.WorkDir = Util.ResolveWorkDir();
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                ReportCrash(ex);
            }
        }

        /// <summary>把异常写成一份可读的报告文件并提示用户，避免直接崩掉。</summary>
        private static void ReportCrash(Exception ex)
        {
            string text = ex == null ? "未知错误" : ex.ToString();
            try
            {
                string file = Path.Combine(Path.GetTempPath(), "86运行库_错误报告.txt");
                File.WriteAllText(file, "86运行库 错误报告 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + Environment.NewLine + Environment.NewLine + text, Encoding.UTF8);
                MessageBox.Show("程序遇到未预期的错误，详情已记录到：\n" + file + "\n\n"
                    + (ex == null ? "" : ex.Message), "86运行库", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                MessageBox.Show(text, "86运行库", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void SelfTest(string outFile)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("86 运行库 — 自检报告");
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("系统：" + Environment.OSVersion.VersionString
                          + "　" + (Environment.Is64BitOperatingSystem ? "64 位" : "32 位"));
            sb.AppendLine("安装包目录：" + Util.ResolveWorkDir());
            sb.AppendLine();

            List<Pkg> pkgs = Catalog.Build();
            sb.AppendLine("清单共 " + pkgs.Count + " 项：");
            sb.AppendLine(new string('-', 100));

            string lastCat = null;
            int installed = 0, missing = 0, na = 0;
            foreach (Pkg p in pkgs)
            {
                if (p.Category != lastCat)
                {
                    lastCat = p.Category;
                    sb.AppendLine();
                    sb.AppendLine("【" + lastCat + "】");
                }

                DetectResult r = Detect.Run(p);
                if (!r.Applicable) na++;
                else if (r.Installed) installed++;
                else missing++;

                sb.AppendLine(string.Format("  {0,-40} {1,-24} {2,-22} {3}",
                    p.Name, p.Note, r.Text, r.Applicable ? "" : "[不适用]"));
            }

            sb.AppendLine();
            sb.AppendLine(new string('-', 100));
            sb.AppendLine("已安装 " + installed + " 项，未安装 " + missing + " 项，不适用 " + na + " 项。");

            // 运行库 DLL 体检明细：逐项列出缺失的 DLL 及其可否就地补齐
            List<DllIssue> issues = Sys.ScanRuntimeDlls(true);
            sb.AppendLine();
            sb.AppendLine("【系统运行库 DLL 体检】清单 " + DllCatalog.Items.Count + " 种，共 "
                          + Sys.TotalChecks + " 项，缺失 " + issues.Count + " 项。");
            foreach (DllIssue it in issues)
            {
                sb.AppendLine("  缺 [" + it.ArchText + "] " + it.Name + "　[" + it.Group + "]　"
                              + (it.Source != null
                                 ? "可从本机系统组件就地补齐"
                                 : "需安装归属运行库：" + (it.Owner != null ? it.Owner : "无（只能靠其它副本）")));
            }

            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        }
    }
}