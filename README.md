# 86运行库

Windows 10 / 11 一键运行库合集。单个 exe、零依赖、免安装，双击即用：自动检测本机已装的各类运行库，勾选缺失项后一键下载安装；同时附带系统 DLL 体检与补齐修复。

## 功能

- **Microsoft Visual C++ 运行库**：VC++ 2005 / 2008 / 2010 / 2012 / 2013 / 2015-2026（v14）全系列 x86/x64，自动识别已装版本。
- **.NET 运行库**：.NET Framework 3.5（系统可选功能，含 Windows Update 环境自动修复与重试）、4.8 检测安装，.NET Desktop Runtime 6.0 ~ 10.0、ASP.NET Core Runtime 8.0 / 9.0。
- **游戏 & 经典运行库**：XNA Framework 3.1 / 4.0 Refresh、Visual J# 2.0 等老游戏依赖。
- **其他常用运行库**：DirectX 9.0c（Jun2010）、Edge WebView2 Runtime、Eclipse Temurin JRE 8 / 21、OpenAL、Vulkan Runtime、VSTO 4.0。
- **系统组件修复（DLL 补齐）**：116 种常见缺失运行库 DLL（DirectX / VC++ / MFC / 系统组件，含 92 个 DirectX DLL）× 双架构共 231 项体检；优先从本机 WinSxS 复制（带 PE 架构校验），本机没有的自动下载官方运行库以 `/repair` 模式补齐。
- **下载与缓存**：安装包首次联网下载到 exe 同级 `packages/` 目录，之后可在其他机器离线复用；支持 HTTP 断点续传，已缓存包按 SHA-256 校验，损坏自动重下。
- **分页勾选界面**：每页实时显示"已选 x/y"，支持全选/反选/仅选未安装/仅选推荐/作用于所有页；安装期间可取消，多条目安装显示总进度。
- 其它：单实例运行（重复启动自动聚焦已有窗口）、日志超限自动截断、一键导出日志、MSI 安装全程留 `/l*v` 详细日志、Per-Monitor V2 高 DPI 适配。

## 使用

1. 下载 Releases 中的 `86运行库.exe`（或自行编译，见下）。
2. 双击运行——**界面以普通权限启动，不会弹 UAC**。
3. 等待自动检测完成，勾选需要的运行库，点击"开始安装选中的运行库"。
4. 此时才会弹出一次 UAC 授权（安装动作由另起的提权工作进程执行，见"权限设计"）。

## 编译

无需安装任何 SDK：使用 Windows 自带的 .NET Framework C# 编译器（`csc.exe`）。

```
双击 build.cmd
```

流程：`csc` 编译 `src\*.cs` → `src\setver.ps1` 注入版本信息资源 → 按 `src\appname.txt`（UTF-8）重命名产物。`build.cmd` 保持纯 ASCII 以避免代码页乱码问题；若仓库根同级的 `github\` 目录存在，编译成功后会自动把 `src\` 同步过去。

要求：Windows 10 / 11，系统自带 .NET Framework 4.x（默认都有）。

## 权限设计

主程序清单为 `asInvoker`：界面永远运行在普通完整性级别。这样任何由低完整性来源（远程桌面、虚拟机控制台、输入注入类工具）送来的鼠标键盘输入都不会被 Windows UIPI 静默丢弃——这正是很多"提权窗口按钮点了没反应"问题的根源。

真正需要管理员权限的安装动作，由界面在点击"开始安装"时通过 `runas` 拉起一个**无界面的 `--worker` 子进程**执行；日志、进度、取消、结果通过临时状态文件回传给界面展示。若进程本身已具备管理员权限（如从管理员命令行启动），则直接在当前进程内安装。

## 目录结构

```
src/Model.cs          数据模型（InstallKind / Pkg）
src/Catalog.cs        运行库清单 + DLL 体检清单
src/Sys.cs            系统目录 / WinSxS 工具
src/Detect.cs         安装状态检测
src/Util.cs           下载（断点续传 / SHA-256）与进程工具
src/MainForm.cs       主界面
src/TaskFile.cs       界面 ↔ 提权工作进程的文件通信通道
src/Program.cs        程序入口（单实例、worker/selftest 分发）
src/app.manifest      应用清单（asInvoker + PerMonitorV2 DPI 感知）
src/app.ico           程序图标
src/appname.txt       产品名（UTF-8）
src/versioninfo.txt   版本信息资源字段（key=value，UTF-8）
src/setver.ps1        版本信息资源注入脚本（纯 ASCII）
build.cmd             一键编译脚本（纯 ASCII）
```

## 自检

```
86运行库.exe /selftest [报告文件]
```

不开界面，直接检测全部清单项并输出文本报告，可用于批量机器巡检。

## 免责声明

- 本程序**不内置、不重新分发任何第三方安装包**：所有运行库均在用户点击安装时从各家官方服务器直接下载——微软（download.microsoft.com / aka.ms / go.microsoft.com）、Eclipse Adoptium（api.adoptium.net）、LunarG（sdk.lunarg.com）、OpenAL（openal.org）。请自行保证使用场景符合各官方分发协议。
- Microsoft、Visual C++、.NET、DirectX、Visual Studio 等均为微软公司的商标；Eclipse Temurin 为 Eclipse 基金会商标；本项目为独立开源工具，与上述公司/组织**无任何关联**。
- 安装、修复系统组件存在一定风险，重要机器请先备份；本程序按 MIT 协议"按现状"提供，不对安装结果承担责任。

## 许可

[MIT](LICENSE)
