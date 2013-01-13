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
    public partial class FalconPeer
    {
        public event PeerAdded PeerAdded;
        public event PeerDropped PeerDropped;

        internal Socket Sock;
        internal List<RemotePeer> RemotePeersToDrop;            // only ACKCheckTick() uses this 

        private int port;
        private Dictionary<IPEndPoint, RemotePeer> peersByIp;   //} Collections hold refs to the same RemotePeers,
        private Dictionary<int, RemotePeer> peersById;          //} just offer different ways to look them up
        private object peersLockObject;                         // used to lock when using above peer collections
        private EndPoint anyRemoteEndPoint;                     // end point to send/receive on
        private EndPoint lastRemoteEndPoint;                    // end point data last received from
        private byte[] receiveBuffer;
        private byte[] sendBuffer;
        private Thread listenThread;
        private bool stop;
        private string networkPass;
        private Timer ackCheckTimer;                            // also used for AwaitingAcceptDetail
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
            this.port = port;
            this.networkPass = netPass;
            this.logLvl = logLevel;

            this.peersByIp = new Dictionary<IPEndPoint, RemotePeer>();
            this.peersById = new Dictionary<int, RemotePeer>();

            this.RemotePeersToDrop = new List<RemotePeer>();

            this.anyRemoteEndPoint = new IPEndPoint(IPAddress.Any, this.port);

            this.receiveBuffer = new byte[Const.MAX_DATAGRAM_SIZE];
            this.sendBuffer = new byte[Const.MAX_DATAGRAM_SIZE];

            this.lastRemoteEndPoint = new IPEndPoint(0, 0);

            this.listenThread = new Thread(Listen);
            this.listenThread.Name = "Falcon ears";
            this.listenThread.IsBackground = true;

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
                    Debug.AutoFlush = true;
                    Debug.Indent();
                }
                Log(LogLevel.Info, "Initialized");
            }
        }

        /// <summary>
        /// Start her up!</summary>
        public TryResult TryStart()
        {
            stop = false;

            // Create a new socket to listen on when starting as only way to stop blocking 
            // ReceiveFrom call when stopping is closing the existing socket.

            Sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            try
            {
                Sock.Bind(anyRemoteEndPoint);
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

            Log(LogLevel.Info, String.Format("Started, listening on port: {0}", this.port));

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
                receiver.Close();
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
        public TryResult TryJoinPeer(string addr, string pass = null)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(addr, out ip))
            {
                return new TryResult(false, "Invalid IP address supplied.");
            }
            else
            {
                ManualResetEvent awaitCallback = new ManualResetEvent(false);
                IPEndPoint endPoint = new IPEndPoint(ip, this.port);
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
        public void BeginTryJoinPeer(string addr, TryCallback callback, string pass = null)
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
                IPEndPoint endPoint = new IPEndPoint(ip, this.port);
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
            lock (sendBuffer) // we could be being called from Timer or application
            {
                Buffer.SetByte(sendBuffer, 0, 0);
                Buffer.SetByte(sendBuffer, 1, Const.JOIN_PACKET_INFO);

                int size = 0;
                if (detail.Pass == null)
                {
                    Buffer.SetByte(sendBuffer, 2, 0);
                    size = Const.NORMAL_HEADER_SIZE;
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

                    Buffer.SetByte(sendBuffer, 2, (byte)count);
                    Buffer.BlockCopy(Settings.TextEncoding.GetBytes(detail.Pass), 0, sendBuffer, Const.NORMAL_HEADER_SIZE, count);
                    size = Const.NORMAL_HEADER_SIZE + count;
                }

                try
                {
                    Sock.BeginSendTo(sendBuffer, 0, size, SocketFlags.None, detail.EndPoint, new AsyncCallback(delegate(IAsyncResult result)
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
        }

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
                rp.BeginSend(opts, PacketType.Application, data);
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
                    rp.BeginSend(opts, PacketType.Application, data);
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

        private void BeginSendTo(IPEndPoint endPoint, SendOptions opts, PacketType type, byte[] payload)
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
        
        internal void EndSendToCallback(IAsyncResult result)
        {
            try
            {
                Sock.EndSendTo(result);
            }
            catch (SocketException se)
            {
                // TODO handel depeding on error code
                Log(LogLevel.Error, "BeginSendTo() SocketException: " + se.Message);
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
        
        internal RemotePeer AddPeer(IPEndPoint ip)
        {
            lock (peersLockObject) // application can use the peer collections e.g. SendToAll()
            {
                peerIdCount++;

                RemotePeer rp = new RemotePeer(this, peerIdCount, ip);
                peersById.Add(peerIdCount, rp);
                peersByIp.Add(ip, rp);

                // raise peer added event 
                if (PeerAdded != null)
                    PeerAdded(rp.Id); // TODO should begin invoke? dont want to hold up

                return rp;
            }
        }

        private void TryRemovePeer(IPEndPoint ip)
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

        private bool TryGetAndRemoveWaitingAcceptDetail(IPEndPoint ip, out AwaitingAcceptDetail detail)
        {
            bool found = false;
            lock (awaitingAcceptDetails)
            {
                detail = awaitingAcceptDetails.Find(aad => aad.EndPoint.Address.Equals(ip.Address) && aad.EndPoint.Port == ip.Port);
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
