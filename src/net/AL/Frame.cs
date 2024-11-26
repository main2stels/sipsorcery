using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using SIPSorcery.Net;

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

        public uint TimeFirstPacketReceiveMs { get; private set; }

        public ushort MinSeq { get { return _frameMinSeq; } }

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

        public Frame(RTPPacket packet, ushort? startSeqNum, Frame previousFrame, long frameId, uint timeMs)
        {
            FrameId = frameId;
            _lastPacketTimeMs = timeMs;
            TimeFirstPacketReceiveMs = timeMs;

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

            CheckFinalPacket(packet);

            if (_frameMinSeq > seqNum)
            {
                _frameMinSeq = seqNum;

                PreviousFrame?.NextFrameSendMinSeq(_frameMinSeq);
            }

            if (_frameMaxSeq < seqNum)
            {
                _frameMaxSeq = seqNum;
            }
        }

        private void CheckFinalPacket(RTPPacket packet)
        {
            if (packet.Header.MarkerBit > 0)
            {
                SetFinalPacketNumber(packet.Header.SequenceNumber);
                FinalPacketDetected?.Invoke((ushort)_finalPacketNumber);
            }
            else if (_rangeDetected)
            {
                _rangePackets[GetPacketRange(packet.Header.SequenceNumber, (ushort)_firstPacketNumber)] = packet;
                //Console.WriteLine($"Запоздал: {packet.Header.SequenceNumber}");
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
            RangeDetected();
        }

        private void SetFirstPacketNumber(ushort value)
        {
            _firstPacketNumber = value;
            RangeDetected();
        }

        private void RangeDetected()
        {
            if (_rangeDetected)
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
                                Console.WriteLine($"nack reSend: {FrameId}id. {missPacket}, send count {nack.SendCount}");
                                lostPackets.Add((ushort)missPacket);
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
                                Console.WriteLine($"nack reSend: {FrameId}id. {(ushort)(i + minSeq)}, send count {nack.SendCount}");
                                lostPackets.Add((ushort)(ushort)(i + minSeq));

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

        public RTPPacket[] GetArrayToSend()
        {
            if (!_rangeDetected)
            {
                return null;
            }

            foreach (var packet in _rangePackets)
            {
                if (packet == null)
                {
                    return null;
                }
            }

            return _rangePackets;
        }

        private int GetPacketRange(ushort seq1, ushort seq2)
        {
            var result = seq1 - seq2;

            if (result >= 0)
            {
                return result;
            }

            return ushort.MaxValue + seq1 - seq2;

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
            _packets = null;
            _nackPackets = null;
            _rangePackets = null;
            _isDisposed = true;
        }
    }
}
