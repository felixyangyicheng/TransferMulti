using System;

namespace TransferMulti.wasm.Services;

/// <summary>
/// 文件分片服务 - 将大文件分割为固定大小的块，支持断点续传
/// </summary>
public sealed class FileChunkService
{
    private const int DefaultChunkSize = 1024 * 1024; // 1MB per chunk

    public (string fileId, string chunkId, long offset, int size) CreateChunks(string fileId, long fileSize)
    {
        var totalChunks = (int)Math.Ceiling(fleSize / (double)DefaultChunkSize);
        var chunkIndex = 0;
        var currentOffset = 0L;

        while (currentOffset < fileSize && chunkIndex < totalChunks)
        {
            var remaining = fileSize - currentOffset;
            var chunkSize = Math.Min(DefaultChunkSize, remaining);

            var chunkId = $"{fileId}_chunk_{chunkIndex}";

            return (
                fileId,
                chunkId,
                currentOffset,
                chunkSize
            );
        }
    }
}
