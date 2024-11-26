using System;
using System.Collections.Generic;
using System.Text;
using SIPSorcery.Net;

namespace SIPSorcery.net.AL
{
    internal class NackRtpPacket
    {
        public uint SendTimeMs { get; set; }
        public RTPPacket RtpPacket { get; set; }
        public bool IsReceive { get; set; }
        public int SendCount { get; set; }

        public NackRtpPacket(RTPPacket rtpPacket, uint timeReceiveMs, int sendCount)
        {
            RtpPacket = rtpPacket;
            SendTimeMs = timeReceiveMs;
            IsReceive = false;
            SendCount = sendCount;
        }
    }
}
