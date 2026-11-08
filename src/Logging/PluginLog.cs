// ─── PluginLog —— 插件自己的文件日志 ─────────────────────────────────────────
// 为什么不用宿主 ILogger：本插件需要在 -epp 联调、后台线程异常等场景留下独立证据；
// 宿主日志里插件异常通常只出现在「插件加载异常」位置，后台线程异常容易被淹没。
//
// 日志路径：<PluginConfigFolder>/logs/yyyyMMdd.log
//   PluginConfigFolder 由 ClassIsland 在调用 PluginBase.Initialize 之前注入（只读属性）；
//   【需实测】实际路径（研究文档 §7.1 推断为
//   %LOCALAPPDATA%\ClassIsland\<PluginConfigsFolderPath>\<manifest.id>）。
// 滚动策略：单文件超过 2 MB 时重命名为 .old（已存在则覆盖），然后继续写新文件。
// 约束：日志组件的任何异常都必须吞掉——绝不能影响插件或宿主运行。
// 线程安全：内部用锁串行化文件写入，后台监控线程可安全调用。

using System;
using System.IO;
using System.Text;

namespace XiaoChengLink.Logging;

public static class PluginLog
{
    private const long MaxLogBytes = 2L * 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _logDirectory;
    private static bool _initialized;

    /// <summary>日志目录（Init 成功后非空）。</summary>
    public static string? LogDirectory
    {
        get
        {
            lock (Gate)
            {
                return _logDirectory;
            }
        }
    }

    /// <summary>
    /// 初始化日志目录。由插件入口在 Initialize 中调用一次；参数来自 PluginBase.PluginConfigFolder。
    /// 初始化失败不会抛异常（只是退化为仅 Debug.WriteLine）。
    /// </summary>
    public static void Init(string? pluginConfigFolder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pluginConfigFolder))
            {
                System.Diagnostics.Debug.WriteLine("[XiaoChengLink] PluginConfigFolder 为空，文件日志禁用。");
                return;
            }

            lock (Gate)
            {
                _logDirectory = Path.Combine(pluginConfigFolder, "logs");
                Directory.CreateDirectory(_logDirectory);
                _initialized = true;
            }

            Info($"文件日志已启用：{LogDirectory}");
        }
        catch (Exception ex)
        {
            _initialized = false;
            System.Diagnostics.Debug.WriteLine("[XiaoChengLink] PluginLog.Init 失败: " + ex.Message);
        }
    }

    public static void Info(string message)
    {
        Write("INFO", message, null);
    }

    public static void Debug(string message)
    {
        Write("DEBUG", message, null);
    }

    public static void Warn(string message)
    {
        Write("WARN", message, null);
    }

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    private static void Write(string level, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var builder = new StringBuilder(256);
        builder.Append('[').Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] [").Append(level).Append("] ").Append(message);
        if (exception is not null)
        {
            builder.AppendLine().Append(exception);
        }

        var text = builder.ToString();
        System.Diagnostics.Debug.WriteLine("[XiaoChengLink] " + text);

        if (!_initialized)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                if (_logDirectory is null)
                {
                    return;
                }

                var path = Path.Combine(_logDirectory, now.ToString("yyyyMMdd") + ".log");
                RotateIfNeeded(path);
                File.AppendAllText(path, text + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志写失败不影响主流程。
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxLogBytes)
            {
                return;
            }

            var backup = path + ".old";
            try
            {
                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }
            }
            catch
            {
                // 旧备份删不掉就继续尝试移动/截断。
            }

            try
            {
                File.Move(path, backup);
            }
            catch
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // 实在处理不了就让文件继续增长，下次再试。
                }
            }
        }
        catch
        {
            // 滚动失败不影响写日志。
        }
    }
}
