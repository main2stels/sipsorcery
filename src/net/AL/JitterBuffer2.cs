using System;
using System.Collections.Generic;
using System.Text;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Org.BouncyCastle.Bcpg;
using SIPSorcery.net.AL.NackSupport;
using System.Collections;

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

        private int _framesOverLatency = 0;
        private uint _downLatencyTime = 10;
        private uint _maxLatencyMs = 800;
        private uint _minLatencyMs = 50;

        private Dictionary<uint, List<NackInfo>> _nackInfo = new Dictionary<uint, List<NackInfo>>();

        public JitterBuffer2(RTCPeerConnection pc, Action<byte[], int, int, int> sendFrameAction, VideoCodecsEnum codec, 
            Action<string> sendLog, bool fpvMode, int fpvLatency)
        {
            _pc = pc;
            _sendFrameAction = sendFrameAction;
            IsSendNack = true;
            _codec = codec;


            _sendThread = new Thread(new ThreadStart(SendFrame));
            _sendThread.Start();
            _sendLog = sendLog;

            if(fpvMode)
            {
                _maxLatencyMs = (uint)fpvLatency;
            }
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

        private Queue<RTPPacket> _lastPackets = new Queue<RTPPacket>();
        private int _frameForNotUpTime = 0;

        public void ReceivePacket(RTPPacket p)
        {
            //var data = p.GetBytes();
            //try
            //{
            //    _sendFrameAction?.Invoke(data, 96, (int)_clockRate, _codec == VideoCodecsEnum.H265 ? 265 : 264);
            //}
            //catch (Exception ex)
            //{
            //    _sendLog?.Invoke("Send frame action error");
            //}

            //return;

            var timeNow = DateTime.UtcNow;
            var timeF = GetTimeF(timeNow);
            _lastPackets.Enqueue(p);

            if (timeF < p.Header.Timestamp)
            {
                //_sendLog?.Invoke($"Update Time time:{TimeFToMillsec(timeF)} packet time: {TimeFToMillsec(p.Header.Timestamp)}");
                _time = (p.Header.Timestamp, DateTime.UtcNow);
                timeF = GetTimeF(timeNow);
                _frameForNotUpTime = 0;
            }
            else
            {
                //_frameForNotUpTime++;

                //if(_frameForNotUpTime == 100)
                //{
                //    _frameForNotUpTime = 0;
                //    //not nack
                //    _time = (p.Header.Timestamp, DateTime.UtcNow);
                //    timeF = GetTimeF(timeNow);
                //}
            }

            if(_lastPackets.Count > 200)
            {
                _lastPackets.Dequeue();
            }


            var timeMs = TimeFToMillsec(timeF);
            _currentTime = timeMs;

            var packetTime = TimeFToMillsec(p.Header.Timestamp);

            RemoveOldNackBuffer();


            var lat = (int)timeMs - packetTime;

            if(lat < 0)
            {
                _sendLog?.Invoke($"lat < 0 {lat}");
            }

            if(CheckMinTimeStamp(lat, p.Header.Timestamp, timeNow))
            {
                _time = (p.Header.Timestamp, DateTime.UtcNow);
                timeF = GetTimeF(timeNow);
                _frameForNotUpTime = 0;
            }
            //if (timeMs - packetLatency > _latencyMs / 2)
            if (lat > _latencyMs / 2)
            {
                var nack = GetNack(p.Header.Timestamp, p.Header.SequenceNumber);
                
                if (nack != null)
                {
                    nack.ReceiveCount++;
                    //_sendLog?.Invoke($"Receive Nack send count: {nack.SendCount}, received count: {nack.ReceiveCount}, seq: {nack.Seq}, latency: {_currentTime - TimeFToMillsec(p.Header.Timestamp)}");
                    if (nack.ReceiveCount > 1)
                    {
                        //_sendLog?.Invoke($"Drop Nack");
                        return;
                    }
                }

                if (lat > _latencyMs)
                {
                    _sendLog?.Invoke($"Drop packet {p.Header.SequenceNumber} Packetlatency: {lat} latency: {_latencyMs}");
                    _framesOverLatency = 0;

                    //_latencyMs += _downLatencyTime / 5;
                    

                    var latDelta = lat - _latencyMs;
                    var coef = latDelta / (double)_latencyMs;
                    var isNack = nack != null;
                    if (coef > 0.3)
                    {
                        
                        _sendLog?.Invoke($"lat delta is big coef:{coef} lat: {lat} isNack: {isNack}");
                        coef = 0.3;
                    }

                    SetLatencyMs(_latencyMs + (uint)(_latencyMs * coef));
                    //_sendLog?.Invoke($"Up latency time {_latencyMs} isNack: {isNack}");

                    SendFrame(p);

                    return;
                }

                if(lat > _latencyMs - _latencyMs * 0.15f)
                {
                    SetLatencyMs(_latencyMs + (uint)(_latencyMs * 0.01f));
                    //_sendLog?.Invoke($"Up latency time {_latencyMs}");
                    _framesOverLatency = 0;
                }
            }
            else
            {
                
            }

            if(_framesOverLatency > 60)
            {
                //_latencyMs -= _downLatencyTime;
                SetLatencyMs(_latencyMs - (uint)(_latencyMs * 0.05f));

                

                _framesOverLatency = 30;
                //_sendLog?.Invoke($"Down latency time {_latencyMs}");
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
                    _framesOverLatency++;
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
                        //_sendLog?.Invoke($"previousFrame null for {frame.FrameId}");
                    }

                    var nackPackets = SendNack(latency, timeMs, frame);
                    SaveNack(nackPackets);


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

        private int _checkMinTimeStampCount = 0;
        private Queue<(long, uint)> _checkMinTimeStamp = new Queue<(long, uint)>();
        private bool CheckMinTimeStamp(long latency, uint timeStamp, DateTime timeNow)
        {
            _checkMinTimeStampCount++;
            _checkMinTimeStamp.Enqueue((latency, timeStamp));

            if(_checkMinTimeStamp.Count > 50)
            {
                _checkMinTimeStamp.Dequeue();

                if(_checkMinTimeStampCount > 50)
                {
                    _checkMinTimeStampCount = 0;

                    var minLat = _checkMinTimeStamp.Min(x => x.Item1);
                    //_sendLog?.Invoke($"min latency: {minLat}");

                    if(minLat > 20)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void SetLatencyMs(uint latency)
        {
            _latencyMs = latency;

            if (_latencyMs < _minLatencyMs)
            {
                _latencyMs = _minLatencyMs;
            }

            if(_latencyMs > _maxLatencyMs)
            {
                _latencyMs = _maxLatencyMs;
            }
            _framesOverLatency = 0;
        }

        private void RemoveOldNackBuffer()
        {
            var nackToRemove = _nackInfo.Where(x => TimeFToMillsec(x.Key) < _currentTime - 5000).ToList();

            foreach(var n in nackToRemove)
            {
                _nackInfo.Remove(n.Key);
            }
        }

        private NackInfo GetNack(uint timeStamp, ushort seq)
        {
            if(!_nackInfo.ContainsKey(timeStamp))
            {
                return null;
            }

            var nack = _nackInfo[timeStamp];

            foreach(var n in nack)
            {
                if(n.Seq == seq)
                {
                    return n;
                }
            }

            return null;
        }

        private void SaveNack(List<NackRequest> nacks)
        {
            if (nacks != null)
            {
                if (nacks.Count > 1)
                {
                    foreach(var nack in nacks)
                    {
                        SaveNack(nack);
                    }
                }
                else if(nacks.Count == 0)
                {

                }
                else
                {
                    var nack = nacks.FirstOrDefault();
                    SaveNack(nack);
                }
            }
        }

        private void SaveNack(NackRequest nack)
        {
            if (_nackInfo.ContainsKey(nack.TimeStamp))
            {
                var nackInfo = _nackInfo[nack.TimeStamp];

                foreach (var n in nack.PacketIds)
                {
                    var ni = nackInfo.FirstOrDefault(x => x.Seq == n);

                    if (ni == null)
                    {
                        nackInfo.Add(new NackInfo(n));
                    }
                    else
                    {
                        ni.SendCount++;
                    }
                }
            }
            else
            {
                _nackInfo[nack.TimeStamp] = nack.PacketIds.Select(x => { return new NackInfo(x); }).ToList();
            }
        }

        private List<NackRequest> SendNack(int latency, uint timeMs, Frame currentFrame)
        {
            var result = new List<NackRequest>();

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
                            _sendLog?.Invoke($"packet lost > 16");
                        }

                        int blp = GetBlp(sort);


                        _sendLog?.Invoke($"blp: {blp}");

                        if (IsSendNack)
                        {
                            var localVideoSsrc = _pc.VideoLocalTrack.Ssrc;
                            var remoteVideoSsrc = _pc.VideoRemoteTrack.Ssrc;
                            result.Add(new NackRequest(sort, oldFrame.Value.TimeStamp));
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
                            result.Add(new NackRequest(sort, oldFrame.Value.TimeStamp));
                            RTCPFeedback nack = new RTCPFeedback(localVideoSsrc, remoteVideoSsrc, RTCPFeedbackTypesEnum.NACK, start, (ushort)blp);
                            _pc.SendRtcpFeedback(SDPMediaTypesEnum.video, nack);
                        }
                    }
                }
            }

            return result;
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

                    //if (TimeFToMillsec(minPacket.TimeStamp) < timeMs - _latencyMs)
                    if (TimeFToMillsec(minPacket.TimeStamp) < _currentTime - _latencyMs)
                    {
                        var packetsToSend = minPacket.GetArrayToSend();

                        if (packetsToSend != null)
                        {
                            foreach (var packet in packetsToSend)
                            {
                                SendFrame(packet);
                                
                                
                            }
                        }
                        else
                        {
                            _sendLog?.Invoke($"Packets to send == null for frame: {minPacket.FrameId}");
                        }

                        if (minPacket.PreviousFrame == null)
                        {
                            //_sendLog?.Invoke($"Previous Packet NULL for frame {minPacket.FrameId}");
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

        private void SendFrame(RTPPacket packet)
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
