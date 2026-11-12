using System;
using System.Reflection;
using XiaoChengLink.Logging;

namespace XiaoChengLink.Integration;

/// <summary>
/// 把「小橙看班」里读到的消息正文转发给「QQ 调课同步」插件（QqClassSync），
/// 让老师在小橙看班发「下午第三节语文改数学」也能触发大屏改课表。
///
/// 为什么用反射而不是直接引用对方 DLL：
///   - 两个插件独立发布，直接引用会让任意一方改名/升级都导致另一方加载失败；
///   - 按约定的类型名在已加载程序集里查找，找不到就静默跳过（只偶尔记一条日志），
///     完全不影响本插件原有功能。
///
/// 约定（由 QqClassSync 提供）：
///   类型：QqClassSync.Bridge.TiaokeBridge
///   方法：public static bool Submit(string text, string source)
/// </summary>
public sealed class TiaokeBridgeClient
{
    private const string BridgeTypeName = "QqClassSync.Bridge.TiaokeBridge";
    private const string SourceLabel = "小橙看班";

    private static readonly TimeSpan MissLogInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(10);

    private MethodInfo? _submit;
    private DateTime _lastMissLogUtc = DateTime.MinValue;
    private string _lastKey = string.Empty;
    private DateTime _lastKeyUtc = DateTime.MinValue;

    /// <summary>
    /// 提交一条消息正文。返回是否成功交给调课插件。
    /// </summary>
    /// <param name="content">消息正文。</param>
    /// <param name="sender">发送人（用于去重，可空）。</param>
    /// <param name="time">消息时间（用于去重，可空）。</param>
    public bool TrySubmit(string? content, string? sender = null, string? time = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        // 同一条数据库记录可能被多次读到：用「时间 + 发送人 + 正文」做去重键，
        // 保证老师真的重复发同样的话（不同时间）仍能被处理。
        var key = $"{time}|{sender}|{content.Trim()}";
        if (key == _lastKey && DateTime.UtcNow - _lastKeyUtc < DedupeWindow)
        {
            return false;
        }

        if (_submit is null && !TryResolveBridge())
        {
            if (DateTime.UtcNow - _lastMissLogUtc > MissLogInterval)
            {
                _lastMissLogUtc = DateTime.UtcNow;
                PluginLog.Info("未检测到「QQ 调课同步」插件（QqClassSync），小橙看班调课联动暂不生效。");
            }

            return false;
        }

        try
        {
            var result = _submit!.Invoke(null, new object?[] { content, SourceLabel });
            if (result is true)
            {
                _lastKey = key;
                _lastKeyUtc = DateTime.UtcNow;
                PluginLog.Info("已把消息正文转发给「QQ 调课同步」，由对方解析并改课表（来源：小橙看班）。");
                return true;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn("转发给「QQ 调课同步」失败：" + ex.GetType().Name);
        }

        return false;
    }

    /// <summary>在已加载程序集里查找桥接类型（找到后缓存 MethodInfo）。</summary>
    private bool TryResolveBridge()
    {
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? type;
                try
                {
                    type = assembly.GetType(BridgeTypeName, throwOnError: false);
                }
                catch
                {
                    continue;
                }

                if (type is null)
                {
                    continue;
                }

                var method = type.GetMethod(
                    "Submit",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(string) },
                    null);

                if (method is not null && method.ReturnType == typeof(bool))
                {
                    _submit = method;
                    PluginLog.Info("调课联动已连接「QQ 调课同步」插件。");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn("查找「QQ 调课同步」插件失败：" + ex.GetType().Name);
        }

        return false;
    }
}
