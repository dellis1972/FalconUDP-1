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
        internal const int ACKTimeout                   = 1000;                             // milliseconds (should be multiple of ACK_TICK_TIME)
        internal const int ACKTickTime                  = 100;                              // milliseconds (note timer could tick just after packet sent so this also defines the error margin)
        internal const int ACKTimeoutTicks              = ACKTimeout / ACKTickTime;         // timeout in ticks 
        internal const int ACKRetryAttempts             = 3;                                // number of times to retry until assuming peer is dead (used by AwaitingAcceptDetail too)
        internal const int OutOfOrderTolerance          = Const.HALF_MAX_SEQ_NUMS / 2;      // packets recveived out-of-order from last received greater than this are dropped
    }
}
