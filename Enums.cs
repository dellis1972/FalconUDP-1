﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FalconUDP
{

    public enum LogLevel : byte
    {
        All,        // must be first in list
        Info,
        Warning,
        Error,
        Fatal,
        NoLogging   // must be last in list
    }

    //
    // send options (bits 3 and 4 of packet info byte in header)
    //
    [Flags]
    public enum SendOptions : byte
    {
        None            = 0,    // 0000 0000
        Reliable        = 16,   // 0001 0000
        InOrder         = 32,   // 0010 0000
        ReliableInOrder = 48    // 0011 0000
    }

    //
    // bits 1 and 2 of packet info byte in header
    //
    enum HeaderPayloadSizeType : byte
    {
        Byte    = 64,   // 0100 0000
        UInt16  = 128,  // 1000 0000
    }

    //
    // packet type (last 4 bits of packet info byte in header)
    //
    enum PacketType : byte
    {
        ACK             = 0,
        AntiACK         = 1,
        AddPeer         = 2,
        DropPeer        = 3,
        AcceptJoin      = 4,
        Resynch         = 5,
        Ping            = 6,
        Application     = 7,
    }
    
}
