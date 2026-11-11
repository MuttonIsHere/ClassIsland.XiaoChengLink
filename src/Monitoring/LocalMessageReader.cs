// ─── LocalMessageReader —— 从“小橙看班”本地数据库读取最新消息正文 ───────────
// 背景（2026-11-09 实测）：
//   · 小橙看班把消息/通知缓存在 D:\inArtBoard\Data.idb（Microsoft Jet / Access .mdb 格式）；
//   · 表结构含 HomeWork / ClassNotice / SchoolNotice / ParentMsg / WelcomeWord 等；
//   · Jet OLEDB 4.0 只有 32 位提供程序，64 位 ClassIsland 插件无法直接打开，因此调用随附的
//     read-message.ps1（32 位 Windows PowerShell）只读查询。
//
// 2026-11-11 修复：读取失败不再静默吞掉——返回 MessageReadResult.Error，由 WindowTextMonitor 直接拼到
// 提醒正文里（【正文读取失败】……），让用户一眼看出是脚本、数据库、PowerShell 还是“无新鲜消息”的问题。
// 隐私/安全：只读本地数据库副本，不上传；可用 settings.json 的 ReadMessageBodyFromDatabase=false 关闭。

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

/// <summary>正文读取结果：Success 时有 Message；失败时有 Error（用于直接显示在提醒里）。</summary>
public sealed class MessageReadResult
{
    public LocalMessage? Message { get; init; }

    public string? Error { get; init; }

    public bool Success => Message is not null;
}

/// <summary>读取小橙看班本地 Jet 数据库中的消息（通过 32 位 PowerShell 辅助脚本）。</summary>
public sealed class LocalMessageReader
{
    private const string ScriptFileName = "read-message.ps1";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(8);

    private readonly MonitorOptions _options;
    private readonly string _baseDirectory;

    public LocalMessageReader(MonitorOptions options)
    {
        _options = options;
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

    /// <summary>读取触发窗口对应的最新消息；失败时返回带 Error 的结果（不会抛出）。</summary>
    public MessageReadResult Read(string? windowClass = null, string? windowTitle = null)
    {
        if (!_options.ReadMessageBodyFromDatabase)
        {
            return new MessageReadResult();
        }

        string? tempDb = null;
        try
        {
            var scriptPath = ResolveScriptPath();
            if (scriptPath is null)
            {
                var folders = string.Join("；", CandidateScriptFolders());
                return Fail($"未找到 {ScriptFileName}。已查找：{folders}");
            }

            var sourceDb = ResolveDatabasePath(out var dbError);
            if (sourceDb is null)
            {
                return Fail(dbError ?? "未找到 Data.idb");
            }

            var tables = ResolveTablesForWindow(windowClass, windowTitle);
            if (tables == "__none__")
            {
                // 当前窗口类型没有消息正文（如班级课表），不显示“读取失败”。
                return new MessageReadResult();
            }

            tempDb = CopyDatabaseReadOnly(sourceDb, out var copyError);
            if (tempDb is null)
            {
                return Fail($"复制 Data.idb 失败：{copyError}");
            }

            var run = RunReader(scriptPath, tempDb, sourceDb, tables);
            if (run.Output is null)
            {
                return Fail(run.Error ?? "PowerShell 未返回输出");
            }

            var parsed = ParseResult(run.Output, tables, Math.Max(1, _options.MessageBodyFreshSeconds));
            if (parsed.Success)
            {
                return parsed;
            }

            return Fail(parsed.Error ?? "脚本输出无法解析");
        }
        catch (Exception ex)
        {
            return Fail("读取异常：" + ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            if (tempDb is not null)
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }

    private static MessageReadResult Fail(string error)
    {
        PluginLog.Warn("数据库正文读取失败：" + error);
        return new MessageReadResult { Error = error };
    }

    /// <summary>
    /// 窗口类名/标题 → 只查询对应的数据库表（避免不同窗口的数据串台）。
    /// 已实测：FMTfrmText2Voice（全屏播报）写入 WelcomeWord 表；FMTfrmMessageCenter（消息提醒）是普通消息汇总。
    /// 返回空字符串表示不限制表；返回 "__none__" 表示该窗口不需要正文。
    /// </summary>
    private static string ResolveTablesForWindow(string? windowClass, string? windowTitle)
    {
        var cls = windowClass ?? string.Empty;
        var title = windowTitle ?? string.Empty;

        if (cls.IndexOf("Text2Voice", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("全屏播报", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("播报", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "WelcomeWord";
        }

        if (cls.IndexOf("MessageCenter", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("消息提醒", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "HomeWork,ClassNotice,SchoolNotice,ParentMsg,WelcomeWord,ScheduledTask";
        }

        if (cls.IndexOf("ClassCourse", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "__none__";
        }

        return string.Empty;
    }

    private IEnumerable<string> CandidateScriptFolders()
    {
        var folders = new List<string>();
        if (!string.IsNullOrWhiteSpace(PluginPaths.PluginFolder)) folders.Add(PluginPaths.PluginFolder!);
        if (!string.IsNullOrWhiteSpace(_baseDirectory)) folders.Add(_baseDirectory);
        if (!string.IsNullOrWhiteSpace(PluginPaths.PluginConfigFolder)) folders.Add(PluginPaths.PluginConfigFolder!);
        return folders;
    }

    private string? ResolveScriptPath()
    {
        var candidates = new List<string>();
        var pluginFolder = PluginPaths.PluginFolder;
        if (!string.IsNullOrWhiteSpace(pluginFolder))
        {
            candidates.Add(Path.Combine(pluginFolder, ScriptFileName));
            candidates.Add(Path.Combine(pluginFolder, "tools", ScriptFileName));
        }

        candidates.Add(Path.Combine(_baseDirectory, ScriptFileName));
        candidates.Add(Path.Combine(_baseDirectory, "tools", ScriptFileName));

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
    private string? ResolveDatabasePath(out string? error)
    {
        error = null;
        var configured = _options.MessageDatabasePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
            if (File.Exists(expanded))
            {
                return expanded;
            }

            error = $"配置的 messageDatabasePath 不存在：{expanded}";
            return null;
        }

        var processes = Process.GetProcessesByName("inArt_PC");
        if (processes.Length == 0)
        {
            error = "未找到运行中的 inArt_PC 进程（请先启动小橙看班，或在设置里填 messageDatabasePath）";
            return null;
        }

        var dirs = new List<string>();
        foreach (var process in processes)
        {
            try
            {
                var exePath = process.MainModule?.FileName;
                var appDir = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath);
                if (string.IsNullOrWhiteSpace(appDir))
                {
                    continue;
                }

                dirs.Add(appDir);
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
                error = "读取 inArt_PC 路径失败：" + ex.Message;
            }
            finally
            {
                process.Dispose();
            }
        }

        error = dirs.Count == 0
            ? (error ?? "无法获取 inArt_PC 程序目录")
            : $"在 {string.Join("、", dirs)} 下未找到 Data.idb";
        return null;
    }

    /// <summary>把正在被小橙看班占用的 Data.idb 以共享读方式复制到临时文件。</summary>
    private static string? CopyDatabaseReadOnly(string sourcePath, out string? error)
    {
        error = null;
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
            error = ex.GetType().Name + "：" + ex.Message;
            return null;
        }
    }

    private ScriptRunResult RunReader(string scriptPath, string dbPath, string liveDbPath, string tables)
    {
        var powershell = Resolve32BitPowerShell();
        if (powershell is null)
        {
            return new ScriptRunResult(null, "未找到 32 位 PowerShell（SysWOW64\\WindowsPowerShell\\v1.0\\powershell.exe）");
        }

        var freshSeconds = Math.Max(1, _options.MessageBodyFreshSeconds);
        var arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                        $"-DbPath \"{dbPath}\" -FreshSeconds {freshSeconds} " +
                        $"-LiveDbPath \"{liveDbPath}\"";
        if (!string.IsNullOrWhiteSpace(tables))
        {
            arguments += $" -Tables \"{tables}\"";
        }

        Process? process = null;
        try
        {
            process = new Process
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
                return new ScriptRunResult(null, "读取超时（PowerShell > 8 秒未返回，脚本可能卡住）");
            }

            var output = SafeResult(stdout);
            var error = SafeResult(stderr);
            if (process.ExitCode != 0)
            {
                // 脚本用退出码 1 表示“没找到新鲜消息/业务失败”，但 stdout 仍是带 error 的 JSON；
                // 交给 ParseResult 转成友好文案，别把原始 JSON 直接弹给用户。
                var trimmed = (output ?? string.Empty).Trim();
                if (trimmed.StartsWith("{") && trimmed.Contains("\"ok\""))
                {
                    return new ScriptRunResult(trimmed, null);
                }

                var detail = string.IsNullOrWhiteSpace(error) ? output : error;
                return new ScriptRunResult(null,
                    $"脚本退出码={process.ExitCode}：{Truncate(detail ?? string.Empty, 220)}");
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                return new ScriptRunResult(null, "脚本退出码=0 但没有输出");
            }

            return new ScriptRunResult(output, null);
        }
        catch (Exception ex)
        {
            return new ScriptRunResult(null, "启动 PowerShell 失败：" + ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
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

    private static MessageReadResult ParseResult(string json, string tables, int freshSeconds)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new MessageReadResult { Error = "脚本输出不是 JSON 对象：" + Truncate(json, 180) };
            }

            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                var scriptError = GetString(root, "error");
                var tableScope = string.IsNullOrWhiteSpace(tables) ? "全部消息表" : tables;
                string friendly;
                if (scriptError.IndexOf("no fresh message", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = $"触发前 {freshSeconds} 秒内 Data.idb 没有新记录（可能只是窗口重绘/程序刚启动，或消息写库超过重试时间；可在设置里调大“正文新鲜度”）";
                }
                else if (scriptError.IndexOf("db file not found", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = "Data.idb 不存在（请确认小橙看班已启动，或检查数据库路径设置）";
                }
                else if (scriptError.IndexOf("no message found", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    friendly = $"数据库里没有消息（查询表={tableScope}）";
                }
                else
                {
                    friendly = string.IsNullOrWhiteSpace(scriptError)
                        ? "脚本未返回原因"
                        : "脚本报告：" + Truncate(scriptError, 160);
                }

                return new MessageReadResult { Error = friendly };
            }

            var message = new LocalMessage
            {
                Table = GetString(root, "table"),
                Title = GetString(root, "title"),
                Content = GetString(root, "content"),
                Time = GetString(root, "time"),
            };

            if (string.IsNullOrWhiteSpace(message.Title) && string.IsNullOrWhiteSpace(message.Content))
            {
                return new MessageReadResult { Error = "脚本返回的标题/正文都为空：" + Truncate(json, 180) };
            }

            return new MessageReadResult { Message = message };
        }
        catch (Exception ex)
        {
            return new MessageReadResult
            {
                Error = "解析脚本输出失败：" + ex.GetType().Name + "：" + Truncate(json, 140),
            };
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
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= maxChars ? oneLine : oneLine.Substring(0, maxChars) + "…";
    }

    private sealed class ScriptRunResult
    {
        public ScriptRunResult(string? output, string? error)
        {
            Output = output;
            Error = error;
        }

        public string? Output { get; }

        public string? Error { get; }
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
