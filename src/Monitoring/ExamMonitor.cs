// ─── ExamMonitor —— 考试模式提醒（极简版，无 UI/设置）───────────────
// 轮询 32 位 PowerShell 读 ExamClock 表；开始 / 剩余15分 / 剩余5分 / 结束各提醒一次。
// 去重键：ExamGuid + 阶段；内存 HashSet，重启后可能重发一次（可接受）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using XiaoChengLink.Logging;
using XiaoChengLink.Notification;

namespace XiaoChengLink.Monitoring;

public sealed class ExamMonitor : BackgroundService
{
    private const string ScriptFileName = "read-examclock.ps1";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    private readonly MonitorOptions _options;
    private readonly ClassIslandNotifier _notifier;
    private readonly HashSet<string> _sent = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ExamMonitor(MonitorOptions options, ClassIslandNotifier notifier)
    {
        _options = options;
        _notifier = notifier;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PluginLog.Info("考试监控启动：轮询=20s");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var rows = ReadExamClocks();
                if (rows is not null)
                {
                    Evaluate(rows, GetNow());
                }
            }
            catch (Exception ex)
            {
                PluginLog.Debug("考试监控轮询失败（已忽略）：" + ex.Message);
            }

            try { await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Evaluate(List<ExamRow> rows, DateTime now)
    {
        foreach (var row in rows)
        {
            var id = string.IsNullOrWhiteSpace(row.ExamGuid) ? row.Id : row.ExamGuid;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var begin = ParseExamTime(row.Begin, row.ExamDate);
            var end = ParseExamTime(row.End, row.ExamDate);
            if (begin is null || end is null) continue;
            // 跨天：EndTime 只有时间（如 23:50 → 00:10），End 要算到第二天。
            if (end.Value <= begin.Value) end = end.Value.AddDays(1);
            if (end.Value <= begin.Value) continue;

            var course = string.IsNullOrWhiteSpace(row.Course) ? "未知科目" : row.Course.Trim();

            // 开始：只对正在进行的考试发；启动时若已在考试中也会补一次（去重保护）
            if (now >= begin.Value && now < end.Value && SendOnce(id + ":start"))
            {
                Notify($"考试已开始，科目：{course}", "#EF4444");
            }

            // 剩余 15 分（错过 15 分窗口就不补发 15 分，避免启动时连弹）
            if (now >= end.Value - TimeSpan.FromMinutes(15) && now < end.Value - TimeSpan.FromMinutes(5) && SendOnce(id + ":15"))
            {
                Notify("离考试结束还有15分钟，请注意掌控时间。", "#F59E0B");
            }

            // 剩余 5 分
            if (now >= end.Value - TimeSpan.FromMinutes(5) && now < end.Value && SendOnce(id + ":5"))
            {
                Notify($"距离 {course} 科目结束还有 5 分钟，请检查答题卡", "#F59E0B");
            }

            // 结束：只处理刚结束 10 分钟内的，避免启动时对历史考试补弹
            if (now >= end.Value && now - end.Value <= TimeSpan.FromMinutes(10) && SendOnce(id + ":end"))
            {
                Notify("考试结束，请立即停笔！", "#10B981");
            }
        }
    }

    private bool SendOnce(string key)
    {
        lock (_gate)
        {
            return _sent.Add(key);
        }
    }

    private void Notify(string text, string color)
    {
        PluginLog.Info("考试提醒：" + text);
        _ = _notifier.SendAsync(text, "考试模式", CancellationToken.None, color);
    }

    private List<ExamRow>? ReadExamClocks()
    {
        var script = FindScript();
        if (script is null) return null;

        var db = ResolveDatabasePath();
        if (db is null) return null;

        var copy = CopyDatabase(db);
        if (copy is null) return null;

        try
        {
            var ps = ResolvePowerShell();
            if (ps is null) return null;
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ps,
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -DbPath \"{copy}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            p.Start();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
            {
                try { p.Kill(); } catch { }
                return null;
            }

            var json = Safe(stdout);
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) return null;
            if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return null;

            var result = new List<ExamRow>();
            foreach (var item in rows.EnumerateArray())
            {
                result.Add(new ExamRow
                {
                    Id = Get(item, "ID"),
                    ExamGuid = Get(item, "ExamGuid"),
                    ExamDate = ParseDate(Get(item, "ExamDate")),
                    Begin = Get(item, "BeginTime"),
                    End = Get(item, "EndTime"),
                    Course = Get(item, "Course"),
                });
            }
            return result;
        }
        catch (Exception ex)
        {
            PluginLog.Debug("读取 ExamClock 失败（已忽略）：" + ex.Message);
            return null;
        }
        finally
        {
            try { File.Delete(copy); } catch { }
        }
    }

    private static string Get(JsonElement o, string name)
    {
        return o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static DateTime ParseDate(string s)
    {
        return DateTime.TryParse(s, out var d) ? d : DateTime.MinValue;
    }

    private static DateTime GetNow()
    {
        // 测试钩子：可用 XIAOCHENG_EXAM_NOW=yyyy-MM-dd HH:mm:ss 模拟时间验证 15/5/结束提醒。
        var v = Environment.GetEnvironmentVariable("XIAOCHENG_EXAM_NOW");
        return DateTime.TryParse(v, out var d) ? d : DateTime.Now;
    }

    private static DateTime? ParseExamTime(string? value, DateTime examDate)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();

        if (DateTime.TryParse(s, out var dt))
        {
            // 带日期的绝对时间（如 2026-11-11 09:00:00）直接用
            if (dt.Date != DateTime.Today || s.Contains('-') || s.Contains('/')) return dt;
        }

        if (DateTime.TryParseExact(s, new[] { "HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var tod))
        {
            var date = examDate != DateTime.MinValue ? examDate.Date : DateTime.Today;
            return date.Add(tod.TimeOfDay);
        }

        return DateTime.TryParse(s, out var fallback) ? fallback : null;
    }

    private static string? FindScript()
    {
        var dirs = new List<string>();
        try { dirs.Add(Path.GetDirectoryName(typeof(ExamMonitor).Assembly.Location) ?? ""); } catch { }
        if (!string.IsNullOrWhiteSpace(PluginPaths.PluginFolder)) dirs.Add(PluginPaths.PluginFolder!);
        if (!string.IsNullOrWhiteSpace(PluginPaths.PluginConfigFolder)) dirs.Add(PluginPaths.PluginConfigFolder!);
        foreach (var d in dirs)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            var p = Path.Combine(d, ScriptFileName);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private string? ResolveDatabasePath()
    {
        var configured = _options.MessageDatabasePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var p = Environment.ExpandEnvironmentVariables(configured.Trim());
            return File.Exists(p) ? p : null;
        }

        foreach (var proc in Process.GetProcessesByName("inArt_PC"))
        {
            try
            {
                var exe = proc.MainModule?.FileName;
                var dir = string.IsNullOrWhiteSpace(exe) ? null : Path.GetDirectoryName(exe);
                if (dir is null) continue;
                foreach (var p in new[] { Path.Combine(dir, "Data.idb"), Path.Combine(dir, "Data", "Data.idb") })
                {
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }
        return null;
    }

    private static string? CopyDatabase(string source)
    {
        try
        {
            var target = Path.Combine(Path.GetTempPath(), "XiaoChengLink", "exam-" + Guid.NewGuid().ToString("N") + ".idb");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var s = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var d = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            s.CopyTo(d);
            return target;
        }
        catch { return null; }
    }

    private static string? ResolvePowerShell()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var p in new[]
        {
            Path.Combine(win, "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe"),
            Path.Combine(win, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
        })
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? Safe(Task<string> t)
    {
        try { return t.Result; } catch { return null; }
    }

    private sealed class ExamRow
    {
        public string Id = "";
        public string ExamGuid = "";
        public DateTime ExamDate;
        public string Begin = "";
        public string End = "";
        public string Course = "";
    }
}
