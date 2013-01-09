using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

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
    public delegate void LogCallback(string line);

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
    public enum PacketType : byte
    {
        ACK         = 0,
        AntiACK     = 1,
        AddPeer     = 2,
        DropPeer    = 3,
        AcceptJoin  = 4,
        Resynch     = 5,
        Ping        = 6,
        Application = 7,
    }

    static class Settings
    {
        internal static int PORT;                                                               // assigned on Init()

        internal const int NORMAL_HEADER_SIZE               = 3;                                // in bytes
        internal const int LARGE_HEADER_SIZE                = 4;                                // in bytes (used when payload size > Byte.MaxValue)
        internal const int JOIN_LISTEN_THREAD_TIMEOUT       = 500;                              // milliseconds
        internal const int MAX_DATAGRAM_SIZE                = 65507;                            // this is an IPv4 limit, v6 allows slightly more but we needn't
        internal const byte MAX_SEQ                         = Byte.MaxValue;
        internal const int MAX_SEQ_NUMS                     = MAX_SEQ + 1;                      // + 1 to include "0"
        internal const int HALF_MAX_SEQ_NUMS                = MAX_SEQ_NUMS / 2;
        internal const int ACK_TIMEOUT                      = 600;                              // milliseconds (should be multiple of ACK_TICK_TIME)
        internal const int ACK_TICK_TIME                    = 100;                              // milliseconds (note timer could tick just as packet sent so this also defines the error margin)
        internal const int ACK_TIMEOUT_TICKS                = ACK_TIMEOUT / ACK_TICK_TIME;      // timeout in ticks 
        internal const int ACK_RETRY_ATTEMPTS               = 3;                                // number of times to retry until assuming peer is dead (used by AwaitingAcceptDetail too)
        internal const byte PAYLOAD_SIZE_TYPE_MASK          = 192;                              // 1100 0000 AND'd with packet info byte returns PayloadSizeHeaderType
        internal const byte SEND_OPTS_MASK                  = 48;                               // 0011 0000 AND'd with packet info byte returns SendOptions
        internal const byte PACKET_TYPE_MASK                = 15;                               // 0000 1111 AND'd with packet info byte returns PacketType
        internal const int MAX_ACTUAL_SEQ                   = Int32.MaxValue - 1;               // Sequence number when reached to send out a re-synch request - upon ACK reset seq counters.

        // Packets received with calculated actual seq num outside max actual seq num received + 
        // or - this value are dropped. The smaller this value the more tolerant of out-of-order
        // we are...

        internal const int OUT_OF_ORDER_TOLERANCE = HALF_MAX_SEQ_NUMS / 2;

        internal const byte JOIN_PACKET_INFO    = (byte)((byte)PacketType.AddPeer | (byte)SendOptions.None | (byte)HeaderPayloadSizeType.Byte);
        internal const byte ACCEPT_PACKET_INFO  = (byte)((byte)PacketType.AcceptJoin | (byte)SendOptions.None | (byte)HeaderPayloadSizeType.Byte);
        internal const byte PING_PACKET_INFO    = (byte)((byte)PacketType.Ping | (byte)SendOptions.None | (byte)HeaderPayloadSizeType.Byte);

    }

    public static class Falcon
    {
        public static event PeerAdded PeerAdded;
        public static event PeerDropped PeerDropped;

        internal static Socket Sender;
        internal static List<RemotePeer> RemotePeersToDrop;              // only a single ACKCheckTick() access this 

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
        private static string networkPass;
        private static Timer ackCheckTimer;                             // also used for AwaitingAcceptDetail
        private static bool initialized;
        private static LogLevel logLvl;
        private static int peerIdCount;
        private static byte[] payloadSizeBytes;
        private static LogCallback logger;
        private static List<AwaitingAcceptDetail> awaitingAcceptDetails;
        private static List<AwaitingAcceptDetail> awaitingAcceptDetailsToRemove;

        static Falcon()
        {
            // do nothing
        }

        /// <summary>
        /// Initialize Falcon</summary>
        /// <param name="port">
        /// Port number to listen and send on.</param>
        /// <param name="netPass">
        /// Password remote peers must supply when requesting to join Falcon - neccessary to send
        /// and receive from peer.</param>
        /// <param name="logLevel">
        /// Level at which to log at - this level and more serious levels are logged.</param>
        public static void Init(int port, string netPass, LogCallback logCallback = null, LogLevel logLevel = LogLevel.Warning)
        {
            Settings.PORT = port;
            networkPass = netPass;
            logLvl = logLevel;

            peersByIp = new Dictionary<IPEndPoint, RemotePeer>();
            peersById = new Dictionary<int, RemotePeer>();

            Sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            RemotePeersToDrop = new List<RemotePeer>();

            anyRemoteEndPoint = new IPEndPoint(IPAddress.Any, Settings.PORT);

            receiveBuffer = new byte[Settings.MAX_DATAGRAM_SIZE];
            sendBuffer = new byte[Settings.MAX_DATAGRAM_SIZE];

            lastRemoteEndPoint = new IPEndPoint(0, 0);

            listenThread = new Thread(Listen);
            listenThread.Name = "Falcon ears";
            listenThread.IsBackground = true;

            peerIdCount = 0;

            peersLockObject = new object();

            awaitingAcceptDetails = new List<AwaitingAcceptDetail>();
            awaitingAcceptDetailsToRemove = new List<AwaitingAcceptDetail>();

            payloadSizeBytes = new byte[2];

            initialized = true;

            if (logLevel != LogLevel.NoLogging)
            {
                if (logCallback != null)
                {
                    logger = logCallback;
                }
                else
                {
                    Debug.AutoFlush = true;
                    Debug.Indent();
                }
                Log(LogLevel.Info, "Initialized");
            }
        }

        /// <summary>
        /// Start her up!</summary>
        public static void Start()
        {
            if (!initialized)
                throw new InvalidOperationException("Not initalized - must have called Init() first.");

            stop = false;

            // Create a new socket to listen on when starting as only way to stop blocking 
            // ReceiveFrom call when stopping is closing the existing socket.

            receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true); // Guarantee the remote host endpoint is always returned: http://msdn.microsoft.com/en-us/library/system.net.sockets.socket.beginreceivefrom(v=vs.100).aspx
            receiver.Bind(anyRemoteEndPoint); // TODO catch possible exceptions e.g. port not avaliable, what to do when caught?

            ackCheckTimer = new Timer(ACKCheckTick, null, Settings.ACK_TICK_TIME, Settings.ACK_TICK_TIME);

            listenThread.Start();

            Log(LogLevel.Info, String.Format("Started, listening on port: {0}", Settings.PORT));
        }

        /// <summary>
        /// Stops Falcon, will stop listening and be unable send. Connected remote peers will be 
        /// lost. To start Falcon again call Start() again.</summary>
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
                listenThread.Join(Settings.JOIN_LISTEN_THREAD_TIMEOUT);
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
            ackCheckTimer.Dispose();
            // TODO should we clear events?

            Log(LogLevel.Info, "Stopped");
        }

        /// <summary>
        /// Attempts to connect to the remote peer. If successful Falcon can send and receive from 
        /// this peer. This Method blocks until operation is complete.</summary>
        /// <param name="addr">
        /// IP address of remote peer.</param>
        /// <param name="pass">
        /// Password remote peer requires, if any.</param>
        /// <returns>
        /// TryResult either success, or failed with message or exception containing fail reason.</returns>
        /// <remarks>
        /// This Method uses BeginTryJoinPeer() and blocks until callback is called.</remarks>
        public static TryResult TryJoinPeer(string addr, string pass = null)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(addr, out ip))
            {
                return new TryResult(false, "Invalid IP address supplied.");
            }
            else
            {
                ManualResetEvent awaitCallback = new ManualResetEvent(false);
                IPEndPoint endPoint = new IPEndPoint(ip, Settings.PORT);
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
        /// this peer and TryResult.Tag will be set to the Id for this remote peer which can also 
        /// be obtained in the PeerAdded event. This Method returns immediatly then calls the callback 
        /// supplied when the operation completes.</summary>
        /// <param name="addr">
        /// IP address of remote peer.</param>
        /// <param name="callback">
        /// Callback to call when operation completes.</param>
        /// <param name="pass">
        /// Password remote peer requires, if any.</param>
        public static void BeginTryJoinPeer(string addr, TryCallback callback, string pass = null)
        {
            if (stop)
                callback(new TryResult(false, "Falcon is not started!"));

            IPAddress ip;
            if (!IPAddress.TryParse(addr, out ip))
            {
                callback(new TryResult(false, "Invalid IP address supplied."));
            }
            else
            {
                IPEndPoint endPoint = new IPEndPoint(ip, Settings.PORT);
                BeginTryJoinPeer(endPoint, pass, callback);
            }
        }

        // called on first attempt
        private static void BeginTryJoinPeer(IPEndPoint endPoint, string pass, TryCallback callback)
        {
            AwaitingAcceptDetail detail = new AwaitingAcceptDetail(endPoint, callback, pass);
            AddWaitingAcceptDetail(detail);
            BeginTryJoinPeer(detail);
        }

        // Called directly when re-sending an AwaitingAcceptDetail already instantiated and in list
        // or via API on intial attempt after creating AwaitingAcceptDetail.
        private static void BeginTryJoinPeer(AwaitingAcceptDetail detail)
        {
            lock (sendBuffer) // we could be being called from Timer or application
            {
                Buffer.SetByte(sendBuffer, 0, 0);
                Buffer.SetByte(sendBuffer, 1, Settings.JOIN_PACKET_INFO);

                int size = 0;
                if (detail.Pass == null)
                {
                    Buffer.SetByte(sendBuffer, 2, 0);
                    size = Settings.NORMAL_HEADER_SIZE;
                }
                else
                {
                    int count = Encoding.UTF8.GetByteCount(detail.Pass);
                    if (count > Byte.MaxValue)
                    {
                        RemoveWaitingAcceptDetail(detail);
                        detail.Callback(new TryResult(false, "pass too long"));
                        return;
                    }

                    Buffer.SetByte(sendBuffer, 2, (byte)count);
                    Buffer.BlockCopy(Encoding.UTF8.GetBytes(detail.Pass), 0, sendBuffer, Settings.NORMAL_HEADER_SIZE, count);
                    size = Settings.NORMAL_HEADER_SIZE + count;
                }

                try
                {
                    Sender.BeginSendTo(sendBuffer, 0, size, SocketFlags.None, detail.EndPoint, new AsyncCallback(delegate(IAsyncResult result)
                        {
                            try
                            {
                                Sender.EndSendTo(result);
                            }
                            catch (SocketException se)
                            {
                                RemoveWaitingAcceptDetail(detail);
                                detail.Callback(new TryResult(se));
                            }
                        }), null);
                }
                catch (SocketException se)
                {
                    RemoveWaitingAcceptDetail(detail);
                    detail.Callback(new TryResult(se));
                }
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
        
        public static List<Packet> ReadReceivedPackets()
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
            RemotePeer rp;
            if (!peersByIp.TryGetValue(endPoint, out rp))
            {
                Log(LogLevel.Error, String.Format("Attempt to SendTo unknown Peer ignored: {0}.", endPoint.ToString()));
                return;
            }
            else
            {
                rp.BeginSend(opts, type, payload);
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
            // NOTE: This callback is run on some arbitary thread in the ThreadPool.

            if (!stop)
            {
                lock (peersLockObject) 
                {
                    foreach (RemotePeer rp in peersByIp.Values)
                    {
                        rp.ACKTick();
                    }

                    if (RemotePeersToDrop.Count > 0)
                    {
                        foreach (RemotePeer rp in RemotePeersToDrop)
                        {
                            RemovePeer(rp.Id);
                        }
                        RemotePeersToDrop.Clear();
                    }
                }

                lock (awaitingAcceptDetails)
                {
                    foreach (AwaitingAcceptDetail aad in awaitingAcceptDetails)
                    {
                        aad.Ticks++;
                        if (aad.Ticks == Settings.ACK_TIMEOUT_TICKS)
                        {
                            aad.RetryCount++;
                            if (aad.RetryCount > Settings.ACK_RETRY_ATTEMPTS)
                            {
                                // give up, peer has not been added yet so no need to drop
                                awaitingAcceptDetailsToRemove.Add(aad);
                                aad.Callback(new TryResult(false, String.Format("Remote peer never responded after {0} join attempts.", Settings.ACK_RETRY_ATTEMPTS)));
                            }
                        }
                    }

                    if (awaitingAcceptDetailsToRemove.Count > 0)
                    {
                        foreach (AwaitingAcceptDetail aad in awaitingAcceptDetailsToRemove)
                        {
                            awaitingAcceptDetails.Remove(aad);
                        }
                        awaitingAcceptDetailsToRemove.Clear();
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
                        // Could be the peer has not been added yet and is requesting to be added. 
                        // Or it could be we are asking to be added and peer is accepting!

                        if (sizeReceived >= Settings.NORMAL_HEADER_SIZE && receiveBuffer[1] == Settings.JOIN_PACKET_INFO)
                        {
                            TryAddPeer(ip, receiveBuffer, sizeReceived, out rp);
                        }
                        else if (sizeReceived >= Settings.NORMAL_HEADER_SIZE && receiveBuffer[1] == Settings.ACCEPT_PACKET_INFO)
                        {
                            AwaitingAcceptDetail detail;
                            if (!TryGetAndRemoveWaitingAcceptDetail(ip, out detail))
                            {
                                // It is theoretically possible the Accept packet duplicated, but 
                                // probably more likely was unsolicited.

                                Log(LogLevel.Warning, String.Format("Dropped Accept Packet from unknown peer: {0}.", ip));
                            }
                            else
                            {
                                rp = AddPeer(ip);
                                TryResult tr = new TryResult(true, null, null, rp.Id);
                                detail.Callback(tr);
                            }
                        }
                        else
                        {
                            Log(LogLevel.Warning, String.Format("Datagram dropped - unknown peer: {0}.", ip.Address.ToString()));
                        }
                    }
                    else
                    {
                        rp.AddReceivedDatagram(sizeReceived, receiveBuffer);
                    }
                }
                catch (SocketException se)
                {
                    // TODO http://msdn.microsoft.com/en-us/library/ms740668.aspx
                    Log(LogLevel.Error, String.Format("EndReceiveFrom() SocketException: {0}.", se.Message));
                }
            }
        }

        private static bool TryAddPeer(IPEndPoint ip, byte[] buffer, int sizeOfPacket, out RemotePeer peer)
        {
            // ASSUMPTION: Caller has checked peer is indeed requesting to be added!

            string pass = null;
            byte payloadSize = receiveBuffer[2];
            if (payloadSize > 0)
                pass = BitConverter.ToString(receiveBuffer, Settings.NORMAL_HEADER_SIZE, payloadSize);

            if (pass != networkPass) // TODO something else?
            {
                peer = null;
                Log(LogLevel.Info, String.Format("Join request from: {0} dropped, bad pass.", ip));
                return false; // Send nothing - the lack of reply means failed.
            }
            else if (peersByIp.ContainsKey(ip))
            {
                peer = null;
                Log(LogLevel.Warning, String.Format("Cannot add peer again: {0}, peer is already added!", ip));
                return false; // Send nothing - the lack of reply means failed.
            }
            else
            {
                peer = AddPeer(ip);
                peer.BeginSend(SendOptions.Reliable, PacketType.AcceptJoin, null);
                return true;
            }
        }

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
                    PeerAdded(rp.Id); // TODO should begin invoke? dont want to hold up

                return rp;
            }
        }

        private static void RemovePeer(IPEndPoint ip)
        {
            RemotePeer rp;
            lock (peersLockObject) // application can use this collection e.g. SendToAll()
            {
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

            // raise the PeerDropped event notifying any listners
            if (rp != null)
                if (PeerDropped != null)
                    PeerDropped(rp.Id);
        }
        
        public static void RemovePeer(int id)
        {
            RemotePeer rp;
            lock (peersLockObject) // application can use this collection e.g. SendToAll()
            {
                if (!peersById.TryGetValue(id, out rp))
                {
                    Log(LogLevel.Error, String.Format("Failed to remove peer with id: {0}, peer unknown.", id));
                }
                else
                {
                    peersById.Remove(rp.Id);
                    peersByIp.Remove(rp.EndPoint);
                }
            }

            // raise the PeerDropped event notifying any listners
            if(rp != null)
                if (PeerDropped != null)
                    PeerDropped(id);
        }

        private static void AddWaitingAcceptDetail(AwaitingAcceptDetail detail)
        {
            lock (awaitingAcceptDetails)
            {
                awaitingAcceptDetails.Add(detail);
            }
        }

        private static bool TryGetAndRemoveWaitingAcceptDetail(IPEndPoint ip, out AwaitingAcceptDetail detail)
        {
            bool found = false;
            lock (awaitingAcceptDetails)
            {
                detail = awaitingAcceptDetails.Find(aad => aad.EndPoint == ip);
                if (detail != null)
                {
                    found = true;
                    awaitingAcceptDetails.Remove(detail);
                }
            }
            return found;
        }

        private static void RemoveWaitingAcceptDetail(AwaitingAcceptDetail detail)
        {
            lock (awaitingAcceptDetails)
            {
                awaitingAcceptDetails.Remove(detail);
            }
        }
        
        internal static void Log(LogLevel lvl, string msg)
        {
            if (lvl >= logLvl)
            {
                string line = String.Format("{0}\t{1}\t{2}", DateTime.Now.ToString(), lvl.ToString(), msg);
                if (logger != null)
                {
                    logger(line);
                }
                else
                {
                    Debug.WriteLine(line);
                }
            }
        }
    }
}
