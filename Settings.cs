using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FalconUDP
{
    static class Settings
    {
        internal static int PORT;                                                           // assigned on Init()
        internal static Encoding TEXT_ENCODING          = Encoding.UTF8;

        internal const int NORMAL_HEADER_SIZE           = 3;                                // in bytes
        internal const int LARGE_HEADER_SIZE            = 4;                                // in bytes (used when payload size > Byte.MaxValue)
        internal const int JOIN_LISTEN_THREAD_TIMEOUT   = 500;                              // milliseconds
        internal const int MAX_DATAGRAM_SIZE            = 65507;                            // this is an IPv4 limit, v6 allows slightly more but we needn't
        internal const byte MAX_SEQ                     = Byte.MaxValue;
        internal const int MAX_SEQ_NUMS                 = MAX_SEQ + 1;                      // + 1 to include "0"
        internal const int HALF_MAX_SEQ_NUMS            = MAX_SEQ_NUMS / 2;
        internal const int ACK_TIMEOUT                  = 100000;                           // milliseconds (should be multiple of ACK_TICK_TIME)
        internal const int ACK_TICK_TIME                = 10000;                            // milliseconds (note timer could tick just as packet sent so this also defines the error margin)
        internal const int ACK_TIMEOUT_TICKS            = ACK_TIMEOUT / ACK_TICK_TIME;      // timeout in ticks 
        internal const int ACK_RETRY_ATTEMPTS           = 3;                                // number of times to retry until assuming peer is dead (used by AwaitingAcceptDetail too)
        internal const byte PAYLOAD_SIZE_TYPE_MASK      = 192;                              // 1100 0000 AND'd with packet info byte returns PayloadSizeHeaderType
        internal const byte SEND_OPTS_MASK              = 48;                               // 0011 0000 AND'd with packet info byte returns SendOptions
        internal const byte PACKET_TYPE_MASK            = 15;                               // 0000 1111 AND'd with packet info byte returns PacketType
        internal const int MAX_ACTUAL_SEQ               = Int32.MaxValue - 1;               // Sequence number when reached to send out a re-synch request - upon ACK reset seq counters.


        // Packets received with calculated actual seq num outside max actual seq num received + 
        // or - this value are dropped. The smaller this value the more tolerant of out-of-order
        // we are...

        internal const int OUT_OF_ORDER_TOLERANCE = HALF_MAX_SEQ_NUMS / 2;

        internal const byte JOIN_PACKET_INFO    = (byte)((byte)PacketType.AddPeer | (byte)SendOptions.None | (byte)HeaderPayloadSizeType.Byte);
        internal const byte PING_PACKET_INFO    = (byte)((byte)PacketType.Ping | (byte)SendOptions.None | (byte)HeaderPayloadSizeType.Byte);
    }
}
