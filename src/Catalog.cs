// ---------------------------------------------------------------------------
//  Part of the 86 RuntimePack sources: runtime catalogs (Catalog, DllCatalog)
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
                "xnafx31_redist.msi", InstallKind.Msi, "/qn /norestart", "xna", "3.1", 7672320, false, false,
                "409173B3A4F4EE6FF4E6910EECE26F95AF8073EFAE2C9FA4C0F8DAD24F7774DE"));

            list.Add(new Pkg(CAT_GAME, "XNA Framework 4.0 Refresh", "老游戏常用（星露谷等）",
                DM + "5/3/A/53A804C8-EC78-43CD-A0F0-2FB4D45603D3/xnafx40_redist.msi",
                "xnafx40_redist.msi", InstallKind.Msi, "/qn /norestart", "xna", "4.0", 7059456, false, false,
                "2B03C130C4BB5F106B9619BEA8150201F7E982709CFE9A0CC8BDE75FF0A83B27"));

            list.Add(new Pkg(CAT_GAME, "Visual J# 2.0 运行库", "J# 编写的老程序 / 老游戏",
                DM + "9/a/0/9a01eb1e-fe80-41af-a3f8-ea41220918f7/vjredist.exe",
                "vjredist.exe", E, "/q", "vjsharp", "", 3798912, false, false,
                "602587003735BE073198B9D2A0A0BD0FCACC0FFC38601055E941FC926E38C27D"));

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

}
