using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using OnTransfert.srv.Hubs;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TransferMulti.srv.Test;

public sealed class FileTransferHubTests : IDisposable
{
    public FileTransferHubTests()
    {
        ClearRooms();
    }

    public void Dispose()
    {
        ClearRooms();
    }

    [Fact]
    public async Task CreateConversationAsync_ReturnsFourDigitRoomId()
    {
        var clients = new RecordingHubCallerClients();
        var hub = CreateHub("sender-1", clients);

        var roomId = await hub.CreateConversationAsync();

        Assert.InRange(roomId, 1000, 9998);
        Assert.Empty(clients.Invocations);
    }

    [Fact]
    public async Task JoinConversationAsync_WhenRoomExists_ReturnsOkAndNotifiesSender()
    {
        var senderClients = new RecordingHubCallerClients();
        var senderHub = CreateHub("sender-1", senderClients);
        var roomId = await senderHub.CreateConversationAsync();

        var receiverHub = CreateHub("receiver-1", senderClients);
        var result = await receiverHub.JoinConversationAsync(roomId);

        Assert.Equal("ok", result);
        var invocation = Assert.Single(senderClients.Invocations);
        Assert.Equal("sender-1", invocation.ConnectionId);
        Assert.Equal("ReceiverJoined", invocation.Method);
        Assert.Equal(roomId, Assert.IsType<int>(Assert.Single(invocation.Arguments)));
    }

    [Fact]
    public async Task JoinConversationAsync_WhenRoomDoesNotExist_ReturnsError()
    {
        var clients = new RecordingHubCallerClients();
        var hub = CreateHub("receiver-1", clients);

        var result = await hub.JoinConversationAsync(1234);

        Assert.NotEqual("ok", result);
        Assert.Empty(clients.Invocations);
    }

    [Fact]
    public async Task JoinConversationAsync_WhenRoomAlreadyHasReceiver_ReturnsError()
    {
        var clients = new RecordingHubCallerClients();
        var senderHub = CreateHub("sender-1", clients);
        var roomId = await senderHub.CreateConversationAsync();

        Assert.Equal("ok", await CreateHub("receiver-1", clients).JoinConversationAsync(roomId));
        clients.Invocations.Clear();

        var result = await CreateHub("receiver-2", clients).JoinConversationAsync(roomId);

        Assert.NotEqual("ok", result);
        Assert.Empty(clients.Invocations);
    }

    [Fact]
    public async Task SendSenderIceCandidateAsync_WhenReceiverJoined_ForwardsCandidateToReceiver()
    {
        var (hub, clients, roomId) = await CreateJoinedConversationAsync();

        var result = await hub.SendSenderIceCandidateAsync(roomId, "sender-candidate");

        Assert.Equal("ok", result);
        AssertSingleInvocation(clients, "receiver-1", "ReceiveSenderIceCandidate", "sender-candidate");
    }

    [Fact]
    public async Task SendSenderIceCandidateAsync_WhenReceiverHasNotJoined_ReturnsError()
    {
        var clients = new RecordingHubCallerClients();
        var hub = CreateHub("sender-1", clients);
        var roomId = await hub.CreateConversationAsync();

        var result = await hub.SendSenderIceCandidateAsync(roomId, "sender-candidate");

        Assert.NotEqual("ok", result);
        Assert.Empty(clients.Invocations);
    }

    [Fact]
    public async Task SendReceiverIceCandidateAsync_ForwardsCandidateToSender()
    {
        var (hub, clients, roomId) = await CreateJoinedConversationAsync();

        var result = await hub.SendReceiverIceCandidateAsync(roomId, "receiver-candidate");

        Assert.Equal("ok", result);
        AssertSingleInvocation(clients, "sender-1", "ReceiveReceiverIceCandidate", "receiver-candidate");
    }

    [Fact]
    public async Task SendOfferAsync_ForwardsOfferToReceiver()
    {
        var (hub, clients, roomId) = await CreateJoinedConversationAsync();

        var result = await hub.SendOfferAsync(roomId, "offer-json");

        Assert.Equal("ok", result);
        AssertSingleInvocation(clients, "receiver-1", "ReceiveOffer", "offer-json");
    }

    [Fact]
    public async Task SendAnswerAsync_ForwardsAnswerToSender()
    {
        var (hub, clients, roomId) = await CreateJoinedConversationAsync();

        var result = await hub.SendAnswerAsync(roomId, "answer-json");

        Assert.Equal("ok", result);
        AssertSingleInvocation(clients, "sender-1", "ReceiveAnswer", "answer-json");
    }

    [Fact]
    public async Task SwitchConnectionTypeAsync_ForwardsSwitchToReceiver()
    {
        var (hub, clients, roomId) = await CreateJoinedConversationAsync();

        var result = await hub.SwitchConnectionTypeAsync(roomId);

        Assert.Equal("ok", result);
        var invocation = Assert.Single(clients.Invocations);
        Assert.Equal("receiver-1", invocation.ConnectionId);
        Assert.Equal("ReceiveSwitchConnectionType", invocation.Method);
        Assert.Empty(invocation.Arguments);
    }

    [Fact]
    public async Task FileTransferMethods_ForwardFileIdAndPayloadToReceiver()
    {
        var (hub, clients, roomId) = await CreateJoinedConversationAsync();
        var buffer = new byte[] { 1, 2, 3 };

        await hub.SendFileInfoAsync(roomId, "file-1", "metadata");
        await hub.SendFileAsync(roomId, "file-1", buffer);
        await hub.SendFileSentAsync(roomId, "file-1");

        Assert.Collection(
            clients.Invocations,
            invocation =>
            {
                Assert.Equal("receiver-1", invocation.ConnectionId);
                Assert.Equal("ReceiveFileInfo", invocation.Method);
                Assert.Equal(["file-1", "metadata"], invocation.Arguments);
            },
            invocation =>
            {
                Assert.Equal("receiver-1", invocation.ConnectionId);
                Assert.Equal("ReceiveFile", invocation.Method);
                Assert.Equal("file-1", invocation.Arguments[0]);
                Assert.Same(buffer, invocation.Arguments[1]);
            },
            invocation =>
            {
                Assert.Equal("receiver-1", invocation.ConnectionId);
                Assert.Equal("ReceiveFileSent", invocation.Method);
                Assert.Equal("file-1", Assert.Single(invocation.Arguments));
            });
    }

    [Fact]
    public async Task FileTransferMethods_WhenReceiverMissing_DoNotSendMessages()
    {
        var clients = new RecordingHubCallerClients();
        var hub = CreateHub("sender-1", clients);
        var roomId = await hub.CreateConversationAsync();

        await hub.SendFileInfoAsync(roomId, "file-1", "metadata");
        await hub.SendFileAsync(roomId, "file-1", [1, 2, 3]);
        await hub.SendFileSentAsync(roomId, "file-1");

        Assert.Empty(clients.Invocations);
    }

    private static async Task<(FileTransferHub Hub, RecordingHubCallerClients Clients, int RoomId)> CreateJoinedConversationAsync()
    {
        var clients = new RecordingHubCallerClients();
        var senderHub = CreateHub("sender-1", clients);
        var roomId = await senderHub.CreateConversationAsync();
        await CreateHub("receiver-1", clients).JoinConversationAsync(roomId);
        clients.Invocations.Clear();

        return (senderHub, clients, roomId);
    }

    private static FileTransferHub CreateHub(string connectionId, RecordingHubCallerClients clients)
    {
        return new FileTransferHub
        {
            Context = new TestHubCallerContext(connectionId),
            Clients = clients
        };
    }

    private static void AssertSingleInvocation(
        RecordingHubCallerClients clients,
        string connectionId,
        string method,
        params object?[] arguments)
    {
        var invocation = Assert.Single(clients.Invocations);
        Assert.Equal(connectionId, invocation.ConnectionId);
        Assert.Equal(method, invocation.Method);
        Assert.Equal(arguments, invocation.Arguments);
    }

    private static void ClearRooms()
    {
        var field = typeof(FileTransferHub).GetField("Connections", BindingFlags.NonPublic | BindingFlags.Static);
        var connections = field?.GetValue(null) ?? throw new InvalidOperationException("Connections field was not found.");
        connections.GetType().GetMethod("Clear")?.Invoke(connections, null);
    }
}

internal sealed record ClientInvocation(string ConnectionId, string Method, object?[] Arguments);

internal sealed class RecordingClientProxy(string connectionId, ConcurrentQueue<ClientInvocation> invocations) : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        invocations.Enqueue(new ClientInvocation(connectionId, method, args));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingHubCallerClients : IHubCallerClients
{
    private readonly RecordingClientProxy _fallback;

    public RecordingHubCallerClients()
    {
        _fallback = new RecordingClientProxy("*", Invocations);
    }

    public ConcurrentQueue<ClientInvocation> Invocations { get; } = new();

    public IClientProxy All => _fallback;
    public IClientProxy Caller => _fallback;
    public IClientProxy Others => _fallback;

    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _fallback;
    public IClientProxy Client(string connectionId) => new RecordingClientProxy(connectionId, Invocations);
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _fallback;
    public IClientProxy Group(string groupName) => _fallback;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _fallback;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => _fallback;
    public IClientProxy OthersInGroup(string groupName) => _fallback;
    public IClientProxy User(string userId) => _fallback;
    public IClientProxy Users(IReadOnlyList<string> userIds) => _fallback;
}

internal sealed class TestHubCallerContext(string connectionId) : HubCallerContext
{
    private readonly CancellationTokenSource _connectionAborted = new();

    public override string ConnectionId { get; } = connectionId;
    public override string? UserIdentifier => null;
    public override ClaimsPrincipal? User => null;
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => _connectionAborted.Token;

    public override void Abort()
    {
        _connectionAborted.Cancel();
    }
}
