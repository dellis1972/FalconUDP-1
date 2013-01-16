namespace FalconUDP
{
    public class Packet
    {
        private int peerId;
        private byte seq;
        private byte[] payload;


        public int PeerId { get { return peerId; } }
        public byte Seq { get { return seq; } }
        public byte[] Data { get { return payload; } }


        public Packet(int peerId, byte seq, byte[] data) // TODO only of ip or peerId need be supplied then the other is calculated
        {
            this.peerId = peerId;
            this.seq = seq;
            this.payload = data;
        }
    }
}
