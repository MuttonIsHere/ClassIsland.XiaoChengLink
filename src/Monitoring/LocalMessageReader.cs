// ─── LocalMessageReader —— 从“小橙看班”本地数据库读取最新消息正文 ───────────
// 背景（2026-11-09 实测）：
//   · 小橙看班把消息/通知缓存在 D:\inArtBoard\Data.idb（Microsoft Jet / Access .mdb 格式）；
//   · 表结构含 HomeWork / ClassNotice / SchoolNotice / ParentMsg / WelcomeWord 等，均有标题/正文/时间字段；
//   · Jet OLEDB 4.0 只有 32 位提供程序，而 ClassIsland 插件跑在 64 位进程里，无法直接打开；
//   · 实测 64 位 .NET 的 OleDb/ADODB 都打不开该库，但 32 位 Windows PowerShell 可以。
// 因此本类在“即将弹提醒”时，把 Data.idb 只读复制到临时目录，调用随插件附带的
// read-message.ps1（32 位 PowerShell 执行）查询最新一条记录，返回标题/正文/时间。
//
// 性能：只在触发提醒时执行（不是每次轮询），一次约 0.3–1s；失败一律返回 null，退回通用提示。
// 隐私/安全：只读本地数据库副本，不上传；脚本在插件目录内，可用 settings.json 关闭（ReadMessageBodyFromDatabase=false）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using XiaoChengLink.Logging;

namespace XiaoChengLink.Monitoring;

/// <summary>从本地数据库读到的最新消息。</summary>
public sealed class LocalMessage
{
    public string Table { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string Time { get; set; } = string.Empty;
}

/// <summary>读取小橙看班本地 Jet 数据库中的最新消息（通过 32 位 PowerShell 辅助脚本）。</summary>
public sealed class LocalMessageReader
{
    private const string ScriptFileName = "read-message.ps1";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(8);

    private readonly MonitorOptions _options;
    private readonly string _baseDirectory;
    private bool _scriptMissingLogged;
    private bool _dbMissingLogged;

    public LocalMessageReader(MonitorOptions options)
    {
        _options = options;
        // 插件程序集所在目录（ClassIsland 会从 data\Plugins\<id>\ 加载；AppContext.BaseDirectory 是宿主 app 目录，
        // 不能用来找插件自带文件）。
        var assemblyDirectory = string.Empty;
        try
        {
            assemblyDirectory = Path.GetDirectoryName(typeof(LocalMessageReader).Assembly.Location) ?? string.Empty;
        }
        catch
        {
            assemblyDirectory = string.Empty;
        }

        _baseDirectory = string.IsNullOrWhiteSpace(assemblyDirectory) ? AppContext.BaseDirectory : assemblyDirectory;
    }

    /// <summary>尝试读取最新消息；失败/关闭/无数据时返回 null（绝不抛出）。</summary>
    public LocalMessage? TryGetLatestMessage()
    {
        if (!_options.ReadMessageBodyFromDatabase)
        {
            return null;
        }

        string? tempDb = null;
        try
        {
            var scriptPath = ResolveScriptPath();
            if (scriptPath is null)
            {
                if (!_scriptMissingLogged)
                {
                    _scriptMissingLogged = true;
                    PluginLog.Warn($"未找到消息读取脚本 {ScriptFileName}（插件目录：{_baseDirectory}），正文将退回通用提示。");
                }

                return null;
            }

            var sourceDb = ResolveDatabasePath();
            if (sourceDb is null)
            {
                if (!_dbMissingLogged)
                {
                    _dbMissingLogged = true;
                    PluginLog.Warn("未找到小橙看班本地数据库（Data.idb），正文将退回通用提示。");
                }

                return null;
            }

            tempDb = CopyDatabaseReadOnly(sourceDb);
            if (tempDb is null)
            {
                return null;
            }

            var json = RunReader(scriptPath, tempDb);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return ParseResult(json);
        }
        catch (Exception ex)
        {
            PluginLog.Debug("读取本地消息正文失败（已忽略）：" + ex.Message);
            return null;
        }
        finally
        {
            if (tempDb is not null)
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }

    private string? ResolveScriptPath()
    {
        var candidates = new List<string>();

        // 优先：插件的实际安装目录（PluginBase.Info.PluginFolderPath，由 Plugin.Initialize 注入）。
        var pluginFolder = PluginPaths.PluginFolder;
        if (!string.IsNullOrWhiteSpace(pluginFolder))
        {
            candidates.Add(Path.Combine(pluginFolder, ScriptFileName));
            candidates.Add(Path.Combine(pluginFolder, "tools", ScriptFileName));
        }

        candidates.Add(Path.Combine(_baseDirectory, ScriptFileName));
        candidates.Add(Path.Combine(_baseDirectory, "tools", ScriptFileName));

        // 兜底：插件配置目录（用户/开发者可手动放脚本的位置）。
        var configFolder = PluginPaths.PluginConfigFolder;
        if (!string.IsNullOrWhiteSpace(configFolder))
        {
            candidates.Add(Path.Combine(configFolder, ScriptFileName));
        }

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>定位 Data.idb：优先用户配置；否则从运行中的 inArt_PC 进程路径推导。</summary>
    private string? ResolveDatabasePath()
    {
        var configured = _options.MessageDatabasePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
            if (File.Exists(expanded))
            {
                return expanded;
            }

            PluginLog.Warn("settings.json 里配置的 MessageDatabasePath 不存在：" + expanded);
            return null;
        }

        foreach (var process in Process.GetProcessesByName("inArt_PC"))
        {
            try
            {
                var exePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    continue;
                }

                var appDir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrWhiteSpace(appDir))
                {
                    continue;
                }

                var candidates = new[]
                {
                    Path.Combine(appDir, "Data.idb"),
                    Path.Combine(appDir, "Data", "Data.idb"),
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Debug("读取 inArt_PC 进程路径失败（已忽略）：" + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    /// <summary>把正在被小橙看班占用的 Data.idb 以共享读方式复制到临时文件。</summary>
    private static string? CopyDatabaseReadOnly(string sourcePath)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "XiaoChengLink");
            Directory.CreateDirectory(tempDir);
            var target = Path.Combine(tempDir, "Data-" + Guid.NewGuid().ToString("N") + ".idb");

            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
            return target;
        }
        catch (Exception ex)
        {
            PluginLog.Debug("复制本地数据库失败（已忽略）：" + ex.Message);
            return null;
        }
    }

    private string? RunReader(string scriptPath, string dbPath)
    {
        var powershell = Resolve32BitPowerShell();
        if (powershell is null)
        {
            return null;
        }

        var arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                        $"-DbPath \"{dbPath}\" -MaxAgeSeconds {Math.Max(0, _options.MessageBodyMaxAgeSeconds)}";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
        {
            try { process.Kill(); } catch { }
            PluginLog.Warn("本地消息读取脚本超时（已忽略），正文退回通用提示。");
            return null;
        }

        var output = SafeResult(stdout);
        var error = SafeResult(stderr);
        if (!string.IsNullOrWhiteSpace(error))
        {
            PluginLog.Debug("本地消息读取脚本 stderr：" + Truncate(error ?? string.Empty, 300));
        }

        if (process.ExitCode != 0)
        {
            PluginLog.Debug($"本地消息读取脚本退出码={process.ExitCode}：{Truncate(output ?? string.Empty, 300)}");
            return null;
        }

        return output;
    }

    private static string? Resolve32BitPowerShell()
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[]
        {
            Path.Combine(windir, "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe"),
            Path.Combine(windir, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? SafeResult(System.Threading.Tasks.Task<string> task)
    {
        try
        {
            return task.Result;
        }
        catch
        {
            return null;
        }
    }

    private static LocalMessage? ParseResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            if (!ok) return null;

            var message = new LocalMessage
            {
                Table = GetString(root, "table"),
                Title = GetString(root, "title"),
                Content = GetString(root, "content"),
                Time = GetString(root, "time"),
            };

            if (string.IsNullOrWhiteSpace(message.Title) && string.IsNullOrWhiteSpace(message.Content))
            {
                return null;
            }

            return message;
        }
        catch
        {
            return null;
        }
    }

    private static string GetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string Truncate(string text, int maxChars)
    {
        return text.Length <= maxChars ? text : text.Substring(0, maxChars) + "…";
    }
}

/// <summary>供不依赖 PluginBase 实例的组件获取插件目录/配置目录（由 Plugin 在 Initialize 时设置）。</summary>
public static class PluginPaths
{
    /// <summary>插件安装目录（ClassIsland 的 PluginInfo.PluginFolderPath）。</summary>
    public static string? PluginFolder { get; set; }

    /// <summary>插件配置目录（settings.json / logs 所在）。</summary>
    public static string? PluginConfigFolder { get; set; }
}
