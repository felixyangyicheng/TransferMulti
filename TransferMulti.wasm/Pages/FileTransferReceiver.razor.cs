using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using System.Collections.Concurrent;
using System.Text.Json;
using TransferMulti.wasm.Enums;
using TransferMulti.wasm.Models;
using TransferMulti.wasm.Services;

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

        [Inject]
        private ILogger<FileTransferReceiver> Logger { get; set; } = null!;

        protected override async Task OnParametersSetAsync()
        {
            await base.OnParametersSetAsync();

            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Logger.LogInformation("准备初始化接收端模块，房间号: {RoomId}", RoomId);
            _objRef = DotNetObjectReference.Create(this);

            _hub = new HubConnectionBuilder()
                .WithUrl($"{Configuration["TransferMulti.srv"]}/file-transfer-hub")
                .Build();

            _hub.On<string>("ReceiveSenderIceCandidate", async candidate =>
            {
                Logger.LogDebug("收到发送方 ICE 候选");
                await JSRuntime.InvokeVoidAsync("receiveIceCandidate", candidate);
            });

            _hub.On<string>("ReceiveOffer", async offer =>
            {
                Logger.LogDebug("收到 WebRTC Offer");
                await JSRuntime.InvokeVoidAsync("createReceiverConnection", offer);
            });

            _hub.On("ReceiveSwitchConnectionType", async () =>
            {
                _connectionType = ConnectionTypeEnum.ServiceRelay;
                Logger.LogInformation("发送方已切换到服务端中继模式");
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
                Logger.LogWarning("加入房间失败: {Reason}", result);
                await Dialog.ShowMessageBox("Avertissement", result, yesText: "Confirmer");
                NavigationManager.NavigateTo("/file-transfer");
            }
        }

        [JSInvokable]
        public async Task SendIceCandidateToServer(string candidate)
        {
            Logger.LogDebug("发送 ICE 候选到服务器");
            var result = await _hub.InvokeAsync<string>("SendReceiverIceCandidate", RoomId, candidate);
            Logger.LogDebug("服务器响应: {Result}", result);
        }

        [JSInvokable]
        public async Task SendAnswerToServer(string answer)
        {
            Logger.LogDebug("发送 WebRTC Answer");
            var result = await _hub.InvokeAsync<string>("SendAnswer", RoomId, answer);
            Logger.LogDebug("服务器响应: {Result}", result);
        }

        [JSInvokable]
        public async Task WebRtcConnectionEstablished()
        {
            if (_connectionType == ConnectionTypeEnum.ServiceRelay)
            {
                return;
            }

            _connectionType = ConnectionTypeEnum.WebRTC;
            Logger.LogInformation("WebRTC 连接已建立");
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

            // 预先分配精确大小的 byte[]，避免 List<byte> 的扩容开销
            var file = new FileTransferInfo
            {
                Id = metadata.Id,
                FileName = metadata.FileName,
                FileSize = metadata.FileSize,
                SHA1 = metadata.SHA1,
                FileContext = metadata.FileSize > 0 ? new byte[(int)metadata.FileSize] : [],
                ReceivedBytes = 0,
                State = FileTransferStateEnum.Sending
            };

            _files[fileId] = file;
            Logger.LogInformation("开始接收文件: {FileName} ({FileSize} bytes)", metadata.FileName, metadata.FileSize);
            await InvokeAsync(StateHasChanged);
        }

        private async Task OnFileReceivingAsync(byte[] buffer, string fileId)
        {
            if (!_files.TryGetValue(fileId, out var file) || file.State != FileTransferStateEnum.Sending)
            {
                return;
            }

            lock (file.LockObject)
            {
                Buffer.BlockCopy(buffer, 0, file.FileContext!, (int)file.ReceivedBytes, buffer.Length);
                file.ReceivedBytes += buffer.Length;
                file.TransferProgress = file.FileSize == 0
                    ? 100
                    : (double)file.ReceivedBytes / file.FileSize * 100;
            }

            await InvokeAsync(StateHasChanged);
        }

        private async Task OnFileReceived(string fileId)
        {
            if (!_files.TryGetValue(fileId, out var file) || file.State != FileTransferStateEnum.Sending)
            {
                return;
            }

            Logger.LogInformation("文件接收完成，校验哈希: {FileName}", file.FileName);
            var sha1 = await HashServiceFactory.Create(HashTypeEnum.SHA1).ComputeHashAsync(file.FileContext!, false);
            file.Succeed = file.SHA1 == sha1;
            file.Message = file.Succeed ? "" : "Échec de la vérification du fichier";
            file.State = FileTransferStateEnum.Sent;
            file.TransferProgress = 100;

            if (file.Succeed)
            {
                Logger.LogInformation("文件校验成功: {FileName}", file.FileName);
            }
            else
            {
                Logger.LogWarning("文件校验失败: {FileName}", file.FileName);
            }

            await InvokeAsync(StateHasChanged);
        }

        private async Task DownloadFileAsync(string fileName)
        {
            var file = _files.Values.FirstOrDefault(x => x.FileName == fileName);
            if (file is not null)
            {
                await JSRuntime.InvokeVoidAsync("saveToFileWithBufferAndName", fileName, file.FileContext!);
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
