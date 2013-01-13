﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FalconUDP
{
    public partial class FalconPeer
    {
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
                    sizeReceived = receiver.ReceiveFrom(receiveBuffer, ref lastRemoteEndPoint);
                    //-------------------------------------------------------------------------

                    IPEndPoint ip = (IPEndPoint)lastRemoteEndPoint;

                    // TODO should we listen to self? Currently cannot since we have to assume the 
                    // remote hosts port is the same as the one we are listening on for the below 
                    // reason. Therefore cannot have more than one FalconPeer (in the same session)
                    // on same host cause have to listen on same port.

                    if (IPAddress.IsLoopback(ip.Address))
                    {
                        Log(LogLevel.Warning, "Dropped datagram received from loopback.");
                        continue;
                    }

                    // We know Falcon only comms with peers on the same port so assume the port is
                    // is the same. (Have to set this manually since:
                    // http://stackoverflow.com/questions/14292602/remote-endpoint-after-socket-receivefrom-has-wrong-port-number
                    // http://social.msdn.microsoft.com/Forums/en-US/netfxbcl/thread/92f6da8c-776b-413e-b27b-6f58d4069b7d).

                    ip.Port = this.port; 
                    
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
    }
}
