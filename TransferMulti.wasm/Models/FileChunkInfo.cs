using System;

namespace TransferMulti.wasm.Models;

/// <summary>
/// 文件分片信息 - 用于断点续传的分片管理
/// </summary>
public sealed class FileChunkInfo
{
    public string FileId { get; set; } = null!;
    public string ChunkId { get; set; } = null!;
    public long Offset { get; set; }
    public int Size => _size;
    private int _size;

    public void SetSize(int size) => _size = size;
}
