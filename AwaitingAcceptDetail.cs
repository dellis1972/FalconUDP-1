using System.Net;

namespace FalconUDP
{
    class AwaitingAcceptDetail
    {
#if NETFX_CORE
        internal IPv4EndPoint EndPoint;
#else
        internal IPEndPoint EndPoint;
#endif

        internal TryCallback Callback;
        internal int Ticks;
        internal string Pass;
        internal int RetryCount;
        internal byte[] JoinPacket;

#if NETFX_CORE
        internal AwaitingAcceptDetail(IPv4EndPoint ip, TryCallback callback, string pass)
#else
        internal AwaitingAcceptDetail(IPEndPoint ip, TryCallback callback, string pass)
#endif
        {
            EndPoint = ip;
            Callback = callback;
            Pass = pass;
            Ticks = 0;
            RetryCount = 0;
        }
    }
}
