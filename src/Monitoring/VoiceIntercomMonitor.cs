// ─── VoiceIntercomMonitor —— 语音对讲状态机（录音发到电脑后播放）─────────────
// 探测结论（2026-11-11 实测）：
//   · 语音对讲/录音播放时，小橙看班会创建一个可见的 FMTfrmSpeaker 窗口（标题 ReviewPop），
//     时长与播放时长一致；过程没有 Data.idb 记录变化，因此用窗口特征判断。
//   · 同时会短暂出现 TfrmPusher（隐藏，音视频采集/视频对话）和 FilterGraphWindow，
//     这些属于音视频采集侧，不作为“语音对讲中”的主判据。
// 防抖：连续检测到 2 秒才算开始；连续消失 3 秒才算结束（可配置）。
// 提醒：开始/结束各发一条 ClassIsland 提醒，颜色/文字来自 MonitorOptions。

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using XiaoChengLink.Logging;
using XiaoChengLink.Notification;

namespace XiaoChengLink.Monitoring;

/// <summary>语音对讲状态监控（BackgroundService 单例，同时给 Component 提供状态）。</summary>
public sealed class VoiceIntercomMonitor : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private readonly MonitorOptions _options;
    private readonly WindowScanner _scanner;
    private readonly ClassIslandNotifier _notifier;
    private readonly object _gate = new();
    private List<Regex>? _classRegexes;
    private bool _isActive;

    public VoiceIntercomMonitor(MonitorOptions options, WindowScanner scanner, ClassIslandNotifier notifier)
    {
        _options = options;
        _scanner = scanner;
        _notifier = notifier;
    }

    /// <summary>当前是否处于语音对讲/播放状态（线程安全读取）。</summary>
    public bool IsActive => _isActive;

    /// <summary>状态变化事件（参数：true=开始，false=结束）。</summary>
    public event EventHandler<bool>? StateChanged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PluginLog.Info(
            $"语音对讲监控启动：类名正则=[{string.Join(", ", _options.VoiceIntercomWindowClassPatterns)}] " +
            $"开始防抖={_options.VoiceIntercomStartDebounceSeconds}s 结束防抖={_options.VoiceIntercomEndDebounceSeconds}s " +
            $"启用={_options.VoiceIntercomEnabled}");

        DateTime? presentSinceUtc = null;
        DateTime? absentSinceUtc = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var present = _options.VoiceIntercomEnabled && DetectPresent();
                var now = DateTime.UtcNow;

                if (present)
                {
                    absentSinceUtc = null;
                    presentSinceUtc ??= now;
                    if (!_isActive &&
                        (now - presentSinceUtc.Value).TotalSeconds >= _options.VoiceIntercomStartDebounceSeconds)
                    {
                        SetActive(true, "连续检测到语音播放窗口");
                    }
                }
                else
                {
                    presentSinceUtc = null;
                    if (_isActive)
                    {
                        absentSinceUtc ??= now;
                        if ((now - absentSinceUtc.Value).TotalSeconds >= _options.VoiceIntercomEndDebounceSeconds)
                        {
                            SetActive(false, "语音播放窗口持续消失");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Debug("语音对讲监控轮询异常（已忽略）：" + ex.Message);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        PluginLog.Info("语音对讲监控停止。");
    }

    private bool DetectPresent()
    {
        var windows = _scanner.Scan();
        if (windows.Count == 0)
        {
            return false;
        }

        var regexes = GetClassRegexes();
        foreach (var window in windows)
        {
            if (!window.IsVisible || string.IsNullOrWhiteSpace(window.ClassName))
            {
                continue;
            }

            foreach (var regex in regexes)
            {
                try
                {
                    if (regex.IsMatch(window.ClassName))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                }
            }
        }

        return false;
    }

    private List<Regex> GetClassRegexes()
    {
        if (_classRegexes is not null)
        {
            return _classRegexes;
        }

        var patterns = _options.VoiceIntercomWindowClassPatterns;
        if (patterns is null || patterns.Length == 0)
        {
            patterns = new[] { "^FMTfrmSpeaker$" };
        }

        var list = new List<Regex>(patterns.Length);
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            try
            {
                list.Add(new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout));
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"语音对讲窗口类名正则无效（已跳过）：{pattern}（{ex.Message}）");
            }
        }

        if (list.Count == 0)
        {
            list.Add(new Regex("^FMTfrmSpeaker$", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout));
        }

        _classRegexes = list;
        return list;
    }

    private void SetActive(bool active, string reason)
    {
        lock (_gate)
        {
            if (_isActive == active)
            {
                return;
            }

            _isActive = active;
        }

        PluginLog.Info($"语音对讲状态：{(active ? "开始" : "结束")}（{reason}）");
        try
        {
            StateChanged?.Invoke(this, active);
        }
        catch (Exception ex)
        {
            PluginLog.Debug("语音对讲状态事件订阅者异常（已忽略）：" + ex.Message);
        }

        // 提醒异步发送，绝不阻塞状态机轮询。
        _ = SendNotificationAsync(active);
    }

    private async Task SendNotificationAsync(bool start)
    {
        try
        {
            var text = start
                ? _options.VoiceIntercomStartNotificationText
                : _options.VoiceIntercomEndNotificationText;
            var color = start
                ? _options.VoiceIntercomStartNotificationColor
                : _options.VoiceIntercomEndNotificationColor;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var sent = await _notifier.SendAsync(text, "语音对讲", CancellationToken.None, color).ConfigureAwait(false);
            if (sent)
            {
                PluginLog.Info($"语音对讲提醒已入队：{(start ? "开始" : "结束")}「{text}」");
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error("发送语音对讲提醒失败（已忽略）", ex);
        }
    }
}
