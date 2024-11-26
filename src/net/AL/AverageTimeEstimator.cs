using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SIPSorcery.Net;

namespace SIPSorcery.net.AL
{
    internal class AverageTimeEstimator
    {
        private int _framesCount = 400;
        //frame time, (latency, packet count)
        private Dictionary<uint, AverageTimeFrame> _frames;

        public AverageTimeEstimator()
        {
            _frames = new Dictionary<uint, AverageTimeFrame>();
        }

        public void InsertPacket(RTPPacket packet, uint currentTime)
        {
            var packetTime = packet.Header.Timestamp;


            if (_frames.ContainsKey(packetTime))
            {
                var f = _frames[packetTime];

                if (f.PacketsCount == 1)
                {
                    f.PacketsCount++;

                    f.Latency = (int)(currentTime - f.LastPacketTime);

                    f.LastPacketTime = currentTime;
                }
                else if (f.PacketsCount > 1)
                {
                    f.PacketsCount++;

                    var latency = (int)(currentTime - f.LastPacketTime);

                    f.Latency = f.Latency + latency / 2;

                    f.LastPacketTime = currentTime;
                }
                else
                {
                    Console.WriteLine("Average Error!!!!");
                }
            }
            else
            {
                _frames.Add(packetTime, new AverageTimeFrame(currentTime));
            }

            if (_frames.Count > _framesCount)
            {
                _frames.Remove(_frames.Min(x => x.Key));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns>in ms, -1 if error</returns>
        public int GetLatency()
        {
            int latency = -1;

            lock (_frames)
            {
                var frames = _frames.Where(x => x.Value.Latency >= 0).ToList();

                if (frames.Count == 0)
                {
                    return -1;
                }

                try
                {
                    var sum = frames.Sum(x => x.Value.Latency);
                    latency = sum / frames.Count;
                }
                catch
                {
                    Console.WriteLine($"{latency}");
                }
            }

            return latency;
        }
    }
}
