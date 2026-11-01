// ---------------------------------------------------------------------------
//  Part of the 86 RuntimePack sources: data model (InstallKind, Pkg)
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
        public string Sha256 = "";   // 安装包 SHA-256（十六进制，大小写不敏感）；为空则跳过校验

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
                   long sizeHint, bool recommended, bool x64Only, string sha256 = null)
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
            Sha256 = sha256 == null ? "" : sha256;
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

}
