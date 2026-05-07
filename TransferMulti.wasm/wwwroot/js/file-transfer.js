let dotNetHelper;
let stunServer;
let localConnection = null;
let remoteConnection = null;

const dataChannels = {};
const readyToSendKey = "ReadyToSend:";
const fileSent = "FileSent";
const channelLabelPrefix = "file:";
const bootstrapChannelId = "bootstrap";
const CHUNK_SIZE = 16384;
const MAX_BUFFERED_AMOUNT = CHUNK_SIZE * 32;
const SEND_INTERVAL = 10;

async function initialization(dotNetHelperValue, stunServerValue) {
    dotNetHelper = dotNetHelperValue;
    stunServer = stunServerValue;
}

function configurePeerConnection(connection) {
    connection.onicecandidate = event => {
        if (event.candidate) {
            dotNetHelper.invokeMethodAsync("SendIceCandidateToServer", JSON.stringify(event.candidate));
        }
    };

    connection.onconnectionstatechange = () => {
        if (connection.connectionState === "connected") {
            dotNetHelper.invokeMethodAsync("WebRtcConnectionEstablished");
        }
    };
}

async function createSenderConnection() {
    const config = { iceServers: [{ urls: stunServer }] };
    localConnection = new RTCPeerConnection(config);
    configurePeerConnection(localConnection);
    createBootstrapDataChannel();

    const offer = await localConnection.createOffer();
    await localConnection.setLocalDescription(offer);
    await dotNetHelper.invokeMethodAsync("SendOfferToServer", JSON.stringify(offer));
}

async function receiveAnswer(answer) {
    const answerObj = JSON.parse(answer);
    await localConnection.setRemoteDescription({
        type: answerObj.type,
        sdp: answerObj.sdp
    });
}

async function createReceiverConnection(offer) {
    const config = { iceServers: [{ urls: stunServer }] };
    remoteConnection = new RTCPeerConnection(config);
    configurePeerConnection(remoteConnection);

    remoteConnection.ondatachannel = event => {
        registerIncomingChannel(event.channel);
    };

    const offerObj = JSON.parse(offer);
    await remoteConnection.setRemoteDescription({
        type: offerObj.type,
        sdp: offerObj.sdp
    });

    const answer = await remoteConnection.createAnswer();
    await remoteConnection.setLocalDescription(answer);
    await dotNetHelper.invokeMethodAsync("SendAnswerToServer", JSON.stringify(answer));
}

function registerIncomingChannel(channel) {
    const fileId = getFileIdFromLabel(channel.label);

    if (fileId === bootstrapChannelId) {
        return;
    }

    channel.binaryType = "arraybuffer";
    dataChannels[fileId] = channel;

    channel.onmessage = event => receiveFileData(event, fileId);
    channel.onclose = () => delete dataChannels[fileId];
}

function getOrCreateDataChannel(fileId) {
    if (dataChannels[fileId]) {
        return dataChannels[fileId];
    }

    if (!localConnection) {
        throw new Error("La connexion WebRTC côté émetteur n'est pas prête.");
    }

    // Un DataChannel par fichier : on obtient donc plusieurs flux indépendants,
    // tout en gardant un seul RTCPeerConnection entre les deux clients.
    const channel = localConnection.createDataChannel(`${channelLabelPrefix}${fileId}`, { ordered: true });
    channel.binaryType = "arraybuffer";
    channel.onclose = () => delete dataChannels[fileId];
    dataChannels[fileId] = channel;
    return channel;
}

function createBootstrapDataChannel() {
    if (!localConnection) {
        return;
    }

    // Ce canal vide force la négociation SCTP dès la première offre.
    // Les vrais canaux de fichiers peuvent ensuite être ouverts sans renégociation.
    const channel = localConnection.createDataChannel(`${channelLabelPrefix}${bootstrapChannelId}`, { ordered: true });
    channel.onclose = () => { };
}

function getFileIdFromLabel(label) {
    return label.startsWith(channelLabelPrefix)
        ? label.substring(channelLabelPrefix.length)
        : label;
}

function waitForChannelToOpen(channel) {
    if (channel.readyState === "open") {
        return Promise.resolve();
    }

    return new Promise((resolve, reject) => {
        const onOpen = () => {
            cleanup();
            resolve();
        };

        const onError = error => {
            cleanup();
            reject(error);
        };

        const cleanup = () => {
            channel.removeEventListener("open", onOpen);
            channel.removeEventListener("error", onError);
        };

        channel.addEventListener("open", onOpen, { once: true });
        channel.addEventListener("error", onError, { once: true });
    });
}

async function sendFile(fileId, fileInfo, fileArray) {
    const channel = getOrCreateDataChannel(fileId);
    await waitForChannelToOpen(channel);

    channel.send(readyToSendKey + fileInfo);
    await sendFileDataChunks(channel, fileId, new Uint8Array(fileArray));
    channel.send(fileSent);
    await dotNetHelper.invokeMethodAsync("FileSent", fileId);
}

async function sendFileDataChunks(channel, fileId, byteArray) {
    for (let offset = 0; offset < byteArray.length; offset += CHUNK_SIZE) {
        while (channel.bufferedAmount > MAX_BUFFERED_AMOUNT) {
            await delay(SEND_INTERVAL);
        }

        const nextOffset = Math.min(offset + CHUNK_SIZE, byteArray.length);
        const chunk = byteArray.slice(offset, nextOffset);
        channel.send(chunk);

        const remainingBytes = byteArray.length - nextOffset;
        await dotNetHelper.invokeMethodAsync("FileSending", fileId, remainingBytes);
    }
}

function receiveFileData(event, fileId) {
    const receivedData = event.data;
    if (typeof receivedData === "string") {
        if (receivedData.startsWith(readyToSendKey)) {
            const fileInfo = receivedData.substring(readyToSendKey.length);
            dotNetHelper.invokeMethodAsync("FileInfoReceived", fileId, fileInfo);
        } else if (receivedData === fileSent) {
            dotNetHelper.invokeMethodAsync("FileReceivedWithWebRTC", fileId);
        }

        return;
    }

    const buffer = receivedData instanceof ArrayBuffer
        ? new Uint8Array(receivedData)
        : new Uint8Array(receivedData);

    dotNetHelper.invokeMethodAsync("FileReceivingWithWebRTC", buffer, fileId);
}

function receiveIceCandidate(candidate) {
    const candidateObj = JSON.parse(candidate);
    const iceCandidate = new RTCIceCandidate({
        candidate: candidateObj.candidate,
        sdpMid: candidateObj.sdpMid,
        sdpMLineIndex: candidateObj.sdpMLineIndex
    });

    const targetConnection = localConnection ?? remoteConnection;
    if (targetConnection) {
        targetConnection.addIceCandidate(iceCandidate).catch(console.error);
    }
}

function delay(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
}

function saveToFileWithBufferAndName(fileName, buffer) {
    const blob = new Blob([buffer], { type: "application/octet-stream" });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(link.href);
}
