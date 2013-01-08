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
        internal IPEndPoint EndPoint;
        internal TryCallback Callback;
        internal int Ticks;
        internal string Pass;
        internal int RetryCount;

        internal AwaitingAcceptDetail(IPEndPoint ip, TryCallback callback, string pass)
        {
            EndPoint = ip;
            Callback = callback;
            Pass = pass;
            Ticks = 0;
            RetryCount = 0;
        }
    }
}
