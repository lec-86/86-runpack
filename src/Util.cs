// ---------------------------------------------------------------------------
//  Part of the 86 RuntimePack sources: download & process helpers (Util)
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
                // 新运行时上再尝试加上 TLS 1.3（0x3000）；不支持则静默跳过
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; }
                catch { }
            }
            catch { }

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            req.AllowAutoRedirect = true;
            req.Timeout = 60000;
            req.ReadWriteTimeout = 180000;

            string part = dest + ".part";
            long existing = 0;
            try { if (File.Exists(part)) existing = new FileInfo(part).Length; } catch { }
            if (existing > 0) req.AddRange(existing);   // 断点续传：只要剩余部分

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    long total;
                    long got;
                    FileMode mode;
                    if (existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent)
                    {
                        // 206：服务器支持 Range，把剩余字节追加到已有 .part 之后
                        total = existing + resp.ContentLength;
                        got = existing;
                        mode = FileMode.Append;
                    }
                    else
                    {
                        // 200：服务器不支持 Range（或没有半成品），回退全量重下
                        total = resp.ContentLength;
                        got = 0;
                        mode = FileMode.Create;
                    }

                    using (Stream rs = resp.GetResponseStream())
                    using (FileStream fs = new FileStream(part, mode, FileAccess.Write, FileShare.None, 65536))
                    {
                        byte[] buf = new byte[65536];
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
            catch (WebException wex)
            {
                // 半成品已等于完整文件（416）时续传没有意义，删掉让下一次全量重下；
                // 其余失败保留 .part，供重试或下次启动继续续传（下载完成后有 SHA-256 兜底校验）。
                try
                {
                    HttpWebResponse er = wex.Response as HttpWebResponse;
                    if (er != null && er.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                        File.Delete(part);
                }
                catch { }
                throw;
            }
        }

        /// <summary>计算文件 SHA-256（小写十六进制）；失败返回空串。</summary>
        public static string Sha256File(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder(h.Length * 2);
                    for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return ""; }
        }

        /// <summary>校验文件 SHA-256（大小写不敏感）；期望值为空时视为通过（跳过校验）。</summary>
        public static bool VerifySha256(string path, string expectedHex)
        {
            if (string.IsNullOrEmpty(expectedHex)) return true;
            return string.Equals(Sha256File(path), expectedHex, StringComparison.OrdinalIgnoreCase);
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

}
