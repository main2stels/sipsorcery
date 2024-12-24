using System;
using System.Collections.Generic;
using System.Text;

namespace SIPSorcery.net.AL
{
    internal class NackRequest
    {
        public ushort[] PacketIds { get; set; }
        public uint TimeStamp { get; set; }

        public NackRequest(ushort[] packetIds, uint timeStamp) 
        {
            PacketIds = packetIds;
            TimeStamp = timeStamp;
        }
    }
}
