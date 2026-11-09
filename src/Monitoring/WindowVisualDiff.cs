// ─── WindowVisualDiff —— 轻量级“窗口画面变化”检测（不做 OCR）─────────────────
// 背景：小橙看班的「消息提醒」窗口是常驻窗口（截图见 research/uia/msg-flags2-*.png），
//       它的正文是 Delphi 自绘/UIA 读不到，但窗口本身通常不消失；仅靠“窗口出现”无法在
//       新消息到达时触发。本类用 PrintWindow 把窗口画到一个 32bpp DIB 上，按固定网格采样
//       亮度，比较前后两次快照的像素变化比例 —— 只回答“画面变了没有”，不识别文字。
//
// 约束（只读、无副作用）：
//   · 只用 user32/gdi32 只读 API（PrintWindow / GetWindowRect 等），不注入、不发消息、不修改目标进程；
//   · 纯 P/Invoke，不依赖 System.Drawing（插件 csc 直编时无需额外引用）；
//   · PrintWindow 对部分 DirectComposition/离屏窗口可能返回黑图（本机小橙看班正文即如此，
//     但标题区“未读数字”可被捕获），拿不到内容时哈希保持不变 → 不会误报。
//
// 【需实测】不同程序/DPI 下 PrintWindow 的可用性；阈值需按实际窗口调优（默认 0.4%）。

using System;
using System.Runtime.InteropServices;

namespace XiaoChengLink.Monitoring;

/// <summary>窗口画面快照（亮度网格）。</summary>
public sealed class WindowVisualSnapshot
{
    public WindowVisualSnapshot(int width, int height, byte[] grid)
    {
        Width = width;
        Height = height;
        Grid = grid;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>按行优先排列的亮度采样（0-255）。</summary>
    public byte[] Grid { get; }

    /// <summary>与另一快照比较，返回亮度差超过阈值的采样点占比（0-1）。尺寸不同视为完全不同。</summary>
    public double ChangedRatio(WindowVisualSnapshot other, byte luminanceThreshold = 24)
    {
        if (other is null || Width != other.Width || Height != other.Height || Grid.Length != other.Grid.Length)
        {
            return 1.0;
        }

        var changed = 0;
        var total = Grid.Length;
        for (var i = 0; i < total; i++)
        {
            var diff = Grid[i] - other.Grid[i];
            if (diff < 0)
            {
                diff = -diff;
            }

            if (diff >= luminanceThreshold)
            {
                changed++;
            }
        }

        return total == 0 ? 0.0 : (double)changed / total;
    }
}

/// <summary>用 PrintWindow + DIB 截图做窗口画面变化检测。</summary>
public static class WindowVisualDiff
{
    private const int GridWidth = 40;
    private const int GridHeight = 40;
    private const uint DibRgbColors = 0;
    private const uint PwRenderFullContent = 2;

    /// <summary>
    /// 抓取窗口画面并采样为网格。失败（窗口无效/PrintWindow 失败/尺寸异常）返回 null。
    /// 调用方需保证在任意后台线程调用安全（GDI 调用本身无 UI 线程要求）。
    /// </summary>
    public static WindowVisualSnapshot? TryCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        if (!GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
        {
            return null;
        }

        var screenDc = IntPtr.Zero;
        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        var bits = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return null;
            }

            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                return null;
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height, // 负数 = 自上而下，便于按 (y*width+x) 索引
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                },
            };

            bitmap = CreateDIBSection(memoryDc, ref info, DibRgbColors, out bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            previous = SelectObject(memoryDc, bitmap);
            var ok = PrintWindow(hwnd, memoryDc, PwRenderFullContent);
            if (!ok)
            {
                return null;
            }

            var buffer = new byte[width * height * 4];
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            return SampleGrid(buffer, width, height);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    private static WindowVisualSnapshot SampleGrid(byte[] bgra, int width, int height)
    {
        var grid = new byte[GridWidth * GridHeight];
        for (var gy = 0; gy < GridHeight; gy++)
        {
            var y = Math.Min(height - 1, (int)((gy + 0.5) * height / GridHeight));
            for (var gx = 0; gx < GridWidth; gx++)
            {
                var x = Math.Min(width - 1, (int)((gx + 0.5) * width / GridWidth));
                var index = (y * width + x) * 4;
                var b = bgra[index];
                var g = bgra[index + 1];
                var r = bgra[index + 2];
                // 亮度近似值（0.299R + 0.587G + 0.114B），整数运算避免浮点开销。
                grid[gy * GridWidth + gx] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
            }
        }

        return new WindowVisualSnapshot(GridWidth, GridHeight, grid);
    }

    // ── P/Invoke（只读：GetWindowRect / PrintWindow；GDI 对象仅用于内存截图）──────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }
}
