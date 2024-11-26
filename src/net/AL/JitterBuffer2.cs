using System;
using System.Collections.Generic;
using System.Text;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Linq;
using System.Net.Sockets;
using System.Threading;

namespace SIPSorcery.net.AL
{
    public class JitterBuffer2
    {
        private const uint TIME_STAMP_FREQ = 90000;
        private const uint FRAME_RATE = 25;

        public bool IsSendNack { get; set; }

        private RTCPeerConnection _pc;
        private UdpClient _udpClient;

        private uint _latencyMs = 400;
        private uint _currentTime;

        private int _gstPort = 6000;

        private Thread _sendThread;


        // frameID, frame
        private Dictionary<long, Frame> _frames = new Dictionary<long, Frame>();
        private Queue<long> _framesQueue = new Queue<long>();
        private long _lastFrameId = 0;

        private long _firstFrameTime = -1;

        private (uint, DateTime) _time = (0, DateTime.UtcNow);

        private bool _isDisposed = false;

        //seq, time
        private Dictionary<ushort, uint> _nackForFrames = new Dictionary<ushort, uint>();


        public JitterBuffer2(RTCPeerConnection pc, UdpClient udpClient, int gsPort)
        {
            _pc = pc;
            _gstPort = gsPort;
            _udpClient = udpClient;
            IsSendNack = true;

            _sendThread = new Thread(new ThreadStart(SendFrame));
            _sendThread.Start();
        }

        public void SetVideoFormats(List<VideoFormat> formats)
        {
            Console.WriteLine("Set video format");
        }

        public void SetLatency(uint latency)
        {
            _latencyMs = latency;
        }


        private AverageTimeEstimator _averageTimeEstimator = new AverageTimeEstimator();

        public void ReceivePacket(RTPPacket p)
        {
            var timeNow = DateTime.UtcNow;
            var timeF = GetTimeF(timeNow);

            if (timeF < p.Header.Timestamp)
            {
                //Console.WriteLine($"Update Time time:{TimeFToMillsec(timeF)} packet time: {TimeFToMillsec(p.Header.Timestamp)}");
                _time = (p.Header.Timestamp, DateTime.UtcNow);
                timeF = GetTimeF(timeNow);
            }

            var timeMs = TimeFToMillsec(timeF);
            _currentTime = timeMs;

            var packetLatency = TimeFToMillsec(p.Header.Timestamp);

            if (timeMs - packetLatency > _latencyMs)
            {
                Console.WriteLine($"Drop packet {p.Header.SequenceNumber}");
                return;
            }

            _averageTimeEstimator.InsertPacket(p, timeMs);

            var latency = _averageTimeEstimator.GetLatency();

            if (latency >= 0)
            {
                //Console.WriteLine($"latency: {latency} ms");
            }



            lock (_frames)
            {
                if (!_frames.ContainsKey(GetFrameId(p)))
                {
                    Frame previousFrame = null;

                    ushort? startSeqNum = null;



                    if (_frames.ContainsKey(GetPreviousFrameId(p)))
                    {
                        previousFrame = _frames[GetPreviousFrameId(p)];

                        if (previousFrame.FinalPacketNumber > 0)
                        {
                            var ssn = (previousFrame.FinalPacketNumber + 1);
                            if (ssn > ushort.MaxValue)
                            {
                                ssn = 0;
                            }

                            startSeqNum = (ushort)ssn;
                        }
                    }

                    var frame = new Frame(p, startSeqNum, previousFrame, GetFrameId(p.Header.Timestamp), timeMs);

                    if (_nackForFrames.ContainsKey(p.Header.SequenceNumber))
                    {
                        Console.WriteLine($"Nack For Frames Receive {GetFrameId(p)}");

                        if (_frames.ContainsKey(GetFrameId(p) + 1))
                        {
                            var nextFrame = _frames[GetFrameId(p) + 1];

                            if (nextFrame.PreviousFrame == null)
                            {
                                Console.WriteLine("Set PreviousFrame for next Frame");
                                nextFrame.SetPreviousFrame(frame);
                            }
                        }
                    }
                    else if (_frames.ContainsKey(GetFrameId(p) + 1))
                    {
                        var nextFrame = _frames[GetFrameId(p) + 1];

                        if (nextFrame.PreviousFrame == null)
                        {
                            Console.WriteLine("Set PreviousFrame without NACK!!!");
                            nextFrame.SetPreviousFrame(frame);
                        }
                    }

                    if (previousFrame == null)
                    {
                        Console.WriteLine($"previousFrame null for {frame.FrameId}");
                    }

                    SendNack(latency, timeMs, frame);

                    _frames.Add(GetFrameId(p), frame);

                    _framesQueue.Enqueue(GetFrameId(p));
                }
                else
                {
                    var frame = _frames[GetFrameId(p)];
                    frame.AddPacket(p, timeMs);

                    if (frame.PreviousFrame == null)
                    {
                        if (_frames.ContainsKey(GetPreviousFrameId(p)))
                        {
                            Console.WriteLine($"previous find!!! {frame.FrameId}");
                            frame.SetPreviousFrame(_frames[GetPreviousFrameId(p)]);
                        }
                    }
                }
            }
        }

        private void SendNack(int latency, uint timeMs, Frame currentFrame)
        {
            foreach (var oldFrame in _frames)
            {
                var nackPackets = oldFrame.Value.CheckLostPackets(timeMs, latency);

                if (nackPackets != null)
                {
                    if (nackPackets.Count > 0)
                    {
                        Console.Write($"Nack send: {oldFrame.Value.FrameId} id.");
                        foreach (var packet in nackPackets)
                        {
                            Console.Write($"{packet}, ");
                        }

                        var sort = nackPackets.OrderBy(x => x, new SeqIdComparer()).ToArray();
                        var start = nackPackets.Min();

                        if (nackPackets.Count > 16)
                        {
                            Console.WriteLine($"packet lost > 16");
                        }

                        int blp = GetBlp(sort);


                        Console.WriteLine($"blp: {blp}");

                        if (IsSendNack)
                        {
                            var localVideoSsrc = _pc.VideoLocalTrack.Ssrc;
                            var remoteVideoSsrc = _pc.VideoRemoteTrack.Ssrc;
                            RTCPFeedback nack = new RTCPFeedback(localVideoSsrc, remoteVideoSsrc, RTCPFeedbackTypesEnum.NACK, start, (ushort)blp);
                            _pc.SendRtcpFeedback(SDPMediaTypesEnum.video, nack);
                        }
                    }
                }

                var deleteNackForFrames = _nackForFrames.Where(x => x.Value < (timeMs - _latencyMs)).Select(x => x.Key).ToList();

                foreach (var seq in deleteNackForFrames)
                {
                    _nackForFrames.Remove(seq);
                }


                if (currentFrame.PreviousFrame == null)
                {
                    var framesLost = new List<ushort>();

                    for (int i = 0; i < 3; i++)
                    {
                        if (!_frames.ContainsKey(currentFrame.FrameId - i - 1))
                        {
                            var seqI = currentFrame.MinSeq - 1 - i;
                            ushort seq = (ushort)seqI;
                            if (seq < 0)
                            {
                                seq = (ushort)(ushort.MaxValue - (seqI + 1));
                            }

                            if (!_nackForFrames.ContainsKey(seq))
                            {
                                framesLost.Add(seq);
                                _nackForFrames.Add(seq, timeMs);
                            }
                        }
                    }

                    if (framesLost.Count > 0)
                    {
                        var sort = framesLost.OrderBy(x => x, new SeqIdComparer()).ToArray();
                        var start = framesLost.Min();
                        var blp = GetBlp(sort);

                        Console.WriteLine($"lost frame blp: {blp}, FrameId: {currentFrame.FrameId - 1}");

                        if (IsSendNack)
                        {
                            var localVideoSsrc = _pc.VideoLocalTrack.Ssrc;
                            var remoteVideoSsrc = _pc.VideoRemoteTrack.Ssrc;
                            RTCPFeedback nack = new RTCPFeedback(localVideoSsrc, remoteVideoSsrc, RTCPFeedbackTypesEnum.NACK, start, (ushort)blp);
                            _pc.SendRtcpFeedback(SDPMediaTypesEnum.video, nack);
                        }
                    }
                }
            }
        }

        private int GetBlp(ushort[] sort)
        {
            int blp = 0;
            for (ushort i = (ushort)(sort.Length - 1); i >= 1; i--)
            {
                var delta = sort[i] - sort[i - 1];

                blp = blp << delta;
                blp++;
            }

            return blp;
        }

        private void SendFrame()
        {
            var spleepTime = 10;
            var synkTime = _currentTime;
            int rateTime = 0;
            while (!_isDisposed)
            {
                var startTime = DateTime.Now;
                lock (_frames)
                {
                    if (_frames.Count == 0)
                    {
                        Thread.Sleep(10);
                        synkTime = _currentTime;
                        continue;
                    }

                    var minTimeFFrame = _frames.Min(x => x.Key);

                    var minPacket = _frames[minTimeFFrame];

                    var timeMs = synkTime + rateTime * spleepTime;

                    if (TimeFToMillsec(minPacket.TimeStamp) < timeMs - _latencyMs)
                    {
                        var packetsToSend = minPacket.GetArrayToSend();

                        if (packetsToSend != null)
                        {
                            foreach (var packet in packetsToSend)
                            {
                                var data = packet.GetBytes();
                                _udpClient.Send(data, data.Length, "127.0.0.1", _gstPort);
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Packets to send == null for frame: {minPacket.FrameId}");
                        }

                        if (minPacket.PreviousFrame == null)
                        {
                            Console.WriteLine($"Previous Packet NULL for frame {minPacket.FrameId}");
                        }
                        else if (minPacket.FrameId - minPacket.PreviousFrame.FrameId > 1)
                        {
                            Console.WriteLine("Delta Error");
                        }



                        minPacket?.PreviousFrame?.Dispose();
                        _frames.Remove(minTimeFFrame);
                    }
                }
                Thread.Sleep(spleepTime);

                if (synkTime == _currentTime)
                {
                    rateTime++;
                }
                else
                {
                    synkTime = _currentTime;
                    rateTime = 0;
                }
            }
        }

        private long GetFrameId(RTPPacket packet)
        {
            return GetFrameId(packet.Header.Timestamp);
        }

        private long GetFrameId(uint packetTime)
        {
            if (_firstFrameTime == -1)
            {
                _firstFrameTime = packetTime;
            }

            return (int)(packetTime - _firstFrameTime) / (int)(TIME_STAMP_FREQ / FRAME_RATE);
        }


        private long GetPreviousFrameId(RTPPacket packet)
        {
            return GetPreviousFrameId(packet.Header.Timestamp);
        }

        private long GetPreviousFrameId(uint packetTime)
        {
            if (_firstFrameTime == -1)
            {
                _firstFrameTime = packetTime;
            }

            return (((long)packetTime - _firstFrameTime) / (int)(TIME_STAMP_FREQ / FRAME_RATE)) - 1;
        }

        private uint GetTimeF(DateTime timeNow)
        {
            var delta = timeNow - _time.Item2;

            return _time.Item1 + ((uint)delta.TotalMilliseconds * (TIME_STAMP_FREQ / 1000));
        }

        private uint TimeFToMillsec(uint timeF)
        {
            return timeF / (TIME_STAMP_FREQ / 1000);
        }

        public void Dispose()
        {

        }
    }
}
