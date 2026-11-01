// ---------------------------------------------------------------------------
//  Part of the 86 RuntimePack sources: task/status file channel (TaskFile)
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

}
