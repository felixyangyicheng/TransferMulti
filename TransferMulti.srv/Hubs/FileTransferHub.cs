using JiuLing.CommonLibs.ExtensionMethods;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using TransferMulti.srv.Models.FileTransfer;

namespace OnTransfert.srv.Hubs;

public class FileTransferHub : Hub
{
    private static readonly ConcurrentDictionary<int, ConnectionDetail> Connections = new();
    private static readonly SemaphoreSlim ConcurrencyLock = new(3, 3); // 允许3个并行传输
    private static readonly TimeSpan RoomExpiration = TimeSpan.FromMinutes(5);

    [HubMethodName("CreateConversation")]
    public async Task<int> CreateConversationAsync()
    {
        await ConcurrencyLock.WaitAsync();
        try
        {
            CleanExpiredRooms();

            int conversationId;
            do
            {
                conversationId = Random.Shared.Next(1000, 9999);
            } while (Connections.ContainsKey(conversationId));

            var sender = new SenderInfo(Context.ConnectionId);
            var expiration = DateTime.Now.Add(RoomExpiration);
            Connections[conversationId] = new ConnectionDetail(
                conversationId,
                sender,
                null,
                expiration
            );

            return conversationId;
        }
        finally
        {
            ConcurrencyLock.Release();
        }
    }

    [HubMethodName("JoinConversation")]
    public async Task<string> JoinConversationAsync(int conversationId)
    {
        if (!Connections.TryGetValue(conversationId, out var connection))
            return "对话不存在";

        if (connection.Receiver != null)
            return "对话已被占用";

        connection.Receiver = new ReceiverInfo(Context.ConnectionId);
        connection.ExpirationTime = DateTime.Now.Add(RoomExpiration);

        await Clients.Client(connection.Sender.Id).SendAsync("ReceiverJoined", conversationId);
        return "ok";
    }

    [HubMethodName("SendSenderIceCandidate")]
    public async Task<string> SendSenderIceCandidateAsync(int conversationId, string candidate)
    {
        if (!Connections.TryGetValue(conversationId, out var connection))
            return "对话不存在";

        if (connection.Receiver == null)
            return "接收方未加入";

        await Clients.Client(connection.Receiver.Id).SendAsync("ReceiveSenderIceCandidate", candidate);
        return "ok";
    }

    [HubMethodName("SendReceiverIceCandidate")]
    public async Task<string> SendReceiverIceCandidateAsync(int conversationId, string candidate)
    {
        if (!Connections.TryGetValue(conversationId, out var connection))
            return "对话不存在";

        await Clients.Client(connection.Sender.Id).SendAsync("ReceiveReceiverIceCandidate", candidate);
        return "ok";
    }

    [HubMethodName("SendOffer")]
    public async Task<string> SendOfferAsync(int conversationId, string offer)
    {
        if (!Connections.TryGetValue(conversationId, out var connection))
            return "对话不存在";

        await Clients.Client(connection.Receiver.Id).SendAsync("ReceiveOffer", offer);
        return "ok";
    }

    [HubMethodName("SendAnswer")]
    public async Task<string> SendAnswerAsync(int conversationId, string answer)
    {
        if (!Connections.TryGetValue(conversationId, out var connection))
            return "对话不存在";

        await Clients.Client(connection.Sender.Id).SendAsync("ReceiveAnswer", answer);
        return "ok";
    }

    [HubMethodName("SendFileInfo")]
    public async Task SendFileInfoAsync(int conversationId, string fileInfo)
    {
        if (Connections.TryGetValue(conversationId, out var connection))
            await Clients.Client(connection.Receiver.Id).SendAsync("ReceiveFileInfo", fileInfo);
    }

    [HubMethodName("SendFile")]
    public async Task SendFileAsync(int conversationId, byte[] buffer)
    {
        if (Connections.TryGetValue(conversationId, out var connection))
            await Clients.Client(connection.Receiver.Id).SendAsync("ReceiveFile", buffer);
    }

    [HubMethodName("SendFileSent")]
    public async Task SendFileSentAsync(int conversationId)
    {
        if (Connections.TryGetValue(conversationId, out var connection))
            await Clients.Client(connection.Receiver.Id).SendAsync("ReceiveFileSent");
    }

    private void CleanExpiredRooms()
    {
        var expiredIds = Connections
            .Where(x => x.Value.ExpirationTime < DateTime.Now)
            .Select(x => x.Key)
            .ToList();

        foreach (var id in expiredIds)
            Connections.TryRemove(id, out _);
    }
}