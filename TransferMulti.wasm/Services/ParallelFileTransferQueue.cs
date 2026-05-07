using System.Collections.Concurrent;
using TransferMulti.wasm.Enums;
using TransferMulti.wasm.Models;

namespace TransferMulti.wasm.Services;

internal sealed class ParallelFileTransferQueue
{
    private readonly ConcurrentDictionary<string, FileTransferInfo> _files;
    private readonly ConcurrentQueue<string> _fileQueue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly List<Task> _workers = new();

    public ParallelFileTransferQueue(
        ConcurrentDictionary<string, FileTransferInfo> files,
        int maxParallelTransfers)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (maxParallelTransfers < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxParallelTransfers),
                "The parallel transfer limit must be at least 1.");
        }

        _files = files;
        MaxParallelTransfers = maxParallelTransfers;
    }

    public int MaxParallelTransfers { get; }

    public IReadOnlyList<Task> Workers => _workers;

    public void Start(
        Func<FileTransferInfo, CancellationToken, Task> transferFileAsync,
        Func<Task> notifyStateChangedAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transferFileAsync);
        ArgumentNullException.ThrowIfNull(notifyStateChangedAsync);

        if (_workers.Count > 0)
        {
            return;
        }

        // 每个 worker 一次只处理一个文件，所以 worker 的数量就是同时传输文件的上限。
        for (var index = 0; index < MaxParallelTransfers; index++)
        {
            _workers.Add(Task.Run(
                () => ProcessFileQueueAsync(transferFileAsync, notifyStateChangedAsync, cancellationToken),
                cancellationToken));
        }
    }

    public bool QueueFile(FileTransferInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);

        // 只有初始状态或失败状态的文件可以进入队列，避免同一个文件被重复发送。
        if (file.State is not (FileTransferStateEnum.Init or FileTransferStateEnum.Fail))
        {
            return false;
        }

        file.State = FileTransferStateEnum.Queue;
        file.TransferProgress = 0;
        file.Message = "";
        _fileQueue.Enqueue(file.Id);
        _queueSignal.Release();
        return true;
    }

    public int QueueFiles(IEnumerable<FileTransferInfo> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var queuedCount = 0;
        foreach (var file in files)
        {
            if (QueueFile(file))
            {
                queuedCount++;
            }
        }

        return queuedCount;
    }

    private async Task ProcessFileQueueAsync(
        Func<FileTransferInfo, CancellationToken, Task> transferFileAsync,
        Func<Task> notifyStateChangedAsync,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 没有文件时 worker 会等待信号；有新文件入队时 QueueFile 会释放信号。
                await _queueSignal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // 如果文件已被删除、状态已变化或队列竞争失败，就跳过并等待下一个文件。
            if (!_fileQueue.TryDequeue(out var fileId)
                || !_files.TryGetValue(fileId, out var file)
                || file.State != FileTransferStateEnum.Queue)
            {
                continue;
            }

            file.State = FileTransferStateEnum.Sending;
            file.TransferProgress = 0;
            file.Message = "";
            await notifyStateChangedAsync();

            try
            {
                await transferFileAsync(file, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 单个文件失败不会停止整个队列，worker 会继续处理后面的文件。
                file.State = FileTransferStateEnum.Fail;
                file.Message = $"Erreur d'envoi : {ex.Message}";
                await notifyStateChangedAsync();
            }
        }
    }
}
