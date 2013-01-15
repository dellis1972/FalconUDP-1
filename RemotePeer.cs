using System;
using System.Collections.Generic;
using System.Net;
#if NETFX_CORE
#else
using System.Net.Sockets;
#endif

namespace FalconUDP
{
    class RemotePeer
    {
#if NETFX_CORE
        internal FalconEndPoint EndPoint;
#else
        internal IPEndPoint EndPoint;
#endif

        internal int Id;
        internal int PacketCount;                               // number of received packets not yet retrived by application

        private FalconPeer localPeer;                           // local peer this remote peers has joined
        private byte sendSeqCount;
        private byte lastReceivedSeq;
        private int seqOrdinal;
        private int receivedSeqInOrderMax;
        private int readInOrderSeqMax;                          // differs from receivedInOrderMax in that = max actually retrived by application
        private SortedDictionary<int, Packet> receivedPackets;  // packets received from this peer not yet "read" by application
        private SortedDictionary<int, PacketDetail> sentPacketsAwaitingACK;
        private List<int> sentPacketsAwaitingACKToRemove;
        private byte[] sendBuffer;                              // buffer recycled for every packet sent to this peer
        private int sendPacketSize;                             // the size of the current packet to be sent in SendBuffer
        private string peerName;                                // e.g. IP address, used internally for logging
        private bool isResynching;
        private List<byte[]> backlog;                           // filled with raw packets when isResynching
        private bool isClearingBacklog;
        private bool hasResynchedAndHasPacketsPreSynch;
        private byte[] payloadSizeBytes = new byte[2];          // buffer recycled for headers with payload size as ushort 

#if NETFX_CORE
        internal RemotePeer(FalconPeer localPeer, int id, FalconEndPoint endPoint)
#else
        internal RemotePeer(FalconPeer localPeer, int id, IPEndPoint endPoint)
#endif
        {
            this.Id                     = id;
            this.localPeer              = localPeer;
            this.sendSeqCount           = 0;
            this.EndPoint               = endPoint;
            this.sendBuffer             = new byte[Const.MAX_DATAGRAM_SIZE];
            this.sendPacketSize         = 0;
            this.PacketCount            = 0;
            this.receivedPackets        = new SortedDictionary<int, Packet>();
            this.sentPacketsAwaitingACK = new SortedDictionary<int, PacketDetail>();
            this.sentPacketsAwaitingACKToRemove = new List<int>();
            this.peerName               = endPoint.ToString();
            this.isResynching           = false;
            this.hasResynchedAndHasPacketsPreSynch = false;
            this.backlog                = new List<byte[]>();

            ResetSequenceCounters();
        }

        internal void ACKTick()
        {
            // NOTE: This method is called by some arbitary thread in the ThreadPool by Falcon's 
            //       Timer.

            lock (sentPacketsAwaitingACK) 
            {
                foreach (KeyValuePair<int, PacketDetail> kv in sentPacketsAwaitingACK)
                {
                    kv.Value.ACKTicks++;
                    if (kv.Value.ACKTicks == Settings.ACKTimeoutTicks)
                    {
                        kv.Value.ACKTicks = 0;
                        kv.Value.ResentCount++;
                        if (kv.Value.ResentCount > Settings.ACKRetryAttempts)
                        {
                            // give-up, assume the peer has disconnected and drop it
                            sentPacketsAwaitingACKToRemove.Add(kv.Key);
                            localPeer.RemotePeersToDrop.Add(this);
                            localPeer.Log(LogLevel.Warning, String.Format("Peer dropped - failed to ACK {0} re-sends of Reliable packet in time.", Settings.ACKRetryAttempts));
                        }
                        else
                        {
                            // try again, re-send the packet
                            BeginSend(kv.Value);
                            localPeer.Log(LogLevel.Info, String.Format("Packet to: {0} re-sent as not ACKnowledged in time.", this.EndPoint));
                        }
                    }
                }

                if (sentPacketsAwaitingACKToRemove.Count > 0)
                {
                    foreach (int k in sentPacketsAwaitingACKToRemove)
                    {
                        sentPacketsAwaitingACK.Remove(k);
                    }
                    sentPacketsAwaitingACKToRemove.Clear();
                }
            }
        }

        internal void Ping()
        {
            __BeginSend__(Const.PING_PACKET);
        }

        internal void BeginSend(SendOptions opts, PacketType type, byte[] payload)
        {
            BeginSend(opts, type, payload, null);
        }

        internal void BeginSend(SendOptions opts, PacketType type, byte[] payload, Action ackCallback)
        {
            HeaderPayloadSizeType hpst = HeaderPayloadSizeType.Byte;

            if (payload != null && payload.Length > Byte.MaxValue) // relies on short-circut if payload is null
            {
                hpst = HeaderPayloadSizeType.UInt16;
                if (payload.Length > Const.MAX_DATAGRAM_SIZE)
                {
                    // We could fragment the payload into seperate packets but then we would have 
                    // to send them reliably so can be assembled at the other end. FalconUDP is 
                    // designed for small packets - keep it that way!

                    throw new InvalidOperationException(String.Format("Data size: {0}, greater than max allowed: {1}.", payload.Length, Const.MAX_DATAGRAM_SIZE));
                }
            }

            sendSeqCount++;

            sendBuffer[0] = sendSeqCount;
            sendBuffer[1] = (byte)((byte)hpst | (byte)opts | (byte)type);

            if (payload == null)
            {
                sendBuffer[2] = 0;
                sendPacketSize = Const.NORMAL_HEADER_SIZE;
            }
            else
            {
                if (hpst == HeaderPayloadSizeType.Byte)
                {
                    sendBuffer[2] = (byte)payload.Length;
                    sendPacketSize = payload.Length + Const.NORMAL_HEADER_SIZE;
                    Buffer.BlockCopy(payload, 0, sendBuffer, Const.NORMAL_HEADER_SIZE, payload.Length);
                }
                else
                {
                    byte[] payloadSizeInBytes = BitConverter.GetBytes((ushort)payload.Length);
                    sendBuffer[2] = payloadSizeInBytes[0];
                    sendBuffer[3] = payloadSizeInBytes[1];
                    sendPacketSize = payload.Length + Const.LARGE_HEADER_SIZE;
                    Buffer.BlockCopy(payload, Const.LARGE_HEADER_SIZE, sendBuffer, Const.LARGE_HEADER_SIZE, payload.Length);
                }
            }

            // If ACK required add detail - just before we send - to be sure we know about it when 
            // we get the reply ACK.

            if ((opts & SendOptions.Reliable) == SendOptions.Reliable)
            {
                // Unfortunatly we have to copy the send buffer at this point in case the packet 
                // needs to be re-sent at which point the send buffer will likely be over-written.

                byte[] rawPacket = new byte[sendPacketSize];
                Buffer.BlockCopy(sendBuffer, 0, rawPacket, 0, sendPacketSize);

                PacketDetail detail = new PacketDetail(rawPacket, ackCallback) { ActualSequence = sendActualSeqCount };

                lock (sentPacketsAwaitingACK)
                {
                    sentPacketsAwaitingACK.Add(sendActualSeqCount, detail);
                }
            }
            else if (ackCallback != null)
            {
                // it is an error to supply an ackCallback if not sending reliably...
                localPeer.Log(LogLevel.Warning, String.Format("ACKCallback supplied in BeginSendTo() {0}, but SendOptions not Reliable - callback will never called.", peerName));
            }

            // au revior 
            __BeginSend__(sendBuffer, sendPacketSize);
        }

        private void BeginSendACK(byte seq)
        {
            BeginSend(SendOptions.None, PacketType.ACK, seq);
        }

        private void BeginSendAntiACK(byte seq)
        {
            BeginSend(SendOptions.None, PacketType.AntiACK, seq);
        }

        private void BeginSend(PacketDetail detail)
        {
            __BeginSend__(detail.RawPacket);
        }

        private void BeginSend(byte[] rawPacket, Action ackCallback)
        {
            __BeginSend__(rawPacket);
        }

        private void __BeginSend__(byte[] rawPacket)
        {
            __BeginSend__(rawPacket, rawPacket.Length);
        }

        private void __BeginSend__(byte[] rawPacket, int count)
        {
            // If we are re-synching, backlog the packet. 

            if (isResynching)
            {
                // Unfortunatly rawPacket[] is probably just a ref to the send buffer so have to 
                // copy the packet.

                byte[] copy = new byte[count];
                Buffer.BlockCopy(rawPacket, 0, copy, 0, count);

                lock(backlog)
                {
                    backlog.Add(copy);
                }
            }
            else if (sendActualSeqCount > Const.CHECK_BACKLOG_AT) // don't bother checking (which requires lock) if we arn't even close
            {
                lock (backlog)
                {
                    if (backlog.Count > 0 && !isClearingBacklog)
                    {
                        isClearingBacklog = true;
                        foreach (byte[] rp in backlog)
                        {
                            __BeginSend__(rp);
                        }
                        backlog.Clear();
                        isClearingBacklog = false;
                    }
                }
            }

            try
            {
                localPeer.Sock.BeginSendTo(rawPacket, 0, count, SocketFlags.None, EndPoint, EndSendToCallback, null);
            }
            catch (SocketException se)
            {
                // TODO
                //sentPacketsAwaitingACK.RemoveAt(sentPacketsAwaitingACK.IndexOfValue(detail));
            }

            // Re-synch if we are going to exceed max seq and backlog subsequent sends until 
            // recipt of ACKnowledgment.

            if (sendActualSeqCount == Const.MAX_ACTUAL_SEQ)
            {
                BeginSend(SendOptions.None, PacketType.Resynch, null, new Action(delegate()
                    {
                        ResetSequenceCounters();
                        isResynching = false;
                    }));
                isResynching = true;
            }
        }

        private void EndSendToCallback(IAsyncResult result)
        {
            try
            {
                localPeer.Sock.EndSendTo(result);
            }
            catch (SocketException se)
            {
                // TODO drop this peer?
                localPeer.Log(LogLevel.Error, String.Format("Sending to: {0}, Socket Exception: {1}.", EndPoint, se.Message));
            }
        }

        private void ResetSequenceCounters()
        {
            sendSeqCount = 0;
            lastReceivedSeq = 0;
            receivedSeqInOrderMax = 0;
            readInOrderSeqMax = 0;
        }

        internal void AddReceivedPacket(byte seq, SendOptions opts, PacketType type, byte[] payload)
        {
            switch (type)
            {
                case PacketType.Ping:
                    {
                        __BeginSend__(Const.PONG_PACKET);
                        return;
                    }
                case PacketType.Pong:
                    {
                        localPeer.RaisePongReceived(this);
                    }
                    break;
                case PacketType.AddPeer:
                    {
                        // Must be hasn't received Accept yet (otherwise AddPeer wouldn't have go 
                        // this far - as this RemotePeer wouldn't be created yet).

                        return;
                    }
                case PacketType.ACK:
                case PacketType.AntiACK:
                    {
                        if (payload.Length != Const.SIZE_OF_SEQ)
                        {
                            localPeer.Log(LogLevel.Error, String.Format("ACK or AntiACK packet dropped from peer: {0}, bad payload size: {1}, expected: {2}.", peerName, payload.Length, Const.SIZE_OF_SEQ));
                            return;
                        }

                        byte seqACKFor = payload[0];

                        lock (sentPacketsAwaitingACK)   // ACK Tick also uses this collection
                        {
                            PacketDetail detail;
                            if (!sentPacketsAwaitingACK.TryGetValue(seqACKFor, out detail))
                            {
                                // ACK has arrived too late and the packet must have already been removed.
                                localPeer.Log(LogLevel.Warning, "Packet for ACK not found - must be too late.");
                                return;
                            }

                            if (type == PacketType.ACK)
                            {
                                // call the callback awaiting ACK, if any
                                if (detail.ACKCallback != null)
                                    detail.ACKCallback();

                                // remove detail of packet that was awaiting ACK
                                sentPacketsAwaitingACK.Remove(seqACKFor);
                            }
                            else // must be AntiACK
                            {
                                // Re-send the unACKnowledged packet right away NOTE: we are not 
                                // incrementing resent count, we are resetting it, because the remote peer 
                                // must be alive to have sent the AntiACK.

                                detail.ACKTicks = 0;
                                detail.ResentCount = 0;
                                BeginSend(detail);
                            }
                        }
                    }
                    break;
                default:
                    {
                        // validate seq
                        byte max = (byte)(lastReceivedSeq + Settings.OutOfOrderTolerance);
                        byte min = (byte)(lastReceivedSeq - Settings.OutOfOrderTolerance);
                        if (max < min) // possible if exceeded Byte.MaxValue and wrapped around
                        {
                            byte tmp = max;
                            max = min;
                            min = tmp;
                        }

                        if (seq > max)
                        {
                            localPeer.Log(LogLevel.Warning, String.Format("Out-of-order packet dropped, out-of-order from current max received by: {0}.", seq - max));
                            return;
                        }
                        else if (seq < min)
                        {
                            localPeer.Log(LogLevel.Warning, String.Format("Out-of-order packet dropped, out-of-order from current max received by: {0}.", min - seq));
                            return;
                        }

                        lastReceivedSeq = seq;

                        bool reqReliable = (opts & SendOptions.Reliable) == SendOptions.Reliable;

                        // If packet requries ACK - send it!
                        if (reqReliable)
                        {
                            BeginSendACK(seq);
                        }

                        // TODO needs to be changed now that we have changed out sequencing algorithim..
                        // If packet required to be in order check it is after the max seq already read by
                        // application, otherwise drop it - in which case if required to be reliable notify
                        // peer packet was dropped so does not neccasirly have to wait until ACK_TIMEOUT to 
                        // determine packet was dropped (it will wait only the minima of the two).
                        //
                        //if ((opts & SendOptions.InOrder) == SendOptions.InOrder)
                        //{
                        //    if (seq < readInOrderSeqMax)
                        //    {
                        //        if (reqReliable)
                        //            BeginSendAntiACK(seq);
                        //        return;
                        //    }
                        //    else
                        //    {
                        //        receivedSeqInOrderMax = actualSeq;
                        //    }
                        //}

                        switch (type)
                        {
                            case PacketType.Application:
                                {
                                    // add payload to list of received for reading by the application

                                    lock (receivedPackets) // collection also used by application
                                    {
                                        // validate we don't already have a packet with same seq!
                                        if (receivedPackets.ContainsKey(acutalSeqOrdinal))
                                        {
                                            // Possible reasons are:
                                            // 1) receivedPackets has not been read for a while and seq has looped
                                            // 2) datagram duplicated
                                            // 3) packet is way out-of-order such that is passed out-of-order validation

                                            localPeer.Log(LogLevel.Warning, String.Format("Dropped packet from {0}, duplicate seq.", peerName));
                                        }
                                        else
                                        {
                                            receivedPackets.Add(seq, new Packet(Id, acutalSeqOrdinal, payload));
                                            PacketCount++;
                                        }
                                    }
                                }
                                break;
                            case PacketType.AcceptJoin:
                                {
                                    // nothing else to do..
                                }
                                break;
                            case PacketType.Resynch:
                                {
                                    ResetSequenceCounters();

                                    lock (receivedPackets) // collection also used by application
                                    {
                                        hasResynchedAndHasPacketsPreSynch = receivedPackets.Count > 0;
                                    }
                                }
                                break;
                        }
                    }
                    break;
            }
        }

        internal IList<Packet> Read()
        {
            lock (receivedPackets) // the application calls this method through Falcon.ReadAll()
            {
                // Minimise garbage by returning the internal collection of the sorted list of
                // Packets held rather than returing a copy. Create a new list to hold subsequent
                // packets. (That way only Packets will become garbage, once the application has 
                // finished with them, rather than the Packets and their copy becoming garbage).

                PacketCount = 0;

                if (readInOrderSeqMax < receivedSeqInOrderMax)
                    readInOrderSeqMax = receivedSeqInOrderMax;

                if (hasResynchedAndHasPacketsPreSynch)
                    hasResynchedAndHasPacketsPreSynch = false;

                IList<Packet> ps = receivedPackets.Values;

                receivedPackets = new SortedDictionary<uint, Packet>();

                return ps;
            }
        }
    }
}
