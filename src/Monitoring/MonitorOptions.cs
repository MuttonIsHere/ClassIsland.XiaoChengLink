// ─── MonitorOptions —— 插件配置模型（settings.json）──────────────────────────
// 配置文件：<PluginConfigFolder>/settings.json
//   · 首次运行不存在时：写入默认配置并记日志（不是 fatal，插件继续用内存默认值）。
//   · 解析失败时：记 Error 日志并回落到默认值（绝不让配置问题拖垮插件/宿主）。
//   · 字段名使用 camelCase；读取时大小写不敏感、允许注释与尾逗号（便于手工编辑）。
// 【需实测】PluginConfigFolder 的实际路径；写入/读取 UTF-8。
// 依赖：仅 BCL（System.Text.Json）。csc 直编时需要额外引用
//       System.Text.Json.dll + System.Memory.dll（见 README 的构建命令）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XiaoChengLink.Logging;

namespace XiaoChengLink.Monitoring;

/// <summary>
/// 关键词匹配方式。
/// （注意：类型名用 Kind，配置字段名仍按任务要求叫 keywordMatchMode，避免同名成员遮蔽。）
/// </summary>
public enum KeywordMatchKind
{
    /// <summary>子串包含（不区分大小写）。默认。</summary>
    Contains = 0,

    /// <summary>正则匹配（每条 keyword 作为一个正则表达式）。</summary>
    Regex = 1,
}

/// <summary>文本来源开关。</summary>
public sealed class TextSourceOptions
{
    /// <summary>
    /// 是否用 UI Automation 读取窗口内文本。
    /// 对本项目目标程序“小橙看班”(inArt_PC，Delphi/VCL 自绘) **无效**：
    /// 本机实测 UIA/MSAA 只能拿到顶层窗口标题（见 research/uia/*.json），
    /// 且对 Delphi 窗口做跨进程 UIA 读取有卡死风险，故默认关闭；
    /// 需要监控其它标准 Win32/WPF/浏览器类窗口时可在 settings.json 里改为 true。
    /// </summary>
    public bool Uia { get; set; } = false;

    /// <summary>
    /// 是否用 EnumChildWindows + GetWindowText 读取子窗口文本。
    /// Delphi 自绘窗口通常没有文本子窗口，实测可能为空。
    /// </summary>
    public bool Win32 { get; set; } = true;

    /// <summary>
    /// 是否把顶层窗口标题作为文本来源。
    /// 对“小橙看班”这是本机唯一实测有效的信息源（如「消息提醒」「班级课表」）。
    /// </summary>
    public bool WindowTitle { get; set; } = true;
}

/// <summary>插件配置（对应 settings.json）。</summary>
public sealed class MonitorOptions
{
    /// <summary>默认文件名（位于插件配置目录下）。</summary>
    public const string FileName = "settings.json";

    private const string DefaultProcessName = "inArt_PC";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = true,
    };

    /// <summary>目标进程名（不含 .exe，不区分大小写）。默认 ["inArt_PC"]；留空表示不限制进程。</summary>
    public string[] ProcessNames { get; set; } = new[] { DefaultProcessName };

    /// <summary>
    /// 目标窗口标题正则（对顶层窗口标题做 Regex 匹配，不区分大小写）。
    /// 默认 ["消息提醒", "班级课表"]；留空表示该进程的所有顶层窗口都监控。
    /// </summary>
    public string[] WindowTitlePatterns { get; set; } = new[] { "消息提醒", "班级课表" };

    /// <summary>
    /// 目标窗口**类名**正则（对 GetClassName 结果匹配，不区分大小写）。
    /// 对 Delphi/VCL 程序（如小橙看班）尤其有用：很多通知/弹出窗体标题为空或标题不变，
    /// 但类名带稳定前缀（FMTfrmXxx / TFormXxx / TfrmXxx）。默认匹配这三类前缀。
    /// 标题与类名是“或”关系；两者都为空 = 该进程所有顶层窗口都监控。
    /// </summary>
    public string[] WindowClassPatterns { get; set; } = new[] { "^FMTfrm", "^TForm", "^Tfrm" };

    /// <summary>
    /// 要忽略的窗口类名正则（不参与任何触发）。默认忽略小橙看班的托盘图标/隐藏应用窗，
    /// 避免程序启动时图标窗口出现造成误报；标题/类名正则仍可单独配置。
    /// </summary>
    public string[] IgnoreWindowClassPatterns { get; set; } = new[] { "^FMTfrmIcon$", "^TfrmTrayIcon$", "^TFMAppClass$" };

    /// <summary>
    /// 关键词是否也匹配“窗口标题”。默认 false：小橙看班标题恒为「消息提醒」，
    /// 标题匹配关键词会在每次新窗口出现时误报；关键词应只匹配 UIA/子窗口等真实文本。
    /// 对其它程序如需标题关键词匹配可改为 true。
    /// </summary>
    public bool KeywordMatchWindowTitle { get; set; } = false;

    /// <summary>
    /// 全局提醒节流（秒）：任一提醒发出后，此时长内不再发下一条，避免程序重启/多窗口出现时
    /// 连发多条提醒。默认 12 秒；0 = 关闭。
    /// </summary>
    public int NotificationThrottleSeconds { get; set; } = 12;

    /// <summary>
    /// 是否启用“界面内容变化”触发（对可见窗口做截图哈希，不做 OCR）。
    /// 小橙看班的「消息提醒」窗口是常驻窗口、正文自绘且 UIA 读不到，窗口不消失时只能靠
    /// 画面变化（如未读数字/内容刷新）来判定“有新消息”。默认 true。
    /// </summary>
    public bool TriggerOnVisualChange { get; set; } = true;

    /// <summary>画面变化判定阈值：采样网格中亮度差超限的像素占比（百分比）。默认 0.4（即 0.4%）。</summary>
    public double VisualChangeThresholdPercent { get; set; } = 0.4;

    /// <summary>参与画面变化判定的最小窗口面积（像素）：太小的图标/提示窗不参与，避免误报。默认 10000。</summary>
    public int VisualChangeMinAreaPixels { get; set; } = 10000;

    /// <summary>扫描诊断日志间隔（秒）；0 = 关闭。默认 30 秒打印一次扫描统计，便于排查“没触发”。</summary>
    public int DiagnosticLogIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// 是否从“小橙看班”本地数据库（Data.idb）读取最新消息正文，附加到 ClassIsland 提醒中。默认 true。
    /// 若读取失败（数据库不存在/版本变化/权限问题），自动退回通用提示，不影响触发。
    /// </summary>
    public bool ReadMessageBodyFromDatabase { get; set; } = true;

    /// <summary>本地数据库路径；留空 = 自动从运行中的 inArt_PC 进程目录查找 Data.idb。</summary>
    public string MessageDatabasePath { get; set; } = string.Empty;

    /// <summary>只接受最近 N 秒内的消息作为正文；0 = 不限制（默认 3600 秒）。</summary>
    public int MessageBodyMaxAgeSeconds { get; set; } = 3600;

    /// <summary>
    /// 消息“新鲜度”窗口（秒）：只把触发前最近 N 秒内写入数据库的记录当作本次提醒的正文。
    /// 全屏播报（FMTfrmText2Voice）写入 WelcomeWord 后约 14 秒删除该行，默认 30 秒可覆盖写入延迟。
    /// </summary>
    public int MessageBodyFreshSeconds { get; set; } = 30;

    /// <summary>正文最大字符数（超出截断），默认 300。</summary>
    public int MessageBodyMaxChars { get; set; } = 300;

    /// <summary>
    /// 调课联动兜底轮询间隔（秒）。0 = 关闭。
    /// 开启后即使窗口没有触发提醒，也会按该间隔读一次数据库，把可能是调课的消息
    /// 转交给「QQ 调课同步」插件解析（默认 30 秒，避免频繁复制数据库的开销）。
    /// </summary>
    public int TiaokeLinkPollSeconds { get; set; } = 30;

    // ── 语音对讲（录音发到电脑后播放）──────────────────────────────────────
    /// <summary>是否启用语音对讲状态监控/组件/开始结束提醒。默认 true。</summary>
    public bool VoiceIntercomEnabled { get; set; } = true;

    /// <summary>判定为语音对讲中的窗口类名正则（默认 FMTfrmSpeaker；实测播放录音时可见）。</summary>
    public string[] VoiceIntercomWindowClassPatterns { get; set; } = { "^FMTfrmSpeaker$" };

    /// <summary>连续检测到对讲窗口多少秒才算开始（防抖），默认 2 秒。</summary>
    public int VoiceIntercomStartDebounceSeconds { get; set; } = 2;

    /// <summary>连续检测不到对讲窗口多少秒才算结束（防抖），默认 3 秒。</summary>
    public int VoiceIntercomEndDebounceSeconds { get; set; } = 3;

    /// <summary>组件空闲时显示的文字。</summary>
    public string VoiceIntercomIdleText { get; set; } = "空闲";

    /// <summary>组件对讲中显示的文字。</summary>
    public string VoiceIntercomActiveText { get; set; } = "🔴 语音对讲中";

    /// <summary>组件空闲文字颜色（#RRGGBB）。</summary>
    public string VoiceIntercomIdleColor { get; set; } = "#9CA3AF";

    /// <summary>组件对讲中文字颜色（#RRGGBB）。</summary>
    public string VoiceIntercomActiveColor { get; set; } = "#EF4444";

    /// <summary>开始对讲提醒的正文。</summary>
    public string VoiceIntercomStartNotificationText { get; set; } = "老师正在说话";

    /// <summary>结束对讲提醒的正文。</summary>
    public string VoiceIntercomEndNotificationText { get; set; } = "对讲已结束";

    /// <summary>开始提醒颜色（#RRGGBB）。</summary>
    public string VoiceIntercomStartNotificationColor { get; set; } = "#EF4444";

    /// <summary>结束提醒颜色（#RRGGBB）。</summary>
    public string VoiceIntercomEndNotificationColor { get; set; } = "#10B981";

    /// <summary>轮询间隔（毫秒），默认 1500。运行时会被钳制到 [500, 60000]。</summary>
    public int PollIntervalMs { get; set; } = 1500;

    /// <summary>
    /// 关键词列表。默认给出一组常见校园通知词作为示例。
    /// Contains 模式下为子串；Regex 模式下每条都是一个正则表达式。
    /// </summary>
    public string[] Keywords { get; set; } = new[] { "消息", "提醒", "作业", "考试", "通知", "点名", "请假" };

    /// <summary>关键词匹配方式：Contains（默认）/ Regex。</summary>
    public string KeywordMatchMode { get; set; } = "Contains";

    /// <summary>
    /// 目标窗口从“不存在/不可见”变为“存在且可见”时，是否触发“窗口出现”通知。默认 true。
    /// 对小橙看班（UIA 读不到正文）这是主要的降级触发方式。
    /// </summary>
    public bool TriggerOnWindowAppear { get; set; } = true;

    /// <summary>
    /// 插件启动时已经存在的窗口是否也当作“窗口出现”处理。默认 false（先建立基线，避免启动即误报）。
    /// </summary>
    public bool TriggerOnExistingWindows { get; set; } = false;

    /// <summary>同一窗口同一文本 hash（或窗口出现事件）的冷却秒数，默认 60。</summary>
    public int CooldownSeconds { get; set; } = 60;

    /// <summary>提醒正文（Overlay）显示秒数，默认 8。</summary>
    public int NotificationDurationSeconds { get; set; } = 8;

    /// <summary>提醒遮罩（Mask）显示秒数，默认 3。</summary>
    public int MaskDurationSeconds { get; set; } = 3;

    /// <summary>文本来源开关。</summary>
    public TextSourceOptions TextSources { get; set; } = new();

    /// <summary>把数值字段钳制到安全范围、过滤空字符串项（原地修改）。</summary>
    public void Normalize()
    {
        ProcessNames = NormalizeArray(ProcessNames, new[] { DefaultProcessName });
        WindowTitlePatterns = NormalizeArray(WindowTitlePatterns, Array.Empty<string>());
        WindowClassPatterns = NormalizeArray(WindowClassPatterns, Array.Empty<string>());
        IgnoreWindowClassPatterns = NormalizeArray(IgnoreWindowClassPatterns, Array.Empty<string>());
        Keywords = NormalizeArray(Keywords, Array.Empty<string>());

        VisualChangeThresholdPercent = Math.Clamp(VisualChangeThresholdPercent, 0.01, 50.0);
        VisualChangeMinAreaPixels = Math.Clamp(VisualChangeMinAreaPixels, 100, 4_000_000);
        DiagnosticLogIntervalSeconds = Math.Clamp(DiagnosticLogIntervalSeconds, 0, 3600);
        NotificationThrottleSeconds = Math.Clamp(NotificationThrottleSeconds, 0, 600);
        MessageBodyMaxAgeSeconds = Math.Clamp(MessageBodyMaxAgeSeconds, 0, 86400);
        MessageBodyMaxChars = Math.Clamp(MessageBodyMaxChars, 20, 2000);
        MessageBodyFreshSeconds = Math.Clamp(MessageBodyFreshSeconds, 1, 600);
        VoiceIntercomStartDebounceSeconds = Math.Clamp(VoiceIntercomStartDebounceSeconds, 1, 30);
        VoiceIntercomEndDebounceSeconds = Math.Clamp(VoiceIntercomEndDebounceSeconds, 1, 30);
        if (VoiceIntercomWindowClassPatterns is null || VoiceIntercomWindowClassPatterns.Length == 0)
        {
            VoiceIntercomWindowClassPatterns = new[] { "^FMTfrmSpeaker$" };
        }

        if (string.IsNullOrWhiteSpace(KeywordMatchMode))
        {
            KeywordMatchMode = "Contains";
        }

        PollIntervalMs = Math.Clamp(PollIntervalMs, 500, 60_000);
        CooldownSeconds = Math.Clamp(CooldownSeconds, 0, 86_400);
        NotificationDurationSeconds = Math.Clamp(NotificationDurationSeconds, 1, 600);
        MaskDurationSeconds = Math.Clamp(MaskDurationSeconds, 1, 60);
        TextSources ??= new TextSourceOptions();
    }

    /// <summary>实际轮询间隔（钳制后）。</summary>
    public int GetPollIntervalMs() => Math.Clamp(PollIntervalMs, 500, 60_000);

    /// <summary>关键词匹配方式（大小写不敏感；未知值退化为 Contains）。</summary>
    public KeywordMatchKind ResolveKeywordMatchMode()
    {
        return string.Equals(KeywordMatchMode, "Regex", StringComparison.OrdinalIgnoreCase)
            ? KeywordMatchKind.Regex
            : KeywordMatchKind.Contains;
    }

    /// <summary>从插件配置目录加载配置；任何异常都回落到默认值（不抛出）。</summary>
    public static MonitorOptions Load(string? pluginConfigFolder)
    {
        var fallback = new MonitorOptions();
        fallback.Normalize();

        try
        {
            if (string.IsNullOrWhiteSpace(pluginConfigFolder))
            {
                PluginLog.Warn("插件配置目录为空，使用默认配置。");
                return fallback;
            }

            var path = Path.Combine(pluginConfigFolder, FileName);
            if (!File.Exists(path))
            {
                var saved = fallback.TrySave(pluginConfigFolder);
                PluginLog.Info($"未找到配置文件，已写入默认配置（success={saved}）：{path}");
                return fallback;
            }

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<MonitorOptions>(json, JsonOptions);
            if (loaded is null)
            {
                PluginLog.Warn($"配置文件内容为空，使用默认配置：{path}");
                return fallback;
            }

            loaded.Normalize();
            PluginLog.Info($"已加载配置：{path}");
            return loaded;
        }
        catch (Exception ex)
        {
            PluginLog.Error($"加载配置文件失败，使用默认配置（配置目录：{pluginConfigFolder}）", ex);
            return fallback;
        }
    }

    /// <summary>把当前配置写回插件配置目录；失败只记日志并返回 false。</summary>
    public bool TrySave(string? pluginConfigFolder)
    {
        if (string.IsNullOrWhiteSpace(pluginConfigFolder))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(pluginConfigFolder);
            var path = Path.Combine(pluginConfigFolder, FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            PluginLog.Warn("写入配置文件失败：" + ex.Message);
            return false;
        }
    }

    private static string[] NormalizeArray(string[]? values, string[] fallback)
    {
        if (values is null)
        {
            return fallback;
        }

        var result = new List<string>(values.Length);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (!result.Exists(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(trimmed);
            }
        }

        // 允许显式配置为空数组（例如“不限制进程/不按关键词触发”），此时不回落默认值。
        return result.ToArray();
    }
}
