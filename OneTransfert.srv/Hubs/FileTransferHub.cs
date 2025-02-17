using JiuLing.CommonLibs.ExtensionMethods;
using Microsoft.AspNetCore.SignalR;
using System;
using JiuLing.CommonLibs;
using OneTransfert.srv.Models;
using OneTransfert.srv.Models.FileTransfer;

namespace OnTransfert.srv.Hubs;
public class FileTransferHub : Hub
{
    private static readonly object Locker = new();
    private static readonly List<ConnectionDetail> Connections = new();

    public FileTransferHub()
    {

    }

    [HubMethodName("CreateRoom")]
    public Task<int> CreateRoomAsync()
    {
        int roomId;
        lock (Locker)
        {
            //supprimer les salles expirées
            var time = DateTime.Now;
            Connections.RemoveAll(x => x.ExpirationTime.Subtract(time).TotalSeconds < 0);

            // supprimer les salles dont la connexion est etablie
            Connections.RemoveAll(x => x.Sender.Id == Context.ConnectionId);

            do
            {
                roomId = Convert.ToInt32(RandomUtils.GetOneByLength(4));
            } while (Connections.Any(x => x.RoomId == roomId));

            var sender = new SenderInfo(Context.ConnectionId);
            Connections.Add(new ConnectionDetail(roomId, sender, null));
        }
        return Task.FromResult(roomId);
    }

    [HubMethodName("JoinRoom")]
    public async Task<string> JoinRoomAsync(int roomId)
    {
        string senderId = "";
        lock (Locker)
        {
            // supprimer la salle dont lien est établi
            Connections.ForEach(connection =>
            {
                if (connection.Receiver != null && connection.Receiver.Id == Context.ConnectionId)
                {
                    connection.Receiver = null;
                }
            });

            var connection = Connections.FirstOrDefault(x => x.RoomId == roomId);
            if (connection == null)
            {
                return "Numéro de salle n'existe pas";
            }
            if (connection.Receiver != null)
            {
                return "salle déjà occupée";
            }
            senderId = connection.Sender.Id;
            var receiver = new ReceiverInfo(Context.ConnectionId);
            Connections.First(x => x.RoomId == roomId).Receiver = receiver;
        }

        await Clients.Client(senderId).SendAsync("ReceiverJoin");
        return "ok";
    }

    [HubMethodName("SendSenderIceCandidate")]
    public async Task<string> SendSenderIceCandidateAsync(string candidate)
    {
        string receiverId;
        lock (Locker)
        {
            var connection = Connections.FirstOrDefault(x => x.Sender.Id == Context.ConnectionId);
            if (connection == null)
            {
                return "salle n'existe pas ";
            }
            if (connection.Sender.Candidate != null)
            {
                return "création de salle dupliquée";
            }
            if (connection.Receiver == null)
            {
                return " en attente d'initialisation destinataire ";
            }
            receiverId = connection.Receiver.Id;
            connection.Sender.Candidate = candidate;
        }
        await Clients.Client(receiverId).SendAsync("ReceiveSenderIceCandidate", candidate);
        return "ok";
    }

    [HubMethodName("SendReceiverIceCandidate")]
    public async Task<string> SendReceiverIceCandidateAsync(string candidate)
    {
        string senderId = "";
        lock (Locker)
        {
            var connection = Connections.FirstOrDefault(x => x.Receiver?.Id == Context.ConnectionId);
            if (connection == null)
            {
                return "salle n'existe pas ";
            }
            if (connection.Receiver == null)
            {
                return "veuillez rejoindre une salle";
            }
            if (connection.Receiver.Candidate != null)
            {
                return " création de lien dupliqué non autorisé";
            }
            connection.Receiver.Candidate = candidate;
            senderId = connection.Sender.Id;
        }
        await Clients.Client(senderId).SendAsync("ReceiveReceiverIceCandidate", candidate);
        return "ok";
    }

    [HubMethodName("SendOffer")]
    public async Task<string> SendOfferAsync(string offer)
    {
        string receiverId;
        lock (Locker)
        {
            var connection = Connections.FirstOrDefault(x => x.Sender.Id == Context.ConnectionId);
            if (connection == null)
            {
                return "salle n'existe pas";
            }
            if (connection.Sender.Offer != null)
            {
                return "création de lien dupliquée";
            }
            if (connection.Receiver == null)
            {
                return " en attente d'initialisation destinateur ";
            }
            receiverId = connection.Receiver.Id;
            connection.Sender.Offer = offer;
        }
        await Clients.Client(receiverId).SendAsync("ReceiveOffer", offer);
        return "ok";
    }

    [HubMethodName("SendAnswer")]
    public async Task<string> SendAnswerAsync(string answer)
    {
        string senderId = "";
        lock (Locker)
        {
            var connection = Connections.FirstOrDefault(x => x.Receiver?.Id == Context.ConnectionId);
            if (connection == null)
            {
                return "salle n'existe pas";
            }

            if (connection.Receiver == null)
            {
                return "veuillez rejoindre une salle";
            }

            if (connection.Receiver.Answer != null)
            {
                return "création lien dupliqué";
            }
            connection.Receiver.Answer = answer;
            senderId = connection.Sender.Id;
        }
        await Clients.Client(senderId).SendAsync("ReceiveAnswer", answer);
        return "ok";
    }

    [HubMethodName("SwitchConnectionType")]
    public async Task<string> SwitchConnectionTypeAsync()
    {
        string receiverId;
        lock (Locker)
        {
            var connection = Connections.FirstOrDefault(x => x.Sender.Id == Context.ConnectionId);
            if (connection == null)
            {
                return "salle n'existe pas";
            }
            if (connection.Receiver == null)
            {
                return "en attente d'initialisation de destinataire";
            }
            receiverId = connection.Receiver.Id;
        }
        await Clients.Client(receiverId).SendAsync("ReceiveSwitchConnectionType");
        return "ok";
    }

    [HubMethodName("SendFileInfo")]
    public async Task SendFileInfoAsync(string fileInfo)
    {
        string receiverId = Connections.FirstOrDefault(x => x.Sender.Id == Context.ConnectionId)?.Receiver?.Id ?? "";
        if (receiverId.IsNotEmpty())
        {
            await Clients.Client(receiverId).SendAsync("ReceiveFileInfo", fileInfo);
        }
    }

    [HubMethodName("SendFile")]
    public async Task SendFileAsync(byte[] buffer)
    {
        string receiverId = Connections.FirstOrDefault(x => x.Sender.Id == Context.ConnectionId)?.Receiver?.Id ?? "";
        if (receiverId.IsNotEmpty())
        {
            await Clients.Client(receiverId).SendAsync("ReceiveFile", buffer);
        }
    }

    [HubMethodName("SendFileSent")]
    public async Task SendFileSentAsync()
    {
        string receiverId = Connections.FirstOrDefault(x => x.Sender.Id == Context.ConnectionId)?.Receiver?.Id ?? "";
        if (receiverId.IsNotEmpty())
        {
            await Clients.Client(receiverId).SendAsync("ReceiveFileSent");
        }
    }

}