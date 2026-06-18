using System;

namespace TransferMulti.wasm.Services;

/// <summary>
/// 日志记录器 - 支持 WASM 环境下的异步写入（避免阻塞 UI）
/// </summary>
public sealed class TransferLogger
{
    private readonly ConcurrentDictionary<string, int> _eventCounts = new();
    private const string Prefix = "[TransferMulti] ";

    public void Info(string category, string message)
    {
        var timestamp = DateTime.UtcNow.ToString("o");
        var line = $"{timestamp} [{category}] {Prefix}{message}";
        Console.WriteLine(line);
    }

    public void Error(string category, Exception ex)
    {
        Info(category, $"Exception: {ex.GetType().Name}: {ex.Message}\nStack:\n{ex.StackTrace}");
    }

    public int GetEventCount(string category) => _eventCounts.GetOrAdd(category, 0);
}
