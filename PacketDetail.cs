using System;

namespace FalconUDP
{
    // used when backlogging packets and tracking sent packets awaiting ACK 
    class PacketDetail
    {
        internal int    ActualSequence;
        internal byte[] RawPacket;
        internal Action ACKCallback;
        internal byte   ACKTicks;
        internal byte   ResentCount;

        internal PacketDetail(byte[] rawPacket, Action ackCallback)
        {
            this.RawPacket = rawPacket;
            this.ACKCallback = ackCallback;
            this.ACKTicks = 0;
            this.ResentCount = 0;
        }
    }
}
