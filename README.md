# 小橙看班联动（XiaoChengLink）— ClassIsland 2.1 插件

<p align="center"><img src="icon.png" width="96" height="96" alt="XiaoChengLink icon"></p>

[![Build & Release](https://github.com/MuttonIsHere/ClassIsland.XiaoChengLink/actions/workflows/build.yml/badge.svg)](https://github.com/MuttonIsHere/ClassIsland.XiaoChengLink/actions/workflows/build.yml)
[![Latest Release](https://img.shields.io/github/v/release/MuttonIsHere/ClassIsland.XiaoChengLink?include_prereleases&label=release)](https://github.com/MuttonIsHere/ClassIsland.XiaoChengLink/releases)

- 仓库：<https://github.com/MuttonIsHere/ClassIsland.XiaoChengLink>（公开）
- 发版包：GitHub Releases 里的 `XiaoChengLink.cipx`，或 Actions 构建产物 `XiaoChengLink-cipx`
- 监听“小橙看班”(`inArt_PC`) 的目标窗口出现/标题变化与 UI Automation 文本，命中关键词后
通过 ClassIsland 弹出提醒。**headless 插件 + 图形化设置页**（设置 → 插件 →「小橙看班联动」）。

- 目标宿主：ClassIsland **2.1.0.1**（`ClassIsland.Core.dll` AssemblyVersion 2.1.0.1）
- 插件清单：`manifest.yml`（`apiVersion: 2.0.0.0`，2.1.0.1 加载器最低要求）
- 状态：**可用**。已在真实 ClassIsland 2.1.0.1 上验证：窗口/画面触发、本地 `Data.idb` 正文读取、
  全屏播报、语音对讲组件，以及 **考试模式监控**（v2.0.0，真实考试开始/15 分钟/5 分钟/结束提醒）。
  带图形化设置页（进程名、标题/类名规则、关键词、轮询、冷却、画面阈值、全局节流、消息正文、语音对讲）。

---

## 0. 使用说明（白话版，先看这里）

### 0.1 这个插件是干什么的

小橙看班（`inArt_PC.exe`）弹出「消息提醒」时，让 ClassIsland 弹出对应的提醒。
因为小橙看班是 Delphi 自绘程序、**UI Automation 读不到里面的文字**（实测），所以插件不靠“读文字”，
而是靠**观察窗口画面有没有变化**来判断“有新消息”，再让 ClassIsland 弹提醒。

### 0.2 安装（两种方式任选）

**方式 A：用 Release 的 .cipx（推荐）**

1. 打开 <https://github.com/MuttonIsHere/ClassIsland.XiaoChengLink/releases>，下载最新的 `XiaoChengLink.cipx`；
2. 打开 ClassIsland → 设置 → 插件 →「从本地安装 / 拖入安装」选择该文件；
3. **重启 ClassIsland**；
4. 启动小橙看班（`D:\inArtBoard\inArt_PC.exe`）。

**方式 B：手动放文件夹**

把包含 `manifest.yml` + `XiaoChengLink.dll` 的文件夹放到：

```
<ClassIsland 安装根目录>\data\Plugins\xiaocheng.link\
```

（本机示例：`C:\Users\36896\Desktop\old\ClassIsland_windows_x64_selfContained_folder_2.0.0.2_2\data\Plugins\xiaocheng.link\`）
然后重启 ClassIsland。

安装后能在 **设置 → 插件 →「小橙看班联动」** 看到设置页，就说明安装成功。

### 0.3 ⚠️ 必须保持 ClassIsland 主界面可见

**这是最重要的前提。** ClassIsland 隐藏主界面时，提醒只会**排队**、不会显示（ClassIsland 自身行为，
见上游 issue <https://github.com/ClassIsland/ClassIsland/issues/1632>）；这段时间插件日志里仍然会有
`提醒已入队`，但你在屏幕上什么都看不到，等重新显示主界面时才会一次性补弹。

- 平时请让主界面保持可见（托盘图标右键 →「显示主界面」；或在 设置 → 窗口 里调整）；
- 如果曾经隐藏过主界面，恢复后建议点一下主菜单里的「**清除所有提醒**」，把排队中的旧提醒清掉，
  避免一次弹出一大堆。

### 0.4 工作原理：为什么是“画面变化检测”

```
每 1.5 秒轮询一次（可调）
 1. 找到小橙看班的窗口：进程名 inArt_PC + 窗口标题/窗口类名（FMTfrmXxx、TFormXxx）匹配
 2. 对可见的目标窗口拍一张“内存截图”，缩成 40×40 的亮度网格并记住
 3. 下一轮再拍一张，和上一张比较：
      · 变化比例 ≥ 阈值（默认 0.4%）→ 认为“有新消息” → 触发 ClassIsland 提醒
      · 没超过阈值 → 忽略（鼠标划过、轻微动画不会触发）
 4. 另外两类触发：
      · 目标窗口从“没有/隐藏”变成“可见”（比如刚打开小橙看班）→ 触发
      · 关键词命中（可选，对小橙看班基本用不上：读不到正文，且默认不拿窗口标题做关键词匹配）
 5. 防连发：任意一条提醒发出后，12 秒内不再发下一条（可调）
```

为什么用画面变化而不是读文字：小橙看班的「消息提醒」窗口是**一直存在**的常驻窗口，新消息只是让
窗口里的未读数字/内容发生刷新；窗口本身不会重新出现。所以“窗口出现”不够用，必须看**画面变化**。
插件只比较画面，不做 OCR、不上传截图、不读文字，所有截图都在内存里处理。

### 0.5 太灵敏 / 太迟钝 / 太吵，怎么调

打开 **设置 → 插件 →「小橙看班联动」**，改完点「保存并应用」即可（从下一轮扫描起生效）：

| 你的感受 | 调哪个设置 | 怎么调 |
|---|---|---|
| **太灵敏**：鼠标划过、界面动画、滚动也弹提醒 | **画面变化阈值**（`visualChangeThresholdPercent`） | **调大**：0.4 → 1.0 / 2.0 / 3.0（越大越迟钝） |
| **太迟钝**：新消息来了不弹提醒 | **画面变化阈值** | **调小**：0.4 → 0.2 / 0.3（最小 0.1；越小越灵敏） |
| **太吵**：一次来好几条、连续弹 | **全局提醒节流**（`notificationThrottleSeconds`） | **调大**：12 → 30 / 60 秒；设 0 = 关闭节流（不推荐） |
| 同一个窗口反复弹 | **冷却时间**（`cooldownSeconds`） | **调大**：60 → 120 / 300 秒 |
| 担心轮询费 CPU | **轮询间隔**（`pollIntervalMs`） | **调大**：1500 → 3000 / 5000 毫秒（越小越及时） |

**调阈值的建议**：先按默认 0.4% 用一天；如果误报多就 1.0/2.0，如果漏报就 0.2。日志里每次触发都会写
`画面变化触发：… 变化比例=0.6% 阈值=0.4%`，你可以照着真实比例微调（把阈值设在常见触发比例之下一点）。

### 0.6 怎么确认它在工作

插件日志在：

```
<ClassIsland 安装根目录>\data\Config\Plugins\xiaocheng.link\logs\yyyyMMdd.log
```

用记事本打开，搜这些关键字：

| 日志关键字 | 含义 |
|---|---|
| `窗口监控启动` | 插件后台服务已启动 |
| `扫描诊断：轮次=… 匹配窗口=2 可见匹配=1` | 每 30 秒打印一次扫描结果（顶层窗口数/匹配窗口） |
| `画面变化触发：… 变化比例=x%` | 画面变化超过阈值，正在发提醒 |
| `提醒已入队` | 已交给 ClassIsland（此时若主界面可见就会弹） |
| `全局节流（12s）内，跳过提醒` | 因为节流被正确跳过，不是故障 |

### 0.7 常见问题

| 现象 | 原因 / 解决 |
|---|---|
| 完全没反应 | ① 小橙看班是否在运行？② 主界面是否可见（见 §0.3）？③ 看日志有没有 `扫描诊断`（有=服务在跑） |
| 日志有 `提醒已入队` 但屏幕没弹窗 | ClassIsland 主界面被隐藏了（提醒会排队）；显示主界面后补弹，可先「清除所有提醒」 |
| 设置页找不到「小橙看班联动」 | 插件没装好/没重启 ClassIsland；检查 `data\Plugins\xiaocheng.link\manifest.yml` 是否存在 |
| 弹得太频繁 | 调大 **画面变化阈值** 和/或 **全局提醒节流**（见 §0.5） |
| 新消息来了不弹 | 调小 **画面变化阈值**（如 0.2）；若本来是 0.4 且日志里没有 `画面变化触发`，说明变化比例低于阈值 |
| 想临时关闭 | 把 **画面变化阈值** 调到 50（或把全局节流调到 600 秒），或者直接禁用插件 |

### 0.8 消息正文从哪里来（本地数据库）

只靠截图/窗口标题拿不到小橙看班的正文，所以插件还会**只读**它的本地 Jet 数据库：

```
D:\inArtBoard\Data.idb   （Microsoft Jet / Access 格式；小橙看班运行时会写它）
表：HomeWork / ClassNotice / SchoolNotice / ParentMsg / WelcomeWord / ScheduledTask / ExamClock …
```

触发提醒时，插件会：
1. 以共享读方式把 `Data.idb` 复制到临时目录（不影响小橙看班使用）；
2. 调用随插件附带的 `read-message.ps1`（由 **32 位 PowerShell** 执行，因为 Jet 只有 32 位提供程序；
   ClassIsland 插件本身是 64 位进程，无法直接打开）；
3. 从所有消息表里选**时间最新、且有标题/正文**的那一条；
4. 把它附加到提醒正文，例如：

```
检测到「消息提醒」窗口出现（进程 inArt_PC，类名 FMTfrmMessageCenter）
【HomeWork】11月9日（周一）
测试
```

相关配置（一般不須改，默认开启）：

| 字段 | 默认 | 说明 |
|---|---|---|
| `readMessageBodyFromDatabase` | `true` | 是否从本地数据库读取正文；改成 `false` 就只显示通用提示 |
| `messageDatabasePath` | `""` | 留空 = 自动从运行中的 `inArt_PC.exe` 所在目录找 `Data.idb` |
| `messageBodyMaxAgeSeconds` | `3600` | 只接受最近 N 秒内的消息；0 = 不限制 |
| `messageBodyMaxChars` | `300` | 正文最大长度，超出截断 |

**读取失败怎么办**：插件会自动退回通用提示（不影响触发）。排查看插件日志里有没有
`数据库正文：table=... title=... content=...`；如果没有，检查 `read-message.ps1` 是否在插件目录下、
以及 `Data.idb` 是否在小橙看班目录。数据库只在本机读取、不写入、不上传。

### 0.9 安全边界

插件只做只读操作：枚举窗口、读窗口标题/类名、`PrintWindow` 截取目标窗口画面（内存中处理）、
只读复制本地数据库并用随附脚本查询；**不注入、不修改小橙看班进程、不破解、不绕过权限、不上传任何数据**。

### 0.10 考试模式监控（v2.0.0）

![考试模式提醒](docs/exam-mode-alert.png)

开启小橙看班「考试模式」后，插件自动读 `ExamClock` 表，按考试时间弹四类提醒：

| 时机 | 提醒内容 |
|---|---|
| 考试开始 | `考试已开始，科目：语文` |
| 距结束 15 分钟 | `离考试结束还有15分钟，请注意掌控时间。` |
| 距结束 5 分钟 | `距离 语文 科目结束还有 5 分钟，请检查答题卡` |
| 考试结束 | `考试结束，请立即停笔！` |

**真实数据格式**（2026-11-11 实测）：

```
ExamDate = 2026-11-11 (DateTime)
BeginTime = 20:31:00 (String, HH:mm:ss)
EndTime   = 21:01:00 (String, HH:mm:ss)
Course    = 语文
```

实现细节：
- 每 20 秒读一次 `ExamClock`（32 位 PowerShell 只读查询），不写库；
- 时间 = `ExamDate + BeginTime/EndTime`，同时兼容 `HH:mm`、绝对时间；**跨天**（End ≤ Begin）自动按次日算；
- 去重 key = `ExamGuid + 开始/15/5/结束`，每个阶段只弹一次；结束只补发“刚结束 10 分钟内”的，避免启动刷历史考试；
- 不需要设置页/额外配置，开启考试模式即生效；
- 测试钩子：设置环境变量 `XIAOCHENG_EXAM_NOW=2026-11-11 20:47:00` 可模拟时间验证 15/5/结束提醒。

> 提醒使用插件自己的 `NotificationProvider`，mask=`考试模式`；开始红色、倒计时橙色、结束绿色。

---

## 1. 工作原理（技术细节）

```
每 1.5s 轮询（pollIntervalMs 可调）
 └─ WindowScanner（Win32 只读 P/Invoke：EnumWindows/GetWindowText/GetClassName/IsWindowVisible/GetWindowRect）
     └─ 按 进程名 +（窗口标题正则 OR 窗口类名正则）过滤目标窗口；忽略托盘/隐藏应用窗类名
         └─ 三类触发（可分别配置）：
             ① 窗口出现：目标窗口从“不存在/不可见” → “存在且可见”
             ② 画面变化：可见大窗口 PrintWindow → 40×40 亮度网格哈希；变化比例 ≥ visualChangeThresholdPercent
             ③ 关键词：Assembled 文本（子窗口/UIA，默认不含窗口标题）命中关键词且文本变化
                  └─ 全局提醒节流（notificationThrottleSeconds）+ 单窗口冷却（cooldownSeconds）
                      └─ ClassIsland 提醒：Mask 显示标题，Overlay 显示正文
```

**发提醒的 API 路径（重要，已实测核对 2.1.0.1 二进制）**

插件注册自己的提醒提供方 `XiaoChengNotificationProvider : NotificationProviderBase`
（`services.AddNotificationProvider<T>()`），再调用它的 `ShowNotification(...)` 发送。
**不存在**“全局静态发提醒 API”或可直接注入的全局 `INotificationSender`；
`INotificationHostService.ShowNotification(request, providerGuid, ...)` 该重载不可访问（internal），
插件直接调用会编译报 `CS0122`。详见 `research/classisland-api/classisland-2.1-plugin-api.md` §5.3 与
`research/classisland-api/snippets/PluginCore.cs` 的编译记录。

> 注意：`AddNotificationProvider<T>()` 内部是 `AddHostedService<T>()`，而
> `AddHostedService<T>` 只登记 `IHostedService -> T`，**不会把 T 登记成可注入单例**。
> `Plugin.cs` 里把该描述符替换成「T 单例 + IHostedService 工厂解析同一实例」，
> 这样宿主启动时仍会构造并启动同一个提供方实例，监控服务也能构造函数注入它发提醒。

---

## 2. 项目结构

```
plugin/
├── .github/workflows/build.yml  # GitHub Actions：push 自动构建 cipx；v* tag 自动发 Release
├── .github/RELEASE.md           # 发版步骤说明
├── .gitignore                   # 排除 bin/ obj/ cipx/ 等构建产物
├── XiaoChengLink.csproj      # dotnet build 工程（net8.0-windows + ClassIsland.PluginSdk 2.1.0.1）
├── manifest.yml              # ClassIsland 插件清单（id/apiVersion/entranceAssembly…）
├── README.md                 # 本文档
├── settings.sample.json      # settings.json 参考样例（字段说明见 §5）
└── src/
    ├── Plugin.cs                              # [PluginEntrance] 入口、DI 注册、异常兜底
    ├── Logging/
    │   └── PluginLog.cs                       # 文件日志（<PluginConfigFolder>/logs/yyyyMMdd.log，2MB 滚动）
    ├── Monitoring/
    │   ├── MonitorOptions.cs                  # settings.json 模型 + 加载/保存 + 默认值
    │   ├── WindowScanner.cs                   # Win32 窗口枚举/筛选（只读；ReloadTitlePatterns 支持设置页热重载）
    │   ├── UiaTextReader.cs                   # System.Windows.Automation 文本读取（防御性）
    │   ├── KeywordMatcher.cs                  # 关键词匹配（Contains/Regex）
    │   └── WindowTextMonitor.cs               # BackgroundService 主循环 + 冷却去重
    ├── Notification/
    │   ├── XiaoChengNotificationProvider.cs   # [NotificationProviderInfo] 提醒提供方（真正发提醒）
    │   └── ClassIslandNotifier.cs             # 发送封装（try/catch + 超时，失败返回 false）
    └── Settings/
        ├── XiaoChengLinkSettingsPage.cs       # [SettingsPageInfo] code-only Avalonia 设置页
        └── README-SETTINGS.md                 # 设置页实现说明（code-only 方案 / XAML 升级路径）
```

编译产物（本机 csc 路线）：`plugin/bin/XiaoChengLink.dll` + `plugin/bin/XiaoChengLink.build.log`。

---

## 3. 构建

### 3.1 方式 A：官方 `dotnet build`（有 .NET 8 SDK 的机器，推荐给最终用户）

前置：.NET 8 SDK（含 Windows Desktop SDK）。工程已按官方 2.1.0.1 示例插件范式配置：
`net8.0-windows`、`Nullable`、`EnableDynamicLoading`、`PackageReference ClassIsland.PluginSdk 2.1.0.1`
（`ExcludeAssets=runtime;native`，避免把宿主程序集复制进插件包）、`CreateCipx=true`。

```powershell
# 在 plugin/ 目录
dotnet build -c Release
# 产物：plugin\bin\Release\net8.0-windows\XiaoChengLink.dll（manifest.yml/README.md/settings.sample.json 会被复制到输出目录）
# 打包：plugin\cipx\XiaoChengLink.cipx（ClassIsland.PluginSdk.targets 的 CreateCipx Target，
#       要求输出目录已有 manifest.yml，否则报 CISDK0001）
```

> `UseWPF=true` 仅用于引用 `System.Windows.Automation`（UIA）程序集，插件不会启动 WPF
> `Application`/`Dispatcher`；ClassIsland 2.x 的 UI 是 Avalonia，本插件不碰 UI。

### 3.2 方式 B：本机 csc 直编（无 dotnet SDK / 无 NuGet 时的实测可用路线）

本机（VS 2022 Roslyn csc 4.14，C# 13）用 `tools/build-env/build-plugin.ps1` 直接引用
ClassIsland 2.1.0.1 应用目录中的实现程序集编译。**已实测 0 error / 0 warning**
（`plugin/bin/XiaoChengLink.build.log`）。

在**工作区根目录**执行（Windows PowerShell 5.1）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-env\build-plugin.ps1 `
  -InputFiles .\plugin\src `
  -OutputDll  .\plugin\bin\XiaoChengLink.dll `
  -ExtraRefs  "CommunityToolkit.Mvvm.dll,UIAutomationClient.dll,UIAutomationTypes.dll,WindowsBase.dll,System.Text.RegularExpressions.dll,System.Text.Json.dll,System.Memory.dll,Avalonia.Base.dll,Avalonia.Controls.dll,Avalonia.Markup.dll,FluentAvalonia.dll"
```

`-InputFiles` 支持传目录（递归收集 `*.cs`）；必须带 `-ExecutionPolicy Bypass`。

**11 个额外引用是必需的**（比最简模板多 7 个；Avalonia 只用于编译，不打包进插件）：

| ExtraRef | 用途 | 缺失时的报错 |
|---|---|---|
| `CommunityToolkit.Mvvm.dll` | `NotificationRequest/NotificationContent` 的基类 `ObservableRecipient` 所在程序集 | CS0012 |
| `UIAutomationClient.dll`,`UIAutomationTypes.dll`,`WindowsBase.dll` | `System.Windows.Automation`（UIA 文本读取） | CS1069 |
| `System.Text.RegularExpressions.dll` | 窗口标题正则 + `keywordMatchMode: Regex` 关键词匹配 | CS1069 |
| `System.Text.Json.dll` + `System.Memory.dll` | 读取 `settings.json`（`JsonSerializer`） | CS0234 / CS0012 |
| `Avalonia.Base.dll` | 把通知构造/发送调度到 Avalonia UI 线程（`Dispatcher.UIThread`，真机实测必需，见 §6.1）；设置页的布局/样式类型也在这里 | CS0246（`Avalonia.Threading`） |
| `Avalonia.Controls.dll` + `Avalonia.Markup.dll` + `FluentAvalonia.dll` | 设置页的 Avalonia 控件树（`SettingsPageBase : UserControl`、TextBox/Button/NumericUpDown 等） | CS0246 / CS0234 |

> 只给最简模板的 4 个 ExtraRefs 会失败：`MonitorOptions.cs` 报 CS0234（System.Text.Json）、
> `WindowScanner.cs`/`KeywordMatcher.cs` 报 CS1069（Regex）。

元数据校验（确认 `[PluginEntrance]` + `PluginBase` + 引用版本与宿主一致）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-env\verify-plugin-dll.ps1 -DllPath .\plugin\bin\XiaoChengLink.dll
# 期望：SUCCESS: 找到 1 个可被 ClassIsland 加载的插件入口: XiaoChengLink.XiaoChengLinkPlugin
#       ClassIsland.Core ref=2.1.0.1 appdir=2.1.0.1 OK
```

限制：csc 路线**不能编译 `.axaml`**（本插件无 UI，不受影响）；产物无 `.deps.json`
（插件 ALC 只按同目录 + 宿主默认 ALC 解析依赖，本插件只用宿主自带程序集）。

---

## 4. 安装 / 联调

### 4.1 正式安装（三种方式，具体以真机实测为准）

1. **从 GitHub Release 安装 `.cipx`（推荐）**：打开
   <https://github.com/MuttonIsHere/ClassIsland.XiaoChengLink/releases> 下载最新的
   `XiaoChengLink.cipx` → ClassIsland「设置 → 插件 → 从本地安装 / 拖入」→ 重启 ClassIsland。
   （没有 Release 时可到 Actions → Build & Release → 最新一次成功的 run → Artifacts 下载。）
2. **应用内安装 `.cipx`（自己构建）**：设置 → 插件 → 从本地安装（或把 `.cipx` 拖进设置页），然后**重启 ClassIsland**。
3. **手动放目录**：把含有 `manifest.yml` + `XiaoChengLink.dll`（+ README.md 等）的**文件夹**
   放到 `<ClassIsland 安装根目录>\data\Plugins\<任意文件夹名>\`，然后重启 ClassIsland。
   - **本机实测**：用户安装目录为 `C:\Users\36896\Desktop\old\ClassIsland_windows_x64_selfContained_folder_2.0.0.2_2\`，
     插件实际从 `...\data\Plugins\<id>\` 加载；插件配置目录为 `...\data\Config\Plugins\<id>\`
     （`settings.json`、`logs/` 均在这里，已实测生成）。
   - 另一种发行形态（非便携/Velopack 安装）可能使用 `%LOCALAPPDATA%\ClassIsland\Plugins\<id>\`
     （研究文档 §7.1 从 IL 推断的路径）。**两种都以首次运行后的日志/配置目录为准**。

### 4.2 开发期联调（`-epp`，不必打包/装进 Plugins）

本仓库已把 `manifest.yml`、`README.md`、`settings.sample.json` 复制到 `plugin/bin/`，
可直接把 `plugin/bin` 当作插件目录：

```powershell
# 先退出已运行的 ClassIsland（有单实例互斥 Global\ClassIsland.Lock）
& "C:\Users\36896\Desktop\old\ClassIsland_windows_x64_selfContained_folder_2.0.0.2_2\app-2.1.0.1-0\ClassIsland.Desktop.exe" `
  -epp "C:\Users\36896\Documents\deepseek-harness\default-workspace\plugin\bin"
```

- 【需实测】`-externalPluginPath/-epp` 在 **Release/Velopack 安装版**是否同样生效（研究文档 §7.3
  从 `Program.AppEntry` IL 确认选项存在，但官方文档只演示 Debug 源码构建）；不行就退回 4.1 手动放目录。
- 用 csc 重新编译后需要**重启 ClassIsland**（Release 版无热重载）。

### 4.3 日志

- 插件日志：`<PluginConfigFolder>/logs/yyyyMMdd.log`（超过 2MB 滚动为 `.old`）。
  记录：初始化完成、配置加载、监控启动/停止、关键词命中/窗口出现、所有异常。
- 宿主日志：ClassIsland 应用内有「打开日志目录」入口（具体路径**需实测**）。

---

## 5. 图形化设置页 + `settings.json` 字段表

### 5.1 设置页（ClassIsland 设置 → 插件 →「小橙看班联动」）

插件注册了自己的 `SettingsPage`（`src/Settings/XiaoChengLinkSettingsPage.cs`，继承
`ClassIsland.Core.Abstractions.Controls.SettingsPageBase`，用**纯 C# 构建 Avalonia 控件**，无 `.axaml`）。
可配置：`processNames`、`windowTitlePatterns`、`keywords`、`keywordMatchMode`、`pollIntervalMs`、
`cooldownSeconds`、`triggerOnWindowAppear`；点击「保存并应用」会写回
`<PluginConfigFolder>/settings.json` 并**热重载标题正则**（其余字段下一轮轮询生效；关键词/匹配方式
需要重启插件生效——如实标注，后续可给 `KeywordMatcher` 加 Reload）。

> 实现细节与后续 XAML 化升级路径见 `src/Settings/README-SETTINGS.md`。
> **真机注意**：设置页在设置窗口中是否正常显示、保存后热生效效果仍需实际打开设置窗口确认
> （代码已通过本机 csc 编译 0 error；宿主真机显示属待验证项，见 §7）。

### 5.2 配置文件字段（也可手动编辑）

位置：`<PluginConfigFolder>/settings.json`。首次运行不存在时插件写入默认配置并记日志；
解析失败回落默认值（不会影响宿主机启动）。参考样例：`plugin/settings.sample.json`。

| 字段 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `processNames` | string[] | `["inArt_PC"]` | 目标进程名（不含 `.exe`，不区分大小写）。留空数组 = 不限制进程（会扫描所有顶层窗口，慎用）。 |
| `windowTitlePatterns` | string[] | `["消息提醒","班级课表"]` | 顶层窗口标题的**正则**（不区分大小写）。 |
| `windowClassPatterns` | string[] | `["^FMTfrm","^TForm","^Tfrm"]` | 顶层窗口**类名**正则。Delphi/VCL 弹出窗体常常没有标题或标题不变，类名（FMTfrmXxx/TFormXxx）更稳定；标题与类名是“或”关系。 |
| `ignoreWindowClassPatterns` | string[] | `["^FMTfrmIcon$","^TfrmTrayIcon$","^TFMAppClass$"]` | 不参与任何触发的窗口类名（默认忽略托盘图标/隐藏应用窗，避免程序启动误报）。 |
| `keywordMatchWindowTitle` | bool | `false` | 关键词是否也匹配窗口标题。默认 false：小橙看班标题恒为「消息提醒」，含标题会每次新窗口都误报。 |
| `notificationThrottleSeconds` | int | `12` | 全局提醒节流：任一提醒发出后此时长内不再发下一条，避免程序重启/多窗口连发。0=关闭。 |
| `triggerOnVisualChange` | bool | `true` | 是否启用“窗口画面变化”触发（PrintWindow 截图网格哈希，不做 OCR）。小橙看班常驻消息窗靠它感知新消息。 |
| `visualChangeThresholdPercent` | double | `0.4` | 画面变化阈值：采样网格中亮度变化超过阈值的像素占比（%）。 |
| `visualChangeMinAreaPixels` | int | `10000` | 参与画面变化判定的最小窗口面积；小图标窗不参与。 |
| `diagnosticLogIntervalSeconds` | int | `30` | 扫描诊断日志间隔（秒），打印顶层窗口数/匹配窗口明细，便于排查“不触发”。 |
| `readMessageBodyFromDatabase` | bool | `true` | 是否从本地 `Data.idb` 读取最新消息正文附加到提醒（见 §0.8）。 |
| `messageDatabasePath` | string | `""` | 数据库路径；留空 = 自动从 inArt_PC 进程目录找 `Data.idb`。 |
| `messageBodyMaxAgeSeconds` | int | `3600` | 只接受最近 N 秒内的消息作为正文；0 = 不限制。 |
| `messageBodyMaxChars` | int | `300` | 正文最大长度（超出截断）。 |
| `pollIntervalMs` | int | `1500` | 轮询间隔（运行时钳制到 500–60000 ms）。 |
| `keywords` | string[] | `["消息","提醒","作业","考试","通知","点名","请假"]` | 关键词列表。 |
| `keywordMatchMode` | string | `"Contains"` | `Contains`（子串，不区分大小写）或 `Regex`（每条 keyword 当正则，250ms 超时）。 |
| `triggerOnWindowAppear` | bool | `true` | 窗口从不可见/不存在 → 可见时，触发“窗口出现”提醒（小橙看班主要降级路径）。 |
| `triggerOnExistingWindows` | bool | `false` | 启动时已存在的窗口是否也算“出现”。默认 `false`：先建立基线，不启动即误报。 |
| `cooldownSeconds` | int | `60` | 同一窗口同一文本 hash / 同一窗口出现事件 的冷却秒数。0 = 不冷却。 |
| `notificationDurationSeconds` | int | `8` | 提醒正文（Overlay）显示秒数。 |
| `maskDurationSeconds` | int | `3` | 提醒遮罩（Mask，作为“标题”）显示秒数。 |
| `textSources.uia` | bool | `false` | 是否用 UI Automation 读窗口文本。**默认关闭**：对小橙看班无效（实测只有窗口标题），且对 Delphi 窗口做跨进程 UIA 读取有卡死风险；监控浏览器/记事本等标准窗口时可临时改为 `true`。 |
| `textSources.win32` | bool | `true` | 是否读子窗口文本（EnumChildWindows + GetWindowText）。 |
| `textSources.windowTitle` | bool | `true` | 是否把顶层窗口标题作为文本来源（对小橙看班是唯一有效来源）。 |

> 关键词提醒只在**文本 hash 变化**时触发（避免标题恒为“消息提醒”导致每 60s 重复误报）；
> 窗口出现提醒按窗口句柄冷却。

---

## 6. 验证步骤

1. 启动“小橙看班”：`D:\inArtBoard\inArt_PC.exe`（本机 v4.1.1.3，Delphi/VCL）。
2. 按 §4 安装/`-epp` 启动 ClassIsland，确认插件列表里出现「小橙看班联动」。
3. 在设置 → 插件里启用插件，重启 ClassIsland。
4. （可选，设置页验证）打开 设置 → 插件 →「小橙看班联动」，改 `processNames` / `keywords` /
   `pollIntervalMs` → 点「保存并应用」，确认状态栏提示成功、`<PluginConfigFolder>/settings.json` 已更新；
   保存后标题正则热重载，关键词类改动需重启插件。
5. 触发小橙看班的「消息提醒」窗口（例如让班级发一条消息，或手工操作使其显示）。
6. **期望**：ClassIsland 弹出提醒 —— 遮罩显示「小橙看班」，正文类似
   `检测到「消息提醒」窗口出现（进程 inArt_PC，类名 FMTfrmMessageCenter)`。
7. 检查 `<PluginConfigFolder>/logs/yyyyMMdd.log`：应能看到监控启动、配置加载、
   `检测到窗口出现…` 等记录。
8. （可选）验证关键词/UIA 路径：把 `processNames` 改为 `["notepad"]`、
   `windowTitlePatterns` 改为 `[]`、`keywords` 改为 `["测试"]`、`textSources.uia` 改为 `true`，
   在记事本里输入“测试”后观察是否弹出“关键词命中”提醒。**这条路径本机未实测**（UIA 读取器尚未在
   标准窗口上做真机验证；对小橙看班已确认读不到正文）。
9. （可选）重新编译并回归：§3.2 的 csc 命令 + verify 脚本均应为 0 error / SUCCESS。

### 6.1 真机联调已发现并修复的问题（2026-11-08，用户本机）

把插件装入 `...\data\Plugins\xiaocheng.link\` 后启动 ClassIsland 2.1.0.1：

| # | 现象（证据） | 根因 | 修复 |
|---|---|---|---|
| 1 | 插件被宿主加载、提醒提供方注册成功、监控启动：日志见 `research/e2e/classisland-20261108-183444.log`、`research/e2e/pluginlog-20261108.log` | —— | —— |
| 2 | 后台线程发提醒抛 `System.InvalidOperationException: Call from invalid thread`（堆栈：`TwoIconsMaskTemplateData..ctor → LucideIconSource..ctor → AvaloniaObject..ctor`），见 `research/e2e/classisland-after-trigger-20261108-183444.log` | `NotificationContent.CreateTwoIconsMask(...)` 内部会 `new` Avalonia 控件（`LucideIconSource`），必须在 Avalonia UI 线程创建；`BackgroundService` 线程不满足 | `XiaoChengNotificationProvider` 用 `Avalonia.Threading.Dispatcher.UIThread.InvokeAsync/Post` 把「构造 NotificationRequest + ShowNotification」整体调度到 UI 线程 |
| 3 | 一次启动连发 3 条“窗口出现”提醒 | Delphi 程序同时产生多个顶层窗口（含隐藏的「班级课表」「frmTrayIcon」） | 只对**可见**窗口判定“出现”，且同一轮最多发一条“窗口出现”提醒 |
| 4 | 监听到目标窗口后可能卡住（UIA 路径） | 对 Delphi 自绘窗口跨进程做 UIA 调用可能长时间阻塞 | `textSources.uia` 默认改为 `false`（小橙看班本来也读不到正文）；其它程序按需开启 |

### 6.2 真机排查与修复记录（2026-11-09）

**现象**：插件已加载、监控日志正常，但小橙看班出现提醒时 ClassIsland 没反应。

**排查**：
- 插件日志显示监控服务 21:03 启动且无异常，但**没有任何“本轮检测到”记录** → 扫描器虽然匹配到了窗口，却没有任何触发；
- 用 `tools/scan-windows.ps1` 抓真实特征：进程 `inArt_PC` 的窗口为
  `FMTfrmMessageCenter`（标题「消息提醒」，**常驻可见**）、`FMTfrmIcon`（「小橙同学」）、
  `FMTfrmClassCourse`（「班级课表」，隐藏）、`TFMAppClass`/`TfrmTrayIcon`（隐藏）；
- 根因：旧版只按「标题正则 + 窗口出现」触发。小橙看班的「消息提醒」是**一直存在的常驻窗口**，
  插件启动时已在基线中；后续新消息只是**窗口内画面变化**，窗口不消失/不重建 → 永远不触发。

**修复（v1.1.0）**：
1. 新增**窗口类名匹配**（`windowClassPatterns`，默认 FMTfrm/TForm/Tfrm），无标题 Delphi 弹窗也能命中；
2. 新增**画面变化检测**（`WindowVisualDiff`：PrintWindow → 40×40 亮度网格哈希，不做 OCR），
   常驻窗口内容变化（如未读数字/新消息）也会触发；
3. 新增**全局提醒节流**（`notificationThrottleSeconds=12`）+ 忽略托盘图标/隐藏应用窗类名，
   避免程序重启时连发多条；
4. 关键词默认**不匹配窗口标题**（避免标题恒为「消息提醒」造成每次窗口创建都误报）；
5. 新增**扫描诊断日志**（每 30s 打印顶层窗口数/匹配窗口的标题、类名、可见性、区域）。

**修复后真机验证（2026-11-09 19:28）**：
- 基线：匹配 2 个窗口（`FMTfrmMessageCenter` 可见 + `FMTfrmClassCourse` 隐藏），画面基线已建立（40x40）；
- 重启小橙看班后：`发现新的匹配窗口 [FMTfrmMessageCenter]` → **提醒已入队**（窗口出现）；
  紧接着 `画面变化 0.6% > 阈值 0.4%` 被识别，但因 12s 全局节流被正确跳过 → **单次重启只发 1 条提醒**；
- 证据：`research/e2e/fix-deploy-*`、`fix-trigger-*.png`、插件日志 `data\Config\Plugins\xiaocheng.link\logs\*.log`。

### 6.3 用户手动验证（2026-11-08）

修复 Avalonia 线程问题后，用户在真实 ClassIsland 2.1.0.1 上重新安装并测试：**插件能正常触发提醒**
（“消息提醒”窗口出现时 ClassIsland 正常弹出）。此前 agent 因 ClassIsland 被 `grantUiAccess`
提权重启而无法自动完成该步，现已由用户手动闭环。

---

## 7. 已知限制

1. **小橙看班读不到正文**：本机实测 UIA 与 MSAA 对该 Delphi/VCL 程序都只能拿到顶层窗口标题
   （`消息提醒`、`班级课表`、`frmTrayIcon` 等；见 `research/uia/inart_pc-*.json`、`msaa-probe-*.json`）。
   因此对它的**有效触发路径是“窗口出现”降级策略**；UIA 文本读取保留给其它标准窗口。
2. 关键词路径对小橙看班可能永远不触发：窗口标题恒为「消息提醒」，文本 hash 不变 → 不会重复报。
   这是有意设计（避免每 60s 重复误报）。
3. 窗口出现判定基于顶层窗口句柄 + 可见性变化；若程序复用同一窗口句柄且不改变可见性（只改内容），
   只能靠文本 hash 变化（UIA 可读时）或冷却过期后的再次出现。
4. UIA 调用无法真正取消：单窗口只做 1.5s 墙钟预算（元素之间检查），目标程序卡死时该轮可能变慢。
5. csc 直编路线不支持 `.axaml`（本插件无 UI）。
6. 除 host 自带程序集外**未引入任何第三方 NuGet 包**；`System.Text.Json`/`Regex`/UIA 都是宿主
   自包含目录里已有的程序集。

### 需实测清单（按风险排序）

1. ~~插件真机加载~~ — **已由用户验证**：ClassIsland 能加载插件并正常触发提醒。
2. ~~`AddHostedService<WindowTextMonitor>` 是否随宿主启动~~ — **已验证**：插件日志出现
   `窗口监控启动：进程=[inArt_PC] 轮询=1500ms …`。
3. ~~`PluginConfigFolder` 实际路径~~ — **已验证**：`<安装根>\data\Config\Plugins\xiaocheng.link\`
   下生成 `settings.json` 与 `logs/`。
4. ~~后台线程 `ShowNotification` 安全性~~ — **已修复并验证**：改成 Avalonia UI 线程调度后用户实测可弹提醒。
5. **设置页真机显示与保存**（新增，最高优先级）：设置窗口能否解析并显示「小橙看班联动」页、DI 构造注入是否生效、
   保存后 `settings.json` 是否更新、标题正则热重载是否生效。代码已本机 csc 编译 0 error，但 UI 显示未真机验证。
6. **关键词热生效**：当前 `KeywordMatcher` 构造时做快照，设置页改 `keywords`/`keywordMatchMode` 后需重启插件；
   如要即时生效需给 `KeywordMatcher` 增加 Reload（后续改进项）。
7. `-epp` 在 Release/Velopack 安装版是否生效（本机未实测；不生效就用 §4.1 的目录/安装包方式）。
8. 小橙看班「消息提醒」窗口出现/隐藏的真实行为（是否同一句柄、是否可见性变化），决定触发灵敏度。
9. 插件 ALC 能否解析宿主的 `UIAutomationClient`/`WindowsBase`（插件已能加载；UIA 路径对普通窗口尚未实测）。

---

## 8. 实测证据来源

> 以下为**本机开发工作区**中的证据文档（`research/`、`tools/` 未随本 GitHub 仓库提交；仓库只含插件源码与 CI）。
> 仓库地址：<https://github.com/MuttonIsHere/ClassIsland.XiaoChengLink>；发版包在
> <https://github.com/MuttonIsHere/ClassIsland.XiaoChengLink/releases>。

- `research/classisland-api/classisland-2.1-plugin-api.md`（2.1.0.1 二进制 Cecil 调查报告；
  §3 manifest/apiVersion、§4 PluginBase/DI、§5 提醒 API、§7 目录与 `-epp`、§8 坑）
- `research/classisland-api/settings-page-metadata.txt`（SettingsPage/AddSettingsPage 的 Cecil 元数据）
- `research/classisland-api/snippets/PluginCore.cs` + `bin/PluginCore.build.log`（provider 路径编译验证）
- `tools/build-env/REPORT.md`（csc 直编方案、引用清单、踩坑）
- `tools/build-env/CompileSmoke/NotificationSmoke.cs`、`UiaSmoke.cs`（通知/UIA 编译验证）
- `research/uia/inart_pc-*.json`、`msaa-probe-*.json`（小橙看班窗口/UIA/MSAA 实测）
- 本插件编译产物与日志：`plugin/bin/XiaoChengLink.dll`（50688 B，含设置页）、`plugin/bin/XiaoChengLink.build.log`

---

## 调课联动（v2.0.2.0 起）

如果同时安装了 **QQ 调课同步（QqClassSync）** 插件，本插件会把从小橙看班数据库读到的消息正文
转发给它，由对方统一解析并修改 ClassIsland 的临时层课表，大屏会弹出「【来源：小橙看班】」的提醒。

- **触发时机**：① 窗口提醒/画面变化触发时立即转发；② 兜底轮询（`tiaokeLinkPollSeconds`，默认 30 秒）
  主动读库转发，避免不弹窗的通知被漏掉。
- **耦合方式**：进程内反射调用 `QqClassSync.Bridge.TiaokeBridge.Submit(text, source)`，
  不编译期引用对方程序集；对方没装时自动跳过，只偶尔记一条日志。
- **关闭方式**：把 `settings.json` 里的 `tiaokeLinkPollSeconds` 设为 `0`（关闭兜底轮询），
  或直接卸载 QQ 调课同步插件。

---

## v2.0.3 变更（调课联动加固）

- 所有联动日志统一前缀 `[调课中枢]`，可与 QQ 调课同步插件的日志一起筛选
- 连接「QQ 调课同步」时读取其桥接契约版本（当前 `2`，支持批量/互换）并打印；
  对方未声明版本时仅警告、仍尝试转发，两个插件可独立升级

---

## 考试模式全局免打扰（v2.1.0）

考试进行中（ExamClock 判定），ClassIsland 的上课/下课铃、自动化提醒都会被静默拦截，
**地震预警永远放行**。

- 实现：注册优先级 `-100000` 的 `INotificationConsumer`；来源 GUID 与名称通过反射读取
- 放行：`ticket.Cancel()` 退回提醒主机，交给下一个消费者播放
- 拦截：取消提醒的 `CompletedTokenSource`，静默完成
- 白名单默认：`B27C0AF3-C917-44DE-A61D-8010C3F3FB92`（地震预警）
- 联调开关：`examDndDryRun`（干跑）、`examDndForceActive`（强制考试中）、`examDndSelfTest`（开机自检）

日志示例：

```
[调课中枢] 考试静默：来源=地震预警（b27c0af3-...）→ 放行（白名单）
[调课中枢] 考试静默：来源=未知提供方（11111111-...）→ 拦截
```

> 注意：来源字段是 ClassIsland 的 internal 成员，本功能依赖反射；ClassIsland 大版本升级后需复测。