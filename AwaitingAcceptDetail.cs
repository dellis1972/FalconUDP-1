using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

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
