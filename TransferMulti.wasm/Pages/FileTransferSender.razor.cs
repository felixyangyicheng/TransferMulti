

namespace TransferMulti.wasm.Pages
{
	public partial class FileTransferSender
	{
		private bool _isClosePage = false;
		private int _roomId;
		private bool _isLoading = false;
		private string _loadingMessage = "";
		private string _qrValue = "";

		private bool _isReceiverJoined = false;
		private ConnectionTypeEnum _connectionType = ConnectionTypeEnum.None;

		private HubConnection _hub = null!;
		private DotNetObjectReference<FileTransferSender> _objRef = null!;

		private readonly List<FileTransferInfo> _files = new List<FileTransferInfo>();

		private readonly SemaphoreSlim _fileQueueSlim = new SemaphoreSlim(3, 3);
        private readonly ConcurrentQueue<FileTransferInfo> _fileQueue = new ConcurrentQueue<FileTransferInfo>();

		protected ElementReference UploadElement { get; set; }
		protected InputFile? inputFile { get; set; }


		protected class UploadModel
		{
			public int Progress { get; set; } = 0;
			public bool Uploaded { get; set; } = false;
			public bool Deleted { get; set; }

			public string Name { get; set; } = "";

			public DateTimeOffset LastModified { get; set; }

			public long Size { get; set; }

			public string ContentType { get; set; } = "";
			public byte[] Content { get; set; } = [];

		}




		protected override async Task OnParametersSetAsync()
		{
			await base.OnInitializedAsync();
			System.Console.WriteLine($"Préparation à l'initialisation de la salle....");
			_objRef = DotNetObjectReference.Create(this);

			_hub = new HubConnectionBuilder()
				.WithUrl($"{Configuration["TransferMulti.srv"]}/file-transfer-hub").WithAutomaticReconnect()
				.Build();


            _hub.On<int>("ReceiverJoined", async (conversationId) =>  // 添加 <int> 和参数
            {
                System.Console.WriteLine("Entrée du destinataire");
                _isReceiverJoined = true;
                await InvokeAsync(StateHasChanged);
                await JSRuntime.InvokeVoidAsync("createSenderConnection");
            });

            _hub.On<string>("ReceiveReceiverIceCandidate", async (candidate) =>
			{
				System.Console.WriteLine("Réception des informations sur le candidat de la partie destinataire");
				await InvokeAsync(StateHasChanged);
				await JSRuntime.InvokeVoidAsync("receiveIceCandidate", candidate);
			});

			_hub.On<string>("ReceiveAnswer", async (answer) =>
			{
				System.Console.WriteLine("Réception de l'instruction de réponse du canal réseau");
				await JSRuntime.InvokeVoidAsync("receiveAnswer", answer);
				await InvokeAsync(StateHasChanged);
			});
			await _hub.StartAsync();

			await JSRuntime.InvokeVoidAsync("initialization", _objRef, Configuration["StunServer"]);

			_roomId = await _hub.InvokeAsync<int>("CreateConversation");
			_qrValue = $"{NavigationManager.BaseUri}file-transfer/receiver/{_roomId}";

	
			//await Task.Factory.StartNew(StartSendFileQueueAsync, TaskCreationOptions.LongRunning);
            System.Console.WriteLine("En attente de l'arrivée du destinataire....");

            // 启动3个并行 worker 任务处理队列
            var workers = new List<Task>();
            for (int i = 0; i < 3; i++)
            {
                workers.Add(Task.Factory.StartNew(ProcessFileQueueAsync, TaskCreationOptions.LongRunning));
            }
            // 可选：await Task.WhenAll(workers); 但由于无限循环，不需要
        }
        // 新 worker 方法：每个任务独立处理队列
        private async Task ProcessFileQueueAsync()
        {
            while (!_isClosePage)
            {
                if (_fileQueue.TryDequeue(out var file))
                {
                    await _fileQueueSlim.WaitAsync();
                    try  // 添加 try-catch 防止异常停止 worker
                    {
                        var currentFile = _files.First(x => x.FileName == file.FileName);
                        currentFile.State = FileTransferStateEnum.Sending;

                        // 移除 FileMetadata 转换，直接用 file
                        if (_connectionType == ConnectionTypeEnum.WebRTC)
                        {
                            await JSRuntime.InvokeVoidAsync("sendFileInfo", JsonSerializer.Serialize(file));  // 改为 file
                            await JSRuntime.InvokeVoidAsync("sendFile", file.FileContext.ToArray());
                        }
                        else if (_connectionType == ConnectionTypeEnum.ServiceRelay)
                        {
                            await _hub.InvokeAsync("SendFileInfo", _roomId, JsonSerializer.Serialize(file));  // 改为 file
                            await SendFileWithSignalRAsync(currentFile);  // 传入 file 以避免 First 查询
                        }
                        await InvokeAsync(StateHasChanged);
                    }
                    catch (Exception ex)
                    {
                        System.Console.WriteLine($"Erreur lors de l'envoi du fichier: {ex.Message}");
                        // 可选：回滚状态或通知用户
                    }
                    finally
                    {
                        _fileQueueSlim.Release();
                    }
                    await Task.Delay(1);
                }
                await Task.Delay(10);
            }
        }
        // 新方法：发送单个文件（支持 WebRTC 和 SignalR）
        // 在 SendFileAsync 中加入判断
        private async Task SendFileAsync(FileTransferInfo file)
        {
            if (_connectionType == ConnectionTypeEnum.WebRTC)
            {
                // WebRTC 单通道暂不适合高并发，强制等待上一个文件完成
                while (_files.Any(f => f.State == FileTransferStateEnum.Sending))
                {
                    await Task.Delay(100);
                }

                var fileJson = JsonSerializer.Serialize(file);
                await JSRuntime.InvokeVoidAsync("sendFileInfo", fileJson);  // 只传一个参数
                await JSRuntime.InvokeVoidAsync("sendFile", file.FileContext.ToArray());
            }
            else if (_connectionType == ConnectionTypeEnum.ServiceRelay)
            {
                await _hub.InvokeAsync("SendFileInfo", _roomId, JsonSerializer.Serialize(file));
                await SendFileWithSignalRAsync(file);
            }
            await InvokeAsync(StateHasChanged);
        }

        private async Task SendFileWithSignalRAsync(FileTransferInfo file)
        {
            int totalBytesSent = 0;
            for (int offset = 0; offset < file.FileContext.Count; offset += _chunkSize)
            {
                int remainingBytes = file.FileContext.Count - offset;
                int chunkToSend = Math.Min(_chunkSize, remainingBytes);
                byte[] chunk = new byte[chunkToSend];
                file.FileContext.CopyTo(offset, chunk, 0, chunkToSend);
                await _hub.InvokeAsync("SendFile", _roomId, chunk);
                totalBytesSent += chunkToSend;
                file.TransferProgress = (double)totalBytesSent / file.FileContext.Count * 100;
                await InvokeAsync(StateHasChanged);
                await Task.Delay(10);
            }
            await _hub.InvokeAsync("SendFileSent", _roomId);
            file.State = FileTransferStateEnum.Sent;
        }
        protected async Task OnChange(InputFileChangeEventArgs e)
		{

			var fileList = e.GetMultipleFiles(e.FileCount);

			var tasks = fileList.Select(async f => await OnSubmit(f));
			await Task.WhenAll(tasks);
		}

		protected async Task OnSubmit(IBrowserFile efile)
		{
			await LoadingAsync("Traitement du fichier en cours...");

			if (efile == null) return;

			var file = new FileTransferInfo();
			file.FileName = efile.Name;
			var ms = new MemoryStream();
			// await efile.OpenReadStream(512000 * 1000).CopyToAsync(ms);
			// var buffer = ms.ToArray();
			var buffer = new byte[1024 * 512];



			file.UploadProgress = 0;

			int count;
			int totalCount = 0;
			using var stream = efile.OpenReadStream(512000 * 1000);
			var finalBuffer = new byte[stream.Length];

			while ((count = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
			{
				Buffer.BlockCopy(buffer, 0, finalBuffer, totalCount, count);
				totalCount += count;
				file.UploadProgress = (int)(totalCount * 100.0 / stream.Length);
				StateHasChanged();
			}

			file.FileContext = new List<byte>(finalBuffer);
			file.FileSize = finalBuffer.Length;
			var hashService = HashServiceFactory.Create(HashTypeEnum.SHA1);
			file.SHA1 = await hashService.ComputeHashAsync(finalBuffer, false);
			_files.Add(file);
			StateHasChanged();

			await LoadingCompletedAsync();
		}
        [JSInvokable]
        public async Task SendIceCandidateToServer(string candidate)
        {
            System.Console.WriteLine("Prêt à envoyer les informations du candidat....");
            var result = await _hub.InvokeAsync<string>("SendSenderIceCandidate", _roomId, candidate);  // 添加 _roomId
            System.Console.WriteLine($"réponse du serveur:{result}");
        }
        [JSInvokable]
        public async Task SendOfferToServer(string offer)
        {
            System.Console.WriteLine("Prêt à envoyer l'instruction de demande de canal réseau....");
            var result = await _hub.InvokeAsync<string>("SendOffer", _roomId, offer);  // 添加 _roomId
            System.Console.WriteLine($"réponse du serveur:{result}");
        }

        [JSInvokable]
		public async Task SenderConnected()
		{
			//发送端准备就绪
			_connectionType = ConnectionTypeEnum.WebRTC;
			await InvokeAsync(StateHasChanged);
		}

        public async Task EnableServiceRelay()
        {
            _connectionType = ConnectionTypeEnum.ServiceRelay;
            var result = await _hub.InvokeAsync<string>("SwitchConnectionType", _roomId);  // 添加 _roomId
            System.Console.WriteLine($"réponse du serveur:{result}");
            await InvokeAsync(StateHasChanged);
        }

        private async void UploadFiles(IReadOnlyList<IBrowserFile> browserFiles)
		{

			IList<IBrowserFile> files = new List<IBrowserFile>();

			foreach (var browserFile in browserFiles)
			{
				if (_files.Any(x => x.FileName == browserFile.Name))
				{
					var options = new DialogOptions()
					{
						NoHeader = true
					};
					var parameters = new DialogParameters();
					parameters.Add("ContentText", "Impossible d'ajouter le fichier de façon répétée");
					await Dialog.ShowAsync<DialogOk>("Avertissement", parameters, options);
					return;
				}
				files.Add(browserFile);
			}

			await LoadingAsync("Traitement du fichier en cours...");
			var uploadTasks = browserFiles.Select(async file => await OnUploadReadStreamAsync(file));
			await Task.WhenAll(uploadTasks);
			await LoadingCompletedAsync();
		}

		protected async Task OnUploadReadStreamAsync(IBrowserFile f)
		{
			long maxFileSize = 100000000;
			if (f.Size >= maxFileSize)
			{
				var options = new DialogOptions()
				{
					NoHeader = true
				};
				var parameters = new DialogParameters();
				parameters.Add("ContentText", $"La taille du fichier {f.Name} dépasse la limite du système");
                await Dialog.ShowAsync<DialogOk>("Avertissement", parameters, options);
				return;
			}
			var file = new FileTransferInfo();
			file.FileName = f.Name;
			var ms = new MemoryStream();
			await f.OpenReadStream(maxFileSize).CopyToAsync(ms);
			var buffer = ms.ToArray();
			file.FileContext = new List<byte>(buffer);
			file.FileSize = buffer.Length;
			var hashService = HashServiceFactory.Create(HashTypeEnum.SHA1);
			file.SHA1 = await hashService.ComputeHashAsync(buffer, false);
			_files.Add(file);
			Snackbar.Add($"{file.FileName} ajouté", Severity.Info);
		}
		private async Task SendFileAsync(string fileName)
		{
			var file = _files.First(x => x.FileName == fileName);
			if (file.State != FileTransferStateEnum.Init)
			{
				return;
			}
			file.State = FileTransferStateEnum.Queue;
			_fileQueue.Enqueue(file);
			await InvokeAsync(StateHasChanged);
		}

		private async Task SendAllFilesAsync()
		{
			foreach (var file in _files)
			{
				if (file.State != FileTransferStateEnum.Init)
				{
					continue;
				}
				file.State = FileTransferStateEnum.Queue;
				_fileQueue.Enqueue(file);
			}
			await InvokeAsync(StateHasChanged);
		}

		private async Task StartSendFileQueueAsync()
		{
			while (!_isClosePage)
			{
				while (_fileQueue.TryDequeue(out var file))
				{
					await _fileQueueSlim.WaitAsync();
					_files.First(x => x.FileName == file.FileName).State = FileTransferStateEnum.Sending;
					FileMetadata fileMetadata = file as FileMetadata;
					if (_connectionType == ConnectionTypeEnum.WebRTC)
					{
						await JSRuntime.InvokeVoidAsync("sendFileInfo", JsonSerializer.Serialize(fileMetadata));
						await JSRuntime.InvokeVoidAsync("sendFile", file.FileContext.ToArray());
					}
					else if (_connectionType == ConnectionTypeEnum.ServiceRelay)
					{
                        await _hub.InvokeAsync("SendFileInfo", _roomId, JsonSerializer.Serialize(fileMetadata));  // 添加 _roomId
                        await SendFileWithSignalRAsync();
					}
					await InvokeAsync(StateHasChanged);
					await Task.Delay(1);
				}
				await Task.Delay(10);
			}
		}

		private int _chunkSize = 16384;
		private async Task SendFileWithSignalRAsync()
		{
			var file = _files.First(x => x.State == FileTransferStateEnum.Sending);
			int totalBytesSent = 0;

			for (int offset = 0; offset < file.FileContext.Count; offset += _chunkSize)
			{
				int remainingBytes = file.FileContext.Count - offset;
				int chunkToSend = Math.Min(_chunkSize, remainingBytes);
				byte[] chunk = new byte[chunkToSend];
				file.FileContext.CopyTo(offset, chunk, 0, chunkToSend);

                await _hub.InvokeAsync("SendFile", _roomId, chunk);  // 添加 _roomId

                totalBytesSent += chunkToSend;
				file.TransferProgress = (double)totalBytesSent / file.FileContext.Count * 100; ;

				await InvokeAsync(StateHasChanged);
				await Task.Delay(10);
			}
            await _hub.InvokeAsync("SendFileSent", _roomId);  // 添加 _roomId
            file.State = FileTransferStateEnum.Sent;
			_fileQueueSlim.Release();
		}

        // FileSending 和 FileSent 也改回无 fileName 参数（因为现在是串行）
        [JSInvokable]
        public async Task FileSending(int length)
        {
            var file = _files.FirstOrDefault(x => x.State == FileTransferStateEnum.Sending);
            if (file != null)
            {
                file.TransferProgress = (double)(file.FileSize - length) / file.FileSize * 100;
                await InvokeAsync(StateHasChanged);
            }
        }

        [JSInvokable]
        public async Task FileSent()
        {
            var file = _files.FirstOrDefault(x => x.State == FileTransferStateEnum.Sending);
            if (file != null)
            {
                file.State = FileTransferStateEnum.Sent;
                await InvokeAsync(StateHasChanged);
            }
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
			_isClosePage = true;
			_objRef?.Dispose();
		}

	}
}
