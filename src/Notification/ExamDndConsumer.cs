using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Shared;
using Microsoft.Extensions.Hosting;
using XiaoChengLink.Logging;
using XiaoChengLink.Monitoring;

namespace XiaoChengLink.Notification;

/// <summary>
/// 考试模式全局免打扰：注册一个最高优先级的提醒消费者。
///
/// 工作机制（读 ClassIsland 2.1.0.1 源码确认）：
///   - 宿主按优先级挑「第一个 AcceptsNotificationRequests 且空闲」的消费者；
///   - <c>ticket.Cancel()</c> 会把提醒退回提醒主机，并移交给下一个消费者（用于白名单放行）；
///   - 直接把提醒的 CompletedTokenSource 取消 = 标记「已播放完成」且不再派发（用于静默吞掉）。
///
/// 白名单：地震预警等必须放行的提醒提供方 GUID（默认内置地震预警）。
/// </summary>
public sealed class ExamDndConsumer : INotificationConsumer, IHostedService
{
    /// <summary>越高优先级（数值越小）越先拿到提醒。</summary>
    private const int ConsumerPriority = -100000;

    private static readonly Guid DefaultWhitelist = new("B27C0AF3-C917-44DE-A61D-8010C3F3FB92");

    // NotificationRequest 的相关成员是 internal，只能反射读取。
    private static readonly PropertyInfo? SourceGuidProperty =
        typeof(NotificationRequest).GetProperty("NotificationSourceGuid", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly PropertyInfo? SourceProperty =
        typeof(NotificationRequest).GetProperty("NotificationSource", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly PropertyInfo? CompletedTokenSourceProperty =
        typeof(NotificationRequest).GetProperty("CompletedTokenSource", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly MonitorOptions _options;
    private readonly ExamModeState _examState;

    private INotificationHostService? _host;
    private MethodInfo? _showNotificationMethod;
    private DateTime _suppressUntilUtc = DateTime.MinValue;
    private DateTime _lastStateLogUtc = DateTime.MinValue;

    public ExamDndConsumer(MonitorOptions options, ExamModeState examState)
    {
        _options = options;
        _examState = examState;
    }

    // ── INotificationConsumer ───────────────────────────────────────────────

    public int QueuedNotificationCount => 0;

    public bool AcceptsNotificationRequests
    {
        get
        {
            if (!_options.ExamDndEnabled || _host is null)
            {
                return false;
            }

            if (!IsExamActive())
            {
                return false;
            }

            // 放行某条提醒后短暂让位，避免宿主又把退回的提醒派给我们造成死循环。
            return DateTime.UtcNow >= _suppressUntilUtc;
        }
    }

    public void ReceiveNotifications(IReadOnlyList<NotificationPlayingTicket> notificationRequests)
    {
        foreach (var ticket in notificationRequests)
        {
            try
            {
                Handle(ticket);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("[调课中枢] 考试静默处理提醒失败：" + ex.GetType().Name);
            }
        }
    }

    private void Handle(NotificationPlayingTicket ticket)
    {
        var request = ticket.Request;
        var sourceGuid = ReadSourceGuid(request);
        var sourceName = ReadSourceName(request) ?? "未知提供方";
        var whitelisted = IsWhitelisted(sourceGuid);
        var dryRun = _options.ExamDndDryRun;

        var action = whitelisted ? "放行（白名单）"
            : dryRun ? "干跑（仅记录，不拦截）"
            : "拦截";

        // 每条提醒打一行，方便排查「哪条被拦了、来源是谁」。
        PluginLog.Info($"[调课中枢] 考试静默：来源={sourceName}（{sourceGuid}）→ {action}");

        if (whitelisted || dryRun)
        {
            // 让位给下一个消费者（MainWindowLine）播放。
            _suppressUntilUtc = DateTime.UtcNow.AddSeconds(1.5);
            ticket.Cancel();
            return;
        }

        // 静默吞掉：标记提醒已完成，不再派发。
        if (CompletedTokenSourceProperty?.GetValue(request) is CancellationTokenSource completed)
        {
            try
            {
                completed.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 提醒已经结束，忽略。
            }
        }
    }

    private bool IsExamActive() =>
        _options.ExamDndForceActive || _examState.IsActive;

    private bool IsWhitelisted(Guid guid)
    {
        var whitelist = _options.ExamDndWhitelistProviderGuids;
        if (whitelist is null || whitelist.Length == 0)
        {
            return false;
        }

        return whitelist.Any(item => Guid.TryParse(item, out var parsed) && parsed == guid);
    }

    private static Guid ReadSourceGuid(NotificationRequest request)
    {
        try
        {
            return SourceGuidProperty?.GetValue(request) is Guid guid ? guid : Guid.Empty;
        }
        catch
        {
            return Guid.Empty;
        }
    }

    private static string? ReadSourceName(NotificationRequest request)
    {
        try
        {
            var source = SourceProperty?.GetValue(request);
            return source?.GetType().GetProperty("Name")?.GetValue(source)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    // ── IHostedService：注册 / 反注册 ───────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _host = IAppHost.GetService<INotificationHostService>();
            _host.RegisterNotificationConsumer(this, ConsumerPriority);

            // ShowNotification(4 参) 在接口上是 internal，但实现类（NotificationHostService）上是 public，
            // 自检时通过反射调用，避免编译期访问不到。
            _showNotificationMethod = _host.GetType().GetMethod(
                "ShowNotification",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(NotificationRequest), typeof(Guid), typeof(Guid), typeof(bool), typeof(bool) },
                null);

            var whitelist = string.Join(", ", _options.ExamDndWhitelistProviderGuids ?? Array.Empty<string>());
            PluginLog.Info($"[调课中枢] 考试静默已注册（优先级 {ConsumerPriority}，开关={_options.ExamDndEnabled}，干跑={_options.ExamDndDryRun}，白名单=[{whitelist}]）");

            if (_options.ExamDndSelfTest)
            {
                _ = Task.Run(() => RunSelfTestAsync());
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn("[调课中枢] 注册考试静默消费者失败：" + ex.GetType().Name);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _host?.UnregisterNotificationConsumer(this);
            PluginLog.Info("[调课中枢] 考试静默已注销");
        }
        catch
        {
            // 忽略
        }

        return Task.CompletedTask;
    }

    // ── 开机自检（验证能否拿到提醒来源 ID、能否拦截/放行）─────────────────────

    private async Task RunSelfTestAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8)); // 等宿主把提醒消费者都注册好

        PluginLog.Info("[调课中枢] 考试静默自检开始：将分别投递「普通来源」和「地震预警」两条测试提醒");

        await ShowSelfTestNotificationAsync("自检·普通来源", Guid.Parse("11111111-2222-3333-4444-555555555555"));
        await Task.Delay(TimeSpan.FromSeconds(3));
        await ShowSelfTestNotificationAsync("自检·地震预警", DefaultWhitelist);
        await Task.Delay(TimeSpan.FromSeconds(3));

        PluginLog.Info("[调课中枢] 考试静默自检结束（请对照上面的「考试静默：来源=…→ 拦截/放行」日志）");
    }

    private Task ShowSelfTestNotificationAsync(string title, Guid providerGuid)
    {
        try
        {
            if (_host is null)
            {
                return Task.CompletedTask;
            }

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var request = new NotificationRequest
                    {
                        MaskContent = NotificationContent.CreateSimpleTextContent(title, content =>
                        {
                            content.Duration = TimeSpan.FromSeconds(2);
                            content.SpeechContent = title;
                        })
                    };
                    _showNotificationMethod?.Invoke(_host, new object?[] { request, providerGuid, Guid.Empty, true, false });
                }
                catch (Exception ex)
                {
                    PluginLog.Warn("[调课中枢] 自检投递提醒失败：" + ex.GetType().Name);
                }
            });
        }
        catch (Exception ex)
        {
            PluginLog.Warn("[调课中枢] 自检投递异常：" + ex.GetType().Name);
        }

        return Task.CompletedTask;
    }
}
