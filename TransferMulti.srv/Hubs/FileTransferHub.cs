using JiuLing.CommonLibs.ExtensionMethods;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using TransferMulti.srv.Models.FileTransfer;

namespace OnTransfert.srv.Hubs;

public class FileTransferHub : Hub
{
    private static readonly ConcurrentDictionary<int, ConnectionDetail> Connections = new();
    private static readonly TimeSpan RoomExpiration = TimeSpan.FromMinutes(5);

    [HubMethodName("CreateConversation")]
    public Task<int> CreateConversationAsync()
    {
        CleanExpiredRooms();

        while (true)
        {
            var conversationId = Random.Shared.Next(1000, 9999);
            var sender = new SenderInfo(Context.ConnectionId);
            var expiration = DateTime.Now.Add(RoomExpiration);
            var connection = new ConnectionDetail(conversationId, sender, null, expiration);

            // La simultanéité des transferts ne doit pas être bloquée par la création de salle.
            // TryAdd suffit ici pour garantir un identifiant unique sans sérialiser tout le hub.
            if (Connections.TryAdd(conversationId, connection))
            {
                return Task.FromResult(conversationId);
            }
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
        if (!Connections.TryGetValue(conversationId, out var connection) || connection.Receiver == null)
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

    [HubMethodName("SwitchConnectionType")]
    public async Task<string> SwitchConnectionTypeAsync(int conversationId)
    {
        if (!Connections.TryGetValue(conversationId, out var connection))
            return "对话不存在";

        if (connection.Receiver == null)
            return "接收方未加入";

        await Clients.Client(connection.Receiver.Id).SendAsync("ReceiveSwitchConnectionType");
        return "ok";
    }

    [HubMethodName("SendFileInfo")]
    public async Task SendFileInfoAsync(int conversationId, string fileId, string fileInfo)
    {
        if (Connections.TryGetValue(conversationId, out var connection) && connection.Receiver != null)
        {
            await Clients.Client(connection.Receiver.Id).SendAsync("ReceiveFileInfo", fileId, fileInfo);
        }
    }

    [HubMethodName("SendFile")]
    public async Task SendFileAsync(int conversationId, string fileId, byte[] buffer)
    {
        if (Connections.TryGetValue(conversationId, out var connection) && connection.Receiver != null)
        {
            // Le hub relaie les chunks avec leur fileId.
            // Les messages de plusieurs fichiers peuvent donc s'entrelacer sans être confondus côté client.
            await Clients.Client(connection.Receiver.Id).SendAsync("ReceiveFile", fileId, buffer);
        }
    }

    [HubMethodName("SendFileSent")]
    public async Task SendFileSentAsync(int conversationId, string fileId)
    {
        if (Connections.TryGetValue(conversationId, out var connection) && connection.Receiver != null)
        {
            await Clients.Client(connection.Receiver.Id).SendAsync("ReceiveFileSent", fileId);
        }
    }

    private void CleanExpiredRooms()
    {
        var expiredIds = Connections
            .Where(x => x.Value.ExpirationTime < DateTime.Now)
            .Select(x => x.Key)
            .ToList();

        foreach (var id in expiredIds)
        {
            Connections.TryRemove(id, out _);
        }
    }
}
