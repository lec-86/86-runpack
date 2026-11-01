// ---------------------------------------------------------------------------
//  Part of the 86 RuntimePack sources: main window (MainForm)
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
        private Button btnInstall, btnAll, btnNone, btnInvert, btnMissing, btnRecommended, btnRescan, btnOpenDir, btnExportLog;
        /// <summary>安装期间要临时禁用的工具栏按钮（防止用户误以为"点了没反应"）。</summary>
        private Button[] toolbarBtns;
        /// <summary>批量勾选（全选/反选等）进行中：此时不逐行写点击日志，避免刷屏。</summary>
        private bool batchToggling;

        private List<Pkg> pkgs = new List<Pkg>();
        private string workDir;

        private volatile bool busy;
        private volatile bool cancelFlag;
        private volatile bool detecting;              // 检测进行中：防止 F5 / 连点按钮重入
        private Thread installThread;                 // 进程内安装线程（关窗时等它真正结束）
        private volatile bool installThreadFinished;
        private int overallBase;                      // 总进度用：已完成任务数
        private int overallCount;                     // 总进度用：任务总数
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
            btnExportLog = MakeButton("导出日志(&E)", 106, delegate { ExportLog(); });

            toolbarBtns = new Button[] { btnAll, btnNone, btnInvert, btnMissing, btnRecommended, btnRescan };

            toolbar.Controls.Add(btnAll);
            toolbar.Controls.Add(btnNone);
            toolbar.Controls.Add(btnInvert);
            toolbar.Controls.Add(btnMissing);
            toolbar.Controls.Add(btnRecommended);
            toolbar.Controls.Add(chkAllPages);
            toolbar.Controls.Add(btnRescan);
            toolbar.Controls.Add(btnOpenDir);
            toolbar.Controls.Add(btnExportLog);

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
                AppendLogText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine);
            });
        }

        /// <summary>日志行数上限：超过后批量截掉最旧的行，避免长时间安装把文本框越拖越重。</summary>
        private const int MaxLogLines = 2000;

        private void AppendLogText(string text)
        {
            log.AppendText(text);
            int lines = log.GetLineFromCharIndex(log.TextLength);
            if (lines <= MaxLogLines) return;
            int cut = lines - (MaxLogLines - 300);
            int idx = log.GetFirstCharIndexFromLine(cut);
            if (idx > 0) log.Text = log.Text.Substring(idx);
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

        private void ExportLog()
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "导出日志";
                dlg.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                dlg.FileName = "86运行库_日志_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, log.Text, new UTF8Encoding(true));
                    Log("日志已导出：" + dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出失败：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        // ---------- 检测 ----------

        private void RunDetection()
        {
            if (busy || detecting) return;
            detecting = true;
            btnInstall.Enabled = false;
            btnRescan.Enabled = false;
            SetStatus("正在检测本机已安装的运行库…");
            Log("开始检测本机运行库安装状态…");

            Thread t = new Thread(delegate()
            {
                try
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
                }
                finally
                {
                    detecting = false;
                    Ui(delegate { btnRescan.Enabled = true; });
                }
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
            installThreadFinished = false;
            Thread t = new Thread(delegate()
            {
                try { InstallWorker(selected); }
                finally { installThreadFinished = true; }
            });
            installThread = t;
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

            if (tag == "L") { AppendLogText(body + Environment.NewLine); return; }
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

                overallCount = selected.Count;
                for (int i = 0; i < selected.Count; i++)
                {
                    Pkg p = selected[i];
                    if (cancelRequested) { Log("已被用户取消，停止后续任务。"); break; }
                    overallBase = i;

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

            // --- 准备安装包（本地已有则校验后复用，支持离线）---
            string file = Path.Combine(workDir, p.FileName);
            EnsureCached(p, file);

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
                string msiLog = "";
                try
                {
                    string logDir = Path.Combine(workDir, "logs");
                    Directory.CreateDirectory(logDir);
                    msiLog = Path.Combine(logDir, Path.GetFileNameWithoutExtension(p.FileName) + ".msi.log");
                }
                catch { }
                string msiArgs = "/i \"" + file + "\" " + p.Args;
                if (msiLog.Length > 0) msiArgs += " /l*v \"" + msiLog + "\"";
                try
                {
                    HandleExitCode(p, Util.RunExe("msiexec.exe", msiArgs,
                        delegate { return cancelRequested; }));
                }
                catch
                {
                    if (msiLog.Length > 0) Log("    msiexec 详细日志：" + msiLog);
                    throw;
                }
            }
            else
            {
                Log("    正在静默安装…");
                HandleExitCode(p, Util.RunExe(file, p.Args, delegate { return cancelRequested; }));
            }
        }

        /// <summary>
        /// 确保安装包就绪：本地缓存先校验 SHA-256（清单未给哈希的包跳过校验），
        /// 缺失或不符则下载；下载完成后再校验一次，仍不符就删除重下一次。
        /// </summary>
        private void EnsureCached(Pkg p, string file)
        {
            bool cached = File.Exists(file) && new FileInfo(file).Length > 0;
            if (cached && p.Sha256.Length > 0 && !Util.VerifySha256(file, p.Sha256))
            {
                Log("    缓存包 SHA-256 校验不符，将重新下载：" + p.FileName);
                try { File.Delete(file); } catch { }
                cached = false;
            }
            if (cached)
            {
                Log("    使用已缓存的安装包：" + p.FileName);
                return;
            }

            Log("    下载：" + p.Url);
            ReportItemProgress(0);
            Util.Download(p.Url, file,
                delegate(int pct) { ReportItemProgress(pct); },
                delegate { return cancelRequested; });

            if (p.Sha256.Length > 0 && !Util.VerifySha256(file, p.Sha256))
            {
                Log("    下载包 SHA-256 校验不符，删除后重新下载一次…");
                try { File.Delete(file); } catch { }
                ReportItemProgress(0);
                Util.Download(p.Url, file,
                    delegate(int pct) { ReportItemProgress(pct); },
                    delegate { return cancelRequested; });
                if (!Util.VerifySha256(file, p.Sha256))
                {
                    try { File.Delete(file); } catch { }
                    throw new Exception("安装包 SHA-256 校验失败：" + p.FileName);
                }
            }
            Log("    下载完成：" + FmtMb(new FileInfo(file).Length));
        }

        /// <summary>把当前项的下载百分比换算成总进度上报：(已完成项 + 当前项进度) / 总项数。</summary>
        private void ReportItemProgress(int pct)
        {
            if (overallCount <= 0) { SetBar(pct); return; }
            SetBar((int)((overallBase * 100.0 + pct) / overallCount));
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
            {
                Log("    复检结果：清单内 " + total + " 项 DLL 现已全部正常。");
            }
            else
            {
                Log("    复检结果：仍有 " + after.Count + " 项缺失 —— " + Detect.GroupSummary(after));
                foreach (DllIssue it in after)
                    Log("      [" + it.ArchText + "] " + it.Name
                        + (it.Source == null ? "（本机与官方运行库均不提供此文件）" : "（本机系统组件里有副本，但复制未成功）"));
            }
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

        /// <summary>把目录改名为带时间戳的 .bak_（旧备份绝不删除，可随时改回），失败只记录、不中断流程。</summary>
        private void RenameAsBackup(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                string bak = dir + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                Directory.Move(dir, bak);
                Log("        已重置：" + dir + "（原目录改名为 " + Path.GetFileName(bak) + "，未删除）");
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
                EnsureCached(p, file);
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
                case 1618:  // ERROR_INSTALL_ALREADY_RUNNING
                    throw new Exception("另一个安装正在进行（错误代码 1618），请等它结束后重试本项。");
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

            if (!elevatedInstall)
            {
                // 进程内安装：直接退出会腰斩 msiexec 子进程，留下装了一半的运行库。
                // 先请求取消，再等安装线程真正结束（最多 30 秒）才放行关闭。
                int waited = 0;
                while (!installThreadFinished && waited < 30000)
                {
                    Thread.Sleep(100);
                    waited += 100;
                }
                if (!installThreadFinished)
                    Log("等待安装线程结束超时，仍将退出（当前安装步骤可能被打断）。");
                return;
            }

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

}
