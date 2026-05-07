using System.Collections.Concurrent;
using TransferMulti.wasm.Enums;
using TransferMulti.wasm.Models;
using TransferMulti.wasm.Services;

namespace TransferMulti.wasm.Test;

public sealed class ParallelFileTransferQueueTests
{
    [Fact]
    public void Constructor_WhenParallelLimitIsInvalid_Throws()
    {
        var files = new ConcurrentDictionary<string, FileTransferInfo>();

        Assert.Throws<ArgumentOutOfRangeException>(() => new ParallelFileTransferQueue(files, 0));
    }

    [Fact]
    public void QueueFile_WhenFileIsInit_QueuesAndResetsTransferState()
    {
        var file = CreateFile("file-1", FileTransferStateEnum.Init);
        file.TransferProgress = 42;
        file.Message = "old message";
        var queue = CreateQueue(file);

        var queued = queue.QueueFile(file);

        Assert.True(queued);
        Assert.Equal(FileTransferStateEnum.Queue, file.State);
        Assert.Equal(0, file.TransferProgress);
        Assert.Equal("", file.Message);
    }

    [Fact]
    public void QueueFile_WhenFileFailed_AllowsRetry()
    {
        var file = CreateFile("file-1", FileTransferStateEnum.Fail);
        file.TransferProgress = 80;
        file.Message = "previous failure";
        var queue = CreateQueue(file);

        var queued = queue.QueueFile(file);

        Assert.True(queued);
        Assert.Equal(FileTransferStateEnum.Queue, file.State);
        Assert.Equal(0, file.TransferProgress);
        Assert.Equal("", file.Message);
    }

    [Theory]
    [InlineData((int)FileTransferStateEnum.Queue)]
    [InlineData((int)FileTransferStateEnum.Sending)]
    [InlineData((int)FileTransferStateEnum.Sent)]
    public void QueueFile_WhenFileIsAlreadyInProgressOrSent_DoesNotQueue(int stateValue)
    {
        var state = (FileTransferStateEnum)stateValue;
        var file = CreateFile("file-1", state);
        var queue = CreateQueue(file);

        var queued = queue.QueueFile(file);

        Assert.False(queued);
        Assert.Equal(state, file.State);
    }

    [Fact]
    public void QueueFiles_QueuesOnlyEligibleFiles()
    {
        var files = new[]
        {
            CreateFile("file-1", FileTransferStateEnum.Init),
            CreateFile("file-2", FileTransferStateEnum.Fail),
            CreateFile("file-3", FileTransferStateEnum.Queue),
            CreateFile("file-4", FileTransferStateEnum.Sending),
            CreateFile("file-5", FileTransferStateEnum.Sent)
        };
        var queue = CreateQueue(files);

        var queuedCount = queue.QueueFiles(files);

        Assert.Equal(2, queuedCount);
        Assert.Equal(FileTransferStateEnum.Queue, files[0].State);
        Assert.Equal(FileTransferStateEnum.Queue, files[1].State);
        Assert.Equal(FileTransferStateEnum.Queue, files[2].State);
        Assert.Equal(FileTransferStateEnum.Sending, files[3].State);
        Assert.Equal(FileTransferStateEnum.Sent, files[4].State);
    }

    [Fact]
    public void Start_WhenCalledTwice_DoesNotCreateExtraWorkers()
    {
        using var cts = new CancellationTokenSource();
        var queue = CreateQueue(maxParallelTransfers: 2);

        queue.Start((_, _) => Task.CompletedTask, () => Task.CompletedTask, cts.Token);
        queue.Start((_, _) => Task.CompletedTask, () => Task.CompletedTask, cts.Token);

        Assert.Equal(2, queue.Workers.Count);
        cts.Cancel();
    }

    [Fact]
    public async Task Start_ProcessesNoMoreThanConfiguredTransfersAtOnce()
    {
        var files = Enumerable.Range(1, 6)
            .Select(index => CreateFile($"file-{index}", FileTransferStateEnum.Init))
            .ToArray();
        var queue = CreateQueue(files, maxParallelTransfers: 3);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var releaseTransfers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeTransfers = 0;
        var maxObservedTransfers = 0;

        queue.Start(
            async (file, cancellationToken) =>
            {
                var active = Interlocked.Increment(ref activeTransfers);
                UpdateMaxObserved(ref maxObservedTransfers, active);

                try
                {
                    await releaseTransfers.Task.WaitAsync(cancellationToken);
                    file.State = FileTransferStateEnum.Sent;
                }
                finally
                {
                    Interlocked.Decrement(ref activeTransfers);
                }
            },
            () => Task.CompletedTask,
            cts.Token);

        queue.QueueFiles(files);

        await WaitUntilAsync(
            () => Volatile.Read(ref activeTransfers) == 3,
            cts.Token);

        Assert.Equal(3, Volatile.Read(ref maxObservedTransfers));
        Assert.Equal(3, files.Count(file => file.State == FileTransferStateEnum.Sending));
        Assert.Equal(3, files.Count(file => file.State == FileTransferStateEnum.Queue));

        releaseTransfers.SetResult();

        await WaitUntilAsync(
            () => files.All(file => file.State == FileTransferStateEnum.Sent),
            cts.Token);

        Assert.Equal(3, Volatile.Read(ref maxObservedTransfers));
    }

    [Fact]
    public async Task Start_WhenOneTransferFails_MarksItFailedAndContinuesWithNextFile()
    {
        var firstFile = CreateFile("file-1", FileTransferStateEnum.Init);
        var secondFile = CreateFile("file-2", FileTransferStateEnum.Init);
        var processedFiles = new ConcurrentQueue<string>();
        var queue = CreateQueue([firstFile, secondFile], maxParallelTransfers: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        queue.Start(
            (file, _) =>
            {
                processedFiles.Enqueue(file.Id);

                if (file.Id == firstFile.Id)
                {
                    throw new InvalidOperationException("test failure");
                }

                file.State = FileTransferStateEnum.Sent;
                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            cts.Token);

        queue.QueueFiles([firstFile, secondFile]);

        await WaitUntilAsync(
            () => firstFile.State == FileTransferStateEnum.Fail
                && secondFile.State == FileTransferStateEnum.Sent,
            cts.Token);

        Assert.Contains("test failure", firstFile.Message);
        Assert.Equal(["file-1", "file-2"], processedFiles);
    }

    private static ParallelFileTransferQueue CreateQueue(
        IEnumerable<FileTransferInfo>? files = null,
        int maxParallelTransfers = 3)
    {
        var dictionary = new ConcurrentDictionary<string, FileTransferInfo>();
        foreach (var file in files ?? [])
        {
            dictionary.TryAdd(file.Id, file);
        }

        return new ParallelFileTransferQueue(dictionary, maxParallelTransfers);
    }

    private static ParallelFileTransferQueue CreateQueue(
        FileTransferInfo file,
        int maxParallelTransfers = 3)
    {
        return CreateQueue([file], maxParallelTransfers);
    }

    private static FileTransferInfo CreateFile(string id, FileTransferStateEnum state)
    {
        return new FileTransferInfo
        {
            Id = id,
            FileName = $"{id}.txt",
            FileSize = 3,
            FileContext = new List<byte> { 1, 2, 3 },
            SHA1 = "sha1",
            State = state
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
        }
    }

    private static void UpdateMaxObserved(ref int maxObservedTransfers, int currentTransfers)
    {
        while (true)
        {
            var currentMax = Volatile.Read(ref maxObservedTransfers);
            if (currentTransfers <= currentMax)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref maxObservedTransfers, currentTransfers, currentMax) == currentMax)
            {
                return;
            }
        }
    }
}
