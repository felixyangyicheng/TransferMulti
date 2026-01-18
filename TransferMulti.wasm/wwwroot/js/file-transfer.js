let dotNetHelper;
let stunServer;
let localConnection, remoteConnection;
let localDataChannel, remoteDataChannel;

async function initialization(dotNetHelperValue, stunServerValue) {
    dotNetHelper = dotNetHelperValue;
    stunServer = stunServerValue;
}

// 在客户端 A 中执行
async function createSenderConnection() {
    const config = { iceServers: [{ urls: stunServer }] };
    localConnection = new RTCPeerConnection(config);
    // 创建数据通道
    let dataChannelOptions = {
        ordered: true, //保证到达顺序
    };
    localDataChannel = localConnection.createDataChannel("dataChannel", dataChannelOptions);
    localDataChannel.onopen = dataChannelStateChange;
    localDataChannel.onclose = dataChannelStateChange;
    localDataChannel.onerror = (error) => console.error("DataChannel error:", error);

    // 监听 ICE candidate 事件
    localConnection.onicecandidate = event => {
        if (event.candidate) {
            dotNetHelper.invokeMethodAsync('SendIceCandidateToServer', JSON.stringify(event.candidate));
        }
    }

    // 创建 SDP Offer
    const offer = await localConnection.createOffer();
    await localConnection.setLocalDescription(offer);

    // 发送 SDP Offer 到信令服务器 
    dotNetHelper.invokeMethodAsync('SendOfferToServer', JSON.stringify(offer));
}

function dataChannelStateChange() {
    if (localDataChannel.readyState === 'open') {
        dotNetHelper.invokeMethodAsync('SenderConnected');
    }
}

// 客户端 A 使用接收到的 SDP answer 设置远程描述
async function receiveAnswer(answer) {
    const answerObj = JSON.parse(answer);
    await localConnection.setRemoteDescription(
        {
            type: answerObj.type,
            sdp: answerObj.sdp
        });
}

// 在客户端 B 中执行
async function createReceiverConnection(offer) {
    const config = { iceServers: [{ urls: stunServer }] };
    remoteConnection = new RTCPeerConnection(config);

    remoteConnection.onicecandidate = event => {
        if (event.candidate) {
            dotNetHelper.invokeMethodAsync('SendIceCandidateToServer', JSON.stringify(event.candidate));
        }
    }
    remoteConnection.ondatachannel = event => {
        event.channel.onopen = handleDataChannelOpen;
        event.channel.onmessage = receiveFileData;
    };

    const offerObj = JSON.parse(offer);
    await remoteConnection.setRemoteDescription(
        {
            type: offerObj.type,
            sdp: offerObj.sdp
        });

    const answer = await remoteConnection.createAnswer();
    await remoteConnection.setLocalDescription(answer);

    dotNetHelper.invokeMethodAsync('SendAnswerToServer', JSON.stringify(answer));
}

function receiveIceCandidate(candidate) {
    const candidateObj = JSON.parse(candidate);
    const iceCandidate = new RTCIceCandidate(
        {
            candidate: candidateObj.candidate,
            sdpMid: candidateObj.sdpMid,
            sdpMLineIndex: candidateObj.sdpMLineIndex
        });
    if (localConnection) {
        localConnection.addIceCandidate(iceCandidate);
    } else if (remoteConnection) {
        remoteConnection.addIceCandidate(iceCandidate);
    }
}

function handleDataChannelOpen() {
    dotNetHelper.invokeMethodAsync('ReceiverConnected');
}

let readyToSendKey = "ReadyToSend";
let fileSent = "FileSent";

// 修改 receiveFileData 以提取 fileName
let currentFileName = null;  // 临时存储当前文件名称（假设串行；如果并行，用 Map 存储）

function receiveFileData(event) {
    const receivedData = event.data;
    if (typeof receivedData === 'string') {
        if (receivedData.startsWith(readyToSendKey)) {
            let fileInfo = receivedData.substring(readyToSendKey.length);
            // 解析 JSON 获取 FileName（假设 fileInfo 是 JSON 字符串）
            let fileInfoObj = JSON.parse(fileInfo);
            currentFileName = fileInfoObj.FileName;  // 提取 FileName 属性
            dotNetHelper.invokeMethodAsync('FileInfoReceived', fileInfo);
        } else if (receivedData === fileSent) {
            dotNetHelper.invokeMethodAsync('FileReceivedWithWebRTC', currentFileName);  // 传递 fileName
            currentFileName = null;  // 重置
        }
    } else {
        dotNetHelper.invokeMethodAsync('FileReceivingWithWebRTC', new Uint8Array(receivedData), currentFileName);  // 传递 2 个参数
    }
}

// sendFileDataChunks: 同上，无需改

// 发送文件信息时也不加 fileName 前缀（保持简单稳定）
function sendFileInfo(fileInfo) {
    localDataChannel.send(readyToSendKey + fileInfo);
}

function sendFile(fileArray) {
    sendFileDataChunks(fileArray);
}

const CHUNK_SIZE = 16384;
const SEND_INTERVAL = 20;

function sendFileDataChunks(byteArray) {
    const chunk = byteArray.slice(0, CHUNK_SIZE);
    localDataChannel.send(chunk);
    byteArray = byteArray.slice(CHUNK_SIZE);
    dotNetHelper.invokeMethodAsync('FileSending', byteArray.length);

    if (byteArray.length > 0) {
        setTimeout(() => {
            sendFileDataChunks(byteArray);
        }, SEND_INTERVAL);
    } else {
        localDataChannel.send(fileSent);
        dotNetHelper.invokeMethodAsync('FileSent');
    }
}

function saveToFileWithBufferAndName(fileName, buffer) {
    const blob = new Blob([buffer], { type: 'application/octet-stream' });
    const link = document.createElement('a');
    link.href = URL.createObjectURL(blob);
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(link.href);
}