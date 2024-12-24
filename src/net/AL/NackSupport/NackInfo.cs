using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SIPSorcery.net.AL.NackSupport
{
    internal class NackInfo
    {
        public ushort Seq { get; set; }
        public int SendCount { get; set; }
        public int ReceiveCount { get; set; }

        public NackInfo(ushort seq) 
        { 
            Seq = seq;
            SendCount = 1;
            ReceiveCount = 0;
        }
    }
}
