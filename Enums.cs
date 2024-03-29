﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FalconUDP
{
    /// <summary>
    /// LogLevel
    /// </summary>
    public enum LogLevel : byte
    {
        All,        // must be first in list
        Info,
        Warning,
        Error,
        Fatal,
        NoLogging   // must be last in list
    }

    /// <summary>
    /// Options how to send a packet.
    /// </summary>
    /// <remarks>
    /// Reliable packets are re-sent until ACKnowledged
    /// InOrder specfies packets not in order when reading packets packets received will be dropped.
    /// </remarks>
    [Flags]
    public enum SendOptions : byte
    {
        None            = 0,    // 0000 0000
        Reliable        = 16,   // 0001 0000
        InOrder         = 32,   // 0010 0000
        ReliableInOrder = 48    // 0011 0000
    }

    // bits 1 and 2 of packet info byte in header
    enum HeaderPayloadSizeType : byte
    {
        Byte    = 64,   // 0100 0000
        UInt16  = 128,  // 1000 0000
    }

    // packet type (last 4 bits of packet info byte in header)
    enum PacketType : byte
    {
        ACK,
        AntiACK,
        AddPeer,
        DropPeer,
        AcceptJoin,
        RejectJoin,
        Ping,
        Pong,
        Application,
    }
    
}
