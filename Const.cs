﻿using System;

namespace FalconUDP
{
    static class Const
    {
        internal static Type HEADER_PAYLOAD_SIZE_TYPE_TYPE      = typeof(HeaderPayloadSizeType);
        internal static Type PACKET_TYPE_TYPE                   = typeof(PacketType);
        internal static Type SEND_OPTIONS_TYPE                  = typeof(SendOptions);

        internal const int NORMAL_HEADER_SIZE                   = 3;                                // in bytes
        internal const int LARGE_HEADER_SIZE                    = 4;                                // in bytes (used when payload size > Byte.MaxValue)

        internal const int MAX_DATAGRAM_SIZE                    = 65507;                            // this is an IPv4 limit, v6 allows slightly more but we needn't

        internal const byte MAX_SEQ                             = Byte.MaxValue;
        internal const int MAX_SEQ_NUMS                         = MAX_SEQ + 1;                      // + 1 to include "0"
        internal const int HALF_MAX_SEQ_NUMS                    = MAX_SEQ_NUMS / 2;
        internal const int MAX_ACTUAL_SEQ                       = Int32.MaxValue - 1;               // Sequence number when reached to send out a re-synch request - upon ACK reset seq counters.

        internal const byte PAYLOAD_SIZE_TYPE_MASK              = 192;                              // 1100 0000 AND'd with packet info byte returns PayloadSizeHeaderType
        internal const byte SEND_OPTS_MASK                      = 48;                               // 0011 0000 AND'd with packet info byte returns SendOptions
        internal const byte PACKET_TYPE_MASK                    = 15;                               // 0000 1111 AND'd with packet info byte returns PacketType

        internal const byte JOIN_PACKET_INFO                    = (byte)((byte)PacketType.AddPeer | (byte)SendOptions.None | (byte)HeaderPayloadSizeType.Byte);
        internal const byte PING_PACKET_INFO                    = (byte)((byte)PacketType.Ping | (byte)SendOptions.None | (byte)HeaderPayloadSizeType.Byte);
    }
}
