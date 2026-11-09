// ─── XiaoChengLinkSettingsPage —— 插件设置页（纯 C# 构建 Avalonia 控件，无 XAML）──
//
// 为什么用「纯 C# 控件树」而不是 .axaml：
//   · 本机没有 .NET SDK / NuGet，也没有 Avalonia 的 XAML 编译任务（Avalonia.Build.Tasks），
//     项目只能用 VS 自带 Roslyn csc 直编验证（tools/build-env/build-plugin.ps1），而 csc
//     **不能**编译 .axaml；用纯 C# 搭建控件树才能在本机真实编译验证（0 error）。
//   · ClassIsland 2.1 的 UI 是 Avalonia 11.3.17，设置页基类
//     ClassIsland.Core.Abstractions.Controls.SettingsPageBase : Avalonia.Controls.UserControl
//     （abstract，protected 无参构造，无必须 override 的抽象成员 —— Mono.Cecil 已核对）。
//   · 以后有 SDK 时可平滑升级为 .axaml + ViewModel + 绑定，见 docs/settings-page-plan.md。
//
// 注册方式：本类上的 [SettingsPageInfo] 由 Plugin.Initialize 里的
//   services.AddSettingsPage<XiaoChengLinkSettingsPage>();
// 读取；AddSettingsPage 会按 info.Id 把页面注册为 keyed transient，设置窗口解析后显示在
//   「设置 → 插件」类目（SettingsPageCategory.External）。
//
// ⚠ 参数顺序（已用 Mono.Cecil 核对 + 官方文档示例确认，任务书里的示例顺序是笔误）：
//   SettingsPageInfo 构造函数签名是 (string id, string name, SettingsPageCategory category)：
//     - 第 1 个参数是 **id**（唯一标识，参与 DI key / 导航，必须稳定，如 xiaocheng.link.settings）
//     - 第 2 个参数是 **name**（设置窗口导航栏显示名，如「小橙看班联动」）
//   若按 (name, id) 填反，页面仍可能出现，但导航/DI key 会变成中文，属于脏数据，故这里按正确顺序写。
//
// 线程/异常约定：
//   · 构造函数只做控件搭建 + 从内存里的 options 读值，**不读文件、不做耗时操作**；
//     页面由宿主解析（keyed transient），构造抛异常可能影响设置窗口 → 全部 try/catch 兜底。
//   · 保存按钮的 Click 在 UI 线程执行：写回 options → Normalize() → TrySave(settings.json)
//     → scanner.ReloadTitlePatterns()。文件很小，同步写可接受；任何异常都只记日志 + 状态文本。
//   · 依赖注入失败时（【需实测】）退化：优先用构造函数注入；宿主若用其它方式实例化本页，
//     无参构造函数会用 ClassIsland.Shared.IAppHost 服务定位器兜底。
//
// 热生效范围（与现有监控实现对齐）：
//   · 立即生效：窗口标题正则（scanner.ReloadTitlePatterns() 整体替换正则快照）；
//   · 下一轮扫描生效：进程名 / 轮询间隔 / 冷却秒数 / 窗口出现触发（监控循环每轮读 options）；
//   · 需要重启插件：关键词列表与匹配方式（KeywordMatcher 在构造时做快照，当前实现不监听配置变化；
//     若要热生效需给 KeywordMatcher 增加 Reload，属于后续改进项，见规划文档）。

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Shared;
using XiaoChengLink.Logging;
using XiaoChengLink.Monitoring;

namespace XiaoChengLink.Settings;

/// <summary>
/// 小橙看班联动插件设置页。
/// 继承 ClassIsland 设置页基类（本质是 Avalonia UserControl），用纯 C# 构建控件树。
/// 说明：类必须是 public + 非 abstract，否则 AddSettingsPage&lt;T&gt; 的泛型约束/反射注册会失败。
/// </summary>
[SettingsPageInfo("xiaocheng.link.settings", "小橙看班联动", SettingsPageCategory.External)]
public sealed class XiaoChengLinkSettingsPage : SettingsPageBase
{
    private const string PageTitle = "小橙看班联动";
    private const string MatchModeContains = "Contains";
    private const string MatchModeRegex = "Regex";

    private static readonly string[] MatchModeOptions = { MatchModeContains, MatchModeRegex };

    private readonly MonitorOptions _options;
    private readonly XiaoChengLinkPlugin? _plugin;
    private readonly WindowScanner? _scanner;

    // 控件引用在 BuildPage 中赋值；全部可空，便于“UI 构建失败时降级”与防御性访问。
    private TextBox? _processNamesBox;
    private TextBox? _titlePatternsBox;
    private TextBox? _keywordsBox;
    private NumericUpDown? _pollIntervalBox;
    private NumericUpDown? _cooldownBox;
    private NumericUpDown? _visualThresholdBox;
    private NumericUpDown? _throttleBox;
    private CheckBox? _readBodyCheck;
    private TextBox? _dbPathBox;
    private NumericUpDown? _freshSecondsBox;
    private NumericUpDown? _bodyMaxCharsBox;
    private ComboBox? _matchModeCombo;
    private CheckBox? _triggerOnAppearCheck;
    private TextBlock? _statusText;

    /// <summary>
    /// 主构造函数（优先路径）：由 ClassIsland 的依赖注入容器解析。
    /// 这三个服务都在 Plugin.Initialize 里注册为单例，且与后台监控共用同一实例：
    ///   · MonitorOptions：配置模型（设置页保存后监控立即可见）
    ///   · XiaoChengLinkPlugin：取 PluginConfigFolder（settings.json 所在目录）
    ///   · WindowScanner：保存后 ReloadTitlePatterns() 热重载标题正则
    /// 【需实测】宿主设置窗口是否真的按构造函数注入解析本页（本机只有编译验证，无真机 UI 验证）。
    /// </summary>
    public XiaoChengLinkSettingsPage(MonitorOptions options, XiaoChengLinkPlugin plugin, WindowScanner scanner)
    {
        _options = options ?? CreateFallbackOptions();
        _plugin = plugin;
        _scanner = scanner;
        BuildPageSafely();
    }

    /// <summary>
    /// 无参兜底构造（【需实测】）：仅当宿主没有按构造函数注入解析本页时才会用到。
    /// 通过 ClassIsland.Shared.IAppHost 服务定位器取注册过的单例；取不到就退化为只读提示页，
    /// 绝不把异常抛给宿主（设置窗口是宿主核心 UI，插件页面不能拖垮它）。
    /// 注：符合规范的依赖注入容器会优先选择参数最多的可解析构造函数，因此正常情况下不会走到这里。
    /// </summary>
    public XiaoChengLinkSettingsPage()
    {
        _options = TryResolveService<MonitorOptions>() ?? CreateFallbackOptions();
        _plugin = TryResolveService<XiaoChengLinkPlugin>();
        _scanner = TryResolveService<WindowScanner>();
        BuildPageSafely();
    }

    /// <summary>
    /// 页面重新挂到可视化树时从共享 options 刷新控件值（页面实例若被宿主复用可避免显示旧值）。
    /// 只读内存、不读文件，异常只记 debug。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        try
        {
            WriteOptionsToControls();
        }
        catch (Exception ex)
        {
            PluginLog.Debug("设置页挂载时刷新控件失败（已忽略）：" + ex.Message);
        }
    }

    // ── 服务定位兜底 ────────────────────────────────────────────────────────

    private static T? TryResolveService<T>()
        where T : class
    {
        try
        {
            return IAppHost.TryGetService<T>();
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"设置页服务定位失败（{typeof(T).Name}，已忽略）：{ex.Message}");
            return null;
        }
    }

    private static MonitorOptions CreateFallbackOptions()
    {
        var options = new MonitorOptions();
        options.Normalize();
        return options;
    }

    // ── UI 构建 ────────────────────────────────────────────────────────────

    private void BuildPageSafely()
    {
        try
        {
            Content = BuildPage();
        }
        catch (Exception ex)
        {
            // 控件搭建失败（例如 Avalonia 主题/资源异常）也不能炸宿主：降级为一个提示文本。
            PluginLog.Error("构建设置页 UI 失败（已降级为提示页）", ex);
            try
            {
                Content = new TextBlock
                {
                    Text = "「小橙看班联动」设置页初始化失败：" + ex.Message +
                           "\n请查看插件日志，或直接编辑插件配置目录下的 settings.json。",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24),
                };
            }
            catch
            {
                // 连提示页都建不出来时只能放弃（宿主会显示空白页，但不会崩）。
            }
        }
    }

    /// <summary>构建整页控件树（纯代码，等价于一段 .axaml）。</summary>
    private Control BuildPage()
    {
        var root = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Margin = new Thickness(24, 16, 24, 24),
        };

        root.Children.Add(new TextBlock
        {
            Text = PageTitle,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });

        root.Children.Add(new TextBlock
        {
            Text = "配置「小橙看班」(inArt_PC) 窗口监控规则。保存后：窗口标题正则立即重载；" +
                   "进程名、轮询间隔、冷却时间、窗口出现触发、画面变化阈值、全局节流从下一轮扫描起生效；" +
                   "关键词与匹配方式在插件重启后生效（当前实现为单例快照）。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 10),
        });

        // 1) 目标进程名
        root.Children.Add(FieldLabel("目标进程名（逗号分隔，不含 .exe）"));
        _processNamesBox = new TextBox
        {
            Text = JoinList(_options.ProcessNames),
            Watermark = "inArt_PC",
            Margin = new Thickness(0, 0, 0, 4),
        };
        root.Children.Add(_processNamesBox);
        root.Children.Add(FieldHint("默认 inArt_PC（小橙看班）。留空表示不限制进程。"));

        // 2) 窗口标题正则
        root.Children.Add(FieldLabel("目标窗口标题正则（逗号分隔）"));
        _titlePatternsBox = new TextBox
        {
            Text = JoinList(_options.WindowTitlePatterns),
            Watermark = "消息提醒, 班级课表",
            Margin = new Thickness(0, 0, 0, 4),
        };
        root.Children.Add(_titlePatternsBox);
        root.Children.Add(FieldHint("对顶层窗口标题做不区分大小写的正则匹配；留空表示该进程所有顶层窗口都监控。保存后立即重载。"));

        // 3) 关键词
        root.Children.Add(FieldLabel("关键词（逗号分隔）"));
        _keywordsBox = new TextBox
        {
            Text = JoinList(_options.Keywords),
            Watermark = "消息, 提醒, 作业, 考试",
            AcceptsReturn = false,
            Margin = new Thickness(0, 0, 0, 4),
        };
        root.Children.Add(_keywordsBox);
        root.Children.Add(FieldHint("Contains 模式为子串匹配；Regex 模式下每个关键词是一条正则表达式。修改后需重启插件生效。"));

        // 4) 匹配方式
        root.Children.Add(FieldLabel("关键词匹配方式"));
        _matchModeCombo = new ComboBox
        {
            ItemsSource = MatchModeOptions,
            SelectedIndex = _options.ResolveKeywordMatchMode() == KeywordMatchKind.Regex ? 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 200,
            Margin = new Thickness(0, 0, 0, 8),
        };
        root.Children.Add(_matchModeCombo);
        root.Children.Add(FieldHint("Contains：不区分大小写的子串匹配（默认）；Regex：把每个关键词当正则。修改后需重启插件生效。"));

        // 5) 轮询间隔
        root.Children.Add(FieldLabel("轮询间隔（毫秒）"));
        _pollIntervalBox = new NumericUpDown
        {
            Minimum = 500,
            Maximum = 60000,
            Increment = 100,
            Value = _options.GetPollIntervalMs(),
            FormatString = "0",
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 200,
            Margin = new Thickness(0, 0, 0, 8),
        };
        root.Children.Add(_pollIntervalBox);
        root.Children.Add(FieldHint("范围 500–60000，默认 1500。保存后从下一轮扫描起生效。"));

        // 6) 冷却时间
        root.Children.Add(FieldLabel("冷却时间（秒）"));
        _cooldownBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 86400,
            Increment = 10,
            Value = _options.CooldownSeconds,
            FormatString = "0",
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 200,
            Margin = new Thickness(0, 0, 0, 8),
        };
        root.Children.Add(_cooldownBox);
        root.Children.Add(FieldHint("同一窗口同一文本在冷却时间内不重复提醒；默认 60 秒。"));

        // 7) 画面变化阈值（太灵敏/太迟钝就调这里）
        root.Children.Add(FieldLabel("画面变化阈值（%，越小越灵敏）"));
        _visualThresholdBox = new NumericUpDown
        {
            Minimum = 0.1m,
            Maximum = 50m,
            Increment = 0.1m,
            Value = (decimal)_options.VisualChangeThresholdPercent,
            FormatString = "0.0",
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 200,
            Margin = new Thickness(0, 0, 0, 8),
        };
        root.Children.Add(_visualThresholdBox);
        root.Children.Add(FieldHint(
            "判定「窗口画面变化」所需的变化比例，默认 0.4%。" +
            "太灵敏（鼠标划过、界面动画、滚动也触发）→ 调大到 1.0–3.0；" +
            "太迟钝（新消息来了不触发）→ 调小到 0.2–0.3。范围 0.1–50，保存后从下一轮扫描起生效。"));

        // 8) 全局提醒节流（防连发）
        root.Children.Add(FieldLabel("全局提醒节流（秒）"));
        _throttleBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 600,
            Increment = 1,
            Value = _options.NotificationThrottleSeconds,
            FormatString = "0",
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 200,
            Margin = new Thickness(0, 0, 0, 8),
        };
        root.Children.Add(_throttleBox);
        root.Children.Add(FieldHint(
            "任一提醒发出后，N 秒内不再发下一条，默认 12 秒。" +
            "觉得弹得太频繁 → 调大（如 20–60）；希望同一时间多条都弹 → 调小或设为 0（0=关闭节流）。" +
            "保存后从下一轮扫描起生效。"));

        // 9) 消息正文（本地数据库）
        _readBodyCheck = new CheckBox
        {
            Content = "从本地数据库读取消息正文（推荐；全屏播报/消息提醒的正文来源）",
            IsChecked = _options.ReadMessageBodyFromDatabase,
            Margin = new Thickness(0, 4, 0, 8),
        };
        root.Children.Add(_readBodyCheck);
        root.Children.Add(FieldHint(
            "小橙看班把消息/播报缓存在 Data.idb（Jet 数据库）。开启后，插件会在触发时用 32 位 PowerShell 只读查询，" +
            "把标题/正文附到 ClassIsland 提醒里；关闭则只显示通用触发提示。"));

        root.Children.Add(FieldLabel("数据库路径（留空 = 自动查找）"));
        _dbPathBox = new TextBox
        {
            Text = _options.MessageDatabasePath ?? string.Empty,
            Watermark = @"留空 = 自动从 inArt_PC.exe 目录找 Data.idb（如 D:\inArtBoard\Data.idb）",
            Margin = new Thickness(0, 0, 0, 8),
        };
        root.Children.Add(_dbPathBox);
        root.Children.Add(FieldHint(
            "通常不用改；如果小橙看班装到其它目录、或你想读别的 Data.idb，可在这里指定完整路径。"));

        root.Children.Add(FieldLabel("正文新鲜度（秒，防串台）"));
        _freshSecondsBox = new NumericUpDown
        {
            Minimum = 1m,
            Maximum = 600m,
            Increment = 5m,
            Value = _options.MessageBodyFreshSeconds,
            FormatString = "0",
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 200,
            Margin = new Thickness(0, 0, 0, 8),
        };
        root.Children.Add(_freshSecondsBox);
        root.Children.Add(FieldHint(
            "只把触发前最近 N 秒内写入数据库的记录当作本次正文，默认 30 秒。" +
            "全屏播报的 WelcomeWord 行只有约 14 秒生命周期，靠这个窗口+重试避免拿到旧消息；" +
            "如果偶尔正文为空可适当调大（如 60）。"));

        root.Children.Add(FieldLabel("正文最大长度（字符）"));
        _bodyMaxCharsBox = new NumericUpDown
        {
            Minimum = 20m,
            Maximum = 2000m,
            Increment = 20m,
            Value = _options.MessageBodyMaxChars,
            FormatString = "0",
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 200,
            Margin = new Thickness(0, 0, 0, 8),
        };
        root.Children.Add(_bodyMaxCharsBox);
        root.Children.Add(FieldHint("超出长度会截断并加省略号；默认 300。"));

        // 9) 窗口出现触发
        _triggerOnAppearCheck = new CheckBox
        {
            Content = "目标窗口出现时触发提醒（UIA 读不到正文时的降级触发）",
            IsChecked = _options.TriggerOnWindowAppear,
            Margin = new Thickness(0, 4, 0, 8),
        };
        root.Children.Add(_triggerOnAppearCheck);

        // 8) 保存按钮 + 状态行
        var saveButton = new Button
        {
            Content = "保存并应用",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(18, 8, 18, 8),
            Margin = new Thickness(0, 8, 0, 8),
        };
        saveButton.Click += OnSaveClick;
        root.Children.Add(saveButton);

        _statusText = new TextBlock
        {
            Text = BuildConfigPathHint(),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0),
        };
        root.Children.Add(_statusText);

        return new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    // ── 控件小工具 ──────────────────────────────────────────────────────────

    private static TextBlock FieldLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 6, 0, 4),
        };
    }

    private static TextBlock FieldHint(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
    }

    private string BuildConfigPathHint()
    {
        try
        {
            var folder = _plugin?.PluginConfigFolder;
            return string.IsNullOrWhiteSpace(folder)
                ? "配置文件：<插件配置目录>/settings.json（插件实例不可用，无法确定实际路径）"
                : "配置文件：" + System.IO.Path.Combine(folder, MonitorOptions.FileName);
        }
        catch (Exception ex)
        {
            PluginLog.Debug("读取插件配置目录失败（已忽略）：" + ex.Message);
            return "配置文件：<插件配置目录>/settings.json";
        }
    }

    private static string JoinList(string[]? values)
    {
        return values is null || values.Length == 0 ? string.Empty : string.Join(", ", values);
    }

    // ── 保存逻辑 ────────────────────────────────────────────────────────────

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            ReadControlsIntoOptions();
            _options.Normalize();
            // 把 Normalize() 钳制/去重后的值回填到控件，用户能看到实际生效的配置。
            WriteOptionsToControls();

            var configFolder = _plugin?.PluginConfigFolder;
            var saved = _options.TrySave(configFolder);

            var scannerReloaded = false;
            if (_scanner is not null)
            {
                try
                {
                    _scanner.ReloadTitlePatterns();
                    scannerReloaded = true;
                }
                catch (Exception ex)
                {
                    // ReloadTitlePatterns 内部已兜底，这里再防一层（页面绝不能把异常抛给宿主）。
                    PluginLog.Error("设置页保存后重载标题正则失败（已忽略）", ex);
                }
            }

            PluginLog.Info(
                $"设置页保存：saved={saved} 进程=[{string.Join(", ", _options.ProcessNames)}] " +
                $"标题正则=[{string.Join(", ", _options.WindowTitlePatterns)}] 关键词={_options.Keywords.Length}个 " +
                $"模式={_options.KeywordMatchMode} 轮询={_options.PollIntervalMs}ms " +
                $"窗口出现触发={_options.TriggerOnWindowAppear} 冷却={_options.CooldownSeconds}s " +
                $"画面阈值={_options.VisualChangeThresholdPercent:F2}% 全局节流={_options.NotificationThrottleSeconds}s " +
                $"正文读库={_options.ReadMessageBodyFromDatabase} 新鲜度={_options.MessageBodyFreshSeconds}s 正文长度={_options.MessageBodyMaxChars} " +
                $"标题正则重载={scannerReloaded}");

            if (saved)
            {
                SetStatus(
                    "已保存，立即生效（标题正则已重载）。进程名/轮询间隔/冷却时间/窗口出现触发/画面变化阈值/全局节流/消息正文从下一轮扫描起生效；" +
                    "关键词与匹配方式需重启插件后生效。",
                    success: true);
            }
            else
            {
                SetStatus(
                    (scannerReloaded
                        ? "已应用到内存并重载标题正则，但写入 settings.json 失败（详见插件日志）。"
                        : "已应用到内存，但写入 settings.json 失败且窗口扫描器不可用（详见插件日志）。") +
                    "重启 ClassIsland 后本次修改会丢失。",
                    success: false);
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error("设置页保存失败（已捕获，不影响宿主）", ex);
            SetStatus("保存失败：" + ex.Message, success: false);
        }
    }

    private void ReadControlsIntoOptions()
    {
        _options.ProcessNames = SplitList(_processNamesBox?.Text);
        _options.WindowTitlePatterns = SplitList(_titlePatternsBox?.Text);
        _options.Keywords = SplitList(_keywordsBox?.Text);

        if (_pollIntervalBox?.Value is decimal pollInterval)
        {
            _options.PollIntervalMs = (int)pollInterval;
        }

        if (_cooldownBox?.Value is decimal cooldown)
        {
            _options.CooldownSeconds = (int)cooldown;
        }

        if (_visualThresholdBox?.Value is decimal visualThreshold)
        {
            _options.VisualChangeThresholdPercent = (double)visualThreshold;
        }

        if (_throttleBox?.Value is decimal throttle)
        {
            _options.NotificationThrottleSeconds = (int)throttle;
        }

        _options.ReadMessageBodyFromDatabase = _readBodyCheck?.IsChecked ?? _options.ReadMessageBodyFromDatabase;
        _options.MessageDatabasePath = (_dbPathBox?.Text ?? string.Empty).Trim();

        if (_freshSecondsBox?.Value is decimal freshSeconds)
        {
            _options.MessageBodyFreshSeconds = (int)freshSeconds;
        }

        if (_bodyMaxCharsBox?.Value is decimal bodyMaxChars)
        {
            _options.MessageBodyMaxChars = (int)bodyMaxChars;
        }

        _options.KeywordMatchMode = _matchModeCombo?.SelectedIndex == 1 ? MatchModeRegex : MatchModeContains;
        _options.TriggerOnWindowAppear = _triggerOnAppearCheck?.IsChecked ?? _options.TriggerOnWindowAppear;
    }

    private void WriteOptionsToControls()
    {
        if (_processNamesBox is not null)
        {
            _processNamesBox.Text = JoinList(_options.ProcessNames);
        }

        if (_titlePatternsBox is not null)
        {
            _titlePatternsBox.Text = JoinList(_options.WindowTitlePatterns);
        }

        if (_keywordsBox is not null)
        {
            _keywordsBox.Text = JoinList(_options.Keywords);
        }

        if (_pollIntervalBox is not null)
        {
            _pollIntervalBox.Value = _options.GetPollIntervalMs();
        }

        if (_cooldownBox is not null)
        {
            _cooldownBox.Value = _options.CooldownSeconds;
        }

        if (_visualThresholdBox is not null)
        {
            _visualThresholdBox.Value = (decimal)_options.VisualChangeThresholdPercent;
        }

        if (_throttleBox is not null)
        {
            _throttleBox.Value = _options.NotificationThrottleSeconds;
        }

        if (_readBodyCheck is not null)
        {
            _readBodyCheck.IsChecked = _options.ReadMessageBodyFromDatabase;
        }

        if (_dbPathBox is not null)
        {
            _dbPathBox.Text = _options.MessageDatabasePath ?? string.Empty;
        }

        if (_freshSecondsBox is not null)
        {
            _freshSecondsBox.Value = _options.MessageBodyFreshSeconds;
        }

        if (_bodyMaxCharsBox is not null)
        {
            _bodyMaxCharsBox.Value = _options.MessageBodyMaxChars;
        }

        if (_matchModeCombo is not null)
        {
            _matchModeCombo.SelectedIndex = _options.ResolveKeywordMatchMode() == KeywordMatchKind.Regex ? 1 : 0;
        }

        if (_triggerOnAppearCheck is not null)
        {
            _triggerOnAppearCheck.IsChecked = _options.TriggerOnWindowAppear;
        }
    }

    private void SetStatus(string message, bool success)
    {
        try
        {
            if (_statusText is null)
            {
                return;
            }

            _statusText.Text = message;
            _statusText.Foreground = success ? Brushes.SeaGreen : Brushes.IndianRed;
        }
        catch (Exception ex)
        {
            PluginLog.Debug("更新设置页状态文本失败（已忽略）：" + ex.Message);
        }
    }

    /// <summary>
    /// 把逗号/分号/换行分隔的文本拆成字符串数组（去空、去首尾空白）。
    /// 中文逗号、中文分号也支持，方便用户从文档里直接粘贴。
    /// 注意：空数组是合法配置（进程名留空=不限制进程；标题正则留空=不按标题过滤）。
    /// </summary>
    private static string[] SplitList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var separators = new[] { ',', '，', ';', '；', '\n', '\r' };
        var result = new List<string>();
        foreach (var piece in text.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = piece.Trim();
            if (trimmed.Length > 0)
            {
                result.Add(trimmed);
            }
        }

        return result.ToArray();
    }
}
