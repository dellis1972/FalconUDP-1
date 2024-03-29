﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
#if NETFX_CORE
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.System.Threading;
using Windows.Storage.Streams;
#else
using System.Net.Sockets;
using System.Threading;
#endif

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
    public partial class FalconPeer
    {
        // TODO: events the proper way http://msdn.microsoft.com/en-us/library/w369ty8x.aspx
        public event PeerAdded PeerAdded;
        public event PeerDropped PeerDropped;
        public event PongReceived PongReceived;

#if NETFX_CORE
        internal DatagramSocket Sock;
        private Dictionary<IPv4EndPoint, RemotePeer> peersByIp; // same RemotePeers as peersById
        private ThreadPoolTimer ackCheckTimer;
        private string localPortAsString;
        private Object processingMessageLock;
#else   
        internal Socket Sock;
                                                        
        private Dictionary<IPEndPoint, RemotePeer> peersByIp;   // same RemotePeers as peersById
        private EndPoint anyAddrEndPoint;                       // end point to send/receive on (combined with port to create IPEndPoint)
        private EndPoint lastRemoteEndPoint;                    // end point data last received from
        private Thread listenThread;
        private Timer ackCheckTimer;                            // also used for AwaitingAcceptDetail
        private byte[] receiveBuffer;
#endif
        internal List<RemotePeer> RemotePeersToDrop;            // only ACKCheckTick() uses this 
        private int localPort;
        private Dictionary<int, RemotePeer> peersById;          // same RemotePeers as peersByIp
        private object peersLockObject;                         // used to lock when using above peer collections
        private bool stop;
        private string networkPass;
        private LogLevel logLvl; 
        private int peerIdCount;
        private byte[] payloadSizeBytes;                        // buffer to store payload size in large headers
        private LogCallback logger;
        private List<AwaitingAcceptDetail> awaitingAcceptDetails;
        private List<AwaitingAcceptDetail> awaitingAcceptDetailsToRemove;

        /// <summary>
        /// Creates a local FalconPeer</summary>
        /// <param name="port">
        /// Port number to listen and send on.</param>
        /// <param name="netPass">
        /// Password remote peers must supply when requesting to join Falcon - neccessary to send
        /// and receive from peer.</param>
        /// <param name="logCallback">
        /// Callback to invoke supplying log message, if null log messages are written to Debug.</param>
        /// <param name="logLevel">
        /// Level at which to log at - this level and more serious levels are logged.</param>
        public FalconPeer(int port, string netPass, LogCallback logCallback = null, LogLevel logLevel = LogLevel.Warning)
        {
            this.localPort = port;
            this.networkPass = netPass;
            this.logLvl = logLevel;

#if NETFX_CORE
            this.localPortAsString = this.localPort.ToString();
            this.peersByIp = new Dictionary<IPv4EndPoint, RemotePeer>();
            this.processingMessageLock = new Object();
#else
            this.peersByIp = new Dictionary<IPEndPoint, RemotePeer>();

            this.anyAddrEndPoint = new IPEndPoint(IPAddress.Any, this.localPort);

            this.lastRemoteEndPoint = new IPEndPoint(0, 0);

            this.listenThread = new Thread(Listen);
            this.listenThread.Name = "Falcon ears";
            this.listenThread.IsBackground = true;

            this.receiveBuffer = new byte[Const.MAX_DATAGRAM_SIZE];
#endif
            this.peersById = new Dictionary<int, RemotePeer>();

            this.RemotePeersToDrop = new List<RemotePeer>();
            
            this.peerIdCount = 0;

            this.peersLockObject = new object();

            this.awaitingAcceptDetails = new List<AwaitingAcceptDetail>();
            this.awaitingAcceptDetailsToRemove = new List<AwaitingAcceptDetail>();

            this.payloadSizeBytes = new byte[2];

            if (logLevel != LogLevel.NoLogging)
            {
                if (logCallback != null)
                {
                    logger = logCallback;
                }
                else
                {
#if !NETFX_CORE
                    Debug.AutoFlush = true;
#endif
                }
                Log(LogLevel.Info, "Initialized");
            }
        }

#if NETFX_CORE

        /// <summary>
        /// Start her up!</summary>
        public async Task<TryResult> TryStartAsync()
        {
            stop = false;
            
            Sock = new DatagramSocket();
            Sock.MessageReceived += MessageReceived;

            try
            {
                await Sock.BindEndpointAsync(null, localPort.ToString());
            }
            catch (Exception ex)
            {
                // This feels like a pretty sloppy way of doing things, we arn't even told what 
                // exceptions could be thrown! (We know at least the address could be in use).
                // This is what the offical sample does: 
                // http://code.msdn.microsoft.com/windowsapps/DatagramSocket-sample-76a7d82b/sourcecode?fileId=57971&pathId=589460989

                switch(SocketError.GetStatus(ex.HResult))
                {
                    case SocketErrorStatus.AddressAlreadyInUse:
                    default:
                        {
                            return new TryResult(ex);
                        }
                }
            }
            
            ackCheckTimer = ThreadPoolTimer.CreatePeriodicTimer(ACKCheckTick, new TimeSpan(0, 0, 0, 0, Settings.ACKTickTime));

            Log(LogLevel.Info, String.Format("Started, listening on port: {0}", this.localPort));

            return TryResult.SuccessResult;
        }

        /// <summary>
        /// Stops Falcon, will stop listening and be unable send. Connected remote peers will be 
        /// lost. To start Falcon again call Start() again.</summary>
        public void Stop()
        {
            stop = true;

            try
            {
                Sock.Dispose(); // TODO not sure if could throw exception
            }
            catch { }
            
            Sock = null;
            peersById.Clear();
            peersByIp.Clear();
            ackCheckTimer.Cancel();
            // TODO should we clear events?

            Log(LogLevel.Info, "Stopped");
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
        public void BeginJoinPeer(string addr, int port, TryCallback callback, string pass = null)
        {
            if (stop)
                callback(new TryResult(false, "Falcon is not started!"));

            IPv4EndPoint fep = new IPv4EndPoint(addr, port.ToString());
            BeginJoinPeer(fep, callback, pass);
        }

        // called on first attempt
        private void BeginJoinPeer(IPv4EndPoint endPoint, TryCallback callback, string pass)
        {
            AwaitingAcceptDetail detail = new AwaitingAcceptDetail(endPoint, callback, pass);
            AddWaitingAcceptDetail(detail);
            JoinPeerAsync(detail);
        }

        // Called directly when re-sending an AwaitingAcceptDetail already instantiated and in list
        // or via API on intial attempt after creating AwaitingAcceptDetail.
        private async void JoinPeerAsync(AwaitingAcceptDetail detail)
        {
            if (detail.JoinPacket == null)
            {
                if (detail.Pass == null)
                {
                    detail.JoinPacket = new byte[Const.NORMAL_HEADER_SIZE];
                    detail.JoinPacket[2] = 0;
                }
                else
                {
                    int count = Settings.TextEncoding.GetByteCount(detail.Pass);
                    if (count > Byte.MaxValue)
                    {
                        RemoveWaitingAcceptDetail(detail);
                        detail.Callback(new TryResult(false, "pass too long"));
                        return;
                    }

                    detail.JoinPacket = new byte[count + Const.NORMAL_HEADER_SIZE];
                    detail.JoinPacket[2] = (byte)count;
                    System.Buffer.BlockCopy(Settings.TextEncoding.GetBytes(detail.Pass), 0, detail.JoinPacket, Const.NORMAL_HEADER_SIZE, count);
                }
                detail.JoinPacket[1] = Const.JOIN_PACKET_INFO;
            }

            try
            {    
                // TODO use same output stream?
                IOutputStream outStream = await Sock.GetOutputStreamAsync(detail.EndPoint.Address, detail.EndPoint.Port);
                using (DataWriter dw = new DataWriter(outStream))
                {
                    dw.WriteBytes(detail.JoinPacket);
                    await dw.StoreAsync();
                }
            }
            catch (Exception ex)
            {
                RemoveWaitingAcceptDetail(detail);
                detail.Callback(new TryResult(ex)); 
            }
        }
#else
        /// <summary>
        /// Start her up!</summary>
        public TryResult TryStart()
        {
            stop = false;

            // Create a new socket when starting as only way to stop blocking ReceiveFrom call 
            // when stopping is closing the existing socket.

            Sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            try
            {
                Sock.Bind(anyAddrEndPoint);
            }
            catch (SocketException se)
            {
                // e.g. address already in use
                return new TryResult(se);
            }

            ackCheckTimer = new Timer(ACKCheckTick, null, Settings.ACKTickTime, Settings.ACKTickTime);

            try
            {
                listenThread.Start();
            }
            catch (ThreadStateException tse)
            {
                // e.g. user application called start when already started!
                return new TryResult(tse);
            }

            Log(LogLevel.Info, String.Format("Started, listening on port: {0}", this.localPort));

            return TryResult.SuccessResult;
        }

        /// <summary>
        /// Stops Falcon, will stop listening and be unable send. Connected remote peers will be 
        /// lost. To start Falcon again call Start() again.</summary>
        public void Stop()
        {
            stop = true;

            try
            {
                Sock.Close();
            }
            catch { }

            try
            {
                listenThread.Join(Settings.JoinListenThreadTimeout);
            }
            catch
            {
                try
                {
                    listenThread.Abort();
                }
                catch { }
            }

            Sock = null;
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
        /// TryResult either success, or failed with message, and maybe exception, containing fail reason.</returns>
        /// <remarks>
        /// This overload uses the same port number as this FalconPeer.</remarks>
        public TryResult TryJoinPeer(string addr, string pass = null)
        {
            return TryJoinPeer(addr, this.localPort, pass);
        }

        /// <summary>
        /// Attempts to connect to the remote peer. If successful Falcon can send and receive from 
        /// this peer. This Method blocks until operation is complete.</summary>
        /// <param name="addr">
        /// IP address of remote peer.</param>
        /// <param name="port">
        /// Port remote peer is listening on.</param>
        /// <param name="pass">
        /// Password remote peer requires, if any.</param>
        /// <returns>
        /// TryResult either success, or failed with message or exception containing fail reason.</returns>
        /// <remarks>
        /// This Method uses BeginTryJoinPeer() and blocks until callback is called.</remarks>
        public TryResult TryJoinPeer(string addr, int port, string pass = null)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(addr, out ip))
            {
                return new TryResult(false, "Invalid IP address supplied.");
            }
            else
            {
                ManualResetEvent awaitCallback = new ManualResetEvent(false);
                IPEndPoint endPoint = new IPEndPoint(ip, port);
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
        public void BeginTryJoinPeer(string addr, int port, TryCallback callback, string pass = null)
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
                IPEndPoint endPoint = new IPEndPoint(ip, port);
                BeginTryJoinPeer(endPoint, pass, callback);
            }
        }

        // called on first attempt
        private void BeginTryJoinPeer(IPEndPoint endPoint, string pass, TryCallback callback)
        {
            AwaitingAcceptDetail detail = new AwaitingAcceptDetail(endPoint, callback, pass);
            AddWaitingAcceptDetail(detail);
            BeginTryJoinPeer(detail);
        }

        // Called directly when re-sending an AwaitingAcceptDetail already instantiated and in list
        // or via API on intial attempt after creating AwaitingAcceptDetail.
        private void BeginTryJoinPeer(AwaitingAcceptDetail detail)
        {
            if (detail.JoinPacket == null)
            {
                if (detail.Pass == null)
                {
                    detail.JoinPacket = new byte[Const.NORMAL_HEADER_SIZE];
                    detail.JoinPacket[2] = 0;
                }
                else
                {
                    int count = Settings.TextEncoding.GetByteCount(detail.Pass);
                    if (count > Byte.MaxValue)
                    {
                        RemoveWaitingAcceptDetail(detail);
                        detail.Callback(new TryResult(false, "pass too long"));
                        return;
                    }

                    detail.JoinPacket = new byte[count + Const.NORMAL_HEADER_SIZE];
                    detail.JoinPacket[2] = (byte)count;
                    System.Buffer.BlockCopy(Settings.TextEncoding.GetBytes(detail.Pass), 0, detail.JoinPacket, Const.NORMAL_HEADER_SIZE, count);
                }
                detail.JoinPacket[1] = Const.JOIN_PACKET_INFO;
            }

            try
            {
                Sock.BeginSendTo(detail.JoinPacket, 0, detail.JoinPacket.Length, SocketFlags.None, detail.EndPoint, new AsyncCallback(delegate(IAsyncResult result)
                    {
                        // NOTE: lock on sendBuffer is lost if completed asynchoronously

                        try
                        {
                            Sock.EndSendTo(result);
                        }
                        catch (SocketException se)
                        {
                            // We, quite likely, are on a different thread to the caller of 
                            // BeginTryJoin() call in this anonymous method, and the caller, 
                            // quite likely, has locked awaitingAcceptDetails e.g. in 
                            // ACKCheckTick() when re-sending, so don't try re-acquire lock 
                            // here - that would be a dead lock!

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
#endif

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="id"></param>
        /// <param name="opts"></param>
        /// <param name="data"></param>
        public void BeginSendTo(int id, SendOptions opts, byte[] data)
        {
            RemotePeer rp;
            if (!peersById.TryGetValue(id, out rp))
            {
                Log(LogLevel.Error, "Attempt to SendTo unknown Peer ignored: " + id.ToString());
                return;
            }
            else
            {
                rp.BeginSend(opts, PacketType.Application, data, null);
            }
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="opts"></param>
        /// <param name="data"></param>
        public void BeginSendToAll(SendOptions opts, byte[] data)
        {
            lock (peersLockObject)
            {
                foreach (RemotePeer rp in peersByIp.Values)
                {
                    rp.BeginSend(opts, PacketType.Application, data, null);
                }
            }
        }
        
        /// <summary>
        /// TODO
        /// </summary>
        /// <returns></returns>
        public List<Packet> ReadReceivedPackets()
        {
            int count = 0;

            lock (peersLockObject)
            {
                foreach (RemotePeer rp in peersByIp.Values)
                {
                    count += rp.UnreadPacketCount;
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

        public bool PingPeer(int id)
        {
            lock(peersLockObject)
            {
                RemotePeer rp;
                if (!peersById.TryGetValue(id, out rp))
                {
                    return false;
                }
                else
                {
                    rp.Ping();
                    return true;
                }
            }
        }

        internal void RaisePongReceived(RemotePeer rp)
        {
            if(PongReceived != null)
                PongReceived(rp.Id);
        }

#if NETFX_CORE
        private void BeginSendTo(IPv4EndPoint endPoint, SendOptions opts, PacketType type, byte[] payload)
#else
        private void BeginSendTo(IPEndPoint endPoint, SendOptions opts, PacketType type, byte[] payload)
#endif
        
        {
            RemotePeer rp;
            if (!peersByIp.TryGetValue(endPoint, out rp))
            {
                Log(LogLevel.Error, String.Format("Attempt to SendTo unknown Peer ignored: {0}.", endPoint.ToString()));
                return;
            }
            else
            {
                rp.BeginSend(opts, type, payload, null);
            }
        }
        
        private void ACKCheckTick(object dummy)
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
                        if (aad.Ticks == Settings.ACKTimeoutTicks)
                        {
                            aad.Ticks = 0;
                            aad.RetryCount++;
                            if (aad.RetryCount == Settings.ACKRetryAttempts)
                            {
                                // give up, peer has not been added yet so no need to drop
                                awaitingAcceptDetailsToRemove.Add(aad);
                                aad.Callback(new TryResult(false, String.Format("Remote peer never responded to join request.")));
                            }
                            else
                            {
                                // try again
#if NETFX_CORE
                                JoinPeerAsync(aad);
#else
                                BeginTryJoinPeer(aad);
#endif
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
#if NETFX_CORE
        internal async Task<RemotePeer> TryAddPeerAsync(IPv4EndPoint ep)
#else
        internal RemotePeer AddPeer(IPEndPoint ep)
#endif
        {
                RemotePeer rp = new RemotePeer(this, ep);
#if NETFX_CORE
                TryResult tr = await rp.InitAsync();
                if (!tr.Success)
                {
                    Log(LogLevel.Error, "Failed to add remote peer: " + tr.NonSuccessMessage);
                    return null;
                }
#endif
                lock (peersLockObject) // application can use the peer collections e.g. SendToAll()
                {
                    peerIdCount++;
                    rp.Id = peerIdCount;
                    peersById.Add(peerIdCount, rp);
                    peersByIp.Add(ep, rp);

                    // raise peer added event 
                    if (PeerAdded != null)
                        PeerAdded(rp.Id); // TODO should begin invoke? dont want to hold up

                    return rp;
                }
        }

#if NETFX_CORE
        private void TryRemovePeer(IPv4EndPoint ep)
#else
        private void TryRemovePeer(IPEndPoint ep)
#endif
        {
            RemotePeer rp;
            lock (peersLockObject) // application can use this collection e.g. SendToAll()
            {
                if (!peersByIp.TryGetValue(ep, out rp))
                {
                    Log(LogLevel.Error, String.Format("Failed to remove peer: {0}, peer unknown.", ep));
                }
                else
                {
                    peersById.Remove(rp.Id);
                    peersByIp.Remove(ep);
                }
            }

            // raise the PeerDropped event notifying any listners
            if (rp != null)
                if (PeerDropped != null)
                    PeerDropped(rp.Id);
        }
        
        public void RemovePeer(int id)
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

        private void AddWaitingAcceptDetail(AwaitingAcceptDetail detail)
        {
            lock (awaitingAcceptDetails)
            {
                awaitingAcceptDetails.Add(detail);
            }
        }

#if NETFX_CORE
        private bool TryGetAndRemoveWaitingAcceptDetail(IPv4EndPoint ep, out AwaitingAcceptDetail detail)
#else
        private bool TryGetAndRemoveWaitingAcceptDetail(IPEndPoint ep, out AwaitingAcceptDetail detail)
#endif
        {
            bool found = false;
            lock (awaitingAcceptDetails)
            {
#if NETFX_CORE
                detail = awaitingAcceptDetails.Find(aad => aad.EndPoint.Equals(ep));
#else
                detail = awaitingAcceptDetails.Find(aad => aad.EndPoint.Address.Equals(ep.Address) && aad.EndPoint.Port == ep.Port);
#endif
                if (detail != null)
                {
                    found = true;
                    awaitingAcceptDetails.Remove(detail);
                }
            }
            return found;
        }

        private void RemoveWaitingAcceptDetail(AwaitingAcceptDetail detail)
        {
            lock (awaitingAcceptDetails)
            {
                awaitingAcceptDetails.Remove(detail);
            }
        }
        
        internal void Log(LogLevel lvl, string msg)
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

