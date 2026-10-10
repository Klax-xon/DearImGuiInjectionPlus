using System;

namespace DearImGuiInjectionPlus;

internal interface ILog
{
    void Debug(object data);
    void Error(object data);
    void Fatal(object data);
    void Info(object data);
    void Message(object data);
    void Warning(object data);
}

internal static class Log
{
    private static ILog _log;

    internal static void Init(ILog log)
    {
        _log = log;
    }

    internal static void Debug(object data) => _log?.Debug(data);
    internal static void Error(object data) => _log?.Error(data);
    internal static void Fatal(object data) => _log?.Fatal(data);
    internal static void Info(object data) => _log?.Info(data);
    internal static void Message(object data) => _log?.Message(data);
    internal static void Warning(object data) => _log?.Warning(data);
}