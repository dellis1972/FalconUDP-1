using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
#if NETFX_CORE
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
#else
using System.Net.Sockets;
#endif


namespace FalconUDP
{
    public partial class FalconPeer
    {

#if NETFX_CORE

        private async void MessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                // accessing the properties of args seems to throw exceptions

                if (args.RemoteAddress.Type != HostNameType.Ipv4)
                {
                    // TODO support other types once the rest of Falcon does
                    Log(LogLevel.Warning, String.Format("Dropped Message - Remote peer: {0} unsupported type: {1}.", args.RemoteAddress.RawName, args.RemoteAddress.Type));
                    return;
                }

                if (args.RemoteAddress.RawName.StartsWith("127.") && args.RemotePort == localPortAsString)
                {
                    Log(LogLevel.Warning, "Dropped Message received from self.");
                    return;
                }

                IPv4EndPoint lastRemoteEndPoint = new IPv4EndPoint(args.RemoteAddress.RawName, args.RemotePort); // careful todo this within the lock so doesn't get re-assigned while we are using it

                DataReader dr = args.GetDataReader();
                int size = (int)dr.UnconsumedBufferLength;

                if (size == 0)
                {
                    // peer has closed 
                    TryRemovePeer(lastRemoteEndPoint);
                    return;
                }
                else if (size < Const.NORMAL_HEADER_SIZE || size > Const.MAX_DATAGRAM_SIZE)
                {
                    Log(LogLevel.Error, String.Format("Message dropped from peer: {0}, bad size: {0}", lastRemoteEndPoint, size));
                    return;
                }

                byte seq = dr.ReadByte();
                byte packetInfo = dr.ReadByte();

                // parse packet info byte
                HeaderPayloadSizeType hpst = (HeaderPayloadSizeType)(packetInfo & Const.PAYLOAD_SIZE_TYPE_MASK);
                SendOptions opts = (SendOptions)(packetInfo & Const.SEND_OPTS_MASK);
                PacketType type = (PacketType)(packetInfo & Const.PACKET_TYPE_MASK);

                // check the header makes sense
                if (!Enum.IsDefined(Const.HEADER_PAYLOAD_SIZE_TYPE_TYPE, hpst)
                    || !Enum.IsDefined(Const.SEND_OPTIONS_TYPE, opts)
                    || !Enum.IsDefined(Const.PACKET_TYPE_TYPE, type))
                {
                    Log(LogLevel.Warning, String.Format("Message dropped from peer: {0}, bad header.", lastRemoteEndPoint));
                    return;
                }

                // parse payload size
                int payloadSize;
                if (hpst == HeaderPayloadSizeType.Byte)
                {
                    payloadSize = dr.ReadByte();
                }
                else
                {
                    if (size < Const.LARGE_HEADER_SIZE)
                    {
                        Log(LogLevel.Error, String.Format("Message with large header dropped from peer: {0}, size: {1}.", lastRemoteEndPoint, size));
                        return;
                    }

                    payloadSizeBytes[0] = dr.ReadByte();
                    payloadSizeBytes[1] = dr.ReadByte();
                    payloadSize = BitConverter.ToUInt16(payloadSizeBytes, 0);
                }

                // validate payload size
                if (payloadSize != dr.UnconsumedBufferLength)
                {
                    Log(LogLevel.Error, String.Format("Message dropped from peer: {0}, payload size: {1}, not as specefied: {2}", lastRemoteEndPoint, dr.UnconsumedBufferLength, payloadSize));
                    return;
                }

                // copy the payload
                byte[] payload = null;
                if (payloadSize > 0)
                {
                    payload = new byte[payloadSize];
                    dr.ReadBytes(payload);
                }

                RemotePeer rp;
                if (!peersByIp.TryGetValue(lastRemoteEndPoint, out rp))
                {
                    // Could be the peer has not been added yet and is requesting to be added. 
                    // Or it could be we are asking to be added and peer is accepting!

                    if (type == PacketType.AddPeer)
                    {
                        string pass = null;
                        if (payloadSize > 0)
                            pass = Settings.TextEncoding.GetString(payload, 0, payloadSize);

                        if (pass != networkPass) // TODO something else?
                        {
                            // TODO send reject and reason
                            Log(LogLevel.Info, String.Format("Join request dropped from peer: {0}, bad pass.", lastRemoteEndPoint));
                        }
                        else if (peersByIp.ContainsKey(lastRemoteEndPoint))
                        {
                            // TODO send reject and reason
                            Log(LogLevel.Warning, String.Format("Join request dropped from peer: {0}, peer is already added!", lastRemoteEndPoint));
                        }
                        else
                        {
                            rp = await TryAddPeerAsync(lastRemoteEndPoint);
                            if(rp != null)
                                rp.BeginSend(SendOptions.Reliable, PacketType.AcceptJoin, null, null);
                        }
                    }
                    else if (type == PacketType.AcceptJoin)
                    {
                        AwaitingAcceptDetail detail;
                        if (!TryGetAndRemoveWaitingAcceptDetail(lastRemoteEndPoint, out detail))
                        {
                            // Possible reasons we do not have detail are: 
                            //  1) Accept is too late,
                            //  2) Accept duplicated and we have already removed it, or
                            //  3) Accept was unsolicited.

                            Log(LogLevel.Warning, String.Format("Accept dropped from peer: {0}, join request not found.", lastRemoteEndPoint));
                        }
                        else
                        {
                            // create the new peer, add the datagram to send ACK, call the callback
                            rp = await TryAddPeerAsync(lastRemoteEndPoint);
                            if (rp != null)
                            {
                                rp.AddReceivedPacket(seq, opts, type, payload);
                                TryResult tr = new TryResult(true, null, null, rp.Id);
                                detail.Callback(tr);
                            }
                        }
                    }
                    else
                    {
                        Log(LogLevel.Warning, String.Format("Message dropped from peer: {0}, peer unknown.", lastRemoteEndPoint));
                    }
                }
                else
                {
                    rp.AddReceivedPacket(seq, opts, type, payload);
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, String.Format("Exception in MessageReceived handler: {0}.", ex.Message));
            }
        }
#else
        private void Listen()
        {
            while (true)
            {
                if (stop)
                    return;

                int sizeReceived = 0;

                try
                {
                    //-------------------------------------------------------------------------
                    sizeReceived = Sock.ReceiveFrom(receiveBuffer, ref lastRemoteEndPoint);
                    //-------------------------------------------------------------------------

                    IPEndPoint ip = (IPEndPoint)lastRemoteEndPoint;

                    // Do not listen to packets sent by us to us. This only drops packets from the 
                    // same instance of Falcon (i.e. on the same port) so we can still send/recv 
                    // packets from another instance of Falcon (i.e. on different port) on the 
                    // same host.

                    if (IPAddress.IsLoopback(ip.Address) && ip.Port == localPort)
                    {
                        Log(LogLevel.Warning, "Dropped datagram received from self.");
                        continue;
                    }
                    
                    if (sizeReceived == 0)
                    {
                        // peer closed connection
                        TryRemovePeer(ip);
                        continue;
                    }
                    else if (sizeReceived < Const.NORMAL_HEADER_SIZE || sizeReceived > Const.MAX_DATAGRAM_SIZE)
                    {
                        Log(LogLevel.Error, String.Format("Datagram dropped from peer: {0}, bad size: {0}", lastRemoteEndPoint, sizeReceived));
                        return;
                    }

                    byte seq        = receiveBuffer[0];
                    byte packetInfo = receiveBuffer[1];

                    // parse packet info byte
                    HeaderPayloadSizeType hpst = (HeaderPayloadSizeType)(packetInfo & Const.PAYLOAD_SIZE_TYPE_MASK);
                    SendOptions opts = (SendOptions)(packetInfo & Const.SEND_OPTS_MASK);
                    PacketType type = (PacketType)(packetInfo & Const.PACKET_TYPE_MASK);

                    // check the header makes sense
                    if (!Enum.IsDefined(Const.HEADER_PAYLOAD_SIZE_TYPE_TYPE, hpst)
                        || !Enum.IsDefined(Const.SEND_OPTIONS_TYPE, opts)
                        || !Enum.IsDefined(Const.PACKET_TYPE_TYPE, type))
                    {
                        Log(LogLevel.Warning, String.Format("Datagram dropped from peer: {0}, bad header.", lastRemoteEndPoint));
                        return;
                    }

                    // read payload
                    int payloadSize;
                    byte[] payload = null;
                    if (hpst == HeaderPayloadSizeType.Byte)
                    {
                        payloadSize = receiveBuffer[2];

                        // validate payload size
                        if (payloadSize != (sizeReceived - Const.NORMAL_HEADER_SIZE))
                        {
                            Log(LogLevel.Error, String.Format("Datagram dropped from peer: {0}, payload size: {1}, not as specefied: {2}", lastRemoteEndPoint, (sizeReceived - Const.NORMAL_HEADER_SIZE), payloadSize));
                            return;
                        }

                        if (payloadSize > 0)
                        {
                            payload = new byte[payloadSize];
                            System.Buffer.BlockCopy(receiveBuffer, Const.NORMAL_HEADER_SIZE, payload, 0, payloadSize);
                        }
                    }
                    else
                    {
                        if (sizeReceived < Const.LARGE_HEADER_SIZE)
                        {
                            Log(LogLevel.Error, String.Format("Datagram with large header dropped from peer: {0}, size: {1}.", lastRemoteEndPoint, sizeReceived));
                            return;
                        }

                        payloadSizeBytes[0] = receiveBuffer[2];
                        payloadSizeBytes[1] = receiveBuffer[3];
                        payloadSize = BitConverter.ToUInt16(payloadSizeBytes, 0);

                        // validate payload size
                        if (payloadSize != (sizeReceived - Const.LARGE_HEADER_SIZE))
                        {
                            Log(LogLevel.Error, String.Format("Datagram dropped from peer: {0}, payload size: {1}, not as specefied: {2}", lastRemoteEndPoint, (sizeReceived - Const.LARGE_HEADER_SIZE), payloadSize));
                            return;
                        }

                        payload = new byte[payloadSize];
                        System.Buffer.BlockCopy(receiveBuffer, Const.NORMAL_HEADER_SIZE, payload, 0, payloadSize);
                    }

                    RemotePeer rp;
                    if (!peersByIp.TryGetValue(ip, out rp))
                    {
                        // Could be the peer has not been added yet and is requesting to be added. 
                        // Or it could be we are asking to be added and peer is accepting!

                        if (type == PacketType.AddPeer)
                        {
                            string pass = null;
                            if (payloadSize > 0)
                                pass = Settings.TextEncoding.GetString(receiveBuffer, Const.NORMAL_HEADER_SIZE, payloadSize);

                            if (pass != networkPass) // TODO something else?
                            {
                                // TODO send reject and reason
                                Log(LogLevel.Info, String.Format("Join request from: {0} dropped, bad pass.", ip));
                            }
                            else if (peersByIp.ContainsKey(ip))
                            {
                                // TODO send reject and reason
                                Log(LogLevel.Warning, String.Format("Cannot add peer again: {0}, peer is already added!", ip));
                            }
                            else
                            {
                                rp = AddPeer(ip);
                                rp.BeginSend(SendOptions.Reliable, PacketType.AcceptJoin, null, null);
                            }
                        }
                        else if (type == PacketType.AcceptJoin)
                        {
                            AwaitingAcceptDetail detail;
                            if (!TryGetAndRemoveWaitingAcceptDetail(ip, out detail))
                            {
                                // Possible reasons we do not have detail are: 
                                //  1) Accept is too late,
                                //  2) Accept duplicated and we have already removed it, or
                                //  3) Accept was unsolicited.

                                Log(LogLevel.Warning, String.Format("Dropped Accept Packet from unknown peer: {0}.", ip));
                            }
                            else
                            {
                                // create the new peer, add the datagram to send ACK, call the callback
                                rp = AddPeer(ip);
                                rp.AddReceivedPacket(seq, opts, type, payload);
                                TryResult tr = new TryResult(true, null, null, rp.Id);
                                detail.Callback(tr);
                            }
                        }
                        else
                        {
                            Log(LogLevel.Warning, String.Format("Datagram dropped - unknown peer: {0}.", ip));
                        }
                    }
                    else
                    {
                        rp.AddReceivedPacket(seq, opts, type, payload);
                    }
                }
                catch (SocketException se)
                {
                    // TODO http://msdn.microsoft.com/en-us/library/ms740668.aspx
                    Log(LogLevel.Error, String.Format("EndReceiveFrom() SocketException: {0}.", se.Message));
                }
            }
        }
#endif
    }  
}
