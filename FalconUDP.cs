using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Text;


/***************************************************
 * 
 *  A FalconUDP packet in bytes
 *  
 *      [0]     sequence number
 *      
 *      [1]     packet info
 *      
 *      [2]     } payload size - either a byte or ushort - defined by HeaderPayloadSizeType in packet info
 *      [n]     }
 *      
 *      [n+1]   }
 *      ...     } payload (if any)
 *      [m]     } 
 *      
 * 
 *  packet info byte in bits
 *  
 *      [0]     }   HeaderPayloadSizeType
 *      [1]     }
 *      
 *      [2]     }   SendOptions
 *      [3]     }
 *      
 *      [4]     }   
 *      [5]     }   PacketType
 *      [6]     }
 *      [7]     }
 *      
 ****************************************************/


namespace FalconUDP
{
    public delegate void PeerAdded(int id);
    public delegate void PeerDropped(int id);
    public delegate void ApplicationEvent(object tag);


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
        None = 0,        // 0000 0000
        Reliable = 16,       // 0001 0000
        InOrder = 32,       // 0010 0000
        ReliableInOrder = 48        // 0011 0000
    }


    //
    // bits 1 and 2 of packet info byte in header
    //
    enum HeaderPayloadSizeType : byte
    {
        Byte = 64,   // 0100 0000
        UInt16 = 128,  // 1000 0000
    }


    //
    // packet type (last 4 bits of packet info byte in header)
    //
    public enum PacketType : byte
    {
        ACK = 0,
        AntiACK = 1,
        AddPeer = 2,
        DropPeer = 3,
        Accept = 4,
        Resynch = 5,
        Application = 6,
    }


    static class Const
    {
        internal static int PORT;                                                           // assigned on Init()


        internal const int NORMAL_HEADER_SIZE = 3;                                // in bytes
        internal const int LARGE_HEADER_SIZE = 4;                                // in bytes (used when payload size > Byte.MaxValue)
        internal const int JOIN_LISTEN_THREAD_TIMEOUT = 500;                              // milliseconds
        internal const int MAX_DATAGRAM_SIZE = 65507;                            // this is an IPv4 limit, v6 allows slightly more but we needn't
        internal const byte MAX_SEQ = Byte.MaxValue;
        internal const int MAX_SEQ_NUMS = MAX_SEQ + 1;                      // + 1 to include "0"
        internal const int HALF_MAX_SEQ_NUMS = MAX_SEQ_NUMS / 2;
        internal const int ACK_TIMEOUT = 600;                              // milliseconds (should be multiple of ACK_TICK_TIME)
        internal const int ACK_TICK_TIME = 100;                              // milliseconds (note timer could tick just as packet sent so this also defines the error margin)
        internal const int ACK_TIMEOUT_TICKS = ACK_TIMEOUT / ACK_TICK_TIME;      // timeout in ticks 
        internal const byte PAYLOAD_SIZE_TYPE_MASK = 192;                              // 1100 0000 AND'd with packet info byte returns PayloadSizeHeaderType
        internal const byte SEND_OPTS_MASK = 48;                               // 0011 0000 AND'd with packet info byte returns SendOptions
        internal const byte PACKET_TYPE_MASK = 15;                               // 0000 1111 AND'd with packet info byte returns PacketType
        internal const int MAX_ACTUAL_SEQ = Int32.MaxValue - 1;               // Sequence number when reached to send out a re-synch request - upon ACK reset seq counters.


        // Packets received with calculated actual seq num outside max actual seq num received + 
        // or - this value are dropped. To ensure calculated actual seq num is not erroneous this
        // tolerance must be less than HALF_MAX_SEQ_NUMS.


        internal const int OUT_OF_ORDER_TOLERANCE = HALF_MAX_SEQ_NUMS - 1;


        internal const byte JOIN_PACKET_INFO = (byte)((byte)PacketType.AddPeer | (byte)SendOptions.Reliable | (byte)HeaderPayloadSizeType.Byte);


    }


    public static class Falcon
    {
        public static event PeerAdded PeerAdded;
        public static event PeerDropped PeerDropped;
        public static event ApplicationEvent ApplicationEvent;


        internal static Socket Sender;


        private static Socket receiver;
        private static Dictionary<IPEndPoint, RemotePeer> peersByIp;    //} Collections hold refs to the same RemotePeers,
        private static Dictionary<int, RemotePeer> peersById;           //} just offer different ways to look them up
        private static object peersLockObject;                          // used to lock when using above peer collections
        private static EndPoint anyRemoteEndPoint;                      // end point to listen on
        private static EndPoint lastRemoteEndPoint;                     // end point data last received from
        private static byte[] receiveBuffer;
        private static byte[] sendBuffer;
        private static Thread listenThread;
        private static bool stop;
        private static string networkName;
        private static Timer ackCheckTimer;
        private static bool initialized;
        private static LogLevel logLvl;
        private static int peerIdCount;
        private static byte[] payloadSizeBytes;



        static Falcon()
        {
            // do nothing
        }


        public static void Init(int port, string netName, LogLevel logLevel = LogLevel.Warning)
        {
            Const.PORT = port;
            networkName = netName;
            logLvl = logLevel;


            peersByIp = new Dictionary<IPEndPoint, RemotePeer>();
            peersById = new Dictionary<int, RemotePeer>();


            Sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);


            anyRemoteEndPoint = new IPEndPoint(IPAddress.Any, Const.PORT);


            receiveBuffer = new byte[Const.MAX_DATAGRAM_SIZE];
            sendBuffer = new byte[Const.MAX_DATAGRAM_SIZE];


            lastRemoteEndPoint = new IPEndPoint(0, 0);


            listenThread = new Thread(Listen);
            listenThread.Name = "Falcon ears";
            listenThread.IsBackground = true;


            ackCheckTimer = new Timer(ACKCheckTick, null, Const.ACK_TICK_TIME, Const.ACK_TICK_TIME);


            peerIdCount = 0;


            peersLockObject = new object();


            payloadSizeBytes = new byte[2];


            initialized = true;


            if (logLevel != LogLevel.NoLogging)
            {
                Debug.AutoFlush = true;
                Debug.Indent();
                Log(LogLevel.Info, "Initialized");
            }
        }


        public static void Start()
        {
            if (!initialized)
                throw new InvalidOperationException("Not initalized - must have called Init() first.");


            stop = false;


            // Create a new socket to listen on when starting as only way to stop blocking 
            // ReceiveFrom call when stopping is closing the existing socket.


            receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true); // Guarantee the remote host endpoint is always returned: http://msdn.microsoft.com/en-us/library/system.net.sockets.socket.beginreceivefrom(v=vs.100).aspx
            receiver.Bind(anyRemoteEndPoint);


            listenThread.Start();


            Log(LogLevel.Info, String.Format("Started, listening on port: {0}", Const.PORT));
        }


        public static void Stop()
        {
            stop = true;


            try
            {
                receiver.Close();
            }
            catch { }


            try
            {
                listenThread.Join(Const.JOIN_LISTEN_THREAD_TIMEOUT);
            }
            catch
            {
                try
                {
                    listenThread.Abort();
                }
                catch { }
            }


            receiver = null;
            peersById.Clear();
            peersByIp.Clear();


            Log(LogLevel.Info, "Stopped");
        }


        /// <summary>
        /// Attempts to connect to the remote peer. If successful Falcon can send and receive from 
        /// this peer. This Method blocks until operation is complete.
        /// </summary>
        /// <param name="addr">IP address.</param>
        /// <param name="pass">Password remote peer requires, if any.</param>
        public static TryResult TryJoinPeer(string addr, string pass = null)
        {
            // TODO check state - notstarted, started, stopped


            IPAddress ip;
            if (!IPAddress.TryParse(addr, out ip))
            {
                return new TryResult(false, "Invalid IP address supplied.");
            }
            else
            {
                ManualResetEvent awaitCallback = new ManualResetEvent(false);
                IPEndPoint endPoint = new IPEndPoint(ip, Const.PORT);
                TryResult tr = null;
                BeginTryJoinPeer(endPoint, pass, new TryCallback(delegate(TryResult result)
                {
                    tr = result;
                    awaitCallback.Set();
                }));
                awaitCallback.WaitOne();
                return tr;
            }
        }


        /// <summary>
        /// Attempts to connect to the remote peer. If successful Falcon can send and receive from 
        /// this peer. This Method returns immediatly then calls the callback supplied when the 
        /// operation succedes or fails.
        /// </summary>
        /// <param name="addr">IP address.</param>
        /// <param name="callback">Callback to call when operation completes.</param>
        /// <param name="pass">Password remote peer requires, if any.</param>
        public static void BeginTryJoinPeer(string addr, TryCallback callback, string pass)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(addr, out ip))
            {
                callback(new TryResult(false, "Invalid IP address supplied."));
            }
            else
            {
                IPEndPoint endPoint = new IPEndPoint(ip, Const.PORT);
                BeginTryJoinPeer(endPoint, pass, callback);
            }
        }


        private static void BeginTryJoinPeer(IPEndPoint endPoint, string pass, TryCallback callback)
        {
            Buffer.SetByte(sendBuffer, 0, 0);
            Buffer.SetByte(sendBuffer, 1, Const.JOIN_PACKET_INFO);


            int size = 0;
            if (pass == null)
            {
                Buffer.SetByte(sendBuffer, 2, 0);
                size = Const.NORMAL_HEADER_SIZE;
            }
            else
            {
                int count = Encoding.UTF8.GetByteCount(pass);
                if (count > Byte.MaxValue)
                {
                    callback(new TryResult(false, "pass too long"));
                    return;
                }


                Buffer.SetByte(sendBuffer, 2, (byte)count);
                Buffer.BlockCopy(Encoding.UTF8.GetBytes(pass), 0, sendBuffer, Const.NORMAL_HEADER_SIZE, count);
                size = Const.NORMAL_HEADER_SIZE + count;
            }


            try
            {
                Sender.BeginSendTo(sendBuffer, 0, size, SocketFlags.None, endPoint, new AsyncCallback(delegate(IAsyncResult result)
                {
                    try
                    {
                        Sender.EndSendTo(result);
                        RemotePeer rp = AddPeer(endPoint);
                        callback(TryResult.SuccessResult);
                    }
                    catch (SocketException se)
                    {
                        callback(new TryResult(false, se.Message));
                    }
                }), null);
            }
            catch (SocketException se)
            {
                callback(new TryResult(false, se.Message));
            }
        }


        // external use only! ALWAYS uses PacketType: Application
        public static void BeginSendTo(int id, SendOptions opts, byte[] data)
        {
            RemotePeer rp;
            if (!peersById.TryGetValue(id, out rp))
            {
                Log(LogLevel.Error, "Attempt to SendTo unknown Peer ignored: " + id.ToString());
                return;
            }
            else
            {
                rp.BeginSend(opts, PacketType.Application, data);
            }
        }


        // external use only! ALWAYS uses PacketType: Application
        public static void BeginSendToAll(SendOptions opts, byte[] data)
        {
            lock (peersLockObject)
            {
                foreach (RemotePeer rp in peersByIp.Values)
                {
                    rp.BeginSend(opts, PacketType.Application, data);
                }
            }
        }


        // TODO PacketReader and Writer like XNA
        public static List<Packet> ReadAllPackets()
        {
            int count = 0;


            lock (peersByIp)
            {
                foreach (RemotePeer rp in peersByIp.Values)
                {
                    count += rp.PacketCount;
                }




                if (count == 0)
                {
                    return null;
                }
                else
                {
                    List<Packet> packets = new List<Packet>(count);
                    foreach (RemotePeer rp in peersByIp.Values)
                    {
                        packets.AddRange(rp.Read());
                    }
                    return packets;
                }
            }
        }


        private static void BeginSendTo(IPEndPoint endPoint, SendOptions opts, PacketType type, byte[] payload)
        {
            RemotePeer p;
            if (!peersByIp.TryGetValue(endPoint, out p))
            {
                Log(LogLevel.Error, "Attempt to SendTo unknown Peer ignored: " + endPoint.ToString());
                return;
            }
            else
            {
                p.BeginSend(opts, type, payload);
            }
        }


        internal static void EndSendToCallback(IAsyncResult result)
        {
            try
            {
                Sender.EndSendTo(result);
            }
            catch (SocketException se)
            {
                // TODO handel depeding on error code
                Falcon.Log(LogLevel.Error, "BeginSendTo() SocketException: " + se.Message);
            }
        }


        private static void ACKCheckTick(object dummy)
        {
            if (!stop)
            {
                lock (peersByIp) // this callback is run on some arbitary thread in the ThreadPool
                {
                    foreach (RemotePeer rp in peersByIp.Values)
                    {
                        rp.ACKTick();
                    }
                }
            }
        }


        private static void Listen()
        {
            while (true)
            {
                if (stop)
                    return;


                int sizeReceived = 0;


                try
                {
                    //-------------------------------------------------------------------------
                    sizeReceived = receiver.ReceiveFrom(receiveBuffer, ref lastRemoteEndPoint);
                    //-------------------------------------------------------------------------


                    if (sizeReceived == 0)
                    {
                        // EOF - socket must be closing
                        return;
                    }


                    IPEndPoint ip = (IPEndPoint)lastRemoteEndPoint;


                    RemotePeer rp;
                    if (!peersByIp.TryGetValue(ip, out rp))
                    {
                        // could be the peer has not been added yet and is requesting to be added
                        if (sizeReceived >= Const.NORMAL_HEADER_SIZE && (PacketType)(receiveBuffer[1] & Const.PACKET_TYPE_MASK) == PacketType.AddPeer)
                        {
                            string pass = null;
                            if ((HeaderPayloadSizeType)(receiveBuffer[1] & Const.PAYLOAD_SIZE_TYPE_MASK) == HeaderPayloadSizeType.Byte)
                            {
                                byte payloadSize = receiveBuffer[2];
                                if (payloadSize > 0)
                                    pass = BitConverter.ToString(receiveBuffer, Const.NORMAL_HEADER_SIZE, payloadSize);
                            }
                            else
                            {
                                ushort payloadSize = BitConverter.ToUInt16(receiveBuffer, 2);
                                if (payloadSize > 0)
                                    pass = BitConverter.ToString(receiveBuffer, Const.LARGE_HEADER_SIZE);
                            }


                            // TODO send ACK?


                            if (!TryAddPeer(ip, pass, out rp))
                            {
                                // do nothing - the lack of reply means failed
                            }
                            else
                            {
                                // send accept
                                rp.BeginSend(SendOptions.Reliable, PacketType.Accept, null);
                            }
                        }
                        else
                        {
                            Log(LogLevel.Warning, "Datagram dropped - unknown peer: " + ip.Address.ToString());
                        }
                    }
                    else
                    {
                        rp.AddReceivedDatagram(sizeReceived, receiveBuffer);
                    }
                }
                catch (SocketException se)
                {
                    // TODO handel exception based on error code.
                    Log(LogLevel.Error, "EndReceiveFrom() SocketException: " + se.Message);
                }
            }
        }


        // used when remote peer requests to be added
        private static bool TryAddPeer(IPEndPoint ip, string pass, out RemotePeer peer)
        {
            if (pass == networkName) // TODO something else?
            {
                peer = AddPeer(ip);
                return true;
            }

            peer = null;
            return false;
        }


        // used when sucessfully request self to be added
        internal static RemotePeer AddPeer(IPEndPoint ip)
        {
            lock (peersLockObject) // application can use the peer collections e.g. SendToAll()
            {
                peerIdCount++;
                int peerId = peerIdCount;
                RemotePeer rp = new RemotePeer(peerId, ip);
                peersById.Add(peerId, rp);
                peersByIp.Add(ip, rp);


                // raise peer added event 
                if (PeerAdded != null)
                    PeerAdded(rp.Id); // TODO should being invoke? dont want to hold up




                return rp;
            }
        }


        internal static void RemovePeer(IPEndPoint ip)
        {
            lock (peersLockObject) // application can use this collection e.g. SendToAll()
            {
                RemotePeer rp;
                if (!peersByIp.TryGetValue(ip, out rp))
                {
                    Log(LogLevel.Error, String.Format("Failed to remove peer: {0}, peer unknown.", ip));
                }
                else
                {
                    peersById.Remove(rp.Id);
                    peersByIp.Remove(ip);
                }
            }
        }


        internal static void Log(LogLevel lvl, string msg)
        {
            if (lvl >= logLvl)
            {
                Debug.WriteLine(String.Format("{0}\t{1}\t{2}", DateTime.Now.ToString(), lvl.ToString(), msg));
            }
        }
    }
}
