//-----------------------------------------------------------------------------
// Filename: WebRTCWebSocketClient.cs
//
// Description: This class is NOT a required component for using WebRTC. It is a
// convenience class provided to assist when using a corresponding WebRTC peer 
// running a web socket server (which is the case for most of the demo applications
// that go with this library).
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// History:
// 01 Oct 2020	Aaron Clauson	Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SIPSorcery.Net
{
    /// <summary>
    /// This class is NOT a required component for using WebRTC. It is a
    /// convenience class provided to assist when using a corresponding WebRTC peer 
    /// running a web socket server (which is the case for most of the demo applications
    /// that go with this library).
    /// </summary>
    public class WebRTCWebSocketClient
    {
        public bool IsClientPing { get; private set; }

        private const int MAX_RECEIVE_BUFFER = 8192;
        private const int MAX_SEND_BUFFER = 8192;
        private const int WEB_SOCKET_CONNECTION_TIMEOUT_MS = 10000;

        private ILogger logger = SIPSorcery.Sys.Log.Logger;

        private Uri _webSocketServerUri;
        private Func<Task<RTCPeerConnection>> _createPeerConnection;

        private RTCPeerConnection _pc;
        public RTCPeerConnection RTCPeerConnection => _pc;

        private ClientWebSocket _ws;
        private Task _readTask;
        private CancellationTokenSource _readTaskCancellationToken;

        private Action<string> _logMp;

        /// <summary>
        /// Default constructor.
        /// </summary>
        /// <param name="webSocketServer">The web socket server URL to connect to for the SDP and 
        /// ICE candidate exchange.</param>
        public WebRTCWebSocketClient(
            string webSocketServer,
            Func<Task<RTCPeerConnection>> createPeerConnection, Action<string> logMp = null)
        {
            _logMp = logMp;
            if (string.IsNullOrWhiteSpace(webSocketServer))
            {
                throw new ArgumentNullException("The web socket server URI must be supplied.");
            }

            _webSocketServerUri = new Uri(webSocketServer);
            _createPeerConnection = createPeerConnection;
        }

        /// <summary>
        /// Creates a new WebRTC peer connection and then starts polling the web socket server.
        /// An SDP offer is expected from the server. Once it has been received an SDP answer 
        /// will be returned.
        /// </summary>
        public async Task Start()
        {
            _pc = await _createPeerConnection().ConfigureAwait(false);
            _readTask?.Dispose();

            LogDebug($"websocket-client attempting to connect to {_webSocketServerUri}.");

            var webSocketClient = new ClientWebSocket();
            _ws = webSocketClient;
            // As best I can tell the point of the CreateClientBuffer call is to set the size of the internal
            // web socket buffers. The return buffer seems to be for cases where direct access to the raw
            // web socket data is desired.
            _ = WebSocket.CreateClientBuffer(MAX_RECEIVE_BUFFER, MAX_SEND_BUFFER);
            CancellationTokenSource connectCts = new CancellationTokenSource();
            connectCts.CancelAfter(WEB_SOCKET_CONNECTION_TIMEOUT_MS);
            await webSocketClient.ConnectAsync(_webSocketServerUri, connectCts.Token).ConfigureAwait(false);
            Thread.Sleep(1000);

            if (webSocketClient.State == WebSocketState.Open)
            {
                LogDebug($"websocket-client starting receive task for server {_webSocketServerUri}.");

                

                _pc.onicecandidate += (iceCandidate) =>
                {
                    if (_pc.signalingState == RTCSignalingState.have_remote_offer ||
                        _pc.signalingState == RTCSignalingState.stable)
                    {
                        //Task.Run(() => webSocketClient.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(iceCandidate.toJSON())), WebSocketMessageType.Text, true, cancellation)).ConfigureAwait(false);
                    }
                };

                _readTaskCancellationToken = new CancellationTokenSource();
                _readTask = Task.Run(() => ReceiveFromWebSocket(_pc, _ws, _readTaskCancellationToken.Token));
                _ = _readTask.ConfigureAwait(false);

                var token = new CancellationTokenSource();

                IsClientPing = false;
                while (webSocketClient.State == WebSocketState.Open && !IsClientPing)
                {
                    SendPing();

                    LogDebug($"ping air-link");
                    Thread.Sleep(2500);
                }

                SendRequest(token.Token);
            }
            else
            {
                _pc.Close("web socket connection failure");
            }
        }

        public async Task ReStart(Func<Task<RTCPeerConnection>> createPeerConnection)
        {
            _createPeerConnection = createPeerConnection;
            _readTaskCancellationToken.Cancel();
            Thread.Sleep(1000);

            if (_ws.State == WebSocketState.Open)
            {
                _pc = await _createPeerConnection().ConfigureAwait(false);
                Thread.Sleep(1000);

                LogDebug($"Restart websocket-client attempting to connect to {_webSocketServerUri}.");
                //_readTask?.Dispose();

                _readTaskCancellationToken.Cancel();

                Thread.Sleep(1000);

                _readTaskCancellationToken = new CancellationTokenSource();
                _readTask = Task.Run(() => ReceiveFromWebSocket(_pc, _ws, _readTaskCancellationToken.Token));
                _ = _readTask.ConfigureAwait(false);

                var token = new CancellationTokenSource();
                IsClientPing = false;
                while (_ws.State == WebSocketState.Open && !IsClientPing)
                {
                    SendPing();

                    LogDebug($"ping air-link");
                    Thread.Sleep(2500);
                }

                SendRequest(token.Token);
                
            }
            else
            {
                Start();
            }
        }

        private async Task ReceiveFromWebSocket(RTCPeerConnection pc, ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[MAX_RECEIVE_BUFFER];
            int posn = 0;

            //while (ws.State == WebSocketState.Open &&
            //    (pc.connectionState == RTCPeerConnectionState.@new || pc.connectionState == RTCPeerConnectionState.connecting 
            //    || pc.connectionState == RTCPeerConnectionState.connected))
            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult receiveResult;
                do
                {
                    receiveResult = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, posn, MAX_RECEIVE_BUFFER - posn), ct).ConfigureAwait(false);
                    posn += receiveResult.Count;
                }
                while (!receiveResult.EndOfMessage);

                if (posn > 0)
                {
                    var jsonMsg = Encoding.UTF8.GetString(buffer, 0, posn);
                    string jsonResp = await OnMessage(jsonMsg, pc, ws);

                    if (jsonResp != null)
                    {
                        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(jsonResp)), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                    }
                }

                posn = 0;
            }

            LogDebug($"websocket-client receive loop exiting.");
        }

        private int sessionId;
        public void SendRequest(CancellationToken ct)
        {
            var exitCts = new CancellationTokenSource();
            if (sessionId == 0)
            {
                sessionId = new Random((int)new TimeSpan(DateTime.Now.Ticks).TotalSeconds).Next();
            }
            //var str = TinyJson.JSONWriter.ToJson(new { id = sessionId.ToString(), type = "request" });
            var str = TinyJson.JSONWriter.ToJson(new { id = "123", type = "request" });
            _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(str)), WebSocketMessageType.Text, true, exitCts.Token).ConfigureAwait(false);
        }

        public void SendPing()
        {
            IsClientPing = false;
            if (_ws.State == WebSocketState.Open)
            {
                var exitCts = new CancellationTokenSource();

                if(sessionId == 0)
                {
                    sessionId = new Random((int)new TimeSpan(DateTime.Now.Ticks).TotalSeconds).Next();
                }

                //var str = TinyJson.JSONWriter.ToJson(new { id = sessionId.ToString(), type = "ping" });
                var str = TinyJson.JSONWriter.ToJson(new { id = "123", type = "ping" });
                _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(str)), WebSocketMessageType.Text, true, exitCts.Token).ConfigureAwait(false);
            }
        }

        private async Task<string> OnMessage(string jsonStr, RTCPeerConnection pc, ClientWebSocket ws)
        {
            if(jsonStr.Contains("partner connect"))
            {
                //var offerSdp = _pc.createOffer(null);
                //await _pc.setLocalDescription(offerSdp).ConfigureAwait(false);

                //LogDebug($"Sending SDP offer to client.");

                //return offerSdp.toJSON();
                //return TinyJson.JSONWriter.ToJson(new { id = "123", type = "request" });
            }

            if (RTCIceCandidateInit.TryParse(jsonStr, out var iceCandidateInit))
            {
                LogDebug("Got remote ICE candidate.");
                pc.addIceCandidate(iceCandidateInit);
            }
            else if (RTCSessionDescriptionInit.TryParse(jsonStr, out var descriptionInit))
            {
                LogDebug($"Got remote SDP, type {descriptionInit.type}.");

                var result = pc.setRemoteDescription(descriptionInit);
                if (result != SetDescriptionResultEnum.OK)
                {
                    LogWarning($"Failed to set remote description, {result}.");
                    pc.Close("failed to set remote description");
                }

                if (descriptionInit.type == RTCSdpType.offer)
                {
                    var answerSdp = pc.createAnswer(null);
                    await pc.setLocalDescription(answerSdp).ConfigureAwait(false);

                    return answerSdp.toJSON();
                }
            }
            else
            {
                if(jsonStr.Contains("ping"))
                {
                    LogWarning($"Ping received");
                    IsClientPing = true;
                    return null;
                }
                LogWarning($"websocket-client could not parse JSON message. {jsonStr}");
            }

            return null;
        }

        private void Close(CancellationToken cancellation)
        {
            _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellation);
        }

        private void LogDebug(string msg)
        {
            logger.LogDebug(msg);
            _logMp?.Invoke(msg);
        }

        private void LogWarning(string msg) 
        {
            logger.LogWarning(msg);
            _logMp?.Invoke(msg);
        }
    }
}
