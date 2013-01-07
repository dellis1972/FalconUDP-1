namespace FalconUDP
{
    public class Packet
    {
        private int peerId;
        private uint seq;
        private byte[] payload;


        public int PeerId { get { return peerId; } }
        public uint Seq { get { return seq; } }
        public byte[] Data { get { return payload; } }


        public Packet(int peerId, uint seq, byte[] data) // TODO only of ip or peerId need be supplied then the other is calculated
        {
            this.peerId = peerId;
            this.seq = seq;
            this.payload = data;
        }
    }
}
