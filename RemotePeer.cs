﻿using System;
using System.Collections.Generic;
using System.Net;
#if NETFX_CORE
using Windows.Storage.Streams;
using System.Threading.Tasks;
#else
using System.Net.Sockets;
#endif

namespace FalconUDP
{
    class RemotePeer
    {
#if NETFX_CORE
        internal IPv4EndPoint EndPoint;
        private IOutputStream outStream;
#else
        internal IPEndPoint EndPoint;
#endif
        internal int Id;
        internal int UnreadPacketCount;                         // number of received packets not yet read by application

        private FalconPeer localPeer;                           // local peer this remote peers has joined
        private byte sendSeqCount;
        private byte lastReceivedSeq;
        private byte lastReadSeq;                               // differs from lastReceivedSeq in that = max actually retrived by application
        private List<Packet> receivedPackets;                   // packets received from this peer not yet "read" by application
        private List<PacketDetail> sentPacketsAwaitingACK;
        private List<PacketDetail> sentPacketsAwaitingACKToRemove;
        private string peerName;                                // e.g. IP address, used internally for logging
        private byte[] payloadSizeBytes = new byte[2];          // buffer recycled for headers with payload size as ushort 
        private byte[] ackBuffer;
        private byte[] antiAckBuffer;
        private object sendSeqCountLock;
        private object receivingPacketLock;

#if NETFX_CORE
        internal RemotePeer(FalconPeer localPeer, IPv4EndPoint endPoint)
#else
        internal RemotePeer(FalconPeer localPeer, IPEndPoint endPoint)
#endif
        {
            this.localPeer              = localPeer;
            this.sendSeqCount           = 0;
            this.EndPoint               = endPoint;
            this.UnreadPacketCount      = 0;
            this.receivedPackets        = new List<Packet>();
            this.sentPacketsAwaitingACK = new List<PacketDetail>();
            this.sentPacketsAwaitingACKToRemove = new List<PacketDetail>();
            this.peerName               = endPoint.ToString();
            this.lastReceivedSeq        = 0;
            this.ackBuffer              = new byte[] { 0, Const.ACK_PACKET_INFO, 0 };
            this.antiAckBuffer          = new byte[] { 0, Const.ANTI_ACK_PACKET_INFO, 0 };
            this.sendSeqCountLock = new object();
            this.receivingPacketLock    = new object();
        }

        internal void ACKTick()
        {
            // NOTE: This method is called by some arbitary thread in the ThreadPool by Falcon's 
            //       Timer.

            lock (sentPacketsAwaitingACK) 
            {
                foreach (PacketDetail pd in sentPacketsAwaitingACK)
                {
                    pd.ACKTicks++;
                    if (pd.ACKTicks == Settings.ACKTimeoutTicks)
                    {
                        pd.ACKTicks = 0;
                        pd.ResentCount++;
                        if (pd.ResentCount > Settings.ACKRetryAttempts)
                        {
                            // give-up, assume the peer has disconnected and drop it TODO BYE
                            sentPacketsAwaitingACKToRemove.Add(pd);
                            localPeer.RemotePeersToDrop.Add(this);
                            localPeer.Log(LogLevel.Warning, String.Format("Peer dropped - failed to ACK {0} re-sends of Reliable packet in time.", Settings.ACKRetryAttempts));
                        }
                        else
                        {
                            // try again, re-send the packet
#if NETFX_CORE
                            SendAsync(pd.RawPacket);
#else
                            BeginSend(pd.RawPacket);
#endif
                            localPeer.Log(LogLevel.Info, String.Format("Packet to: {0} re-sent as not ACKnowledged in time.", this.EndPoint));
                        }
                    }
                }

                if (sentPacketsAwaitingACKToRemove.Count > 0)
                {
                    foreach (PacketDetail pd in sentPacketsAwaitingACKToRemove)
                    {
                        sentPacketsAwaitingACK.Remove(pd);
                    }
                    sentPacketsAwaitingACKToRemove.Clear();
                }
            }
        }

        internal void Ping()
        {
#if NETFX_CORE
            SendAsync(Const.PING_PACKET);
#else
            BeginSend(Const.PING_PACKET);
#endif

        }
        
        internal void BeginSend(SendOptions opts, PacketType type, byte[] payload, Action ackCallback)
        {
            HeaderPayloadSizeType hpst = HeaderPayloadSizeType.Byte;

            if (payload != null && payload.Length > Byte.MaxValue) // relies on short-circut if payload is null
            {
                hpst = HeaderPayloadSizeType.UInt16;
                if (payload.Length > Const.MAX_PAYLOAD_SIZE)
                {
                    // We could fragment the payload into seperate packets but then we would have 
                    // to send them reliably so can be assembled at the other end. FalconUDP is 
                    // designed for small packets - keep it that way!

                    throw new InvalidOperationException(String.Format("Data size: {0}, greater than max allowed: {1}.", payload.Length, Const.MAX_PAYLOAD_SIZE));
                }
            }

            // create a new raw packet from the parameters supplied

            int sendPacketSize, payloadSize;
            if (payload == null)
            {
                payloadSize = 0;
                sendPacketSize = Const.NORMAL_HEADER_SIZE;
            }
            else
            {
                payloadSize = payload.Length;
                if (hpst == HeaderPayloadSizeType.Byte)
                    sendPacketSize = payload.Length + Const.NORMAL_HEADER_SIZE;
                else
                    sendPacketSize = payload.Length + Const.LARGE_HEADER_SIZE;
            }

            byte[] rawPacket = new byte[sendPacketSize];

            lock (sendSeqCountLock)
            {
                sendSeqCount++;
                rawPacket[0] = sendSeqCount;
                rawPacket[1] = (byte)((byte)hpst | (byte)opts | (byte)type);

                if (hpst == HeaderPayloadSizeType.Byte)
                {
                    rawPacket[2] = (byte)payloadSize;

                    if (payloadSize > 0)
                    {
                        System.Buffer.BlockCopy(payload, 0, rawPacket, Const.NORMAL_HEADER_SIZE, payloadSize);
                    }
                }
                else
                {
                    byte[] payloadSizeInBytes = BitConverter.GetBytes((ushort)payload.Length);
                    rawPacket[2] = payloadSizeInBytes[0];
                    rawPacket[3] = payloadSizeInBytes[1];
                    System.Buffer.BlockCopy(payload, 0, rawPacket, Const.LARGE_HEADER_SIZE, payloadSize);
                }

                // If ACK required add detail - just before we send - to be sure we know about it when 
                // we get the reply ACK.

                if ((opts & SendOptions.Reliable) == SendOptions.Reliable)
                {
                    PacketDetail detail = new PacketDetail(rawPacket, ackCallback) { Sequence = sendSeqCount };

                    lock (sentPacketsAwaitingACK)
                    {
                        sentPacketsAwaitingACK.Add(detail);
                    }
                }
                else if (ackCallback != null)
                {
                    // it is an error to supply an ackCallback if not sending reliably...
                    localPeer.Log(LogLevel.Warning, String.Format("ACKCallback supplied in BeginSendTo() {0}, but SendOptions not Reliable - callback will never called.", peerName));
                }
            }

#if NETFX_CORE
            SendAsync(rawPacket);
#else
            BeginSend(rawPacket);
#endif
        }
        
#if NETFX_CORE

        internal async Task<TryResult> InitAsync()
        {
            try
            {
                this.outStream = await localPeer.Sock.GetOutputStreamAsync(EndPoint.Address, EndPoint.Port);
                return TryResult.SuccessResult;
            }
            catch (Exception ex)
            {
                return new TryResult(ex);
            }
        }

        private async void SendAsync(byte[] rawPacket)
        {
            try
            {
                // TODO is there a better way than creating a DataWriter every time? http://social.msdn.microsoft.com/Forums/en-US/winappswithcsharp/thread/0d321642-13bd-40d4-b83f-963638b0d5df/#0d321642-13bd-40d4-b83f-963638b0d5df

                // TODO lock outStream
                using (DataWriter dw = new DataWriter(outStream))
                {
                    dw.WriteBytes(rawPacket);
                    await dw.StoreAsync();
                    dw.DetachStream(); // so socket can be used with another DataWriter
                }
            }
            catch (Exception ex)
            {
                localPeer.Log(LogLevel.Error, String.Format("Exception sending to peer {0}: {1}", peerName, ex.Message));
            }
        }

#else

        private void BeginSend(byte[] rawPacket)
        {
            try
            {
                // TODO do we need ensure calls to BeginSendTo are not concurrent?
                localPeer.Sock.BeginSendTo(rawPacket, 0, rawPacket.Length, SocketFlags.None, EndPoint, EndSendToCallback, null);
            }
            catch (SocketException se)
            {
                // TODO drop this peer?
                localPeer.Log(LogLevel.Error, String.Format("BeginSendTo: {0}, Socket Exception: {1}.", peerName, se.Message));
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
                localPeer.Log(LogLevel.Error, String.Format("EndSendTo: {0}, Socket Exception: {1}.", peerName, se.Message));
            }
        }

#endif

        internal void AddReceivedPacket(byte seq, SendOptions opts, PacketType type, byte[] payload)
        {
            lock (receivingPacketLock)
            {
                switch (type)
                {
                    case PacketType.Ping:
                        {
#if NETFX_CORE
                            SendAsync(Const.PONG_PACKET);
#else
                            BeginSend(Const.PONG_PACKET);
#endif
                            return;
                        }
                    case PacketType.Pong:
                        {
                            localPeer.RaisePongReceived(this);
                        }
                        break;
                    case PacketType.AddPeer:
                        {
                            // Must be hasn't received Accept yet and is resending (otherwise 
                            // AddPeer wouldn't have go this far - as this RemotePeer wouldn't be 
                            // created yet).

                            return;
                        }
                    case PacketType.ACK:
                    case PacketType.AntiACK:
                        {
                            lock (sentPacketsAwaitingACK)   // ACK Tick also uses this collection
                            {
                                // Look for the oldest PacketDetail with the same seq which we ASSUME 
                                // the ACK is for.

                                PacketDetail detail = sentPacketsAwaitingACK.Find(pd => pd.Sequence == seq);

                                if (detail == null)
                                {
                                    // Possible reasons in order of likelyhood:
                                    // 1) ACK has arrived too late and the packet must have already been removed.
                                    // 2) ACK duplicated and has already been processed
                                    // 3) ACK was unsolicited (i.e. malicious or buggy peer)

                                    localPeer.Log(LogLevel.Warning, "Packet for ACK not found - too late?");
                                    return;
                                }

                                if (type == PacketType.ACK)
                                {
                                    // call the callback awaiting ACK, if any
                                    if (detail.ACKCallback != null)
                                        detail.ACKCallback();

                                    // remove detail of packet that was awaiting ACK
                                    sentPacketsAwaitingACK.Remove(detail);
                                }
                                else // must be AntiACK
                                {
                                    // Re-send the unACKnowledged packet right away NOTE: we are not 
                                    // incrementing resent count, we are resetting it, because the remote
                                    // peer must be alive to have sent the AntiACK.

                                    detail.ACKTicks = 0;
                                    detail.ResentCount = 0;
#if NETFX_CORE
                                    SendAsync(detail.RawPacket);
#else
                                    BeginSend(detail.RawPacket);
#endif
                                }
                            }
                        }
                        break;
                    default:
                        {
                            // validate seq
                            byte min = unchecked((byte)(lastReceivedSeq - Settings.OutOfOrderTolerance));
                            byte max = unchecked((byte)(lastReceivedSeq + Settings.OutOfOrderTolerance));

                            // NOTE: Max could be less than min if exceeded Byte.MaxValue, likewise 
                            //       min could be greater than max if less than 0. So have to check 
                            //       seq between min - max range which is a loop, inclusive.

                            if (seq > max && seq < min)
                            {
                                localPeer.Log(LogLevel.Warning, String.Format("Out-of-order packet dropped, out-of-order from last by: {0}.", seq - lastReceivedSeq));
                                return;
                            }

                            lastReceivedSeq = seq;

                            bool reqReliable = (opts & SendOptions.Reliable) == SendOptions.Reliable;

                            // If packet requries ACK - send it!
                            if (reqReliable)
                            {
                                ackBuffer[0] = seq;
#if NETFX_CORE
                                SendAsync(ackBuffer);
#else
                                BeginSend(ackBuffer);
#endif
                            }

                            // If packet required to be in order check it is after the max seq already 
                            // read by application, otherwise drop it - in which case if required to 
                            // be reliable notify peer packet was dropped so does not neccasirly have 
                            // to wait until ACK_TIMEOUT to determine packet was dropped (it will wait 
                            // only the minima of the two).

                            if ((opts & SendOptions.InOrder) == SendOptions.InOrder)
                            {
                                // TODO needs a re-think
                                //if (seq < lastReadSeq && (seq - lastReadSeq) < Settings.OutOfOrderTolerance)
                                //{
                                //    // This is expected now and then, no need to log.

                                //    if (reqReliable)
                                //        antiAckBuffer[0] = seqAckFor;
                                //        SendAsync(antiAckBuffer);

                                //    return;
                                //}
                            }

                            switch (type)
                            {
                                case PacketType.Application:
                                    {
                                        // Insert the packet in order of seq to list of received 
                                        // packets. Most of the time will be adding to the end.

                                        lock (receivedPackets) // collection also used by application
                                        {
                                            if (receivedPackets.Count == 0)
                                            {
                                                receivedPackets.Add(new Packet(Id, seq, payload));
                                                UnreadPacketCount++;
                                            }
                                            else if (receivedPackets[receivedPackets.Count - 1].Seq < seq)
                                            {
                                                if ((seq - receivedPackets[receivedPackets.Count - 1].Seq) > Settings.OutOfOrderTolerance)
                                                {
                                                    // seq must be from previous loop and another seq 
                                                    // has already come in from new loop. Cycle back 
                                                    // till we are in the old loop then insert it.

                                                    for (int i = receivedPackets.Count - 2; i >= 0; i--)
                                                    {
                                                        if ((seq - receivedPackets[i].Seq) < Settings.OutOfOrderTolerance) // could be negative
                                                        {
                                                            // OK we are dealing with seq from the same loop.
                                                            for (int j = i; j >= 0; j--)
                                                            {
                                                                if (receivedPackets[j].Seq == seq)
                                                                {
                                                                    localPeer.Log(LogLevel.Warning, "Dropped duplicate packet");
                                                                    break;
                                                                }
                                                                else if (receivedPackets[j].Seq < seq)
                                                                {
                                                                    receivedPackets.Insert(j + 1, new Packet(Id, seq, payload));
                                                                    UnreadPacketCount++;
                                                                    break;
                                                                }
                                                                else if (j == 0)
                                                                {
                                                                    receivedPackets.Insert(0, new Packet(Id, seq, payload));
                                                                    UnreadPacketCount++;
                                                                }
                                                            }
                                                        }
                                                        else if (i == 0)
                                                        {
                                                            // we never found our loop
                                                            receivedPackets.Insert(0, new Packet(Id, seq, payload));
                                                            UnreadPacketCount++;
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    receivedPackets.Add(new Packet(Id, seq, payload));
                                                    UnreadPacketCount++;
                                                }
                                            }
                                            else if ((receivedPackets[receivedPackets.Count - 1].Seq - seq) > Settings.OutOfOrderTolerance)
                                            {
                                                // seq must have looped
                                                receivedPackets.Add(new Packet(Id, seq, payload));
                                                UnreadPacketCount++;
                                            }
                                            else
                                            {
                                                for (int i = receivedPackets.Count - 1; i >= 0; i--)
                                                {
                                                    if (receivedPackets[i].Seq == seq)
                                                    {
                                                        localPeer.Log(LogLevel.Warning, "Dropped duplicate packet");
                                                        break;
                                                    }
                                                    else if (receivedPackets[i].Seq < seq)
                                                    {
                                                        receivedPackets.Insert(i + 1, new Packet(Id, seq, payload));
                                                        UnreadPacketCount++;
                                                        break;
                                                    }
                                                    else if (i == 0)
                                                    {
                                                        receivedPackets.Insert(0, new Packet(Id, seq, payload));
                                                        UnreadPacketCount++;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    break;
                                case PacketType.AcceptJoin:
                                    {
                                        // nothing else to do..
                                    }
                                    break;
                            }
                        }
                        break;
                }
            }
        }

        internal List<Packet> Read()
        {
            lock (receivedPackets) // the application calls this method through Falcon.ReadAll()
            {
                if (UnreadPacketCount == 0)
                {
                    return null;
                }
                else
                {
                    // Minimise garbage by returning the internal collection of the sorted list of
                    // Packets held rather than returing a copy. Create a new list to hold subsequent
                    // packets. (That way only Packets will become garbage, once the application has 
                    // finished with them, rather than the Packets and their copy becoming garbage).

                    lastReadSeq         = receivedPackets[receivedPackets.Count-1].Seq;
                    UnreadPacketCount   = 0;
                    List<Packet> ps     = receivedPackets;
                    receivedPackets     = new List<Packet>(ps.Count + 10); // guess the size needed + some to mitigate resizing
                    return ps;
                }
            }
        }
    }
}
