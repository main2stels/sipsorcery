using System;
using System.Collections.Generic;
using System.Text;

namespace SIPSorcery.net.AL
{
    class SeqIdComparer : IComparer<ushort>
    {
        public int Compare(ushort x, ushort y)
        {
            var d = x - y;

            if (d > (ushort.MaxValue - 60))
            {
                return -1;
            }
            else if (d < (-ushort.MaxValue + 60))
            {
                return 1;
            }

            return d;
        }
    }
}
