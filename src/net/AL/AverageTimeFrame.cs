namespace SIPSorcery.net.AL
{
    internal class AverageTimeFrame
    {
        public int PacketsCount { get; set; }

        public uint LastPacketTime { get; set; }

        //ms
        public int Latency { get; set; }

        public AverageTimeFrame(uint lastPacketTime)
        {
            Latency = -1;
            PacketsCount = 1;
            LastPacketTime = lastPacketTime;
        }
    }
}
