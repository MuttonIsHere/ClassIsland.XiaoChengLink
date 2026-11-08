// ─── UiaTextReader —— 用 UI Automation 读取窗口内文本 ───────────────────────
// 实现：System.Windows.Automation（WPF 的 UIA 客户端，ClassIsland 运行时目录里自带
//       UIAutomationClient.dll / UIAutomationTypes.dll / WindowsBase.dll）。
//
// 防御性约束（本机无法用 WPF 目标程序实测，运行期行为需实测）：
//   · 元素上限 1500；
//   · 单窗口 wall-clock 上限 1.5s（UIA 调用无法真正取消，只能在元素之间检查时间）；
//   · 每个元素的属性/Pattern 读取单独 try/catch，单个元素失败不影响其它元素；
//   · 异常全部吞掉并记 debug 日志，绝不冒泡到监控循环；
//   · 文本去重 + 总长度上限 8000 字符。
//
// ⚠ 已知限制（本机实测，见 research/uia/）：
//   目标程序“小橙看班”inArt_PC.exe 是 Delphi/VCL 自绘窗口，UIA 与 MSAA 都读不到正文
//   （只有顶层窗口标题，如「消息提醒」）。本读取器对它基本只会返回窗口标题/空字符串，
//   因此插件的主触发路径是“窗口出现/标题匹配”降级策略；
//   UIA 文本读取保留给其它标准 Win32/WPF/浏览器窗口。
//
// 运行期风险（需实测）：
//   · 插件 ALC 能否解析到宿主的 UIAutomationClient/WindowsBase（加载器回退 Default ALC，理论可行）；
//   · 从后台线程调用 UIA 是否稳定（UIAutomationClient 对 MTA 线程通常可用，但未实测）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using XiaoChengLink.Logging;

namespace XiaoChengLink.Monitoring;

/// <summary>UI Automation 文本读取器（无状态，线程安全）。</summary>
public sealed class UiaTextReader
{
    /// <summary>单窗口最多处理的元素数。</summary>
    public const int MaxElements = 1500;

    /// <summary>单窗口墙钟预算。</summary>
    public static readonly TimeSpan PerWindowBudget = TimeSpan.FromMilliseconds(1500);

    private const int MaxTotalChars = 8000;
    private const int MaxElementTextChars = 1000;

    /// <summary>读取窗口内的可见文本；失败/不支持时返回空字符串（不抛异常）。</summary>
    public string Read(IntPtr windowHandle, CancellationToken cancellationToken)
    {
        try
        {
            return ReadCore(windowHandle, cancellationToken);
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"UIA 读取失败（hwnd=0x{windowHandle.ToInt64():X}）：{ex.Message}");
            return string.Empty;
        }
    }

    private static string ReadCore(IntPtr windowHandle, CancellationToken cancellationToken)
    {
        AutomationElement root;
        try
        {
            root = AutomationElement.FromHandle(windowHandle);
        }
        catch (Exception ex)
        {
            // ElementNotAvailableException / ArgumentException / COM 异常等
            PluginLog.Debug($"UIA FromHandle 失败（hwnd=0x{windowHandle.ToInt64():X}）：{ex.Message}");
            return string.Empty;
        }

        if (root is null)
        {
            return string.Empty;
        }

        AutomationElementCollection elements;
        try
        {
            elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        }
        catch (Exception ex)
        {
            PluginLog.Debug("UIA FindAll 失败：" + ex.Message);
            return string.Empty;
        }

        if (elements is null)
        {
            return string.Empty;
        }

        var stopwatch = Stopwatch.StartNew();
        var builder = new StringBuilder(512);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var processed = 0;

        for (var index = 0; index < elements.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (processed >= MaxElements || stopwatch.Elapsed >= PerWindowBudget || builder.Length >= MaxTotalChars)
            {
                break;
            }

            processed++;
            try
            {
                var element = elements[index];
                if (element is null)
                {
                    continue;
                }

                AppendText(builder, seen, ReadName(element));
                AppendText(builder, seen, ReadTextPattern(element));
                AppendText(builder, seen, ReadValuePattern(element));
            }
            catch (Exception)
            {
                // 单个元素读取异常（窗口关闭/元素失效）直接忽略。
            }
        }

        if (builder.Length > MaxTotalChars)
        {
            return builder.ToString(0, MaxTotalChars);
        }

        return builder.ToString();
    }

    private static string ReadName(AutomationElement element)
    {
        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadTextPattern(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) || patternObject is not TextPattern textPattern)
            {
                return string.Empty;
            }

            var ranges = textPattern.GetVisibleRanges();
            if (ranges is null || ranges.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(128);
            foreach (var range in ranges)
            {
                try
                {
                    var text = range.GetText(MaxElementTextChars);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    if (builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(text);
                }
                catch
                {
                    // 单个 range 失效忽略。
                }
            }

            return builder.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadValuePattern(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObject) || patternObject is not ValuePattern valuePattern)
            {
                return string.Empty;
            }

            return valuePattern.Current.Value ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void AppendText(StringBuilder builder, HashSet<string> seen, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || builder.Length >= MaxTotalChars)
        {
            return;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > MaxElementTextChars)
        {
            trimmed = trimmed.Substring(0, MaxElementTextChars);
        }

        if (!seen.Add(trimmed))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(trimmed);
    }
}
