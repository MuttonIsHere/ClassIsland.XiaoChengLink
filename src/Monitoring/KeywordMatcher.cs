// ─── KeywordMatcher —— 关键词匹配 ────────────────────────────────────────────
// 两种模式（MonitorOptions.KeywordMatchMode）：
//   Contains：不区分大小写的子串匹配（默认；中文关键词下大小写无关，最稳）。
//   Regex   ：把每条 keyword 当作一个正则表达式（不区分大小写、250ms 超时，防灾难性回溯）。
// 无效正则在构造时被记录并跳过，运行期绝不抛异常到调用方。
//
// 依赖：System.Text.RegularExpressions（csc 直编时需额外 /reference:System.Text.RegularExpressions.dll）。
//
// 【需实测】
//   · Regex 模式在真机上的匹配效果（本机只做了编译验证，没有可运行的 ClassIsland 宿主）；
//   · Contains 模式为纯内存字符串操作，无外部依赖，风险最低（默认模式）。

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using XiaoChengLink.Logging;

namespace XiaoChengLink.Monitoring;

/// <summary>关键词匹配器（构造后只读，线程安全）。</summary>
public sealed class KeywordMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly string[] _keywords;
    private readonly List<Regex> _regexPatterns = new();
    private readonly bool _useRegex;

    public KeywordMatcher(MonitorOptions options)
    {
        _keywords = options.Keywords ?? Array.Empty<string>();
        _useRegex = options.ResolveKeywordMatchMode() == KeywordMatchKind.Regex;

        if (!_useRegex)
        {
            return;
        }

        foreach (var keyword in _keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            try
            {
                _regexPatterns.Add(new Regex(keyword, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout));
            }
            catch (ArgumentException ex)
            {
                PluginLog.Warn($"关键词正则无效（已忽略 '{keyword}'）：{ex.Message}");
            }
        }
    }

    /// <summary>命中时返回命中的关键词（Regex 模式返回原始表达式），未命中返回 null。</summary>
    public string? FindMatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            if (_useRegex)
            {
                foreach (var pattern in _regexPatterns)
                {
                    try
                    {
                        if (pattern.IsMatch(text))
                        {
                            return pattern.ToString();
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        PluginLog.Debug("关键词正则匹配超时（已跳过）：" + pattern);
                    }
                }

                return null;
            }

            foreach (var keyword in _keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return keyword;
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Debug("关键词匹配异常（已忽略）：" + ex.Message);
        }

        return null;
    }
}
