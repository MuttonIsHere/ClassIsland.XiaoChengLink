namespace XiaoChengLink.Monitoring;

/// <summary>
/// 考试模式状态（由 <see cref="ExamMonitor"/> 每轮轮询时更新，供提醒静默消费器读取）。
/// 线程安全：bool / string 引用赋值都是原子的。
/// </summary>
public sealed class ExamModeState
{
    private volatile bool _isActive;
    private string _course = string.Empty;

    /// <summary>当前是否处于考试进行中。</summary>
    public bool IsActive => _isActive;

    /// <summary>当前考试科目（无考试时为空）。</summary>
    public string CurrentCourse => _course;

    /// <summary>最近一次状态变化时间（UTC）。</summary>
    public DateTime LastChangedUtc { get; private set; } = DateTime.MinValue;

    public void Update(bool active, string? course)
    {
        var text = course?.Trim() ?? string.Empty;
        var changed = active != _isActive || !string.Equals(text, _course, StringComparison.Ordinal);

        _isActive = active;
        _course = text;

        if (changed)
        {
            LastChangedUtc = DateTime.UtcNow;
        }
    }
}
