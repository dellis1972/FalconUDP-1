using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FalconUDP
{
    public static partial class Falcon
    {
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

                    IPEndPoint ip = (IPEndPoint)lastRemoteEndPoint;

                    if (sizeReceived == 0)
                    {
                        // peer closed connection
                        RemovePeer(ip);
                        continue;
                    }

                    RemotePeer rp;
                    if (!peersByIp.TryGetValue(ip, out rp))
                    {
                        // Could be the peer has not been added yet and is requesting to be added. 
                        // Or it could be we are asking to be added and peer is accepting!

                        if (sizeReceived >= Settings.NORMAL_HEADER_SIZE && receiveBuffer[1] == Settings.JOIN_PACKET_INFO)
                        {
                            TryAddPeer(ip, receiveBuffer, sizeReceived, out rp);
                        }
                        else if (sizeReceived >= Settings.NORMAL_HEADER_SIZE && (receiveBuffer[1] & (byte)PacketType.AcceptJoin) == (byte)PacketType.AcceptJoin)
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
                                // create the new peer, add the Accept to send ACK, call the callback
                                rp = AddPeer(ip);
                                rp.AddReceivedDatagram(sizeReceived, receiveBuffer);
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
    }
}
