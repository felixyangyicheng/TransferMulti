using System;
using System.Collections.Concurrent;
using System.Linq;

namespace TransferMulti.wasm.Services;

/// <summary>
/// 断点续传服务 - 管理文件传输的暂停/恢复状态
/// </summary>
public sealed class FileCheckpointService
{
    private readonly ConcurrentDictionary<string, TransferCheckpoint> _checkpoints = new();
    private const int MaxCheckpointAgeHours = 24; // 超过 24 小时的断点自动清理

    public void SaveCheckpoint(string fileId, long offset, int size, int totalChunks, int completed)
    {
        var checkpoint = new TransferCheckpoint(fileId, offset, size, totalChunks, completed);
        _checkpoints[checkpoint.FileId] = checkpoint;
    }

    public bool TryLoadCheckpoint(string fileId, out TransferCheckpoint? checkpoint)
    {
        return _checkpoints.TryGetValue(fileId, out checkpoint) &&
               checkpoint!.Timestamp.AddHours(MaxCheckpointAgeHours) > DateTime.UtcNow;
    }

    public void RemoveExpiredCheckpoints()
    {
        var expired = _checkpoints.Where(kvp => kvp.Value.Timestamp < DateTime.UtcNow - TimeSpan.FromHours(MaxCheckpointAgeHours));
        foreach (var kvp in expired)
            _checkpoints.TryRemove(kvp.Key, out _);
    }

    public int GetTotalPendingBytes() => _checkpoints.Values.Sum(c => (int)(c.Offset + c.Size));
}
