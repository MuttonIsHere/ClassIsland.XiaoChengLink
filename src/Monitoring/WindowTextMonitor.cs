// ─── WindowTextMonitor —— 后台监控主循环（BackgroundService）─────────────────
// 每 PollIntervalMs 扫描一次目标窗口：
//   1) 组装文本（标题 / Win32 子窗口文本 / UIA 文本，按 TextSources 开关）；
//   2) 关键词匹配命中且文本 hash 变化 → 通过 ClassIslandNotifier 发提醒；
//   3) 无关键词命中、窗口从“不存在/不可见”变为“存在且可见”且 TriggerOnWindowAppear →
//      发“窗口出现”提醒（小橙看班 UIA 读不到正文时的主要降级触发方式）；
//   4) 冷却去重：同一窗口同一文本 hash 在 CooldownSeconds 内不重复。
//
// 关键安全点（研究文档 §8.3）：.NET 8 的 BackgroundService 未捕获异常默认
// BackgroundServiceExceptionBehavior=StopHost，会直接把 ClassIsland 宿主停掉。
// 因此本循环的每一层都 try/catch，任何异常都只记日志并继续。
//
// 【需实测】
//   · services.AddHostedService<WindowTextMonitor>() 注册的后台服务是否在宿主启动时被执行
//     （本机未启动 ClassIsland 验证；若不被执行，见 Plugin.cs 注释里的 AppBase.AppStarted 备用方案）；
//   · 从后台线程调用提醒提供方 ShowNotification 是否安全（预计宿主会切 UI 线程，未实测）。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using XiaoChengLink.Logging;
using XiaoChengLink.Notification;

namespace XiaoChengLink.Monitoring;

/// <summary>窗口文本/窗口出现监控服务。</summary>
public sealed class WindowTextMonitor : BackgroundService
{
    private const int MaxTextChars = 8000;
    private const int PreviewChars = 180;

    private readonly MonitorOptions _options;
    private readonly WindowScanner _scanner;
    private readonly UiaTextReader _uiaTextReader;
    private readonly KeywordMatcher _keywordMatcher;
    private readonly ClassIslandNotifier _notifier;

    /// <summary>窗口句柄 -> 上一轮的可见性/文本哈希。</summary>
    private readonly Dictionary<IntPtr, WindowState> _windowStates = new();

    /// <summary>冷却键 -> 上次触发时间（UTC）。</summary>
    private readonly Dictionary<string, DateTime> _lastTriggerUtc = new(StringComparer.Ordinal);

    public WindowTextMonitor(
        MonitorOptions options,
        WindowScanner scanner,
        UiaTextReader uiaTextReader,
        KeywordMatcher keywordMatcher,
        ClassIslandNotifier notifier)
    {
        _options = options;
        _scanner = scanner;
        _uiaTextReader = uiaTextReader;
        _keywordMatcher = keywordMatcher;
        _notifier = notifier;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PluginLog.Info(
            $"窗口监控启动：进程=[{string.Join(", ", _options.ProcessNames)}] " +
            $"轮询={_options.GetPollIntervalMs()}ms 关键词={_options.Keywords.Length}个 " +
            $"来源(标题={_options.TextSources.WindowTitle},Win32={_options.TextSources.Win32},UIA={_options.TextSources.Uia}) " +
            $"窗口出现触发={_options.TriggerOnWindowAppear} 冷却={_options.CooldownSeconds}s");

        try
        {
            var baselineRound = true;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ScanOnceAsync(baselineRound, stoppingToken).ConfigureAwait(false);
                    baselineRound = false;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 注意：这里必须兜住所有异常，否则 BackgroundService 可能停掉整个宿主。
                    PluginLog.Error("扫描循环异常（已忽略，继续运行）", ex);
                }

                try
                {
                    await Task.Delay(_options.GetPollIntervalMs(), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error("监控服务异常退出", ex);
        }

        PluginLog.Info("窗口监控已停止。");
    }

    private async Task ScanOnceAsync(bool baselineRound, CancellationToken cancellationToken)
    {
        var windows = _scanner.Scan();
        var seenHandles = new HashSet<IntPtr>();
        var pending = new List<PendingNotification>(2);

        foreach (var window in windows)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            seenHandles.Add(window.Handle);
            _windowStates.TryGetValue(window.Handle, out var previousState);

            // “窗口出现”只对【可见】窗口判定：Delphi/VCL 程序常有一批隐藏的辅助窗体
            // （本机实测小橙看班有隐藏的「班级课表」「frmTrayIcon」等），若把隐藏窗体也算出现，
            // 一次启动会连发多条提醒（2026-11-08 联调实测 3 条 → 现已收敛为可见窗口）。
            var appeared = window.IsVisible && (previousState is null
                ? (!baselineRound || _options.TriggerOnExistingWindows)
                : !previousState.WasVisible);

            var text = BuildText(window, cancellationToken);
            var textHash = ComputeTextHash(text);
            var matchedKeyword = _keywordMatcher.FindMatch(text);
            var textChanged = previousState is null || !string.Equals(previousState.LastTextHash, textHash, StringComparison.Ordinal);

            _windowStates[window.Handle] = new WindowState
            {
                WasVisible = window.IsVisible,
                LastTextHash = textHash,
            };

            // 首轮先建立基线：不触发任何提醒（除非显式开启 triggerOnExistingWindows）。
            if (baselineRound && !_options.TriggerOnExistingWindows)
            {
                continue;
            }

            if (matchedKeyword is not null && textChanged)
            {
                var keywordKey = "keyword:" + window.Handle.ToInt64().ToString("X") + ":" + textHash;
                if (TryEnterCooldown(keywordKey))
                {
                    pending.Add(new PendingNotification(
                        DefaultKeywordMask,
                        BuildKeywordOverlay(window, matchedKeyword, text)));
                }

                // 已按关键词触发时，不再重复发“窗口出现”。
                continue;
            }

            if (appeared && _options.TriggerOnWindowAppear)
            {
                var appearKey = "appear:" + window.Handle.ToInt64().ToString("X");
                // 同一轮扫描里最多只发一条“窗口出现”提醒（一次启动可能同时出现多个窗口）。
                var hasAppear = pending.Exists(item => item.MaskText == DefaultAppearMask);
                if (!hasAppear && TryEnterCooldown(appearKey))
                {
                    pending.Add(new PendingNotification(
                        DefaultAppearMask,
                        BuildAppearOverlay(window)));
                }
            }
        }

        // 移除已消失窗口的状态，避免句柄复用导致误判。
        if (_windowStates.Count > 0)
        {
            var staleHandles = new List<IntPtr>();
            foreach (var handle in _windowStates.Keys)
            {
                if (!seenHandles.Contains(handle))
                {
                    staleHandles.Add(handle);
                }
            }

            foreach (var handle in staleHandles)
            {
                _windowStates.Remove(handle);
            }
        }

        if (pending.Count > 0)
        {
            PluginLog.Info($"本轮检测到 {pending.Count} 条待发提醒（窗口命中）。");
        }

        foreach (var notification in pending)
        {
            var sent = await _notifier.SendAsync(notification.OverlayText, notification.MaskText, cancellationToken).ConfigureAwait(false);
            PluginLog.Info(sent
                ? $"提醒已入队：mask=「{notification.MaskText}」正文=「{Truncate(notification.OverlayText, 80)}」"
                : $"提醒发送失败（详见上一条 ERROR）：mask=「{notification.MaskText}」");
        }

        PruneCooldownTable();
    }

    private static string Truncate(string text, int maxChars)
    {
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= maxChars ? oneLine : oneLine.Substring(0, maxChars) + "…";
    }

    private string BuildText(WindowSnapshot window, CancellationToken cancellationToken)
    {
        var pieces = new List<string>(3);
        var sources = _options.TextSources;

        if (sources.WindowTitle && !string.IsNullOrWhiteSpace(window.Title))
        {
            pieces.Add(window.Title.Trim());
        }

        if (sources.Win32)
        {
            var childText = _scanner.ReadChildWindowText(window.Handle);
            if (!string.IsNullOrWhiteSpace(childText))
            {
                pieces.Add(childText);
            }
        }

        if (sources.Uia && window.IsVisible && !cancellationToken.IsCancellationRequested)
        {
            var uiaText = _uiaTextReader.Read(window.Handle, cancellationToken);
            if (!string.IsNullOrWhiteSpace(uiaText))
            {
                pieces.Add(uiaText);
            }
        }

        var text = string.Join("\n", pieces);
        if (text.Length > MaxTextChars)
        {
            text = text.Substring(0, MaxTextChars);
        }

        return text;
    }

    private static string BuildKeywordOverlay(WindowSnapshot window, string keyword, string text)
    {
        var preview = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (preview.Length > PreviewChars)
        {
            preview = preview.Substring(0, PreviewChars) + "…";
        }

        return $"窗口「{window.Title}」命中关键词「{keyword}」\n{preview}";
    }

    private static string BuildAppearOverlay(WindowSnapshot window)
    {
        return $"检测到「{window.Title}」窗口出现（进程 {window.ProcessName}，类名 {window.ClassName}）";
    }

    private bool TryEnterCooldown(string key)
    {
        var now = DateTime.UtcNow;
        if (_options.CooldownSeconds > 0
            && _lastTriggerUtc.TryGetValue(key, out var last)
            && (now - last).TotalSeconds < _options.CooldownSeconds)
        {
            return false;
        }

        _lastTriggerUtc[key] = now;
        return true;
    }

    private void PruneCooldownTable()
    {
        if (_lastTriggerUtc.Count < 512)
        {
            return;
        }

        var seconds = Math.Max(_options.CooldownSeconds, 120) + 30;
        var threshold = DateTime.UtcNow.AddSeconds(-seconds);
        var expired = new List<string>();
        foreach (var pair in _lastTriggerUtc)
        {
            if (pair.Value < threshold)
            {
                expired.Add(pair.Key);
            }
        }

        foreach (var key in expired)
        {
            _lastTriggerUtc.Remove(key);
        }
    }

    /// <summary>FNV-1a 32 位哈希（只用于本轮/上轮文本比较与冷却键，不需要密码学强度）。</summary>
    private static string ComputeTextHash(string text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in text)
            {
                hash = (hash ^ ch) * 16777619u;
            }

            return hash.ToString("X8");
        }
    }

    private const string DefaultKeywordMask = "小橙看班提醒";
    private const string DefaultAppearMask = "小橙看班";

    private sealed class WindowState
    {
        public bool WasVisible { get; set; }

        public string LastTextHash { get; set; } = string.Empty;
    }

    private readonly struct PendingNotification
    {
        public PendingNotification(string maskText, string overlayText)
        {
            MaskText = maskText;
            OverlayText = overlayText;
        }

        public string MaskText { get; }

        public string OverlayText { get; }
    }
}
