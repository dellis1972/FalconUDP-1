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

    public static partial class Falcon
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
                    int count = Settings.TEXT_ENCODING.GetByteCount(detail.Pass);
                    if (count > Byte.MaxValue)
                    {
                        RemoveWaitingAcceptDetail(detail);
                        detail.Callback(new TryResult(false, "pass too long"));
                        return;
                    }

                    Buffer.SetByte(sendBuffer, 2, (byte)count);
                    Buffer.BlockCopy(Settings.TEXT_ENCODING.GetBytes(detail.Pass), 0, sendBuffer, Settings.NORMAL_HEADER_SIZE, count);
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
                                // We, quite likely, are on a different thread to the caller, not 
                                // of this anonoymous method - but of BeginTryJoin() call, and this 
                                // caller, quite likely, has locked awaitingAcceptDetails e.g. in 
                                // ACKCheckTick() when re-sending, so don't try re-acquire lock 
                                // here.

                                awaitingAcceptDetailsToRemove.Add(detail); 
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
                            aad.Ticks = 0;
                            aad.RetryCount++;
                            if (aad.RetryCount == 1) // TODO review
                            {
                                // give up, peer has not been added yet so no need to drop
                                awaitingAcceptDetailsToRemove.Add(aad);
                                aad.Callback(new TryResult(false, String.Format("Remote peer never responded join request.")));
                            }
                            else
                            {
                                // try again
                                BeginTryJoinPeer(aad);
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

        private static bool TryAddPeer(IPEndPoint ip, byte[] buffer, int sizeOfPacket, out RemotePeer peer)
        {
            // ASSUMPTION: Caller has checked peer is indeed requesting to be added!

            string pass = null;
            byte payloadSize = receiveBuffer[2];
            if (payloadSize > 0)
                pass = Settings.TEXT_ENCODING.GetString(receiveBuffer, Settings.NORMAL_HEADER_SIZE, payloadSize);

            if (pass != networkPass) // TODO something else?
            {
                peer = null;
                Log(LogLevel.Info, String.Format("Join request from: {0} dropped, bad pass.", ip));
                return false; // TODO should we send something?
            }
            else if (peersByIp.ContainsKey(ip))
            {
                peer = null;
                Log(LogLevel.Warning, String.Format("Cannot add peer again: {0}, peer is already added!", ip));
                return false; // TODO should we send something?
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
