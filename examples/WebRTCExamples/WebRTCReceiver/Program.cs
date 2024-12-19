//-----------------------------------------------------------------------------
// Filename: Program.cs
//
// Description: Displays a VP8 video stream received from a WebRTC peer.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
// 
// History:
// 05 Feb 2020	Aaron Clauson	Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CommandLine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.FFmpeg;
using WebSocketSharp.Server;
using SIPSorcery.net.AL;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Tls;
using static System.Windows.Forms.AxHost;

namespace demo
{
    public class Options
    {
        [Option("cert", Required = false,
            HelpText = "Path to a `.pfx` certificate archive for the web socket server listener. Format \"--cert=mycertificate.pfx.")]
        public string WSSCertificate { get; set; }

        [Option("ipv6", Required = false,
            HelpText = "If set the web socket server will listen on IPv6 instead of IPv4.")]
        public bool UseIPv6 { get; set; }

        [Option("noaudio", Required = false,
           HelpText = "If set the an audio track will not be included in the SDP offer.")]
        public bool NoAudio { get; set; }
    }

    class Program
    {
        private const int VIDEO_INITIAL_WIDTH = 640;
        private const int VIDEO_INITIAL_HEIGHT = 480;

        private static Form _form;
        private static PictureBox _picBox;
        private static Options _options;

        private static Microsoft.Extensions.Logging.ILogger logger = NullLogger.Instance;

        private static UdpClient _udpClient = new UdpClient(10000);
        private static WebRTCWebSocketClient _wsClient;
        private static CancellationTokenSource _exitCts;
        private static int _gstreamerPort = 6000;

        static void Main(string[] args)
        {
            Console.WriteLine("WebRTC Receive Demo");

            logger = AddConsoleLogger();

            var parseResult = Parser.Default.ParseArguments<Options>(args);
            _options = (parseResult as Parsed<Options>)?.Value;
            X509Certificate2 wssCertificate = (_options.WSSCertificate != null) ? LoadCertificate(_options.WSSCertificate) : null;

            _exitCts = new CancellationTokenSource();
            // Start web socket.
            Console.WriteLine("Starting web socket server...");
            //_wsClient = new WebRTCWebSocketClient("ws://apcloud.csky.space/wstest?name=GS0015E&partnerName=0015E", CreatePeerConnection);
            _wsClient = new WebRTCWebSocketClient("ws://apcloud.csky.space/wstest?name=GS0015E&partnerName=0015E", CreatePeerConnection);
            _wsClient.Start();

            _form = new Form();
            _form.AutoSize = true;
            _form.BackgroundImageLayout = ImageLayout.Center;
            _picBox = new PictureBox
            {
                Size = new Size(VIDEO_INITIAL_WIDTH, VIDEO_INITIAL_HEIGHT),
                Location = new Point(0, 0),
                Visible = true
            };
            //_form.Controls.Add(_picBox);
            var button = new Button() { Location = new Point(20, 20), Size = new Size(50, 50), Visible = true };
            button.Click += Button_Click;

            var stopNackButton = new Button() { Location = new Point(80, 20), Size = new Size(50, 50), Visible = true };
            stopNackButton.Click += StopNackClick;

            var pingButton = new Button() { Location = new Point(140, 20), Size = new Size(50, 50), Visible = true, Text = "Ping" };
            pingButton.Click += PingButton_Click; ;

            var setLatencyButton = new Button() { Location = new Point(20, 80), Size = new Size(50, 50), Visible = true, Text = "Latency" };
            setLatencyButton.Click += SetLatency;

            var reStartButton = new Button() { Location = new Point(80, 80), Size = new Size(50, 50), Visible = true, Text = "Restart" };
            reStartButton.Click += ReStart;

            var stop = new Button() { Location = new Point(140, 80), Size = new Size(50, 50), Visible = true, Text = "Stop" };
            stop.Click += Stop;

            _form.Controls.Add(button);
            _form.Controls.Add(stopNackButton);
            _form.Controls.Add(setLatencyButton);
            _form.Controls.Add(reStartButton);
            _form.Controls.Add(stop);
            _form.Controls.Add(pingButton);

            Application.EnableVisualStyles();
            Application.Run(_form);
        }

        private static void PingButton_Click(object sender, EventArgs e)
        {
            _wsClient.SendPing();
        }

        private static void Button_Click(object sender, EventArgs e)
        {
            _wsClient.SendRequest(_exitCts.Token);
        }

        private static void StopNackClick(object sender, EventArgs e)
        {
            _jb.IsSendNack = !_jb.IsSendNack;
        }

        private static uint latency = 400;
        private static void SetLatency(object sender, EventArgs e)
        {
            switch (latency)
            {
                case 200:
                    latency = 100;
                    break;
                case 100:
                    latency = 50;
                    break;
                case 50:
                    latency = 1000;
                    break;
                case 400:
                    latency = 200;
                    break;
                case 1000:
                    latency = 400;
                    break;
            }
            _jb.SetLatency(latency);
            Console.WriteLine($"Set Latency: {latency}");
        }

        private static void ReStart(object sender, EventArgs e)
        {
            //_exitCts = new CancellationTokenSource();
            //_pc.Close(null);
            //_pc.restartIce();
            Restart();
            //_wsClient.SendRequest(_exitCts.Token);
        }

        private static void Restart()
        {
            Thread.Sleep(1000);
            _wsClient.ReStart(CreatePeerConnection);
        }

        private static void Stop(object sender, EventArgs e)
        {
            //_exitCts = new CancellationTokenSource();
            Console.WriteLine("Stop");
            //_pc.Close(null);
            //_pc.restartIce();
            _jb.Dispose();
            _pc.Dispose();
            
            _exitCts.Cancel();

            Thread.Sleep(5000);
        }

        private static RTCPeerConnection _pc;
        private static JitterBuffer2 _jb;

        private static void SendToGstreamer(byte[] data, int a, int b, int c)
        {
            _udpClient.Send(data, data.Length, "127.0.0.1", _gstreamerPort);
        }

        private static void Log(string message)
        {
            logger.LogDebug(message);
        }

        private static Task<RTCPeerConnection> CreatePeerConnection()
        {
            RTCConfiguration config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer> { new RTCIceServer { urls = "turn:turn.air-link.space", credential = "lbjT3jXHt", credentialType = RTCIceCredentialType.password, username = "airlink" } }
                //X_UseRtpFeedbackProfile = true
            };
            _pc = new RTCPeerConnection(config);
            _jb = new JitterBuffer2(_pc, SendToGstreamer, VideoCodecsEnum.H265, Log);

            // Add local receive only tracks. This ensures that the SDP answer includes only the codecs we support.
            if (!_options.NoAudio)
            {
                MediaStreamTrack audioTrack = new MediaStreamTrack(SDPMediaTypesEnum.audio, false,
                    new List<SDPAudioVideoMediaFormat> { new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.PCMU) }, MediaStreamStatusEnum.RecvOnly);
                //_pc.addTrack(audioTrack);
            }

            var formats = new List<SDPAudioVideoMediaFormat> { new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.H264, 96)), new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.H265, 96)) };
            var videoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, formats, MediaStreamStatusEnum.RecvOnly);

            _pc.addTrack(videoTrack);

            _pc.OnVideoFrameReceived += VideoFrameReceived;

            _pc.OnRtpPacketReceived += RtpPacketReceived;
            _pc.OnRtpPacketReceivedByIndex += _pc_OnRtpPacketReceivedByIndex;
            _pc.OnVideoFormatsNegotiated += SetVideoSinkFormat;

            _pc.OnSendReport += Pc_OnSendReport;
            //pc.SendRtcpReport(SDP)


            _pc.onconnectionstatechange += async (state) =>
            {
                logger.LogDebug($"Peer connection state change to {state}.");

                if (state == RTCPeerConnectionState.failed)
                {
                    _pc.Close("ice disconnection");
                }
                else if (state == RTCPeerConnectionState.closed)
                {
                    logger.LogWarning("Reconnect!");
                    Restart();
                }
            };

            // Diagnostics.
            _pc.OnReceiveReport += (re, media, rr) => logger.LogDebug($"RTCP Receive for {media} from {re}\n{rr.GetDebugSummary()}");
            _pc.OnSendReport += (media, sr) => logger.LogDebug($"RTCP Send for {media}\n{sr.GetDebugSummary()}");
            _pc.onicecandidateerror += (candidate, error) => logger.LogWarning($"Error adding remote ICE candidate. {error} {candidate}");
            //pc.GetRtpChannel().OnStunMessageReceived += (msg, ep, isRelay) => logger.LogDebug($"RECV STUN {msg.Header.MessageType} (txid: {msg.Header.TransactionId.HexStr()}) from {ep}.");
            //pc.GetRtpChannel().OnStunMessageSent += (msg, ep, isRelay) => logger.LogDebug($"SEND STUN {msg.Header.MessageType} (txid: {msg.Header.TransactionId.HexStr()}) to {ep}.");
            _pc.oniceconnectionstatechange += (state) => logger.LogDebug($"ICE connection state change to {state}.");
            _pc.OnRtcpBye += (reason) => logger.LogDebug($"RTCP BYE receive, reason: {(string.IsNullOrWhiteSpace(reason) ? "<none>" : reason)}.");
            //_pc.createDataChannel("bombom", new RTCDataChannelInit() { negotiated})
            _pc.ondatachannel += (qwe) => {
                qwe.onmessage += (datachan, type, data) =>
                {
                    switch (type)
                    {
                        case DataChannelPayloadProtocols.WebRTC_Binary_Empty:
                        case DataChannelPayloadProtocols.WebRTC_String_Empty:
                            logger.LogInformation($"Data channel {datachan.label} empty message type {type}.");
                            break;

                        case DataChannelPayloadProtocols.WebRTC_Binary:
                            _udpClient.Send(data, data.Length, "127.0.0.1", 6000);
                            //_udpClient.Send(data, data.Length, "192.168.3.157", 9000);
                            break;

                        case DataChannelPayloadProtocols.WebRTC_String:
                            var msg = Encoding.UTF8.GetString(data);
                            logger.LogInformation($"Data channel {datachan.label} message {type} received: {msg}.");

                            var loadTestMatch = Regex.Match(msg, @"^\s*(?<sendSize>\d+)\s*x\s*(?<testCount>\d+)");

                            // Do a string echo.
                            //qwe.send($"echo: {msg}");

                            break;
                    }
                };
            };


            return Task.FromResult(_pc);
        }

        private static void _pc_OnRtpPacketReceivedByIndex(int arg1, IPEndPoint arg2, SDPMediaTypesEnum arg3, RTPPacket arg4)
        {
            if (arg3 == SDPMediaTypesEnum.video)
            {
                //_videoPacketsByIndex.Add(arg4);
            }
        }

        private static void Pc_OnSendReport(SDPMediaTypesEnum arg1, RTCPCompoundPacket arg2)
        {
            Console.WriteLine("RTSP fedback");
        }

        private static X509Certificate2 LoadCertificate(string path)
        {
            if (!File.Exists(path))
            {
                logger.LogWarning($"No certificate file could be found at {path}.");
                return null;
            }
            else
            {
                X509Certificate2 cert = new X509Certificate2(path, "", X509KeyStorageFlags.Exportable);
                if (cert == null)
                {
                    logger.LogWarning($"Failed to load X509 certificate from file {path}.");
                }
                else
                {
                    logger.LogInformation($"Certificate file successfully loaded {cert.Subject}, thumbprint {cert.Thumbprint}, has private key {cert.HasPrivateKey}.");
                }
                return cert;
            }
        }

        /// <summary>
        /// Adds a console logger. Can be omitted if internal SIPSorcery debug and warning messages are not required.
        /// </summary>
        private static Microsoft.Extensions.Logging.ILogger AddConsoleLogger()
        {
            var serilogLogger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Is(Serilog.Events.LogEventLevel.Debug)
                .WriteTo.Console()
                .CreateLogger();
            var factory = new SerilogLoggerFactory(serilogLogger);
            SIPSorcery.LogFactory.Set(factory);
            return factory.CreateLogger<Program>();
        }

        private static List<VideoFormat> _remoteFormats = new List<VideoFormat>();
        private static List<RTPPacket> _videoPacketsByIndex = new List<RTPPacket>();
        private static List<RTPPacket> _videoPackets = new List<RTPPacket>();
        private static List<RTPPacket> _videoFrames = new List<RTPPacket>();


        //seq, time




        private static void RtpPacketReceived(IPEndPoint ip, SDPMediaTypesEnum mt, RTPPacket p)
        {
            if (mt == SDPMediaTypesEnum.video)
            {
                try
                {
                    _jb.ReceivePacket(p);
                    //_videoPackets.Add(p);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
            else
            {
                Console.WriteLine($"not video packet receive : {p.Header.SequenceNumber}");
            }
        }

        private static void SetVideoSinkFormat(List<VideoFormat> formats)
        {
            _remoteFormats = formats;
            _jb.SetVideoFormats(formats);

            //create sdp

            var dir = System.AppDomain.CurrentDomain.BaseDirectory;
            var fileName = "air-link.sdp";
            var path = dir + fileName;

            var sdpText = CreateSdpText(formats.Where(x => x.Codec == VideoCodecsEnum.H264 || x.Codec == VideoCodecsEnum.H265).FirstOrDefault());

            File.WriteAllText(path, sdpText);

            //start gstreamer
        }

        private static string CreateSdpText(VideoFormat format)
        {
            var result = "v=0\r\n";
            result += "o=- 0 0 IN IP4 127.0.0.1\r\n";
            result += "s=LIVE555 Streaming Media v2019.08.12\r\n";
            result += "c=IN IP4 127.0.0.1\r\n";
            result += "t=0 0\r\n";
            result += "a=tool:libavformat 58.76.100\r\n";
            result += $"m=video {_gstreamerPort} RTP/AVP 96\r\n";
            result += $"a=rtpmap:96 {GetCodecName(format.Codec)}/{format.ClockRate}\r\n";
            result += $"a=fmtp:96 {format.Parameters}";

            return result;
        }

        private static string GetCodecName(VideoCodecsEnum codec)
        {
            switch(codec)
            {
                case VideoCodecsEnum.H265:
                    return "H265";
                case VideoCodecsEnum.H264:
                    return "H264";
                default:
                    return "Codec Error";
            }
        }

        private static void VideoFrameReceived(IPEndPoint ip, uint time, byte[] payload, VideoFormat format)
        {
            //var rtp = new RTPPacket(payload, payload.Length);
            //var data = rtp.GetBytes();
            //_videoFrames.Add(rtp);
            //_udpClient.Send(payload, payload.Length, "127.0.0.1", 6000);
        }

    }
}
