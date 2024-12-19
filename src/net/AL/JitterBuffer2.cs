using System;
using System.Collections.Generic;
using System.Text;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Org.BouncyCastle.Bcpg;

namespace SIPSorcery.net.AL
{
    public class JitterBuffer2
    {
        private uint _clockRate = 90000;
        private const uint FRAME_RATE = 25;

        public bool IsSendNack { get; set; }

        private VideoCodecsEnum _codec;

        private RTCPeerConnection _pc;

        private uint _latencyMs = 400;
        private uint _currentTime;

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

        private Action<byte[], int, int, int> _sendFrameAction;

        private Action<string> _sendLog;


        public JitterBuffer2(RTCPeerConnection pc, Action<byte[], int, int, int> sendFrameAction, VideoCodecsEnum codec, Action<string> sendLog)
        {
            _pc = pc;
            _sendFrameAction = sendFrameAction;
            IsSendNack = true;
            _codec = codec;


            _sendThread = new Thread(new ThreadStart(SendFrame));
            _sendThread.Start();
            _sendLog = sendLog;

            
        }

        public void SetVideoFormats(List<VideoFormat> formats)
        {
            _sendLog?.Invoke("Set video format");
            var format = formats.Where(x => x.Codec == VideoCodecsEnum.H264 || x.Codec == VideoCodecsEnum.H265)
                .FirstOrDefault();
            _codec = format.Codec;
            _clockRate = (uint)format.ClockRate;
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
                //_sendLog?.Invoke($"Update Time time:{TimeFToMillsec(timeF)} packet time: {TimeFToMillsec(p.Header.Timestamp)}");
                _time = (p.Header.Timestamp, DateTime.UtcNow);
                timeF = GetTimeF(timeNow);
            }

            var timeMs = TimeFToMillsec(timeF);
            _currentTime = timeMs;

            var packetLatency = TimeFToMillsec(p.Header.Timestamp);

            if (timeMs - packetLatency > _latencyMs)
            {
                //_sendLog?.Invoke($"Drop packet {p.Header.SequenceNumber} latency: {timeMs - packetLatency}");
                //var data = p.GetBytes();
                //_udpClient.Send(data, data.Length, "127.0.0.1", _gstPort);
                return;
            }

            _averageTimeEstimator.InsertPacket(p, timeMs);

            var latency = _averageTimeEstimator.GetLatency();

            if (latency >= 0)
            {
                //_sendLog?.Invoke($"latency: {latency} ms");
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

                    var frame = new Frame(p, startSeqNum, previousFrame, GetFrameId(p.Header.Timestamp), timeMs, _codec);

                    if (_nackForFrames.ContainsKey(p.Header.SequenceNumber))
                    {
                        _sendLog?.Invoke($"Nack For Frames Receive {GetFrameId(p)}");

                        if (_frames.ContainsKey(GetFrameId(p) + 1))
                        {
                            var nextFrame = _frames[GetFrameId(p) + 1];

                            if (nextFrame.PreviousFrame == null)
                            {
                                _sendLog?.Invoke("Set PreviousFrame for next Frame");
                                nextFrame.SetPreviousFrame(frame);
                            }
                        }
                    }
                    else if (_frames.ContainsKey(GetFrameId(p) + 1))
                    {
                        var nextFrame = _frames[GetFrameId(p) + 1];

                        if (nextFrame.PreviousFrame == null)
                        {
                            _sendLog?.Invoke("Set PreviousFrame without NACK!!!");
                            nextFrame.SetPreviousFrame(frame);
                        }
                    }

                    if (previousFrame == null)
                    {
                        _sendLog?.Invoke($"previousFrame null for {frame.FrameId}");
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
                            _sendLog?.Invoke($"previous find!!! {frame.FrameId}");
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
                        //Console.Write($"Nack send: {oldFrame.Value.FrameId} id.");
                        foreach (var packet in nackPackets)
                        {
                            //Console.Write($"{packet}, ");
                        }

                        var sort = nackPackets.OrderBy(x => x, new SeqIdComparer()).ToArray();
                        var start = nackPackets.Min();

                        if (nackPackets.Count > 16)
                        {
                            _sendLog?.Invoke($"packet lost > 16");
                        }

                        int blp = GetBlp(sort);


                        //_sendLog?.Invoke($"blp: {blp}");

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

                        _sendLog?.Invoke($"lost frame blp: {blp}, FrameId: {currentFrame.FrameId - 1}");

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
                                //_udpClient.Send(data, data.Length, "127.0.0.1", _gstPort);
                                try 
                                {
                                    _sendFrameAction?.Invoke(data, 96, (int)_clockRate, _codec == VideoCodecsEnum.H265 ? 265 : 264);
                                }
                                catch (Exception ex)
                                {
                                    _sendLog?.Invoke("Send frame action error");
                                }
                                
                            }
                        }
                        else
                        {
                            _sendLog?.Invoke($"Packets to send == null for frame: {minPacket.FrameId}");
                        }

                        if (minPacket.PreviousFrame == null)
                        {
                            _sendLog?.Invoke($"Previous Packet NULL for frame {minPacket.FrameId}");
                        }
                        else if (minPacket.FrameId - minPacket.PreviousFrame.FrameId > 1)
                        {
                            _sendLog?.Invoke("Delta Error");
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

            return (int)(packetTime - _firstFrameTime) / (int)(_clockRate / FRAME_RATE);
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

            return (((long)packetTime - _firstFrameTime) / (int)(_clockRate / FRAME_RATE)) - 1;
        }

        private uint GetTimeF(DateTime timeNow)
        {
            var delta = timeNow - _time.Item2;

            return _time.Item1 + ((uint)delta.TotalMilliseconds * (_clockRate / 1000));
        }

        private uint TimeFToMillsec(uint timeF)
        {
            return timeF / (_clockRate / 1000);
        }

        public void Dispose()
        {
            _isDisposed = true;
        }
    }
}
