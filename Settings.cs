using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FalconUDP
{
    static class Settings
    {
        internal static Encoding TextEncoding           = Encoding.UTF8;
        internal const int JoinListenThreadTimeout      = 500;                              // milliseconds
        internal const int ACKTimeout                   = 10000;                            // milliseconds (should be multiple of ACK_TICK_TIME)
        internal const int ACKTickTime                  = 1000;                             // milliseconds (note timer could tick just after packet sent so this also defines the error margin)
        internal const int ACKTimeoutTicks              = ACKTimeout / ACKTickTime;         // timeout in ticks 
        internal const int ACKRetryAttempts             = 3;                                // number of times to retry until assuming peer is dead (used by AwaitingAcceptDetail too)
        
        // Packets received with calculated actual seq num outside max actual seq num received + 
        // or - this value are dropped. The smaller this value the smaller the error margin of 
        // the sequencing algorithim of out-of-order packets - but the more out-of-order packets
        // will be dropped.

        // i.e. Correctness is inversely proporitnal to 

        internal const int OUT_OF_ORDER_TOLERANCE = Const.HALF_MAX_SEQ_NUMS / 2;
    }
}
