namespace TransferMulti.wasm.Pages
{
    public partial class FileTransferSender : IDisposable
    {
        // 同时最多发送 3 个文件；这个值会创建 3 个并行 worker。
        private const int MaxParallelTransfers = 3;
        private const int ChunkSize = 16 * 1024;
        private const long MaxBrowserFileSize = 512_000L * 1000;

        private bool _isLoading;
        private int _roomId;
        private string _loadingMessage = "";
        private string _qrValue = "";
        private bool _isReceiverJoined;
        private ConnectionTypeEnum _connectionType = ConnectionTypeEnum.None;

        private HubConnection _hub = null!;
        private DotNetObjectReference<FileTransferSender> _objRef = null!;
        private readonly ConcurrentDictionary<string, FileTransferInfo> _files = new();
        private readonly CancellationTokenSource _transferCts = new();
        private ParallelFileTransferQueue _transferQueue = null!;

        protected ElementReference UploadElement { get; set; }
        protected InputFile? inputFile { get; set; }

        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();
            System.Console.WriteLine("Preparation a l'initialisation de la salle....");

            _objRef = DotNetObjectReference.Create(this);
            _hub = new HubConnectionBuilder()
                .WithUrl($"{Configuration["TransferMulti.srv"]}/file-transfer-hub")
                .WithAutomaticReconnect()
                .Build();

            _hub.On<int>("ReceiverJoined", async _ =>
            {
                System.Console.WriteLine("Entree du destinataire");
                _isReceiverJoined = true;
                await InvokeAsync(StateHasChanged);
                await JSRuntime.InvokeVoidAsync("createSenderConnection");
            });

            _hub.On<string>("ReceiveReceiverIceCandidate", async candidate =>
            {
                System.Console.WriteLine("Reception des informations ICE du destinataire");
                await JSRuntime.InvokeVoidAsync("receiveIceCandidate", candidate);
            });

            _hub.On<string>("ReceiveAnswer", async answer =>
            {
                System.Console.WriteLine("Reception de la reponse WebRTC");
                await JSRuntime.InvokeVoidAsync("receiveAnswer", answer);
            });

            await _hub.StartAsync();
            await JSRuntime.InvokeVoidAsync("initialization", _objRef, Configuration["StunServer"]);

            _roomId = await _hub.InvokeAsync<int>("CreateConversation");
            _qrValue = $"{NavigationManager.BaseUri}file-transfer/receiver/{_roomId}";

            _transferQueue = new ParallelFileTransferQueue(_files, MaxParallelTransfers);
            StartQueueWorkers();
            System.Console.WriteLine("En attente de l'arrivee du destinataire....");
        }

        private void StartQueueWorkers()
        {
            // 页面初始化后立即启动固定数量的 worker，后续文件只需要加入队列即可。
            _transferQueue.Start(
                TransferFileAsync,
                () => InvokeAsync(StateHasChanged),
                _transferCts.Token);
        }

        private async Task TransferFileAsync(FileTransferInfo file, CancellationToken cancellationToken)
        {
            // 队列负责并发控制；这里根据当前连接类型选择真正的传输通道。
            switch (_connectionType)
            {
                case ConnectionTypeEnum.WebRTC:
                    await SendFileWithWebRtcAsync(file);
                    return;
                case ConnectionTypeEnum.ServiceRelay:
                    await SendFileWithSignalRAsync(file, cancellationToken);
                    return;
                default:
                    file.State = FileTransferStateEnum.Fail;
                    file.Message = "Aucun canal de transfert n'est disponible.";
                    await InvokeAsync(StateHasChanged);
                    return;
            }
        }

        private async Task SendFileWithWebRtcAsync(FileTransferInfo file)
        {
            // 控制消息只发送元数据；文件内容会通过 file.Id 对应的独立 DataChannel 发送。
            await JSRuntime.InvokeVoidAsync(
                "sendFile",
                file.Id,
                CreateFileMetadataPayload(file),
                file.FileContext.ToArray());
        }

        private async Task SendFileWithSignalRAsync(FileTransferInfo file, CancellationToken cancellationToken)
        {
            await _hub.InvokeAsync("SendFileInfo", _roomId, file.Id, CreateFileMetadataPayload(file));

            var totalBytesSent = 0;
            for (var offset = 0; offset < file.FileContext.Count; offset += ChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remainingBytes = file.FileContext.Count - offset;
                var chunkToSend = Math.Min(ChunkSize, remainingBytes);
                var chunk = new byte[chunkToSend];
                file.FileContext.CopyTo(offset, chunk, 0, chunkToSend);

                await _hub.InvokeAsync("SendFile", _roomId, file.Id, chunk);

                totalBytesSent += chunkToSend;
                file.TransferProgress = file.FileSize == 0
                    ? 100
                    : (double)totalBytesSent / file.FileSize * 100;

                await InvokeAsync(StateHasChanged);
                await Task.Delay(1, cancellationToken);
            }

            await _hub.InvokeAsync("SendFileSent", _roomId, file.Id);
            file.State = FileTransferStateEnum.Sent;
            file.TransferProgress = 100;
            await InvokeAsync(StateHasChanged);
        }

        protected async Task OnChange(InputFileChangeEventArgs e)
        {
            await LoadingAsync("Traitement des fichiers en cours...");

            try
            {
                var selectedFiles = e.GetMultipleFiles(e.FileCount);
                var namesInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var readTasks = new List<Task<FileTransferInfo?>>();

                foreach (var browserFile in selectedFiles)
                {
                    if (_files.Values.Any(x => string.Equals(x.FileName, browserFile.Name, StringComparison.OrdinalIgnoreCase))
                        || !namesInBatch.Add(browserFile.Name))
                    {
                        Snackbar.Add($"Le fichier {browserFile.Name} est deja present dans la liste.", Severity.Warning);
                        continue;
                    }

                    readTasks.Add(ReadBrowserFileAsync(browserFile));
                }

                var files = await Task.WhenAll(readTasks);
                foreach (var file in files.Where(static x => x is not null))
                {
                    _files.TryAdd(file!.Id, file);
                }

                await InvokeAsync(StateHasChanged);
            }
            finally
            {
                await LoadingCompletedAsync();
            }
        }

        private async Task<FileTransferInfo?> ReadBrowserFileAsync(IBrowserFile browserFile)
        {
            if (browserFile is null)
            {
                return null;
            }

            var fileSize = checked((int)browserFile.Size);
            var file = new FileTransferInfo
            {
                Id = Guid.NewGuid().ToString(),
                FileName = browserFile.Name,
                FileSize = fileSize,
                UploadProgress = 0
            };

            var readBuffer = new byte[1024 * 512];
            var fileBuffer = new byte[fileSize];

            var totalRead = 0;
            await using var stream = browserFile.OpenReadStream(MaxBrowserFileSize);
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(readBuffer, 0, readBuffer.Length)) > 0)
            {
                Buffer.BlockCopy(readBuffer, 0, fileBuffer, totalRead, bytesRead);
                totalRead += bytesRead;
                file.UploadProgress = fileSize == 0 ? 100 : totalRead * 100d / fileSize;
                await InvokeAsync(StateHasChanged);
            }

            file.FileContext = new List<byte>(fileBuffer);
            file.SHA1 = await HashServiceFactory.Create(HashTypeEnum.SHA1).ComputeHashAsync(fileBuffer, false);
            return file;
        }

        [JSInvokable]
        public async Task SendIceCandidateToServer(string candidate)
        {
            System.Console.WriteLine("Pret a envoyer le candidat ICE de l'emetteur");
            var result = await _hub.InvokeAsync<string>("SendSenderIceCandidate", _roomId, candidate);
            System.Console.WriteLine($"Reponse du serveur : {result}");
        }

        [JSInvokable]
        public async Task SendOfferToServer(string offer)
        {
            System.Console.WriteLine("Pret a envoyer l'offre WebRTC");
            var result = await _hub.InvokeAsync<string>("SendOffer", _roomId, offer);
            System.Console.WriteLine($"Reponse du serveur : {result}");
        }

        [JSInvokable]
        public async Task WebRtcConnectionEstablished()
        {
            // WebRTC 连接建立后，队列仍然负责限制同时活跃的文件数量。
            if (_connectionType == ConnectionTypeEnum.ServiceRelay)
            {
                return;
            }

            _connectionType = ConnectionTypeEnum.WebRTC;
            await InvokeAsync(StateHasChanged);
        }

        public async Task EnableServiceRelay()
        {
            _connectionType = ConnectionTypeEnum.ServiceRelay;
            var result = await _hub.InvokeAsync<string>("SwitchConnectionType", _roomId);
            System.Console.WriteLine($"Reponse du serveur : {result}");
            await InvokeAsync(StateHasChanged);
        }

        private async Task SendFileAsync(string fileName)
        {
            var file = _files.Values.FirstOrDefault(x => x.FileName == fileName);
            if (file is null)
            {
                return;
            }

            QueueFile(file);
            await InvokeAsync(StateHasChanged);
        }

        private async Task SendAllFilesAsync()
        {
            // 批量发送时只是把所有可发送文件入队，worker 会自动按 3 个一组并行处理。
            _transferQueue.QueueFiles(_files.Values.OrderBy(x => x.FileName));
            await InvokeAsync(StateHasChanged);
        }

        private void QueueFile(FileTransferInfo file)
        {
            // 单个文件发送也复用同一个队列，保证不会绕过并发上限。
            _transferQueue.QueueFile(file);
        }

        [JSInvokable]
        public async Task FileSending(string fileId, int remainingBytes)
        {
            if (!_files.TryGetValue(fileId, out var file) || file.State != FileTransferStateEnum.Sending)
            {
                return;
            }

            file.TransferProgress = file.FileSize == 0
                ? 100
                : (double)(file.FileSize - remainingBytes) / file.FileSize * 100;

            await InvokeAsync(StateHasChanged);
        }

        [JSInvokable]
        public async Task FileSent(string fileId)
        {
            if (!_files.TryGetValue(fileId, out var file) || file.State != FileTransferStateEnum.Sending)
            {
                return;
            }

            file.State = FileTransferStateEnum.Sent;
            file.TransferProgress = 100;
            await InvokeAsync(StateHasChanged);
        }

        private static string CreateFileMetadataPayload(FileTransferInfo file)
        {
            return JsonSerializer.Serialize(new FileMetadata
            {
                Id = file.Id,
                FileName = file.FileName,
                FileSize = file.FileSize,
                SHA1 = file.SHA1
            });
        }

        private async Task LoadingAsync(string message)
        {
            _isLoading = true;
            _loadingMessage = message;
            await InvokeAsync(StateHasChanged);
        }

        private async Task LoadingCompletedAsync()
        {
            _isLoading = false;
            _loadingMessage = "";
            await InvokeAsync(StateHasChanged);
        }

        public void Dispose()
        {
            _transferCts.Cancel();
            _objRef?.Dispose();

            if (_hub is not null)
            {
                _ = _hub.DisposeAsync();
            }
        }
    }
}
