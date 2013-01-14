using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
#if NETFX_CORE
using Windows.Networking;
using Windows.Networking.Sockets;
#else
using System.Net.Sockets;
#endif


namespace FalconUDP
{
    public partial class FalconPeer
    {

#if NETFX_CORE

        private void MessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            if(args.RemoteAddress.Type != HostNameType.Ipv4)
            {
                // TODO support other types once the rest of Falcon does
                Log(LogLevel.Warning, String.Format("Dropped Message - Remote peer: {0} unsupported type: {1}.", args.RemoteAddress.RawName, args.RemoteAddress.Type));
                return;
            }

            FalconEndPoint fep = new FalconEndPoint(args.RemoteAddress.RawName, args.RemotePort);

            RemotePeer rp;
            if (!peersByIp.TryGetValue(fep, out rp))
            {

            }
            else
            {
                // NETFX_CORE insists messages are received asynchoronously so we are going to have to 
                // lock the RemotePeer message is for as we can't have more than one message being 
                // added with all the class level sequence counters and so on being used.

                lock (rp)
                {
 
                }
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

                    RemotePeer rp;
                    if (!peersByIp.TryGetValue(ip, out rp))
                    {
                        // Could be the peer has not been added yet and is requesting to be added. 
                        // Or it could be we are asking to be added and peer is accepting!

                        if (sizeReceived >= Const.NORMAL_HEADER_SIZE && receiveBuffer[1] == Const.JOIN_PACKET_INFO)
                        {
                            string pass = null;
                            byte payloadSize = receiveBuffer[2];
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
                                rp.BeginSend(SendOptions.Reliable, PacketType.AcceptJoin, null);
                            }
                        }
                        else if (sizeReceived >= Const.NORMAL_HEADER_SIZE && (receiveBuffer[1] & (byte)PacketType.AcceptJoin) == (byte)PacketType.AcceptJoin)
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
                                rp.AddReceivedDatagram(sizeReceived, receiveBuffer);
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
#endif
    }  
}
