using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Ocsp;

namespace SIPSorcery.net.AL
{
    class Frame
    {
        private const ushort NACK_FRAMES_REQEST_OVER_NEXT_FRAME = 3;
        private static Queue<uint> _nackLatency = new Queue<uint>();

        public long FrameId { get; private set; }
        public uint TimeStamp { get; private set; }
        public int FinalPacketNumber { get { return _finalPacketNumber ?? -1; } }

        public Action<ushort> FinalPacketDetected;

        public Frame PreviousFrame { get; private set; }
        public Frame NextFrame { get; private set; }

        public uint TimeFirstPacketReceiveMs { get; private set; }

        public ushort MinSeq { get { return _frameMinSeq; } }

        private VideoCodecsEnum _codec;

        private int _frameNumber;

        private ushort? _finalPacketNumber = null;
        private ushort? _firstPacketNumber = null;

        private ushort? _nextFrameMinSeq = null;
        private ushort _frameMinSeq;
        private ushort _frameMaxSeq;

        private uint _lastPacketTimeMs = 0;

        private Dictionary<ushort, RTPPacket> _packets = new Dictionary<ushort, RTPPacket>();
        private Dictionary<ushort, NackRtpPacket> _nackPackets = new Dictionary<ushort, NackRtpPacket>();

        private bool _rangeDetected = false;
        private RTPPacket[] _rangePackets = null;

        private bool _frameIsAssembled = false;
        private bool _isDisposed = false;
        private bool _isFindMarckerBit = false;

        public Frame(RTPPacket packet, ushort? startSeqNum, Frame previousFrame, long frameId, uint timeMs, VideoCodecsEnum codec)
        {
            FrameId = frameId;
            _lastPacketTimeMs = timeMs;
            TimeFirstPacketReceiveMs = timeMs;
            _codec = codec;

            _frameMinSeq = packet.Header.SequenceNumber;
            _frameMaxSeq = packet.Header.SequenceNumber;
            _packets[packet.Header.SequenceNumber] = packet;
            CheckFinalPacket(packet);
            TimeStamp = packet.Header.Timestamp;

            PreviousFrame = previousFrame;

            PreviousFrame?.NextFrameSendMinSeq(_frameMinSeq);

            if (startSeqNum == null)
            {
                if (previousFrame != null)
                {
                    previousFrame.FinalPacketDetected = PreviousPacketFinalDetected;
                }
            }
            else
            {
                SetFirstPacketNumber((ushort)startSeqNum);
            }

            if (previousFrame?.FinalPacketNumber >= 0)
            {
                var seq = previousFrame.FinalPacketNumber + 1;
                if (seq > ushort.MaxValue)
                {
                    seq = 0;
                }

                SetFirstPacketNumber((ushort)seq);

                if (packet.Header.PayloadSize < 1200 && packet.Header.SequenceNumber == _firstPacketNumber)
                {
                    //SetFinalPacketNumber((ushort)_firstPacketNumber);
                }
            }


        }

        public void AddPacket(RTPPacket packet, uint timeMs)
        {
            var seqNum = packet.Header.SequenceNumber;

            NackRtpPacket nPacket = null;
            if (_nackPackets.Count > 0)
            {
                if (_nackPackets.TryGetValue(seqNum, out nPacket))
                {
                    nPacket.RtpPacket = packet;
                    nPacket.IsReceive = true;

                    if (timeMs > nPacket.SendTimeMs)
                    {
                        AddNackLatency(timeMs - nPacket.SendTimeMs);
                    }

                    //Console.WriteLine($"Nack packet {seqNum} latency: {timeMs - nPacket.SendTimeMs} frame: {FrameId}");
                }
            }

            if (nPacket == null)
            {
                _lastPacketTimeMs = timeMs;
            }

            lock (_packets)
            {
                _packets[seqNum] = packet;
            }


            if (_frameMinSeq > seqNum)
            {
                _frameMinSeq = seqNum;

                PreviousFrame?.NextFrameSendMinSeq(_frameMinSeq);

                if(_firstPacketNumber > seqNum)
                {
                    SetFirstPacketNumber(seqNum);
                }
            }

            if (_frameMaxSeq < seqNum)
            {
                _frameMaxSeq = seqNum;

                if(_finalPacketNumber != null)
                {
                    if(_finalPacketNumber < seqNum)
                    {
                        SetFinalPacketNumber(seqNum);
                    }
                }
            }

            CheckFinalPacket(packet);
        }


        private List<RTPPacket> _packetsOverMarkerBit = new List<RTPPacket>();
        private void CheckFinalPacket(RTPPacket packet)
        {
            
            if (packet.Header.MarkerBit > 0 && _codec == VideoCodecsEnum.H264)
            {
                if (_codec == VideoCodecsEnum.H264)
                {
                    SetFinalPacketNumber(packet.Header.SequenceNumber);
                    FinalPacketDetected?.Invoke((ushort)_finalPacketNumber);
                }


                //todo: hz
                //if(_packetsOverMarkerBit.Count > 0)
                //{
                //    Console.WriteLine($"OverMarkerbits for Frame {FrameId}");
                //}

                //_isFindMarckerBit = true;
            }

            if (_rangeDetected)
            {
                try
                {
                    if(packet.Header.SequenceNumber < _firstPacketNumber)
                    {
                        Console.WriteLine("Force range detected");
                        RangeDetected(true);
                    }

                    _rangePackets[GetPacketRange(packet.Header.SequenceNumber, (ushort)_firstPacketNumber)] = packet;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("CheckFinalPacket ERROR");
                    RangeDetected(true);
                }

                //var packetRange = GetPacketRange(packet.Header.SequenceNumber, (ushort)_finalPacketNumber);
                //if (packetRange > 0)
                //{
                //    if (_packetsOverMarkerBit.Count > 0)
                //    {
                //        Console.WriteLine($"OverMarkerbits!! {FrameId}, find marker bit: {_isFindMarckerBit}");
                //        foreach(var f in _packets)
                //        {
                //            Console.Write($"{f.Key} {f.Value.Header.MarkerBit} ");
                //        }
                //        Console.Write($"\n");
                //    }
                //    if (_packetsOverMarkerBit.Count > 0)
                //    {
                //        _rangeDetected = false;
                //        _finalPacketNumber = null;
                //    }
                //    _packetsOverMarkerBit.Add(packet);
                //}
                //else
                //{
                //    try
                //    {
                //        _rangePackets[GetPacketRange(packet.Header.SequenceNumber, (ushort)_firstPacketNumber)] = packet;
                //    }
                //    catch (Exception ex)
                //    {
                //        Console.WriteLine("CheckFinalPacket ERROR");
                //    }
                //}

                
                
                //Console.WriteLine($"Запоздал: {packet.Header.SequenceNumber}");
            }

            if(_nextFrameMinSeq != null && _codec != VideoCodecsEnum.H264)
            {
                var nextFS = (ushort)_nextFrameMinSeq;

                var range = GetPacketRange(packet.Header.SequenceNumber, nextFS);

                if(range == 1)
                {
                    if (_finalPacketNumber != null)
                    {
                        SetFinalPacketNumber(packet.Header.SequenceNumber);
                        FinalPacketDetected?.Invoke((ushort)_finalPacketNumber);
                        //Console.WriteLine($"SetFinalPacketNumber {FrameId} CheckFinalPacket");
                    }
                }
            }
        }

        private void PreviousPacketFinalDetected(ushort seqNum)
        {
            PreviousFrame.FinalPacketDetected = null;
            //PreviousFrame = null;
            SetFirstPacketNumber(++seqNum);
        }

        private void SetFinalPacketNumber(ushort value)
        {
            _finalPacketNumber = value;
            RangeDetected(true);
        }

        private void SetFirstPacketNumber(ushort value)
        {
            _firstPacketNumber = value;
            RangeDetected(true);
        }

        private void RangeDetected(bool force = false)
        {
            if (_rangeDetected && !force)
            {
                return;
            }

            var isDetect = _firstPacketNumber != null && _finalPacketNumber != null;

            if (isDetect)
            {
                var lenght = GetRengeLenght();
                _rangePackets = new RTPPacket[lenght];

                lock (_packets)
                {
                    foreach (var p in _packets.Values)
                    {
                        try
                        {
                            _rangePackets[GetPacketRange(p.Header.SequenceNumber, (ushort)_firstPacketNumber)] = p;
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.ToString());
                        }
                    }
                }

                //todo:remove:
                for (int i = 0; i < _rangePackets.Length; i++)
                {
                    if (_rangePackets[i] == null)
                    {
                        //Console.WriteLine($"packet {i + _firstPacketNumber} lost");
                    }
                }

            }
            else if (_firstPacketNumber == null)
            {
                if (PreviousFrame != null)
                {
                    //Console.WriteLine($"First packet not detected");
                }
                //Console.WriteLine($"range error. Previev frame:{(_previousFrame == null ? "not null!!": "null") }");
            }
            else if (_finalPacketNumber == null)
            {
                //Console.WriteLine($"Final packet not detected, next frame min seq: {_nextFrameMinSeq}");
            }

            _rangeDetected = isDetect;
        }

        private int GetRengeLenght()
        {
            return GetPacketRange((ushort)_finalPacketNumber, (ushort)_firstPacketNumber) + 1;
            if (_finalPacketNumber >= _firstPacketNumber)
            {
                return (ushort)_finalPacketNumber - (ushort)_firstPacketNumber + 1;
            }
            else
            {
                return ushort.MaxValue - (ushort)_firstPacketNumber + (ushort)_finalPacketNumber + 1;
            }
        }


        public List<ushort> CheckLostPackets(uint timeMs, int latency)
        {
            if (_frameIsAssembled)
            {
                return null;
            }

            if (timeMs < (_lastPacketTimeMs + (latency * 2)))
            {
                //Console.WriteLine($"It's too early to send a request. time: {timeMs}, {(_lastPacketTimeMs + (latency * 2))}, latency: {latency}");
                return null;
            }

            List<ushort> lostPackets = new List<ushort>();
            //todo: учитывать время прихода последнего пакета
            if (_rangePackets != null)
            {
                _frameIsAssembled = true;
                for (ushort i = 0; i < _rangePackets.Length; i++)
                {
                    if (_rangePackets[i] == null)
                    {
                        _frameIsAssembled = false;
                        if (_firstPacketNumber == null)
                        {
                            Console.WriteLine("_firstPacketNumber ERROR!!!!");
                        }

                        var missPacket = i + (ushort)_firstPacketNumber;

                        if (missPacket > ushort.MaxValue)
                        {
                            missPacket = missPacket - ushort.MaxValue - 1;
                        }

                        if (!_nackPackets.ContainsKey((ushort)missPacket))
                        {
                            lostPackets.Add((ushort)missPacket);
                        }
                        else
                        {
                            var nack = _nackPackets[(ushort)missPacket];

                            if (timeMs - nack.SendTimeMs > GetNackResendLatency())
                            {
                                //Console.WriteLine($"nack reSend: {FrameId}id. {missPacket}, send count {nack.SendCount}");
                                //lostPackets.Add((ushort)missPacket);

                                var seq = (ushort)missPacket;

                                if (_nextFrameMinSeq != null)
                                {
                                    if (Compare(seq, (ushort)_nextFrameMinSeq))
                                    {
                                        //Console.WriteLine($"nack reSend: {FrameId}id. {seq}, send count {nack.SendCount}");
                                        lostPackets.Add(seq);
                                    }
                                    else
                                    {
                                        Console.WriteLine("Drop nack for next frame");
                                    }
                                }
                                else
                                {
                                    //Console.WriteLine($"nack reSend: {FrameId}id. {seq}, send count {nack.SendCount}");
                                    lostPackets.Add(seq);
                                }
                            }
                        }
                        //Console.WriteLine($"packet {i + _firstPacketNumber} lost");
                    }
                }
            }
            else
            {
                ushort minSeq = 0;
                ushort maxSeq = 0;

                if (_firstPacketNumber != null)
                {
                    minSeq = _firstPacketNumber.Value;
                }
                else
                {
                    minSeq = _frameMinSeq;
                }

                if (_finalPacketNumber != null)
                {
                    maxSeq = _finalPacketNumber.Value;
                }
                else
                {
                    if (_nextFrameMinSeq != null)
                    {
                        var max = _nextFrameMinSeq.Value - 1;

                        if (max < 0)
                        {
                            Console.WriteLine("Error set max frame");
                        }
                        maxSeq = (ushort)max;
                    }
                    else
                    {
                        var max = _frameMaxSeq + NACK_FRAMES_REQEST_OVER_NEXT_FRAME;

                        if (max > ushort.MaxValue)
                        {
                            Console.WriteLine("Max Frame over max value");
                        }

                        maxSeq = (ushort)max;
                    }
                }

                if (minSeq > maxSeq)
                {
                    Console.WriteLine("Error: max seq!!!!!!!!!");
                }

                if (minSeq == 0)
                {
                    Console.WriteLine("Error: min seq!!!!!!!");
                }

                var packetCount = maxSeq - minSeq + 1;

                for (ushort i = 0; i < packetCount; i++)
                {
                    if (!_packets.ContainsKey((ushort)(i + minSeq)))
                    {
                        if (!_nackPackets.ContainsKey((ushort)(i + minSeq)))
                        {
                            lostPackets.Add((ushort)(i + minSeq));
                        }
                        else
                        {
                            var nack = _nackPackets[(ushort)(i + minSeq)];

                            if (timeMs - nack.SendTimeMs > 100)
                            {
                                var seq = (ushort)(i + minSeq);

                                if (_nextFrameMinSeq != null)
                                {
                                    if (Compare(seq, (ushort)_nextFrameMinSeq))
                                    {
                                        //Console.WriteLine($"nack reSend: {FrameId}id. {seq}, send count {nack.SendCount}");
                                        lostPackets.Add(seq);
                                    }
                                    else
                                    {
                                        Console.WriteLine("Drop nack for next frame");
                                    }
                                }
                                else
                                {
                                    //Console.WriteLine($"nack reSend: {FrameId}id. {seq}, send count {nack.SendCount}");
                                    lostPackets.Add(seq);
                                }
                            }
                        }
                    }
                }
            }

            foreach (var lp in lostPackets)
            {
                if (!_nackPackets.ContainsKey(lp))
                {
                    _nackPackets.Add(lp, new NackRtpPacket(null, timeMs, 1));
                }
                else
                {
                    var nackP = _nackPackets[lp];
                    nackP.SendTimeMs = timeMs;
                    nackP.SendCount++;
                }
            }

            return lostPackets;
        }

        public void NextFrameSendMinSeq(ushort seq)
        {
            _nextFrameMinSeq = seq;

            //if(_finalPacketNumber == null)
            //{
            //    ushort finalPacketSeq = 0;

            //    if(seq - 1 > 0)
            //    {
            //        finalPacketSeq = (ushort)(seq - 1);
            //    }
            //    else
            //    {
            //        finalPacketSeq = ushort.MaxValue;
            //    }

            //    //Console.WriteLine($"SetFinalPacketNumber {FrameId} NextFrameSendMinSeq");
            //    SetFinalPacketNumber(finalPacketSeq);
            //    FinalPacketDetected?.Invoke((ushort)_finalPacketNumber);
            //}

            ushort finalPacketSeq = 0;

            if (seq - 1 > 0)
            {
                finalPacketSeq = (ushort)(seq - 1);
            }
            else
            {
                finalPacketSeq = ushort.MaxValue;
            }

            //Console.WriteLine($"SetFinalPacketNumber {FrameId} NextFrameSendMinSeq");
            SetFinalPacketNumber(finalPacketSeq);
            FinalPacketDetected?.Invoke((ushort)_finalPacketNumber);
        }

        public void SetPreviousFrame(Frame pf)
        {
            PreviousFrame = pf;
            PreviousFrame.FinalPacketDetected = PreviousPacketFinalDetected;


            if (PreviousFrame?.FinalPacketNumber >= 0)
            {
                var seq = PreviousFrame.FinalPacketNumber + 1;
                if (seq > ushort.MaxValue)
                {
                    seq = 0;
                }

                SetFirstPacketNumber((ushort)seq);
            }
        }

        public void SetNextFrame(Frame nf)
        {
            NextFrame = nf;
        }

        public RTPPacket[] GetArrayToSend()
        {
            if (!_rangeDetected)
            {
                lock (_packets)
                {
                    Console.Write($"Send drop packets {FrameId}");
                    var result = _packets.Values.OrderBy(x => x.Header.SequenceNumber).ToArray();
                    foreach ( RTPPacket packet in result)
                    {
                        Console.Write($" {packet.Header.SequenceNumber}");
                    }

                    Console.WriteLine(".");
                    
                    return result;
                }
                return null;
            }

            //todo: отправить, что есть

            var listLostPackets = new List<int>();
            for(ushort i = 0; i < _rangePackets.Length; i++)
            {
                var packet = _rangePackets[i];
                if (packet == null)
                {
                    listLostPackets.Add(GetPacketRange(i, (ushort)_firstPacketNumber));
                    //Console.WriteLine($"Return array to send with drop packets {FrameId}");
                    
                    //return null;
                }
            }

            if(listLostPackets.Count > 0)
            {
                Console.Write("Range packets with lost packet:");
                foreach (var packet in listLostPackets)
                {
                    Console.Write($" {packet}");
                }
                Console.WriteLine(".");

                return _rangePackets.Where(x => x != null).ToArray();
            }

            return _rangePackets;
        }

        private int GetPacketRange(ushort seq1, ushort seq2)
        {
            var result = seq1 - seq2;

            if(result < 0)
            {
                result = -result;
            }

            if (result > ushort.MaxValue / 2)
            {
                result = ushort.MaxValue - result + 1;
            }

            return result;
        }

        //min < max = true
        public static bool Compare(ushort min, ushort max)
        {
            var delta = max - min;

            if (delta == 0)
            {
                return false;
            }

            var a = delta;

            if(a < 0)
            {
                a = -a;
            }

            if (a > ushort.MaxValue / 2)
            {
                return min > max;
            }

            return min < max;
        }

        public static bool Compare(int min, int max)
        {
            var delta = max - min;

            if (delta == 0)
            {
                return false;
            }

            var a = delta;

            if (a < 0)
            {
                a = -a;
            }

            if (a > ushort.MaxValue / 2)
            {
                return min > max;
            }

            return min < max;
        }


        private uint GetNackResendLatency()
        {
            if (_nackLatency.Count == 0)
            {
                return 100;
            }

            return (uint)(_nackLatency.Sum(x => x) / _nackLatency.Count * 1.2d);
        }

        private void AddNackLatency(uint latency)
        {
            _nackLatency.Enqueue(latency);

            if (_nackLatency.Count > 10)
            {
                _nackLatency.Dequeue();
            }

        }

        public void Dispose()
        {

            FinalPacketDetected = null;
            PreviousFrame = null;
            NextFrame = null;
            _packets = null;
            _nackPackets = null;
            _rangePackets = null;
            _isDisposed = true;
        }
    }
}
