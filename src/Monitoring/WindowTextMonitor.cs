// ─── WindowTextMonitor —— 后台监控主循环（BackgroundService）─────────────────
// 每 PollIntervalMs 扫描一次目标窗口，支持四类触发（可分别配置）：
//   1) 关键词命中：Assembled 文本（标题/子窗口文本/UIA 文本）命中关键词且文本变化 → 提醒；
//   2) 窗口出现：目标窗口从不存在/不可见 → 存在且【可见】→ 提醒（窗口类名/标题任一匹配即可）；
//   3) 画面变化：对可见的大窗口做 PrintWindow 截图网格哈希，画面发生显著变化 → 提醒
//      （小橙看班「消息提醒」是常驻窗口、正文自绘且 UIA 读不到，这是新消息到达的主要信号）；
//   4) 诊断日志：每 DiagnosticLogIntervalSeconds 打印扫描统计与匹配窗口快照，便于排查“不触发”。
//
// 匹配规则在 WindowScanner 中：进程名 + （窗口标题正则 OR 窗口类名正则），
// 对 Delphi/VCL 程序（FMTfrmXxx / TFormXxx 类名）尤其关键。
//
// 关键安全点（研究文档 §8.3）：.NET 8 的 BackgroundService 未捕获异常默认
// BackgroundServiceExceptionBehavior=StopHost，会直接把 ClassIsland 宿主停掉。
// 因此本循环的每一层都 try/catch，任何异常都只记日志并继续。
//
// 只读约束：EnumWindows / GetWindowText / PrintWindow / GetWindowRect 等均为只读 API，
// 不注入、不修改目标进程、不发送窗口消息。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using XiaoChengLink.Logging;
using XiaoChengLink.Notification;

namespace XiaoChengLink.Monitoring;

/// <summary>窗口文本/窗口出现/画面变化监控服务。</summary>
public sealed class WindowTextMonitor : BackgroundService
{
    private const int MaxTextChars = 8000;
    private const int PreviewChars = 180;
    private const string DefaultKeywordMask = "小橙看班提醒";
    private const string DefaultAppearMask = "小橙看班";
    private const string DefaultVisualMask = "小橙看班";

    private readonly MonitorOptions _options;
    private readonly WindowScanner _scanner;
    private readonly UiaTextReader _uiaTextReader;
    private readonly KeywordMatcher _keywordMatcher;
    private readonly ClassIslandNotifier _notifier;
    private readonly LocalMessageReader _messageReader;

    /// <summary>窗口句柄 -> 上一轮的可见性/文本哈希/画面快照。</summary>
    private readonly Dictionary<IntPtr, WindowState> _windowStates = new();

    /// <summary>冷却键 -> 上次触发时间（UTC）。</summary>
    private readonly Dictionary<string, DateTime> _lastTriggerUtc = new(StringComparer.Ordinal);

    /// <summary>上次打印诊断日志的时间（UTC）与扫描轮次。</summary>
    private DateTime _lastDiagnosticUtc = DateTime.MinValue;
    private int _scanRound;

    /// <summary>上次成功发出提醒的时间（UTC），用于全局节流（避免多窗口/重启连发）。</summary>
    private DateTime _lastAnyNotificationUtc = DateTime.MinValue;

    public WindowTextMonitor(
        MonitorOptions options,
        WindowScanner scanner,
        UiaTextReader uiaTextReader,
        KeywordMatcher keywordMatcher,
        ClassIslandNotifier notifier,
        LocalMessageReader messageReader)
    {
        _options = options;
        _scanner = scanner;
        _uiaTextReader = uiaTextReader;
        _keywordMatcher = keywordMatcher;
        _notifier = notifier;
        _messageReader = messageReader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PluginLog.Info(
            $"窗口监控启动：进程=[{string.Join(", ", _options.ProcessNames)}] " +
            $"标题正则=[{string.Join(", ", _options.WindowTitlePatterns)}] " +
            $"类名正则=[{string.Join(", ", _options.WindowClassPatterns)}] " +
            $"轮询={_options.GetPollIntervalMs()}ms 关键词={_options.Keywords.Length}个 " +
            $"来源(标题={_options.TextSources.WindowTitle},Win32={_options.TextSources.Win32},UIA={_options.TextSources.Uia}) " +
            $"窗口出现={_options.TriggerOnWindowAppear} 画面变化={_options.TriggerOnVisualChange} " +
            $"关键词含标题={_options.KeywordMatchWindowTitle} 全局节流={_options.NotificationThrottleSeconds}s " +
            $"冷却={_options.CooldownSeconds}s 诊断间隔={_options.DiagnosticLogIntervalSeconds}s");

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
        _scanRound++;
        IReadOnlyList<WindowSnapshot> windows;
        try
        {
            windows = _scanner.Scan();
        }
        catch (Exception ex)
        {
            PluginLog.Error("窗口扫描失败（已忽略本轮）", ex);
            return;
        }

        var seenHandles = new HashSet<IntPtr>();
        var pending = new List<PendingNotification>(2);
        var matchedVisible = 0;
        WindowSnapshot? triggerWindow = null;

        foreach (var window in windows)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            seenHandles.Add(window.Handle);
            _windowStates.TryGetValue(window.Handle, out var previousState);

            if (previousState is null && !baselineRound)
            {
                // 新出现的匹配窗口（句柄第一次被看到）——记录完整特征，便于排查“类名/标题对不对”。
                PluginLog.Info(
                    "发现新的匹配窗口：" + Describe(window) +
                    $" 标题匹配={_options.WindowTitlePatterns.Length}条 类名匹配={_options.WindowClassPatterns.Length}条");
            }
            else if (previousState is null)
            {
                PluginLog.Info("基线窗口：" + Describe(window));
            }

            // “窗口出现”只对【可见】窗口判定：Delphi/VCL 程序常有一批隐藏的辅助窗体
            // （本机实测小橙看班有隐藏的「班级课表」「frmTrayIcon」等），若把隐藏窗体也算出现，
            // 一次启动会连发多条提醒（2026-11-08 联调实测 3 条 → 现已收敛为可见窗口）。
            var appeared = window.IsVisible && (previousState is null
                ? (!baselineRound || _options.TriggerOnExistingWindows)
                : !previousState.WasVisible);

            var text = BuildText(window, cancellationToken);
            var keywordText = BuildKeywordText(window, cancellationToken);
            var textHash = ComputeTextHash(text);
            var matchedKeyword = _keywordMatcher.FindMatch(keywordText);
            var textChanged = previousState is null || !string.Equals(previousState.LastTextHash, textHash, StringComparison.Ordinal);

            if (window.IsVisible)
            {
                matchedVisible++;
            }

            var state = previousState ?? new WindowState();
            state.WasVisible = window.IsVisible;
            state.LastTextHash = textHash;
            _windowStates[window.Handle] = state;

            // 首轮先建立基线：不触发任何提醒（除非显式开启 triggerOnExistingWindows）。
            if (baselineRound && !_options.TriggerOnExistingWindows)
            {
                UpdateVisualBaseline(window, state);
                continue;
            }

            var handled = false;

            if (matchedKeyword is not null && textChanged)
            {
                var keywordKey = "keyword:" + window.Handle.ToInt64().ToString("X") + ":" + textHash;
                if (TryEnterCooldown(keywordKey) &&
                    TryAddPending(pending, DefaultKeywordMask, BuildKeywordOverlay(window, matchedKeyword, text)))
                {
                    handled = true;
                }
            }

            if (!handled && appeared && _options.TriggerOnWindowAppear)
            {
                var appearKey = "appear:" + window.Handle.ToInt64().ToString("X");
                // 同一轮扫描里最多只发一条“窗口出现”提醒（一次启动可能同时出现多个窗口）。
                var hasAppear = pending.Exists(item => item.MaskText == DefaultAppearMask);
                if (!hasAppear && TryEnterCooldown(appearKey))
                {
                    handled = TryAddPending(pending, DefaultAppearMask, BuildAppearOverlay(window));
                }
            }

            // 画面变化判定：仅在窗口可见、尺寸足够、且不是本轮刚出现（出现/关键词已触发）时进行。
            if (!handled && _options.TriggerOnVisualChange && window.IsVisible)
            {
                var area = Math.Max(0, window.Right - window.Left) * Math.Max(0, window.Bottom - window.Top);
                if (area >= _options.VisualChangeMinAreaPixels)
                {
                    handled = TryVisualChange(window, state, pending);
                }
            }

            if (appeared || handled)
            {
                // 窗口刚出现/已触发时，重置画面基线，避免同一事件重复触发。
                UpdateVisualBaseline(window, state);
            }

            if (handled && triggerWindow is null)
            {
                // 记住触发本次提醒的窗口（类名用于只查对应的数据库表，避免不同类型消息串台）。
                triggerWindow = window;
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
            PluginLog.Info($"本轮检测到 {pending.Count} 条待发提醒（匹配窗口 {windows.Count} 个、可见 {matchedVisible} 个）。");
        }

        // 从本地数据库读取最新消息正文（只在有提醒要发时才执行，避免轮询开销）。
        MessageReadResult? readResult = null;
        if (pending.Count > 0 && _options.ReadMessageBodyFromDatabase)
        {
            readResult = _messageReader.Read(triggerWindow?.ClassName, triggerWindow?.Title);
            if (readResult.Message is not null)
            {
                var message = readResult.Message;
                PluginLog.Info(
                    $"数据库正文：table={message.Table} time={message.Time} " +
                    $"title=「{Truncate(message.Title, 60)}」 content=「{Truncate(message.Content, 80)}」" +
                    $"（触发窗口类={triggerWindow?.ClassName} 标题={triggerWindow?.Title}）");
            }
            else if (!string.IsNullOrWhiteSpace(readResult.Error))
            {
                PluginLog.Warn($"数据库正文读取失败（将直接显示在提醒里）：{readResult.Error}");
            }
        }

        foreach (var notification in pending)
        {
            var overlay = notification.OverlayText;
            if (readResult?.Message is not null)
            {
                overlay = AppendMessageBody(overlay, readResult.Message);
            }
            else if (!string.IsNullOrWhiteSpace(readResult?.Error))
            {
                // 关键修复：读取失败不再静默退回通用提示，直接把原因拼进提醒正文，方便一眼定位。
                overlay += $"\n【正文读取失败】{Truncate(readResult.Error!, 220)}";
            }

            var sent = await _notifier.SendAsync(overlay, notification.MaskText, cancellationToken).ConfigureAwait(false);
            PluginLog.Info(sent
                ? $"提醒已入队：mask=「{notification.MaskText}」正文=「{Truncate(overlay, 200)}」"
                : $"提醒发送失败（详见上一条 ERROR）：mask=「{notification.MaskText}」");
        }

        LogDiagnostics(windows.Count, matchedVisible);
        PruneCooldownTable();
    }

    /// <summary>对单个可见窗口做截图对比；画面变化超过阈值且通过节流则加入待发提醒，返回是否加入。</summary>
    private bool TryVisualChange(WindowSnapshot window, WindowState state, List<PendingNotification> pending)
    {
        WindowVisualSnapshot? snapshot;
        try
        {
            snapshot = WindowVisualDiff.TryCapture(window.Handle);
        }
        catch (Exception ex)
        {
            state.CaptureFailureLogged = true;
            PluginLog.Debug("画面截图异常（已忽略）：" + ex.Message);
            return false;
        }

        if (snapshot is null)
        {
            if (!state.CaptureFailureLogged)
            {
                state.CaptureFailureLogged = true;
                PluginLog.Debug($"画面截图失败（该窗口不参与画面变化判定）：{Describe(window)}");
            }

            return false;
        }

        var previous = state.LastVisual;
        state.LastVisual = snapshot;
        state.CaptureFailureLogged = false;

        if (!state.VisualBaselineLogged)
        {
            state.VisualBaselineLogged = true;
            PluginLog.Info($"画面变化检测已启用：{Describe(window)}（基线快照 {snapshot.Width}x{snapshot.Height}）");
        }

        if (previous is null)
        {
            return false;
        }

        var changedRatio = snapshot.ChangedRatio(previous);
        if (changedRatio * 100.0 < _options.VisualChangeThresholdPercent)
        {
            return false;
        }

        var visualKey = "visual:" + window.Handle.ToInt64().ToString("X");
        if (!TryEnterCooldown(visualKey))
        {
            return false;
        }

        var added = TryAddPending(
            pending,
            DefaultVisualMask,
            BuildVisualOverlay(window, changedRatio));
        if (added)
        {
            PluginLog.Info(
                $"画面变化触发：{Describe(window)} 变化比例={changedRatio * 100.0:F2}% " +
                $"阈值={_options.VisualChangeThresholdPercent:F2}%");
        }

        return added;
    }

    private void UpdateVisualBaseline(WindowSnapshot window, WindowState state)
    {
        if (!window.IsVisible)
        {
            state.LastVisual = null;
            return;
        }

        try
        {
            state.LastVisual = WindowVisualDiff.TryCapture(window.Handle);
            if (state.LastVisual is not null && !state.VisualBaselineLogged)
            {
                state.VisualBaselineLogged = true;
                PluginLog.Info($"画面变化基线已建立：{Describe(window)}（{state.LastVisual.Width}x{state.LastVisual.Height}）");
            }
        }
        catch
        {
            state.LastVisual = null;
        }
    }

    /// <summary>加入待发提醒前统一做全局节流，避免程序重启/多窗口出现时连发多条。</summary>
    private bool TryAddPending(List<PendingNotification> pending, string maskText, string overlayText)
    {
        var throttle = _options.NotificationThrottleSeconds;
        if (throttle > 0)
        {
            var now = DateTime.UtcNow;
            if (_lastAnyNotificationUtc != DateTime.MinValue &&
                (now - _lastAnyNotificationUtc).TotalSeconds < throttle)
            {
                PluginLog.Debug($"全局节流（{throttle}s）内，跳过提醒：{overlayText}");
                return false;
            }

            _lastAnyNotificationUtc = now;
        }

        pending.Add(new PendingNotification(maskText, overlayText));
        return true;
    }

    private void LogDiagnostics(int matchedCount, int matchedVisible)
    {
        var interval = _options.DiagnosticLogIntervalSeconds;
        if (interval <= 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_lastDiagnosticUtc != DateTime.MinValue && (now - _lastDiagnosticUtc).TotalSeconds < interval)
        {
            return;
        }

        _lastDiagnosticUtc = now;

        var descriptions = new List<string>(_windowStates.Count);
        foreach (var window in _scanner.Scan())
        {
            // 仅为诊断再枚举一次成本很低（纯只读）；也可以复用本轮结果，这里为独立性保留。
            descriptions.Add(Describe(window));
        }

        PluginLog.Info(
            $"扫描诊断：轮次={_scanRound} 顶层窗口={_scanner.LastEnumWindowsCount} " +
            $"匹配窗口={matchedCount} 可见匹配={matchedVisible} " +
            $"进程=[{string.Join(",", _options.ProcessNames)}] " +
            $"标题规则={_options.WindowTitlePatterns.Length} 类名规则={_options.WindowClassPatterns.Length} " +
            $"画面变化={_options.TriggerOnVisualChange} 监控中窗口={_windowStates.Count}。" +
            (descriptions.Count == 0 ? " 当前无匹配窗口。" : " 匹配窗口：" + string.Join(" | ", descriptions)));
    }

    private static string Describe(WindowSnapshot window)
    {
        var title = string.IsNullOrEmpty(window.Title) ? "<无标题>" : window.Title;
        return $"[0x{window.Handle.ToInt64():X}] 标题=「{title}」类名={window.ClassName} " +
               $"可见={window.IsVisible} 进程={window.ProcessName} 区域=({window.Left},{window.Top})-({window.Right},{window.Bottom})";
    }

    /// <summary>把本地数据库读到的最新消息正文附加到提醒文本后（超长截断）。</summary>
    private string AppendMessageBody(string overlay, LocalMessage message)
    {
        try
        {
            var title = (message.Title ?? string.Empty).Trim();
            var content = (message.Content ?? string.Empty).Trim();
            if (title.Length == 0 && content.Length == 0)
            {
                return overlay;
            }

            var body = title.Length == 0
                ? content
                : (content.Length == 0 ? title : title + "\n" + content);

            var max = Math.Max(20, _options.MessageBodyMaxChars);
            if (body.Length > max)
            {
                body = body.Substring(0, max) + "…";
            }

            var tag = string.IsNullOrWhiteSpace(message.Table) ? "消息" : message.Table;
            return overlay + $"\n【{tag}】{body}";
        }
        catch
        {
            return overlay;
        }
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

        AppendAccessibleText(window, pieces, cancellationToken);
        return TrimText(string.Join("\n", pieces));
    }

    /// <summary>
    /// 关键词匹配专用文本：默认不含窗口标题（小橙看班标题恒为「消息提醒」，
    /// 含标题会让关键词每次窗口创建都误报）。只使用子窗口文本/UIA 文本等真正的内容来源。
    /// </summary>
    private string BuildKeywordText(WindowSnapshot window, CancellationToken cancellationToken)
    {
        var pieces = new List<string>(2);
        if (_options.KeywordMatchWindowTitle && !string.IsNullOrWhiteSpace(window.Title))
        {
            pieces.Add(window.Title.Trim());
        }

        AppendAccessibleText(window, pieces, cancellationToken);
        return TrimText(string.Join("\n", pieces));
    }

    private void AppendAccessibleText(WindowSnapshot window, List<string> pieces, CancellationToken cancellationToken)
    {
        var sources = _options.TextSources;

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
    }

    private static string TrimText(string text)
    {
        return text.Length > MaxTextChars ? text.Substring(0, MaxTextChars) : text;
    }

    private static string BuildKeywordOverlay(WindowSnapshot window, string keyword, string text)
    {
        var preview = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (preview.Length > PreviewChars)
        {
            preview = preview.Substring(0, PreviewChars) + "…";
        }

        var title = string.IsNullOrEmpty(window.Title) ? window.ClassName : window.Title;
        return $"窗口「{title}」命中关键词「{keyword}」\n{preview}";
    }

    private static string BuildAppearOverlay(WindowSnapshot window)
    {
        var title = string.IsNullOrEmpty(window.Title) ? window.ClassName : window.Title;
        return $"检测到「{title}」窗口出现（进程 {window.ProcessName}，类名 {window.ClassName}）";
    }

    private static string BuildVisualOverlay(WindowSnapshot window, double changedRatio)
    {
        var title = string.IsNullOrEmpty(window.Title) ? window.ClassName : window.Title;
        return $"检测到「{title}」画面变化（{changedRatio * 100.0:F1}%），可能有新消息（进程 {window.ProcessName}）";
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

    private sealed class WindowState
    {
        public bool WasVisible { get; set; }

        public string LastTextHash { get; set; } = string.Empty;

        /// <summary>上一次成功抓取的画面快照（用于画面变化检测）。</summary>
        public WindowVisualSnapshot? LastVisual { get; set; }

        /// <summary>是否已记录过截图失败，避免每个轮询都刷日志。</summary>
        public bool CaptureFailureLogged { get; set; }

        /// <summary>是否已打印过画面基线日志。</summary>
        public bool VisualBaselineLogged { get; set; }
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
