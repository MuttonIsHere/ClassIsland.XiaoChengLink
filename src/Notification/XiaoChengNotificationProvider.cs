// ─── XiaoChengNotificationProvider —— 插件自己的 ClassIsland 提醒提供方 ──────
// 为什么不用全局 INotificationSender：
//   研究文档（research/classisland-api/classisland-2.1-plugin-api.md §5.3）用 Mono.Cecil
//   核对了 2.1.0.1 二进制：ClassIsland 没有“全局静态发提醒”入口；插件发提醒的官方路径是
//   注册自己的 NotificationProviderBase 子类（services.AddNotificationProvider<T>()），
//   再调用它的 ShowNotification(...)。INotificationHostService.ShowNotification(...) 是
//   不可访问（internal 级别）的，不能从插件调用（实测编译报 CS0122）。
//
// 注意：
//   · [NotificationProviderInfo] 在 2.1.0.1 的 4 参构造是 (string guid, string name, string iconGlyph, string description)，
//     iconGlyph 是 FluentIcon 字形字符串，可以留空 ""（不是 1.x 的 PackIconKind 枚举）。
//   · MaskContent 是必填（NotificationRequest 上有 [Required]），OverlayContent 可选；
//     “标题”通过 Mask 文本表达（NotificationContent 没有 Title 属性）。
//   · 本类在宿主启动时由 DI 构造；基类 NotificationProviderBase 的构造会：
//       1) IAppHost.GetService<INotificationHostService>()（要求宿主已构建）；
//       2) 在 NotificationProviderRegistryService.RegisteredProviders 中按 GUID 找到本提供方注册信息，
//          找不到会抛异常 —— 所以 Plugin.Initialize 里必须先调用 AddNotificationProvider<T>()；
//       3) 创建 Avalonia FontIcon 控件（仅作为设置页图标，插件不启动 UI）。
//   · 【需实测】提供方构造/ShowNotification 在真机上的线程要求与显示效果。

using System;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;

namespace XiaoChengLink.Notification;

/// <summary>“小橙看班联动”提醒提供方。</summary>
[NotificationProviderInfo(
    "6F1B2C3D-4E5F-4A6B-8C7D-9E0F1A2B3C4D",
    "小橙看班联动",
    "",
    "由窗口文本/窗口出现监控触发的提醒")]
public sealed class XiaoChengNotificationProvider : NotificationProviderBase
{
    // 【真机实测 · 重要】NotificationContent.CreateTwoIconsMask(...) 内部会 new LucideIconSource()
    // （AvaloniaObject），Avalonia 要求它必须在 UI 线程创建；从 BackgroundService 线程调用会抛
    // "Call from invalid thread"（2026-11-08 本机联调实测，见 research/e2e/）。
    // 因此：所有 NotificationRequest 的构造 + ShowNotification 都切到 Avalonia UI 线程执行。
    private static readonly TimeSpan UiDispatchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>同步发送一条提醒（内部切到 Avalonia UI 线程后入队，不会长时间阻塞后台线程）。</summary>
    public void Notify(string maskText, string overlayText, int maskSeconds = 3, int overlaySeconds = 8, string? colorHex = null)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ShowNotification(BuildRequest(maskText, overlayText, maskSeconds, overlaySeconds, colorHex));
            return;
        }

        // 后台线程：Post 到 UI 线程；异常只吞掉（监控循环不会因提醒失败而中断）。
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                ShowNotification(BuildRequest(maskText, overlayText, maskSeconds, overlaySeconds, colorHex));
            }
            catch
            {
                // 提醒发送失败不应影响宿主；具体错误由 ClassIslandNotifier 的超时/日志兜底。
            }
        });
    }

    /// <summary>异步发送一条提醒（在 Avalonia UI 线程构造并发送）。</summary>
    public Task NotifyAsync(string maskText, string overlayText, int maskSeconds = 3, int overlaySeconds = 8, string? colorHex = null)
    {
        var operation = Dispatcher.UIThread.InvokeAsync(() =>
            ShowNotification(BuildRequest(maskText, overlayText, maskSeconds, overlaySeconds, colorHex)));
        return operation.GetTask();
    }

    private static NotificationRequest BuildRequest(string maskText, string overlayText, int maskSeconds, int overlaySeconds, string? colorHex)
    {
        var brush = BuildBrush(colorHex);
        return new NotificationRequest
        {
            MaskContent = NotificationContent.CreateTwoIconsMask(
                maskText,
                factory: content =>
                {
                    content.Duration = TimeSpan.FromSeconds(Math.Max(maskSeconds, 1));
                    if (brush is not null) content.Color = brush;
                }),
            OverlayContent = NotificationContent.CreateSimpleTextContent(
                overlayText,
                content =>
                {
                    content.Duration = TimeSpan.FromSeconds(Math.Max(overlaySeconds, 1));
                    if (brush is not null) content.Color = brush;
                }),
        };
    }

    private static IBrush? BuildBrush(string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return null;
        }

        try
        {
            return new SolidColorBrush(Color.Parse(colorHex.Trim()));
        }
        catch
        {
            return null;
        }
    }
}
