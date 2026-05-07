namespace TransferMulti.wasm.Pages
{
    public partial class FileTransferReceiver : IDisposable
    {
        [Parameter]
        public int RoomId { get; set; }

        private bool _initialized;
        private ConnectionTypeEnum _connectionType = ConnectionTypeEnum.None;

        private HubConnection _hub = null!;
        private DotNetObjectReference<FileTransferReceiver> _objRef = null!;
        private readonly ConcurrentDictionary<string, FileTransferInfo> _files = new();

        protected override async Task OnParametersSetAsync()
        {
            await base.OnParametersSetAsync();

            if (_initialized)
            {
                return;
            }

            _initialized = true;
            System.Console.WriteLine("Préparation à l'initialisation du module....");
            _objRef = DotNetObjectReference.Create(this);

            _hub = new HubConnectionBuilder()
                .WithUrl($"{Configuration["TransferMulti.srv"]}/file-transfer-hub")
                .Build();

            _hub.On<string>("ReceiveSenderIceCandidate", async candidate =>
            {
                System.Console.WriteLine("Réception des informations ICE de l'émetteur");
                await JSRuntime.InvokeVoidAsync("receiveIceCandidate", candidate);
            });

            _hub.On<string>("ReceiveOffer", async offer =>
            {
                System.Console.WriteLine("Réception de l'offre WebRTC");
                await JSRuntime.InvokeVoidAsync("createReceiverConnection", offer);
            });

            _hub.On("ReceiveSwitchConnectionType", async () =>
            {
                _connectionType = ConnectionTypeEnum.ServiceRelay;
                await InvokeAsync(StateHasChanged);
            });

            _hub.On<string, string>("ReceiveFileInfo", async (fileId, fileInfo) =>
            {
                await OnReceiveFileInfo(fileId, fileInfo);
            });

            _hub.On<string, byte[]>("ReceiveFile", async (fileId, buffer) =>
            {
                await OnFileReceivingAsync(buffer, fileId);
            });

            _hub.On<string>("ReceiveFileSent", async fileId =>
            {
                await OnFileReceived(fileId);
            });

            await _hub.StartAsync();
            await JSRuntime.InvokeVoidAsync("initialization", _objRef, Configuration["StunServer"]);

            var result = await _hub.InvokeAsync<string>("JoinConversation", RoomId);
            if (result != "ok")
            {
                await Dialog.ShowMessageBox("Avertissement", result, yesText: "Confirmer");
                NavigationManager.NavigateTo("/file-transfer");
            }
        }

        [JSInvokable]
        public async Task SendIceCandidateToServer(string candidate)
        {
            System.Console.WriteLine("Prêt à envoyer le candidat ICE du destinataire");
            var result = await _hub.InvokeAsync<string>("SendReceiverIceCandidate", RoomId, candidate);
            System.Console.WriteLine($"Réponse du serveur : {result}");
        }

        [JSInvokable]
        public async Task SendAnswerToServer(string answer)
        {
            System.Console.WriteLine("Prêt à envoyer la réponse WebRTC");
            var result = await _hub.InvokeAsync<string>("SendAnswer", RoomId, answer);
            System.Console.WriteLine($"Réponse du serveur : {result}");
        }

        [JSInvokable]
        public async Task WebRtcConnectionEstablished()
        {
            if (_connectionType == ConnectionTypeEnum.ServiceRelay)
            {
                return;
            }

            _connectionType = ConnectionTypeEnum.WebRTC;
            await InvokeAsync(StateHasChanged);
        }

        [JSInvokable]
        public async Task FileReceivingWithWebRTC(byte[] buffer, string fileId)
        {
            await OnFileReceivingAsync(buffer, fileId);
        }

        [JSInvokable]
        public async Task FileInfoReceived(string fileId, string fileInfo)
        {
            await OnReceiveFileInfo(fileId, fileInfo);
        }

        [JSInvokable]
        public async Task FileReceivedWithWebRTC(string fileId)
        {
            await OnFileReceived(fileId);
        }

        private async Task OnReceiveFileInfo(string fileId, string fileInfo)
        {
            var metadata = JsonSerializer.Deserialize<FileMetadata>(fileInfo);
            if (metadata is null)
            {
                return;
            }

            var file = new FileTransferInfo
            {
                Id = metadata.Id,
                FileName = metadata.FileName,
                FileSize = metadata.FileSize,
                SHA1 = metadata.SHA1,
                FileContext = new List<byte>(),
                State = FileTransferStateEnum.Sending
            };

            // Le fileId devient la clé unique commune à SignalR et WebRTC.
            // La progression reste donc correcte même quand 3 fichiers arrivent en parallèle.
            _files[fileId] = file;
            await InvokeAsync(StateHasChanged);
        }

        private async Task OnFileReceivingAsync(byte[] buffer, string fileId)
        {
            if (!_files.TryGetValue(fileId, out var file) || file.State != FileTransferStateEnum.Sending)
            {
                return;
            }

            lock (file.FileContext)
            {
                file.FileContext.AddRange(buffer);
                file.TransferProgress = file.FileSize == 0
                    ? 100
                    : (double)file.FileContext.Count / file.FileSize * 100;
            }

            await InvokeAsync(StateHasChanged);
        }

        private async Task OnFileReceived(string fileId)
        {
            if (!_files.TryGetValue(fileId, out var file) || file.State != FileTransferStateEnum.Sending)
            {
                return;
            }

            var sha1 = await HashServiceFactory.Create(HashTypeEnum.SHA1).ComputeHashAsync(file.FileContext.ToArray(), false);
            file.Succeed = file.SHA1 == sha1;
            file.Message = file.Succeed ? "" : "Échec de la vérification du fichier";
            file.State = FileTransferStateEnum.Sent;
            file.TransferProgress = 100;
            await InvokeAsync(StateHasChanged);
        }

        private async Task DownloadFileAsync(string fileName)
        {
            var file = _files.Values.FirstOrDefault(x => x.FileName == fileName);
            if (file is not null)
            {
                await JSRuntime.InvokeVoidAsync("saveToFileWithBufferAndName", fileName, file.FileContext.ToArray());
            }
        }

        public void Dispose()
        {
            _objRef?.Dispose();

            if (_hub is not null)
            {
                _ = _hub.DisposeAsync();
            }
        }
    }
}
