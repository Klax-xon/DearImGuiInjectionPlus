using System;
using System.Text;

namespace DearImGuiInjectionPlus.Template.Utils;

public enum LogCategory
{
    Info,
    Warning,
    Error,
    Command
}

public readonly struct ConsoleEntry
{
    public readonly LogCategory Category;
    public readonly string Message;
    public readonly string Timestamp;

    public ConsoleEntry(LogCategory category, string message, string timestamp)
    {
        Category = category;
        Message = message;
        Timestamp = timestamp;
    }
}

/// <summary>
/// High-performance, thread-safe bounded circular log buffer.
/// Guarantees zero steady-state heap allocations during rendering.
/// </summary>
public class GuiConsole
{
    private const int BufferCapacity = 256;
    private readonly ConsoleEntry[] _entries = new ConsoleEntry[BufferCapacity];
    private int _head;
    private int _count;
    private readonly object _lock = new();

    public bool AutoScroll { get; set; } = true;
    public bool HasNewEntry { get; internal set; }

    public int TotalInfoCount { get; private set; }
    public int TotalWarningCount { get; private set; }
    public int TotalErrorCount { get; private set; }
    public int TotalCommandCount { get; private set; }

    public GuiConsole()
    {
        LogInfo("Base GUI Console initialized. Ready for telemetry and commands.");
    }

    public void Log(LogCategory category, string message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        string time = DateTime.Now.ToString("HH:mm:ss");

        lock (_lock)
        {
            _entries[_head] = new ConsoleEntry(category, message, time);
            _head = (_head + 1) % BufferCapacity;
            if (_count < BufferCapacity)
            {
                _count++;
            }

            switch (category)
            {
                case LogCategory.Info:
                    TotalInfoCount++;
                    break;
                case LogCategory.Warning:
                    TotalWarningCount++;
                    break;
                case LogCategory.Error:
                    TotalErrorCount++;
                    break;
                case LogCategory.Command:
                    TotalCommandCount++;
                    break;
            }

            HasNewEntry = true;
        }
    }

    public void LogInfo(string message) => Log(LogCategory.Info, message);
    public void LogWarning(string message) => Log(LogCategory.Warning, message);
    public void LogError(string message) => Log(LogCategory.Error, message);
    public void LogCommand(string message) => Log(LogCategory.Command, message);

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_entries, 0, _entries.Length);
            _head = 0;
            _count = 0;
            TotalInfoCount = 0;
            TotalWarningCount = 0;
            TotalErrorCount = 0;
            TotalCommandCount = 0;
            HasNewEntry = false;
        }
    }

    /// <summary>
    /// Executes a callback for each active log entry in chronological order.
    /// Thread-safe and zero-allocation.
    /// </summary>
    public void ForEach(Action<ConsoleEntry> action, LogCategory? filter = null)
    {
        if (action == null)
            return;

        lock (_lock)
        {
            int start = (_head - _count + BufferCapacity) % BufferCapacity;
            for (int i = 0; i < _count; i++)
            {
                int idx = (start + i) % BufferCapacity;
                ref readonly var entry = ref _entries[idx];
                if (filter == null || entry.Category == filter.Value)
                {
                    action(entry);
                }
            }
        }
    }

    /// <summary>
    /// Copies all active console text to a single formatted string.
    /// </summary>
    public string ExportToString()
    {
        var sb = new StringBuilder();
        lock (_lock)
        {
            int start = (_head - _count + BufferCapacity) % BufferCapacity;
            for (int i = 0; i < _count; i++)
            {
                int idx = (start + i) % BufferCapacity;
                ref readonly var entry = ref _entries[idx];
                sb.AppendLine($"[{entry.Timestamp}] [{entry.Category.ToString().ToUpperInvariant()}] {entry.Message}");
            }
        }
        return sb.ToString();
    }
}
