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

// ---------------------------------------------------------------------------
//  Part of the 86 RuntimePack sources: program entry (Program)
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
    internal static class Program
    {
        /// <summary>单实例互斥体（进程存活期间必须持有引用，防止被 GC 回收句柄）。</summary>
        private static Mutex singleInstanceMutex;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

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

            // 单实例：已有窗口开着就把对方前置，然后退出本实例。
            // （/selftest 与 --worker 分支在上面已经返回，不受限制。互斥体名用纯 ASCII。）
            bool createdNew;
            singleInstanceMutex = new Mutex(true, "86RunPack_SingleInstance", out createdNew);
            if (!createdNew)
            {
                try
                {
                    IntPtr h = FindWindow(null, "86运行库");
                    if (h != IntPtr.Zero)
                    {
                        if (IsIconic(h)) ShowWindow(h, 9);   // SW_RESTORE
                        SetForegroundWindow(h);
                    }
                }
                catch { }
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
