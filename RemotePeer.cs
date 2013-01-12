﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace FalconUDP
{
    class RemotePeer
    {
        internal int Id;
        internal IPEndPoint EndPoint;
        internal int PacketCount;                               // number of received packets not yet retrived by application

        private byte sendSeqCount;
        private int sendActualSeqCount;
        private int receivedSeqLoopCount;
        private int receivedSeqMax;
        private int receivedSeqLoopCutoff;
        private int receivedSeqInOrderMax;
        private int readInOrderSeqMax;                          // differs from receivedInOrderMax in that = max actually retrived by application
        private SortedList<uint, Packet> receivedPackets;       // packets received from this peer not yet retrived by application TODO after a re-synch new packets will erroneously be pushed to the top of the list...
        private SortedList<int, PacketDetail> sentPacketsAwaitingACK;
        private List<int> sentPacketsAwaitingACKToRemove;
        private byte[] receiveBuffer;
        private byte[] sendBuffer;                              // buffer recycled for every packet sent to this peer
        private int sendPacketSize;                             // the size of the current packet to be sent in SendBuffer
        private string peerName;                                // e.g. IP address, used internally for logging
        private bool isResynching;
        private List<byte[]> backlog;                           // filled with raw packets when isResynching
        private bool isClearingBacklog;
        private bool hasResynchedAndHasPacketsPreSynch;
        private byte[] payloadSizeBytes = new byte[2];          // buffer recycled for headers with payload size as ushort 

        internal RemotePeer(int id, IPEndPoint endPoint)
        {
            this.Id                     = id;
            this.sendSeqCount           = 0;
            this.EndPoint               = endPoint;
            this.receiveBuffer          = new byte[Settings.MAX_DATAGRAM_SIZE];
            this.sendBuffer             = new byte[Settings.MAX_DATAGRAM_SIZE];
            this.sendPacketSize         = 0;
            this.PacketCount            = 0;
            this.receivedPackets        = new SortedList<uint, Packet>();
            this.sentPacketsAwaitingACK = new SortedList<int, PacketDetail>();
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
                    if (kv.Value.ACKTicks == Settings.ACK_TIMEOUT_TICKS)
                    {
                        kv.Value.ACKTicks = 0;
                        kv.Value.ResentCount++;
                        if (kv.Value.ResentCount == Settings.ACK_RETRY_ATTEMPTS)
                        {
                            // give-up, assume the peer has disconnected and drop it
                            sentPacketsAwaitingACKToRemove.Add(kv.Key);
                            Falcon.RemotePeersToDrop.Add(this);
                            Falcon.Log(LogLevel.Warning, String.Format("Peer dropped - failed to ACK {0} re-sends of Reliable packet in time.", Settings.ACK_RETRY_ATTEMPTS));
                        }
                        else
                        {
                            // try again, re-send the packet
                            BeginSend(kv.Value);
                            Falcon.Log(LogLevel.Info, String.Format("Packet to: {0} re-sent as not ACKnowledged in time.", this.EndPoint));
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

        internal void BeginSend(SendOptions opts, PacketType type, byte[] payload)
        {
            BeginSend(opts, type, payload, null);
        }

        internal void BeginSend(SendOptions opts, PacketType type, byte[] payload, Action ackCallback)
        {
            // fill send buffer with raw packet from opts, type and payload
            PackagePacket(opts, type, payload);

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
                // it is an error to supply an ackCallback but not send reliably...
            }

            // au revior 
            __BeginSend__(sendBuffer, sendPacketSize);
        }

        private void BeginSendACK(int actualSequence)
        {
            BeginSend(SendOptions.None, PacketType.ACK, BitConverter.GetBytes(actualSequence));
        }

        private void BeginSendAntiACK(int actualSequence)
        {
            BeginSend(SendOptions.None, PacketType.AntiACK, BitConverter.GetBytes(actualSequence));
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
            else 
            {
                // It seems unfortunate we have to obtain a lock on backlog for what will only be 
                // neccessary after every 2 147 483 646 packets received! TODO

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
                Falcon.Sender.BeginSendTo(rawPacket, 0, count, SocketFlags.None, EndPoint, EndSendToCallback, null);
            }
            catch (SocketException se)
            {
                // TODO
                //sentPacketsAwaitingACK.RemoveAt(sentPacketsAwaitingACK.IndexOfValue(detail));
            }

            // Re-synch if we are going to exceed max seq and backlog subsequent sends until 
            // recipt of ACKnowledgment.

            if (sendActualSeqCount == Settings.MAX_ACTUAL_SEQ)
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
                Falcon.Sender.EndSendTo(result);
            }
            catch (SocketException se)
            {
                // TODO drop this peer?
                Falcon.Log(LogLevel.Error, String.Format("Sending to: {0}, Socket Exception: {1}.", EndPoint, se.Message));
            }
        }

        private void ResetSequenceCounters()
        {
            sendSeqCount = 0;
            sendActualSeqCount = 0;
            receivedSeqLoopCount = 0;
            receivedSeqMax = Settings.HALF_MAX_SEQ_NUMS;
            receivedSeqLoopCutoff = 0;
            receivedSeqInOrderMax = 0;
            readInOrderSeqMax = 0;
        }

        private void PackagePacket(SendOptions opts, PacketType type, byte[] data)
        {
            HeaderPayloadSizeType hpst = HeaderPayloadSizeType.Byte;

            if (data != null && data.Length > Byte.MaxValue) // relies on short-circut if data is null
            {
                hpst = HeaderPayloadSizeType.UInt16;
                if (data.Length > Settings.MAX_DATAGRAM_SIZE)
                {
                    // We could fragment the payload into seperate packets but then we would have 
                    // to send them reliably so can be assembled at the other end. FalconUDP is 
                    // designed for small packets - keep it that way!

                    throw new InvalidOperationException(String.Format("Data size: {0}, greater than max allowed: {1}.", data.Length, Settings.MAX_DATAGRAM_SIZE));
                }
            }

            sendSeqCount++;         // NOTE: will be reset to 0 if 255
            sendActualSeqCount++;   // keep track of actual seq num used for ACK's

            sendBuffer[0] = sendSeqCount;
            sendBuffer[1] = (byte)((byte)hpst | (byte)opts | (byte)type);

            if (data == null)
            {
                sendBuffer[2] = 0;
                sendPacketSize = Settings.NORMAL_HEADER_SIZE;
            }
            else
            {
                if (hpst == HeaderPayloadSizeType.Byte)
                {
                    sendBuffer[2] = (byte)data.Length;
                    sendPacketSize = data.Length + Settings.NORMAL_HEADER_SIZE;
                    data.CopyTo(sendBuffer, Settings.NORMAL_HEADER_SIZE);
                }
                else
                {
                    byte[] payloadSizeInBytes = BitConverter.GetBytes((ushort)data.Length);
                    sendBuffer[2] = payloadSizeInBytes[0];
                    sendBuffer[3] = payloadSizeInBytes[1];
                    sendPacketSize = data.Length + Settings.LARGE_HEADER_SIZE;
                    data.CopyTo(sendBuffer, Settings.LARGE_HEADER_SIZE);
                }
            }
        }

        internal void AddReceivedDatagram(int size, byte[] buffer)
        {
            if (size < Settings.NORMAL_HEADER_SIZE)
            {
                Falcon.Log(LogLevel.Error, String.Format("Datagram dropped - size: {0}, less than min header size: {1}.", size, Settings.NORMAL_HEADER_SIZE));
                return;
            }

            byte seq = buffer[0];

            // parse packet info byte
            HeaderPayloadSizeType hpst = (HeaderPayloadSizeType)(receiveBuffer[1] & Settings.PAYLOAD_SIZE_TYPE_MASK);
            SendOptions opts = (SendOptions)(receiveBuffer[1] & Settings.SEND_OPTS_MASK);
            PacketType type = (PacketType)(receiveBuffer[1] & Settings.PACKET_TYPE_MASK);

            // check the header makes sense
            if (!Enum.IsDefined(Const.HeaderPayloadSizeTypeType, hpst)
                || !Enum.IsDefined(Const.SendOptionsType, opts)
                || !Enum.IsDefined(Const.PacketTypeType, type))
            {
                Falcon.Log(LogLevel.Warning, String.Format("Dropped packet from peer: {0}, bad header.", peerName));
                return;
            }

            // parse payload size
            int payloadSize, payloadStartIndex;
            if (hpst == HeaderPayloadSizeType.Byte)
            {
                payloadSize = receiveBuffer[3];
                payloadStartIndex = Settings.NORMAL_HEADER_SIZE;
            }
            else
            {
                if (size < Settings.LARGE_HEADER_SIZE)
                {
                    Falcon.Log(LogLevel.Error, String.Format("Datagram with large header specified dropped - size: {0}, less than large header size: {1}.", size, Settings.LARGE_HEADER_SIZE));
                    return;
                }


                Buffer.BlockCopy(buffer, 2, payloadSizeBytes, 0, 2);
                payloadSize = BitConverter.ToUInt16(payloadSizeBytes, 0);
                payloadStartIndex = Settings.LARGE_HEADER_SIZE;
            }


            if (type == PacketType.ACK || type == PacketType.AntiACK)
            {
                int actualSeqACKFor = BitConverter.ToInt32(receiveBuffer, payloadStartIndex);

                lock (sentPacketsAwaitingACK)   // ACK Tick also uses this collection
                {
                    PacketDetail detail;
                    if (!sentPacketsAwaitingACK.TryGetValue(actualSeqACKFor, out detail))
                    {
                        // ACK has arrived too late and the packet must have already been removed.
                        Falcon.Log(LogLevel.Warning, "Packet for ACK not found - must be too late."); // TODO packet summary
                        return;
                    }

                    if (type == PacketType.ACK)
                    {
                        // call the callback awaiting ACK, if any
                        if (detail.ACKCallback != null)
                            detail.ACKCallback();

                        // remove detail of packet that was awaiting ACK
                        sentPacketsAwaitingACK.Remove(actualSeqACKFor);
                    }
                    else // must be AntiACK
                    {
                        // Re-send the unACKnowledged packet right away NOTE: we are not 
                        // incrementing resent count here becuase the remote peer must be alive to 
                        // have sent the AntiACK.

                        BeginSend(detail);
                    }
                }
            }
            else
            {
                // Further processing common to rest of packet types done in seperate function 
                // only for sake of neatness.

                int actualSeq;
                bool success = FurtherProcessPacket(seq, opts, out actualSeq);

                if (success)
                {
                    switch (type)
                    {
                        case PacketType.Application:
                            {
                                // copy packet's payload to list of received for reading by the application

                                byte[] payload = new byte[payloadSize - payloadStartIndex];
                                Buffer.BlockCopy(receiveBuffer, payloadStartIndex, payload, 0, payload.Length);

                                lock (receivedPackets) // collection also used by application
                                {
                                    uint acutalSeqOrdinal = (uint)actualSeq;

                                    if (hasResynchedAndHasPacketsPreSynch)
                                    {
                                        // Re-synch has recently occured and application has 
                                        // not yet read packets from before the re-synch. So 
                                        // add to max actual seq num to actual seq num to sort
                                        // packet correctly.

                                        acutalSeqOrdinal += Int32.MaxValue;
                                    }

                                    // validate we don't already have a packet with same seq!
                                    if (receivedPackets.ContainsKey(acutalSeqOrdinal))
                                    {
                                        // Either packet it out-of-order by a multiple of 
                                        // MAX_SEQ_NUMS or duplicated.

                                        Falcon.Log(LogLevel.Warning, String.Format("Dropped packet from {0}, duplicate seq.", peerName));
                                    }
                                    else
                                    {
                                        receivedPackets.Add(acutalSeqOrdinal, new Packet(Id, acutalSeqOrdinal, payload));
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
            }
        }

        private bool FurtherProcessPacket(byte seq, SendOptions opts, out int actualSeq)
        {
            // Calculate the actual sequence number of the packet by adding the number of possible 
            // sequence numbers in a loop (MAX_SEQ_NUMS which is equal to MAX_SEQ_NUM + 1) times 
            // the number loops since we started.

            // A trailing cutoff point follows the maximum actual sequence number received by half 
            // the number of sequence numbers (HALF_MAX_SEQ_NUMS) in a loop. A packet with a seq 
            // less than this is assumed to be from the next loop so in such cases add the number 
            // of sequence numbers in a loop to the actual sequence num. The variable tracking the 
            // number of loops since we started is only incremented when the trailing cutoff point 
            // divided by the number of numbers in a loop passes the current value of the varible.

            // At this point we are tolerant of out-of-order packets to a magnitude + or - half 
            // the number of possible sequence numbers. Should a packet be out-of-order by more than 
            // this it will be given an erroneous actual seq num. Reduce this possibility further 
            // by checking actual seq num in a range (less than half the number of possible seq 
            // numbers) + or - the current actual max seq num received. However even in the most 
            // extreme case - setting this range to 0 - we can only be tolerant of out-of-order 
            // within + or - MAX_SEQ_NUMS, i.e. one loop.

            actualSeq = seq + (Settings.MAX_SEQ_NUMS * receivedSeqLoopCount);

            bool reqReliable = (opts & SendOptions.Reliable) == SendOptions.Reliable;

            if (actualSeq < receivedSeqLoopCutoff)
            {
                // ASSUMPTION: We must be in the next loop and not have incremented seqLoopCount 
                //             yet, or out-of-order to the extent validation will drop it, or 
                //             out-of-order to the extent we cannot tell we are out-of-order!

                actualSeq += Settings.MAX_SEQ_NUMS;
            }
            else
            {
                // validate not too low
                if (actualSeq < (receivedSeqMax - Settings.OUT_OF_ORDER_TOLERANCE))
                {
                    if (reqReliable)
                        BeginSendAntiACK(actualSeq);
                    Falcon.Log(LogLevel.Warning, String.Format("Dropped packet too late from {0}.", peerName));
                    return false;
                }
            }

            if (actualSeq > receivedSeqMax)
            {
                // validate not too high
                if (actualSeq > (receivedSeqInOrderMax + Settings.OUT_OF_ORDER_TOLERANCE))
                {
                    // A packet is never too early! (Though it can be too late - they are not 
                    // wizards you know). So it must be have erroneously thought to be from the 
                    // next loop.

                    if (reqReliable)
                        BeginSendAntiACK(actualSeq);
                    Falcon.Log(LogLevel.Warning, String.Format("Dropped packet too early from {0}.", peerName));
                    return false;
                }

                // update the max seq received and the trailing cutoff point
                receivedSeqMax = actualSeq;
                receivedSeqLoopCutoff = receivedSeqMax - Settings.HALF_MAX_SEQ_NUMS;


                // update the loop count if cutoff / MAX_SEQ is greater than it
                int minLoopCount = receivedSeqLoopCutoff / Settings.MAX_SEQ_NUMS;
                if (minLoopCount > receivedSeqLoopCount)
                    receivedSeqLoopCount = minLoopCount;
            }

            // If packet required to be in order check it is after the max seq already read by
            // application, otherwise drop it - in which case if required to be reliable notify
            // peer packet was dropped so does not neccasirly have to wait until ACK_TIMEOUT to 
            // determine packet was dropped (it will wait only the minima of the two).

            if ((opts & SendOptions.InOrder) == SendOptions.InOrder)
            {
                if (actualSeq < readInOrderSeqMax)
                {
                    if (reqReliable)
                        BeginSendAntiACK(actualSeq);
                    return false;
                }
                else
                {
                    receivedSeqInOrderMax = actualSeq;
                }
            }

            // If packet requries ACK - send it!
            if (reqReliable)
            {
                BeginSendACK(actualSeq);
            }

            return true;
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

                receivedPackets = new SortedList<uint, Packet>();

                return ps;
            }
        }
    }
}
