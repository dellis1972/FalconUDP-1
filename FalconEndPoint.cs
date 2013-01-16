#if NETFX_CORE
using System;
using System.Text;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;

namespace FalconUDP
{
    /// <summary>
    /// Replacement for IPEndPoint which is lacking in NETFX_CORE.
    /// </summary>
    class FalconEndPoint : IEquatable<FalconEndPoint>
    {
        private HostName hostName;
        private string portAsString;
        private uint ip;
        private ushort port;
        private int hash;

        public HostName Address { get { return hostName; } }    //} dumbass NETFX_CORE expects these types
        public string Port { get { return portAsString; } }     //} (why the frig is port a string?!)

        internal FalconEndPoint(string ip, string port)
        {
            this.hostName = new HostName(ip);
            this.portAsString = port;

            // TODO fix this shit up

            // Currently we only accept the real deal - raw IP address and port as numbers,
            // I'm not going to waste flipping time trying to connect to DNS and resolve the
            // IP. But when we do get around to it: http://msdn.microsoft.com/en-us/library/windows/apps/hh701245.aspx
            // though that should be done outside this class.

            this.port = UInt16.Parse(port);
            this.ip = StringToIP(hostName.RawName);

            UpdateHash();
        }

        private void UpdateHash()
        {
            this.hash = (int)this.ip | this.port;
        }

        private static uint StringToIP(string str)
        {
            uint ip = 0;
            string[] octets = str.Split('.');
            ip |= (UInt32.Parse(octets[0]) << 24);
            ip |= (UInt32.Parse(octets[1]) << 16);
            ip |= (UInt32.Parse(octets[2]) << 8);
            ip |= UInt32.Parse(octets[3]);
            return ip;
        }

        public override int GetHashCode()
        {
            return hash;
        }

        public bool Equals(FalconEndPoint other)
        {
            return this.hostName.IsEqual(other.hostName) && this.port == other.port; // how are we able to access private fields? (cause we are in the class..)
        }

        public override string ToString()
        {
            return String.Format("{0}:{1}", hostName.CanonicalName, portAsString);
        }
    }
}
#endif
