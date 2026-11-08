# 设置页实现说明（code-only 方案）

本目录的 `XiaoChengLinkSettingsPage.cs` 是「小橙看班联动」插件的设置页：
设置 → 插件 → 「小橙看班联动」。

## 为什么是纯 C# 控件，没有 .axaml

本机没有 .NET SDK / NuGet / Avalonia XAML 编译器（详见 `tools/build-env/REPORT.md`），
插件只能用 VS 自带 Roslyn csc 直编验证，而 **csc 不能编译 .axaml**。
因此设置页用代码构建 Avalonia 控件树（`SettingsPageBase.Content = ...`），
好处是能真实编译验证（0 error），代价是需要手写控件与事件。
以后有 SDK/CI 时可升级为 .axaml + ViewModel 绑定，步骤见 `docs/settings-page-plan.md` §6。

## 关键事实（Mono.Cecil 实测）

- `ClassIsland.Core.Abstractions.Controls.SettingsPageBase : Avalonia.Controls.UserControl`，
  abstract，protected 无参构造，无必须 override 的抽象成员。
- `[SettingsPageInfo]` 构造参数顺序是 **`(string id, string name, SettingsPageCategory category)`**，
  id 在前、name 在后（任务书示例顺序是笔误；id 参与 DI key/导航，必须稳定）。
- `SettingsPageCategory` 在 `ClassIsland.Core.Enums.SettingsWindow`，插件用 `External`。
- 注册：`ClassIsland.Core.Extensions.Registry.SettingsWindowRegistryExtensions.AddSettingsPage<T>()`，
  在 `Plugin.Initialize` 调用；内部按 id 注册为 keyed transient。

## 页面字段

processNames、windowTitlePatterns、keywords、keywordMatchMode、pollIntervalMs、
cooldownSeconds、triggerOnWindowAppear；保存写回 `MonitorOptions` →
`Normalize()` → `TrySave(PluginConfigFolder)` → `WindowScanner.ReloadTitlePatterns()`。

生效范围（不夸大）：标题正则立即生效；进程名/轮询间隔/冷却/窗口出现触发下一轮扫描生效；
关键词与匹配方式需重启插件（`KeywordMatcher` 为构造快照，见 docs 规划）。

## 编译

```powershell
# 在工作区根目录执行
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-env\build-plugin.ps1 `
  -InputFiles .\plugin\src -OutputDll .\plugin\bin\XiaoChengLink.dll `
  -ExtraRefs "CommunityToolkit.Mvvm.dll,UIAutomationClient.dll,UIAutomationTypes.dll,WindowsBase.dll,System.Text.RegularExpressions.dll,System.Text.Json.dll,System.Memory.dll,Avalonia.Base.dll,Avalonia.Controls.dll,Avalonia.Markup.dll,FluentAvalonia.dll"
```

> 注意：Avalonia 11.3.17 应用目录里没有 `Avalonia.Styling.dll` / `Avalonia.Layout.dll`
> （相关类型已合并进 `Avalonia.Base.dll`），不要照抄包含它们的引用列表。

## 未验证（需真机）

- 设置窗口是否显示「小橙看班联动」页、DI 构造函数注入是否生效；
- 保存后标题正则热重载、`settings.json` 写回的真实效果；
- 页面在 Fluent 主题下的视觉/布局。
