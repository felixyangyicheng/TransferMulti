using System;

namespace TransferMulti.wasm.Models;

/// <summary>
/// 传输断点记录 - 保存文件传输的暂停状态
/// </summary>
public sealed class TransferCheckpoint
{
    public string FileId { get; set; } = null!;
    public long Offset { get; set; }
    public int Size => _size;
    private int _size;
    public int TotalChunks { get; set; }
    public int CompletedChunks { get; set; }
    public DateTime Timestamp => _timestamp;
    private DateTime _timestamp = DateTime.UtcNow;

    public TransferCheckpoint() { }

    public TransferCheckpoint(string fileId, long offset, int size, int totalChunks, int completedChunks)
    {
        FileId = fileId;
        Offset = offset;
        _size = size;
        TotalChunks = totalChunks;
        CompletedChunks = completedChunks;
        _timestamp = DateTime.UtcNow;
    }

    public void SetSize(int size) => _size = size;
}
