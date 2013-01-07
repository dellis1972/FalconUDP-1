using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace FalconUDP
{
    class RemotePeer
    {
        internal int Id;
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
        private IPEndPoint endPoint;
        private byte[] receiveBuffer;
        private byte[] sendBuffer;                              // buffer recycled for every packet sent to this peer
        private int sendPacketSize;                             // the size of the current packet to be sent in SendBuffer
        private string peerName;                                // e.g. IP address, used internally for logging
        private bool isResynching;
        private List<PacketDetail> backlog;                     // filled with raw packets when pauseSending set.
        private bool isClearingBacklog;
        private bool hasResynchedAndHasPacketsPreSynch;
        private byte[] payloadSizeBytes = new byte[2];          // buffer recycled for headers with payload size as ushort 


        internal int PacketCount;                               // number of received packets not yet retrived by application


        internal RemotePeer(int id, IPEndPoint endPoint)
        {
            this.Id = id;
            this.sendSeqCount = 0;
            this.endPoint = endPoint;
            this.receiveBuffer = new byte[Const.MAX_DATAGRAM_SIZE];
            this.sendBuffer = new byte[Const.MAX_DATAGRAM_SIZE];
            this.sendPacketSize = 0;
            this.PacketCount = 0;
            this.receivedPackets = new SortedList<uint, Packet>();
            this.sentPacketsAwaitingACK = new SortedList<int, PacketDetail>();
            this.sentPacketsAwaitingACKToRemove = new List<int>();
            this.peerName = endPoint.ToString();
            this.isResynching = false;
            this.hasResynchedAndHasPacketsPreSynch = false;
            this.backlog = new List<PacketDetail>();


            ResetSequenceCounters();
        }


        private void ResetSequenceCounters()
        {
            sendSeqCount = 0;
            sendActualSeqCount = 0;
            receivedSeqLoopCount = 0;
            receivedSeqMax = Const.HALF_MAX_SEQ_NUMS;
            receivedSeqLoopCutoff = 0;
            receivedSeqInOrderMax = 0;
            readInOrderSeqMax = 0;
        }


        internal void BeginSend(SendOptions opts, PacketType type, byte[] payload, Action ackCallback = null)
        {
            PackagePacket(opts, type, payload);
            BeginSend(sendBuffer, ackCallback);
        }


        internal void ACKTick()
        {
            lock (sentPacketsAwaitingACK) // this method is called by some arbitary thread in the ThreadPool by the Timer
            {
                foreach (KeyValuePair<int, PacketDetail> kv in sentPacketsAwaitingACK)
                {
                    kv.Value.ACKTicks++;
                    if (kv.Value.ACKTicks == Const.ACK_TIMEOUT_TICKS)
                    {
                        // re-send the packet and add to list of packets to remove
                        BeginSend(kv.Value.RawPacket, kv.Value.ACKCallback);
                        sentPacketsAwaitingACKToRemove.Add(kv.Key);
                        Falcon.Log(LogLevel.Info, "Packet re-sent as not ACKnowledged in time."); // TODO Packet summary
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


        private void BeginSendACK(int actualSequence)
        {
            BeginSend(SendOptions.None, PacketType.ACK, BitConverter.GetBytes(actualSequence));
        }


        private void BeginSendAntiACK(int actualSequence)
        {
            BeginSend(SendOptions.None, PacketType.AntiACK, BitConverter.GetBytes(actualSequence));
        }


        // all sends ultimitly sent here
        private void BeginSend(byte[] rawPacket, Action ackCallback)
        {
            if (isResynching)
            {
                backlog.Add(new PacketDetail(rawPacket, ackCallback));
            }
            else if (backlog.Count > 0 && !isClearingBacklog)
            {
                isClearingBacklog = true;
                foreach (PacketDetail pd in backlog)
                {
                    BeginSend(pd.RawPacket, pd.ACKCallback);
                }
                backlog.Clear();
                isClearingBacklog = false;
            }


            // If ACK required add detail - just before we send - to be sure we know about it when 
            // we get the reply ACK.


            PacketDetail detail = null;
            if ((rawPacket[2] & (byte)SendOptions.Reliable) == (byte)SendOptions.Reliable)
            {
                detail = new PacketDetail(rawPacket, ackCallback) { ActualSequence = sendActualSeqCount };
                sentPacketsAwaitingACK.Add(sendActualSeqCount, detail);
            }


            try
            {
                Falcon.Sender.BeginSendTo(rawPacket, 0, rawPacket.Length, SocketFlags.None, endPoint, Falcon.EndSendToCallback, null);
            }
            catch (SocketException se)
            {
                // TODO
                //sentPacketsAwaitingACK.RemoveAt(sentPacketsAwaitingACK.IndexOfValue(detail));
            }


            // Re-synch if we are going to exceed max seq and backlog subsequent sends until 
            // recipt of acknowledgment.


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


        private void PackagePacket(SendOptions opts, PacketType type, byte[] data)
        {
            HeaderPayloadSizeType hpst = HeaderPayloadSizeType.Byte;


            if (data != null && data.Length > Byte.MaxValue) // relies on short-circut if data is null
            {
                hpst = HeaderPayloadSizeType.UInt16;
                if (data.Length > Const.MAX_DATAGRAM_SIZE)
                {
                    // We could fragment the payload into seperate packets but then we would have 
                    // to send them reliably so can be assembled at the other end. FalconUDP is 
                    // designed for small packets - keep it that way!


                    throw new InvalidOperationException(String.Format("Data size: {0}, greater than max allowed: {1}.", data.Length, Const.MAX_DATAGRAM_SIZE));
                }
            }


            sendSeqCount++;         // NOTE: will be reset to 0 if 255
            sendActualSeqCount++;   // keep track of actual seq num used for ACK's


            sendBuffer[0] = sendSeqCount;
            sendBuffer[1] = (byte)((byte)hpst | (byte)opts | (byte)type);


            if (data == null)
            {
                sendBuffer[2] = 0;
                sendPacketSize = Const.NORMAL_HEADER_SIZE;
            }
            else
            {
                if (hpst == HeaderPayloadSizeType.Byte)
                {
                    sendBuffer[2] = (byte)data.Length;
                    sendPacketSize = data.Length + Const.NORMAL_HEADER_SIZE;
                    data.CopyTo(sendBuffer, Const.NORMAL_HEADER_SIZE);
                }
                else
                {
                    byte[] payloadSizeInBytes = BitConverter.GetBytes((ushort)data.Length);
                    sendBuffer[2] = payloadSizeInBytes[0];
                    sendBuffer[3] = payloadSizeInBytes[1];
                    sendPacketSize = data.Length + Const.LARGE_HEADER_SIZE;
                    data.CopyTo(sendBuffer, Const.LARGE_HEADER_SIZE);
                }
            }
        }


        internal void AddReceivedDatagram(int size, byte[] buffer)
        {
            if (size < Const.NORMAL_HEADER_SIZE)
            {
                Falcon.Log(LogLevel.Error, String.Format("Datagram dropped - size: {0}, less than min header size: {1}.", size, Const.NORMAL_HEADER_SIZE));
                return;
            }


            byte seq = buffer[0];


            // parse packet info byte
            HeaderPayloadSizeType hpst = (HeaderPayloadSizeType)(receiveBuffer[1] & Const.PAYLOAD_SIZE_TYPE_MASK);
            SendOptions opts = (SendOptions)(receiveBuffer[1] & Const.SEND_OPTS_MASK);
            PacketType type = (PacketType)(receiveBuffer[1] & Const.PACKET_TYPE_MASK);


            // parse payload size
            int payloadSize, payloadStartIndex;
            if (hpst == HeaderPayloadSizeType.Byte)
            {
                payloadSize = receiveBuffer[3];
                payloadStartIndex = Const.NORMAL_HEADER_SIZE;
            }
            else
            {
                if (size < Const.LARGE_HEADER_SIZE)
                {
                    Falcon.Log(LogLevel.Error, String.Format("Datagram with large header specified dropped - size: {0}, less than large header size: {1}.", size, Const.LARGE_HEADER_SIZE));
                    return;
                }


                Buffer.BlockCopy(buffer, 2, payloadSizeBytes, 0, 2);
                payloadSize = BitConverter.ToUInt16(payloadSizeBytes, 0);
                payloadStartIndex = Const.LARGE_HEADER_SIZE;
            }


            if (type == PacketType.ACK || type == PacketType.AntiACK)
            {
                int actualSeqACKFor = BitConverter.ToInt32(receiveBuffer, payloadStartIndex);


                lock (sentPacketsAwaitingACK)   // Falcon's ACK Timer also uses this collection
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
                    }
                    else
                    {
                        // re-send the unacknowledged packet right away 
                        BeginSend(detail.RawPacket, detail.ACKCallback);
                    }


                    // remove detail of packet that was awaiting ACK
                    sentPacketsAwaitingACK.Remove(actualSeqACKFor);
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


                                byte[] payload = new byte[receiveBuffer.Length - payloadStartIndex];
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


                                    receivedPackets.Add(acutalSeqOrdinal, new Packet(Id, acutalSeqOrdinal, payload));
                                    PacketCount++;
                                }
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


            // At this point we are tolerant of out-of-order packets to a magnitude of half the 
            // number of possible sequence numbers. Should a packet be out-of-order by more than 
            // this it will be given an erroneous actual seq num. So drop packets with a calculated
            // actual seq num outside a range (less than half the number of possible seq numbers) 
            // + or - the current actual max seq num received.


            actualSeq = seq + (Const.MAX_SEQ_NUMS * receivedSeqLoopCount);


            bool reqReliable = (opts & SendOptions.Reliable) == SendOptions.Reliable;


            if (actualSeq < receivedSeqLoopCutoff)
            {
                // ASSUMPTION: we must be in the next loop and not have incremented seqLoopCount yet.
                actualSeq += Const.MAX_SEQ_NUMS;
            }
            else
            {
                // validate not too low
                if (actualSeq < (receivedSeqMax - Const.OUT_OF_ORDER_TOLERANCE))
                {
                    if (reqReliable)
                        BeginSendAntiACK(actualSeq);


                    Falcon.Log(LogLevel.Warning, String.Format("Dropped packet too late from {0}.", peerName)); // TODO packet summary


                    return false;
                }
            }


            if (actualSeq > receivedSeqMax)
            {
                // validate not too high
                if (actualSeq > (receivedSeqInOrderMax + Const.OUT_OF_ORDER_TOLERANCE))
                {
                    // A packet is never too early! (Though it can be too late - they are not 
                    // wizards you know). So it must be have erroneously thought to be from the 
                    // next loop.


                    if (reqReliable)
                        BeginSendAntiACK(actualSeq);


                    Falcon.Log(LogLevel.Warning, String.Format("Dropped packet too early from {0}.", peerName)); // TODO packet summary


                    return false;
                }


                // update the max seq received and the trailing cutoff point
                receivedSeqMax = actualSeq;
                receivedSeqLoopCutoff = receivedSeqMax - Const.HALF_MAX_SEQ_NUMS;


                // update the loop count if cutoff / MAX_SEQ is greater than it
                int minLoopCount = receivedSeqLoopCutoff / Const.MAX_SEQ_NUMS;
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
