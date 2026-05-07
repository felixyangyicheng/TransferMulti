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

        // One worker handles one file at a time. The worker count is therefore
        // the single limit for how many files can be active together.
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
                await _queueSignal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

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
                file.State = FileTransferStateEnum.Fail;
                file.Message = $"Erreur d'envoi : {ex.Message}";
                await notifyStateChangedAsync();
            }
        }
    }
}
