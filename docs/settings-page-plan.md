# 小橙看班联动插件设置页规划（Task-5）

- 插件：XiaoChengLink（小橙看班联动），ClassIsland 2.1.0.1 插件
- 页面：设置 → 插件 → 「小橙看班联动」（id：`xiaocheng.link.settings`，分类 `SettingsPageCategory.External`）
- 实现文件：`plugin/src/Settings/XiaoChengLinkSettingsPage.cs`（纯 C# 构建 Avalonia 控件，无 `.axaml`）
- 相关小改：`plugin/src/Monitoring/WindowScanner.cs`（新增 `public void ReloadTitlePatterns()`）、`plugin/src/Plugin.cs`（`services.AddSettingsPage<...>()`）
- 本次已验证：本机 csc 管线 **exit 0 / 0 error / 0 warning**，产物 50688 字节；Mono.Cecil 元数据校验通过
- 未验证（必须真机确认）：ClassIsland 设置窗口实际加载该页面、DI 构造函数注入、保存后热生效的真实效果

---

## 1. 为什么继承 `SettingsPageBase`

ClassIsland 2.1 的设置窗口用**注册表 + DI** 管理设置页，插件不能自己弹设置窗口，而应注册页面：

| 事实（Mono.Cecil 实测，见 `research/classisland-api/settings-page-metadata.txt`） | 结论 |
|---|---|
| `ClassIsland.Core.Abstractions.Controls.SettingsPageBase : Avalonia.Controls.UserControl`，abstract，protected 无参构造，无必须 override 的抽象成员 | 直接继承即可，页面本质是 Avalonia 控件；可 override `OnAttachedToVisualTree` 等 |
| `ClassIsland.Core.Attributes.SettingsPageInfo`，构造 `(string id, string name, SettingsPageCategory category)`，另有 4/5/6 参重载（可带图标 glyph / hideDefault） | `[SettingsPageInfo("xiaocheng.link.settings", "小橙看班联动", SettingsPageCategory.External)]` |
| `SettingsPageCategory` 枚举在 `ClassIsland.Core.Enums.SettingsWindow`：`Internal=0, External=1, About=2, Debug=3` | 插件页面用 `External`（出现在「设置 → 插件」类目） |
| `SettingsWindowRegistryExtensions.AddSettingsPage<T>()` 读取 `[SettingsPageInfo]`，按 `Id` 注册为 **keyed transient**（`AddKeyedTransient<SettingsPageBase, T>(id)`），并把 `SettingsPageInfo` 加入 `SettingsWindowRegistryService.Registered` | 在 `Plugin.Initialize` 中调用一次即可被设置窗口发现 |
| 设置页构造函数参数由宿主 DI 解析（真实插件样例：bywhite.mediaisland、SystemTools、gordon.dutyroster 等均构造函数注入 Plugin/Settings） | 页面构造函数注入插件已注册的单例 |

> ⚠ 参数顺序坑：`SettingsPageInfo` 第 1 个参数是 **id**、第 2 个才是 **name**（Cecil 读取的参数名 + 官方文档示例均如此）。
> 任务书示例 `[SettingsPageInfo("小橙看班联动", "xiaocheng.link.settings", ...)]` 顺序是反的；本实现按正确顺序写，避免导航/DI key 变成中文。

## 2. 为什么用「纯 C# 控件树」而不是 XAML

本机限制（已实测）：没有 .NET SDK、没有 NuGet、外网 TLS 被拦，插件只能用 VS 自带 Roslyn `csc.exe` 直编验证（`tools/build-env/build-plugin.ps1`）。而 **csc 不能编译 `.axaml`**（需要 Avalonia.Build.Tasks/MSBuild 的 XAML 编译任务）。

所以本设置页用 code-only 方式：在 C# 里 `new StackPanel / TextBox / Button / ...` 搭控件树，赋给 `SettingsPageBase.Content`。好处：

- 本机可以真实编译验证（0 error），不是“只写不验证”；
- 不依赖 XAML 资源/编译产物，插件 DLL 也更小；
- 后续修复字段/文案只需改 C#，csc 可快速回归。

代价：没有可视化预览、样式靠 Fluent 主题默认值、绑定需要手写事件（当前页面字段少，直接事件即可）。XAML 升级路径见 §6。

## 3. 页面结构与字段映射

页面从上到下：标题 → 说明 → 7 个配置项（标签 + 输入控件 + 灰色提示）→ 「保存并应用」按钮 → 状态/配置文件路径行。
根容器 `ScrollViewer`（超高时出现滚动条），内部 `StackPanel`。

| # | UI 控件 | 显示文案 | 写回的 `MonitorOptions` 字段 | `settings.json` 键 | 默认值 |
|---|---|---|---|---|---|
| 1 | `TextBox`（逗号分隔） | 目标进程名（不含 .exe） | `ProcessNames: string[]` | `processNames` | `["inArt_PC"]` |
| 2 | `TextBox`（逗号分隔，正则） | 目标窗口标题正则 | `WindowTitlePatterns: string[]` | `windowTitlePatterns` | `["消息提醒","班级课表"]` |
| 3 | `TextBox`（逗号分隔） | 关键词 | `Keywords: string[]` | `keywords` | 7 个示例词 |
| 4 | `ComboBox`（Contains / Regex） | 关键词匹配方式 | `KeywordMatchMode: string` | `keywordMatchMode` | `Contains` |
| 5 | `NumericUpDown`（500–60000，步进 100） | 轮询间隔（毫秒） | `PollIntervalMs: int` | `pollIntervalMs` | `1500` |
| 6 | `NumericUpDown`（0–86400，步进 10） | 冷却时间（秒） | `CooldownSeconds: int` | `cooldownSeconds` | `60` |
| 7 | `CheckBox` | 目标窗口出现时触发提醒 | `TriggerOnWindowAppear: bool` | `triggerOnWindowAppear` | `true` |

其余字段（`TriggerOnExistingWindows`、`NotificationDurationSeconds`、`MaskDurationSeconds`、`TextSources.*`）暂不在页面上暴露，仍可手动编辑 `settings.json`（读取逻辑不变），后续可按需扩充（见 §8）。

列表解析规则（`SplitList`）：支持英文/中文逗号、分号、换行分隔，去首尾空白、去空项；**允许留空**（进程名留空 = 不限制进程；标题正则留空 = 不按标题过滤；关键词留空 = 只做窗口出现触发）。

## 4. 数据流：控件 → options → settings.json → 监控热生效

```
用户改控件
   │ 点击「保存并应用」（UI 线程）
   ▼
ReadControlsIntoOptions()       // TextBox/NumericUpDown/ComboBox/CheckBox → MonitorOptions 字段
   ▼
options.Normalize()             // 去空/去重、数值钳制（500–60000 等）、keywordMatchMode 兜底
   ▼
WriteOptionsToControls()        // 把 Normalize 后的实际值回填控件（用户能看到真正生效的值）
   ▼
options.TrySave(plugin.PluginConfigFolder)   // 写 <插件配置目录>/settings.json（UTF-8 无 BOM，失败只记日志）
   ▼
scanner.ReloadTitlePatterns()   // 重新编译标题正则，整体替换正则快照（加锁）
   ▼
状态 TextBlock 提示成功/失败 + 配置文件路径
```

**热生效范围**（与现有监控实现严格对齐，不夸大）：

| 配置项 | 生效时机 | 原因 |
|---|---|---|
| `WindowTitlePatterns` | **立即** | 保存时调用 `WindowScanner.ReloadTitlePatterns()`，扫描器读新快照 |
| `ProcessNames` | 下一轮扫描 | `WindowScanner.IsProcessNameAllowed` 每轮读 `_options.ProcessNames` |
| `PollIntervalMs` | 下一轮循环 | `WindowTextMonitor.ExecuteAsync` 每轮 `_options.GetPollIntervalMs()` |
| `CooldownSeconds` | 下一轮扫描 | `WindowTextMonitor.TryEnterCooldown` 每次读 options |
| `TriggerOnWindowAppear` | 下一轮扫描 | 每轮读 options |
| `Keywords` / `KeywordMatchMode` | **需重启插件** | `KeywordMatcher` 构造时把关键词/模式做成快照；本次改动范围不含该文件。后续改进见 §8 |

后台监控还会用 `PluginLog` 输出每轮命中情况，设置页保存动作本身也会写一条 `设置页保存：...` 日志，便于核对。

## 5. 线程与保存时机

- 页面由设置窗口在 UI 线程构造；构造函数**只搭控件、只读内存里的 options**（不读文件、不阻塞），并整体 `try/catch`：控件搭建失败时降级为一个提示 `TextBlock`，绝不把异常抛给宿主设置窗口。
- 保存按钮 `Click` 在 Avalonia UI 线程执行。写回内存 → `Normalize()` → 写 `settings.json`（文件很小，同步写可接受）→ 重载正则 → 更新状态文本。整个处理器 `try/catch`，失败只记 `PluginLog.Error` 并在状态行显示原因。
- `WindowScanner.ReloadTitlePatterns()` 线程安全：先在锁外编译新 `Regex[]`，再在锁内整体替换引用；扫描线程 `IsTitleAllowed` 在锁内取一次快照后遍历，不会看到“半成品”列表。编译单条无效正则只 WARN 跳过，整体异常只 ERROR 并保留旧规则。
- 页面重新挂到可视化树（`OnAttachedToVisualTree`）时从共享 options 刷新控件显示（页面实例若被宿主复用不会显示旧值），异常只记 debug。
- 监控线程与页面共享同一个 `MonitorOptions` 单例；跨线程读写的是**引用字段**（数组/字符串/数值），对本场景足够安全（保存瞬间下一轮扫描读到新数组，无撕裂风险；不需要额外锁）。

## 6. XAML 升级路径（以后有 SDK / CI 时）

当前 code-only 是“本机无 XAML 编译器”的妥协。CI（GitHub Actions + .NET 8 SDK）或者装了 Avalonia 模板的机器上，可以平滑升级：

1. **页面拆分**：`Views/Settings/XiaoChengLinkSettingsPage.axaml` + `.axaml.cs`（`partial class`，继承 `SettingsPageBase`），构造函数里 `InitializeComponent()`；C# 文件保留 `[SettingsPageInfo]`。
2. **ViewModel**：`XiaoChengLinkSettingsViewModel : ObservableObject`，字段用 `[ObservableProperty]`（CommunityToolkit.Mvvm 已由宿主提供），列表字段可用 `ObservableCollection<string>` 或仍用逗号字符串换算。
3. **绑定**：`DataContext = new XiaoChengLinkSettingsViewModel(options, plugin, scanner)`；XAML 里 `Text="{Binding ProcessNamesText}"`、`SelectedIndex="{Binding MatchModeIndex}"`、`Command="{Binding SaveCommand}"`。
4. **保存逻辑复用**：把当前页面里的 `ReadControlsIntoOptions` / `Normalize` / `TrySave` / `ReloadTitlePatterns` 流程搬进 ViewModel（或抽成 `SettingsSaver` 服务），保证 code-only 与 XAML 两版行为一致。
5. **csproj**：`<UseAvalonia>` 由 ClassIsland.PluginSdk 提供（官方模板 `net8.0` + `ClassIsland.PluginSdk 2.1.0.1`），`.axaml` 会被自动编进 DLL；注意 **不要把 Avalonia 程序集打进插件输出**（宿主已提供，当前 csproj 的 `ExcludeAssets=runtime;native` 已处理）。
6. **样式**：优先用 FluentAvalonia 主题资源（`TextBlock Classes="h1"` 等，参考系统其余设置页），避免自造颜色，保证浅色/深色主题一致。
7. **回退**：XAML 版本上线前，code-only 版作为已知可编译的基线保留（可放 `Settings/CodeOnly/`），便于对比与应急。

## 7. 与 ClassIsland 设置窗口的集成方式

1. `Plugin.Initialize` 里注册（本次已加，且单独 `try/catch`，注册失败不影响提醒/监控）：
   ```csharp
   services.AddSettingsPage<XiaoChengLinkSettingsPage>();
   ```
2. `AddSettingsPage<T>` 在注册时反射读取 `[SettingsPageInfo]`：
   - 校验 `id` 非空、同 id 未重复，否则抛 `ArgumentException`；
   - 把 `SettingsPageInfo` 加入 `SettingsWindowRegistryService.Registered`（设置窗口据此显示左侧导航项）；
   - `services.AddKeyedTransient<SettingsPageBase, T>(info.Id)`，设置窗口导航到该 id 时由 DI 解析页面实例。
3. 页面构造函数注入的三个单例都在 `Plugin.Initialize` 注册过，且与后台监控共用：
   - `MonitorOptions`（`services.AddSingleton(options)`）
   - `XiaoChengLinkPlugin`（`services.AddSingleton(this)`，宿主也会 `AddSingleton(type, instance)`）
   - `WindowScanner`（`services.AddSingleton<WindowScanner>()`）
4. 若宿主没有按构造函数注入解析（**需实测**）：页面还有一个 public 无参构造，用 `ClassIsland.Shared.IAppHost.TryGetService<T>()` 服务定位兜底；都取不到时退化为只读提示页。
   - 依赖注入容器（Microsoft.Extensions.DependencyInjection 8）选择构造函数的规则已用 IL 核对：按参数个数从多到少尝试，**选第一个参数全部可解析的构造函数**（`CreateArgumentCallSites(..., throwIfCallSiteNotFound:false)`），因此参数最多的 3 参构造优先；只有它不可解析时才退到无参构造。
5. 页面显示在「设置 → 插件」类目（`SettingsPageCategory.External`），无需改宿主任何代码。

## 8. 测试步骤（真机）

前置：本机 ClassIsland 2.1.0.1 安装根 `C:\Users\36896\Desktop\old\ClassIsland_windows_x64_selfContained_folder_2.0.0.2_2\`，插件安装目录 `<安装根>\data\Plugins\xiaocheng.link\`，插件配置目录 `<安装根>\data\Config\Plugins\xiaocheng.link\`。

1. 构建：见 §9 编译命令；把 `plugin/bin/XiaoChengLink.dll` 与 `plugin/manifest.yml` 复制到 `<安装根>\data\Plugins\xiaocheng.link\`。
2. 启动 `ClassIsland.exe`，查看宿主日志：确认加载插件列表里有「小橙看班联动(xiaocheng.link,1.0.0.0)」；查看 `<配置目录>\logs\yyyyMMdd.log` 有“插件初始化完成”。若设置页注册失败，日志里应有“注册设置页失败”。
3. 打开 设置 → 插件 → 左侧应出现「小橙看班联动」；页面应显示 7 个字段、默认值与 `settings.json` 路径。**这一步是本次交付最主要的未验证点。**
4. 修改字段（如进程名加一个 `notepad`、轮询间隔改 3000、取消“窗口出现触发”），点「保存并应用」：状态行出现绿色“已保存，立即生效（标题正则已重载）…”。
5. 用文本编辑器打开 `<配置目录>\settings.json`：确认 `processNames` / `pollIntervalMs` / `triggerOnWindowAppear` 等已更新（camelCase、缩进）。
6. 观察插件日志：出现 `窗口标题正则已重载：N 条生效` 与 `设置页保存：...`；之后监控循环日志里的轮询间隔/配置应与新值一致。
7. 热重载验证：把「目标窗口标题正则」临时改成一个不含目标窗口的规则并保存 → 监控应不再识别到窗口；再改回来 → 恢复识别（无需重启宿主）。
8. 回归：重启 ClassIsland，确认设置持久化（页面显示上次保存的值、`settings.json` 未丢失）。
9. 异常路径：把 `settings.json` 手工改成非法 JSON 后重启 → 插件应回落到默认值并继续运行（设置页保存会写回合法 JSON）。

## 9. 本机编译验证（真实执行记录）

```powershell
# 在工作区根目录执行（ClassIsland 应用目录默认已内置在脚本里）
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-env\build-plugin.ps1 `
  -InputFiles .\plugin\src `
  -OutputDll .\plugin\bin\XiaoChengLink.dll `
  -ExtraRefs "CommunityToolkit.Mvvm.dll,UIAutomationClient.dll,UIAutomationTypes.dll,WindowsBase.dll,System.Text.RegularExpressions.dll,System.Text.Json.dll,System.Memory.dll,Avalonia.Base.dll,Avalonia.Controls.dll,Avalonia.Markup.dll,FluentAvalonia.dll"
```

- 任务书给出的 ExtraRefs 含 `Avalonia.Styling.dll,Avalonia.Layout.dll`：**这两个文件在 Avalonia 11.3.17 应用目录中不存在**（Layout/Styling 类型已合并进 `Avalonia.Base.dll`），照抄会直接报“以下引用在 AppDir 中找不到”。上面的命令是实际可用版本。
- 实际结果：`csc 退出码 0 / 错误数 0 / 警告数 0 / 产物 50688 bytes`，10 个源文件（新增 `plugin/src/Settings/XiaoChengLinkSettingsPage.cs`）。
- 日志：`plugin/bin/XiaoChengLink.build.log`（含逐行 csc 命令行，可复现）。
- Mono.Cecil 元数据校验（附加，非任务要求）：
  - `XiaoChengLink.Settings.XiaoChengLinkSettingsPage : ClassIsland.Core.Abstractions.Controls.SettingsPageBase`，public sealed；
  - `[SettingsPageInfo]` 构造函数参数 = `(id="xiaocheng.link.settings", name="小橙看班联动", category=1(External))`（用 Cecil 加 AssemblyResolver 读出，确认参数顺序正确）；
  - 两个 public 构造：`(MonitorOptions, XiaoChengLinkPlugin, WindowScanner)` 与 `()`；
  - `XiaoChengLink.Monitoring.WindowScanner.ReloadTitlePatterns()` 为 public 无参方法；
  - 引用版本与宿主一致：ClassIsland.Core 2.1.0.1、Avalonia 11.3.17.0、ClassIsland.Shared 2.1.0.1。

## 10. 风险与后续改进

**风险 / 未验证点**

1. **未真机验证设置窗口加载**（最高风险）：未能确认宿主设置窗口能解析页面、DI 构造函数注入生效、导航显示「小橙看班联动」。已从 IL 层面确认 DI 构造函数选择规则与注册 API 形态，但宿主实际行为需真机确认。
2. **id/name 顺序**：按 Cecil 参数名与官方文档修正为 (id, name)；若宿主某版本实际按 (name, id)（与元数据不符），导航名/DI key 会异常——可看宿主日志与设置窗口显示判断。
3. **关键词不能热生效**：`KeywordMatcher` 为构造快照，页面改了关键词需重启插件；状态行已如实提示。后续可给 `KeywordMatcher` 加 `Reload` 并在保存时调用（本次写入范围不允许改该文件）。
4. **文件同步写在 UI 线程**：`settings.json` 很小，实测风险低；若未来配置文件变大，改为 `Task.Run` + 回 UI 线程更新状态。
5. **主题适配**：`Brushes.Gray/SeaGreen/IndianRed` 是硬编码颜色，深色主题下对比度可能一般；XAML 升级时改用主题资源。
6. **code-only 无 XAML 编译器验证**：控件树运行期视觉/布局未截图验证（只保证编译与类型正确）。

**后续改进**

- 校验强化：保存时对每个窗口标题正则/Regex 模式关键词做 `new Regex(...)` 试编译，非法项在状态行逐条提示（不阻塞其它字段保存）。
- 增加字段：提醒正文时长、遮罩时长、文本来源开关（UIA/Win32/窗口标题）、`triggerOnExistingWindows`、日志级别；可用折叠面板分组，避免页面过长。
- 关键词热生效：给 `KeywordMatcher` 增加 `Reload(IEnumerable<string>, KeywordMatchKind)`（快照替换 + 锁），保存在写回 options 后调用。
- 国际化：`SettingsPageInfo.Name` 与页面文案目前硬编码中文；多语言可用宿主语言服务/资源方案，后续再定。
- “恢复默认值”按钮 + “测试提醒”按钮（点击后用 `ClassIslandNotifier` 发一条测试提醒，验证通知链路）。
- 若宿主提供设置项抽屉（`SettingsPageBase.OpenDrawer`），可把高级配置放进抽屉，主页面保持精简。
