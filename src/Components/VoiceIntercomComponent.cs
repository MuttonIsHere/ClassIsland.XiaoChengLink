// ─── VoiceIntercomComponent —— 主界面“语音对讲”状态组件 ─────────────────────
// 添加方式：ClassIsland → 设置 → 主界面 → 组件 → 添加“小橙语音对讲”。
// 平时显示“空闲”，检测到 FMTfrmSpeaker（录音播放窗口）时显示“🔴 语音对讲中”。
// 文字/颜色来自插件设置（MonitorOptions），组件每秒同步一次，设置保存后无需重启。

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using XiaoChengLink.Monitoring;

namespace XiaoChengLink.Components;

[ComponentInfo(
    "C7F3B5A1-9E2D-4C8B-A6F4-1D2E3C4B5A69",
    "小橙语音对讲",
    "",
    "显示老师是否正在语音对讲（录音发送到电脑播放时显示）")]
public sealed class VoiceIntercomComponent : ComponentBase
{
    private readonly VoiceIntercomMonitor _monitor;
    private readonly MonitorOptions _options;
    private readonly TextBlock _statusText;
    private readonly DispatcherTimer _refreshTimer;
    private bool _subscribed;

    public VoiceIntercomComponent(VoiceIntercomMonitor monitor, MonitorOptions options)
    {
        _monitor = monitor;
        _options = options;

        _statusText = new TextBlock
        {
            Text = options.VoiceIntercomIdleText,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(4, 2, 4, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _statusText },
        };

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _refreshTimer.Tick += (_, _) => UpdateDisplay();

        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (!_subscribed)
        {
            _monitor.StateChanged += OnStateChanged;
            _subscribed = true;
        }

        _refreshTimer.Start();
        UpdateDisplay();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_subscribed)
        {
            _monitor.StateChanged -= OnStateChanged;
            _subscribed = false;
        }

        _refreshTimer.Stop();
    }

    private void OnStateChanged(object? sender, bool active)
    {
        Dispatcher.UIThread.Post(UpdateDisplay);
    }

    private void UpdateDisplay()
    {
        try
        {
            var active = _monitor.IsActive;
            var text = active ? _options.VoiceIntercomActiveText : _options.VoiceIntercomIdleText;
            var colorHex = active ? _options.VoiceIntercomActiveColor : _options.VoiceIntercomIdleColor;
            _statusText.Text = string.IsNullOrWhiteSpace(text) ? (active ? "🔴 语音对讲中" : "空闲") : text;
            _statusText.Foreground = new SolidColorBrush(ParseColor(colorHex, active));
        }
        catch
        {
            // 组件刷新失败不应影响宿主。
        }
    }

    private static Color ParseColor(string? hex, bool active)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                return Color.Parse(hex.Trim());
            }
            catch
            {
            }
        }

        return active ? Color.Parse("#EF4444") : Color.Parse("#9CA3AF");
    }
}
