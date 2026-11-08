// ─── WindowScanner —— 只读的顶层窗口枚举 ────────────────────────────────────
// 职责：用 Win32 API 枚举当前桌面的顶层窗口，按“进程名 + 窗口标题正则”筛出目标窗口，
//       返回句柄、标题、类名、可见性、矩形、PID、进程名。
// 约束：只读。不注入进程、不发窗口消息、不修改任何状态（GetWindowText/GetWindowRect 等全是只读 API）。
//
// 进程名解析：用 OpenProcess + QueryFullProcessImageNameW 做 pid -> 进程名 缓存（5 分钟 TTL），
//   而不是 System.Diagnostics.Process：
//   · 本机 csc 直编管线的引用清单里没有 System.Diagnostics.Process.dll（可加，但没必要）；
//   · 每轮轮询只对目标窗口的 pid 做一次解析，比 Process.GetProcessesByName 全量枚举更省；
//   · dotnet build（net8.0-windows）下同样可编译运行，行为一致。
//
// 本机实测（research/uia/inart_pc-probe-*.json）：
//   小橙看班进程名 inArt_PC，顶层窗口有「消息提醒」(FMTfrmMessageCenter)、「班级课表」、
//   「frmTrayIcon」等；有的窗口隐藏、标题为空，因此这里把可见性也返回给上层判断。
//
// 【需实测】
//   · QueryFullProcessImageNameW 对同用户进程可用；若目标进程以管理员权限运行，
//     OpenProcess 可能失败（本实现把该窗口跳过）。
//   · GetWindowText 对隐藏顶层窗口的返回行为（对小橙看班已实测能拿到标题，见 research/uia 数据）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using XiaoChengLink.Logging;

namespace XiaoChengLink.Monitoring;

/// <summary>一次扫描得到的单个顶层窗口快照（不可变数据载体）。</summary>
public sealed class WindowSnapshot
{
    public IntPtr Handle { get; set; }

    public uint ProcessId { get; set; }

    /// <summary>进程名（不含 .exe）。无法解析时为空字符串。</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>窗口标题（GetWindowText）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>窗口类名（GetClassName），例如 FMTfrmMessageCenter / TFMAppClass。</summary>
    public string ClassName { get; set; } = string.Empty;

    /// <summary>是否可见（IsWindowVisible）。</summary>
    public bool IsVisible { get; set; }

    public int Left { get; set; }

    public int Top { get; set; }

    public int Right { get; set; }

    public int Bottom { get; set; }

    public override string ToString()
    {
        return $"0x{Handle.ToInt64():X} '{Title}' [{ClassName}] pid={ProcessId} {ProcessName} visible={IsVisible} rect=({Left},{Top},{Right},{Bottom})";
    }
}

/// <summary>顶层窗口扫描器（线程安全：只读缓存 + 锁）。</summary>
public sealed class WindowScanner
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaxWindowTitleChars = 1024;
    private const int MaxClassNameChars = 256;
    private const int MaxChildWindows = 200;
    private const int MaxChildTextChars = 4000;
    private const int ProcessPathChars = 1024;

    private static readonly TimeSpan ProcessNameCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TitleRegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly MonitorOptions _options;
    private readonly Dictionary<uint, ProcessNameCacheEntry> _processNameCache = new();
    private readonly object _cacheGate = new();

    /// <summary>标题正则的当前快照（不可变数组；ReloadTitlePatterns 整体替换，读侧加锁取引用）。</summary>
    private Regex[] _titlePatterns = Array.Empty<Regex>();
    private readonly object _titlePatternsGate = new();

    public WindowScanner(MonitorOptions options)
    {
        _options = options;
        ReloadTitlePatterns();
    }

    /// <summary>枚举并筛选目标窗口。任何单窗口异常都不影响整轮结果。</summary>
    public IReadOnlyList<WindowSnapshot> Scan()
    {
        var handles = new List<IntPtr>(64);
        try
        {
            EnumWindows((handle, _) =>
            {
                handles.Add(handle);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            PluginLog.Error("EnumWindows 调用失败", ex);
            return Array.Empty<WindowSnapshot>();
        }

        var result = new List<WindowSnapshot>(handles.Count);
        foreach (var handle in handles)
        {
            try
            {
                var snapshot = CreateSnapshot(handle);
                if (snapshot is not null)
                {
                    result.Add(snapshot);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Debug("读取窗口信息失败（已跳过）：" + ex.Message);
            }
        }

        return result;
    }

    /// <summary>
    /// 读取一个窗口的子窗口文本（EnumChildWindows + GetWindowText）。
    /// 对 Delphi/VCL 自绘窗口通常为空；对标准 Win32 控件窗口有效。
    /// </summary>
    public string ReadChildWindowText(IntPtr parentHandle)
    {
        var builder = new StringBuilder(256);
        var count = 0;

        try
        {
            EnumChildWindows(parentHandle, (childHandle, _) =>
            {
                if (count >= MaxChildWindows || builder.Length >= MaxChildTextChars)
                {
                    return false;
                }

                count++;
                var text = ReadWindowText(childHandle, 512);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (builder.Length > 0)
                    {
                        builder.Append('\n');
                    }

                    builder.Append(text.Trim());
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            PluginLog.Debug("EnumChildWindows 失败（已忽略）：" + ex.Message);
        }

        return builder.Length > MaxChildTextChars ? builder.ToString(0, MaxChildTextChars) : builder.ToString();
    }

    private WindowSnapshot? CreateSnapshot(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var threadId = GetWindowThreadProcessId(handle, out var processId);
        if (threadId == 0 || processId == 0)
        {
            return null;
        }

        var processName = ResolveProcessName(processId);
        if (!IsProcessNameAllowed(processName))
        {
            return null;
        }

        var title = ReadWindowText(handle, MaxWindowTitleChars);
        if (!IsTitleAllowed(title))
        {
            return null;
        }

        var className = ReadClassName(handle);
        var visible = IsWindowVisible(handle);
        var hasRect = GetWindowRect(handle, out var rect);

        return new WindowSnapshot
        {
            Handle = handle,
            ProcessId = processId,
            ProcessName = processName,
            Title = title,
            ClassName = className,
            IsVisible = visible,
            Left = hasRect ? rect.Left : 0,
            Top = hasRect ? rect.Top : 0,
            Right = hasRect ? rect.Right : 0,
            Bottom = hasRect ? rect.Bottom : 0,
        };
    }

    /// <summary>
    /// 按当前 <see cref="MonitorOptions.WindowTitlePatterns"/> 重新编译窗口标题正则（线程安全）。
    /// 由构造函数调用一次；设置页保存后再次调用即可让新的标题规则立即生效（热重载）。
    /// 编译失败的单条正则会被跳过并记 WARN；整体异常只记 ERROR，保留原有规则，绝不抛出。
    /// 空列表语义不变：不做标题过滤（该进程所有顶层窗口都监控）。
    /// </summary>
    public void ReloadTitlePatterns()
    {
        try
        {
            var patterns = _options.WindowTitlePatterns;
            var compiled = new List<Regex>();
            if (patterns is not null)
            {
                foreach (var pattern in patterns)
                {
                    if (string.IsNullOrWhiteSpace(pattern))
                    {
                        continue;
                    }

                    try
                    {
                        compiled.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TitleRegexTimeout));
                    }
                    catch (ArgumentException ex)
                    {
                        PluginLog.Warn($"窗口标题正则无效（已忽略 '{pattern}'）：{ex.Message}");
                    }
                }
            }

            // 先在锁外编译好，再在锁内整体替换引用：读侧要么看到旧快照、要么看到新快照，不会看到半成品。
            lock (_titlePatternsGate)
            {
                _titlePatterns = compiled.ToArray();
            }

            PluginLog.Info($"窗口标题正则已重载：{compiled.Count} 条生效（配置 {patterns?.Length ?? 0} 条）。");
        }
        catch (Exception ex)
        {
            PluginLog.Error("重载窗口标题正则失败（保留原有规则）", ex);
        }
    }

    private bool IsProcessNameAllowed(string processName)
    {
        var allowed = _options.ProcessNames;
        if (allowed is null || allowed.Length == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(processName))
        {
            // 进程名解析失败（例如权限不足的进程）：无法确认是目标进程，跳过。
            return false;
        }

        foreach (var name in allowed)
        {
            if (!string.IsNullOrWhiteSpace(name) && string.Equals(name.Trim(), processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsTitleAllowed(string title)
    {
        // 取标题正则的当前快照（设置页可能随时热重载）：只读快照，遍历期间无需持锁。
        Regex[] patterns;
        lock (_titlePatternsGate)
        {
            patterns = _titlePatterns;
        }

        if (patterns.Length == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(title))
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            try
            {
                if (pattern.IsMatch(title))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                PluginLog.Debug("窗口标题正则匹配超时（已跳过）：" + title);
            }
        }

        return false;
    }

    private string ResolveProcessName(uint processId)
    {
        lock (_cacheGate)
        {
            if (_processNameCache.TryGetValue(processId, out var entry) && DateTime.UtcNow - entry.Timestamp < ProcessNameCacheTtl)
            {
                return entry.Name;
            }
        }

        var name = QueryProcessName(processId);

        lock (_cacheGate)
        {
            _processNameCache[processId] = new ProcessNameCacheEntry(name, DateTime.UtcNow);
        }

        return name;
    }

    private static string QueryProcessName(uint processId)
    {
        var processHandle = IntPtr.Zero;
        try
        {
            processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(ProcessPathChars);
            var size = builder.Capacity;
            if (!QueryFullProcessImageNameW(processHandle, 0, builder, ref size) || size <= 0)
            {
                return string.Empty;
            }

            var path = builder.ToString(0, size);
            try
            {
                return Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (processHandle != IntPtr.Zero)
            {
                CloseHandle(processHandle);
            }
        }
    }

    private static string ReadWindowText(IntPtr handle, int maxChars)
    {
        try
        {
            var length = GetWindowTextLengthW(handle);
            if (length <= 0)
            {
                return string.Empty;
            }

            var capacity = Math.Min(length + 1, Math.Max(maxChars, 1) + 1);
            var builder = new StringBuilder(capacity);
            var copied = GetWindowTextW(handle, builder, capacity);
            return copied > 0 ? builder.ToString(0, copied) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadClassName(IntPtr handle)
    {
        try
        {
            var builder = new StringBuilder(MaxClassNameChars);
            var copied = GetClassNameW(handle, builder, builder.Capacity);
            return copied > 0 ? builder.ToString(0, copied) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private readonly struct ProcessNameCacheEntry
    {
        public ProcessNameCacheEntry(string name, DateTime timestamp)
        {
            Name = name;
            Timestamp = timestamp;
        }

        public string Name { get; }

        public DateTime Timestamp { get; }
    }

    // ── P/Invoke（全部只读 API）──────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
