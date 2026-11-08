// ─── ClassIslandNotifier —— 提醒发送的薄封装 ────────────────────────────────
// 实际发提醒的是 XiaoChengNotificationProvider（通过 DI 注入；provider 由
// services.AddNotificationProvider<T>() 注册，并在 Plugin.Initialize 里改造成可注入的单例，
// 详见 Plugin.cs 的 AddSharedNotificationProvider 注释）。
//
// 本类职责：
//   · 组装/发送提醒（Mask=标题，Overlay=正文），时长来自 MonitorOptions；
//   · 统一 try/catch：发送失败只记 Error 日志并返回 false，绝不把异常抛给监控循环/宿主；
//   · 异步发送带超时（宿主若未及时响应，不阻塞监控轮询）。
//
// 【需实测】
//   · 从 BackgroundService 后台线程调用 ShowNotification/ShowNotificationAsync 是否安全；
//   · 真机上提醒是否按预期显示（Mask 3s + Overlay 8s）。

using System;
using System.Threading;
using System.Threading.Tasks;
using XiaoChengLink.Logging;
using XiaoChengLink.Monitoring;

namespace XiaoChengLink.Notification;

/// <summary>ClassIsland 提醒发送封装。</summary>
public sealed class ClassIslandNotifier
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly XiaoChengNotificationProvider _provider;
    private readonly MonitorOptions _options;

    public ClassIslandNotifier(XiaoChengNotificationProvider provider, MonitorOptions options)
    {
        _provider = provider;
        _options = options;
    }

    /// <summary>同步发送：正文 overlayText，遮罩（标题）maskText 为空时用默认标题。</summary>
    public bool Send(string overlayText, string? maskText = null)
    {
        try
        {
            _provider.Notify(
                ResolveMaskText(maskText),
                overlayText,
                _options.MaskDurationSeconds,
                _options.NotificationDurationSeconds);
            return true;
        }
        catch (Exception ex)
        {
            PluginLog.Error("发送 ClassIsland 提醒失败（同步）", ex);
            return false;
        }
    }

    /// <summary>异步发送，带超时与异常兜底；失败/超时返回 false。</summary>
    public async Task<bool> SendAsync(string overlayText, string? maskText = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            var sendTask = _provider.NotifyAsync(
                ResolveMaskText(maskText),
                overlayText,
                _options.MaskDurationSeconds,
                _options.NotificationDurationSeconds);

            var completed = await Task.WhenAny(sendTask, Task.Delay(SendTimeout, cancellationToken)).ConfigureAwait(false);
            if (completed != sendTask)
            {
                PluginLog.Error("发送 ClassIsland 提醒超时（5s 内宿主未完成入队）");
                return false;
            }

            await sendTask.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            PluginLog.Error("发送 ClassIsland 提醒失败（异步）", ex);
            return false;
        }
    }

    private static string ResolveMaskText(string? maskText)
    {
        return string.IsNullOrWhiteSpace(maskText) ? DefaultMaskText : maskText.Trim();
    }

    private const string DefaultMaskText = "小橙看班联动";
}
