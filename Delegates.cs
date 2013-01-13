namespace FalconUDP
{
    /// <summary>
    /// Raised when peer joins, can now send/receive to/from peer.
    /// </summary>
    /// <param name="id">
    /// id of peer added</param>
    public delegate void PeerAdded(int id);

    /// <summary>
    /// Raised when peer who was joined is dropped, hereon cannot send/receive to/from peer.
    /// </summary>
    /// <param name="id">
    /// id or peer dropped</param>
    public delegate void PeerDropped(int id);

    /// <summary>
    /// Raised when Pong is received from peer in reply to Ping we sent.
    /// </summary>
    /// <param name="id">
    /// id of peer that send the Pong</param>
    public delegate void PongReceived(int id);

    /// <summary>
    /// Callback to invoke when Falcon logs a line.
    /// </summary>
    /// <param name="line">
    /// Line to be logged.</param>
    public delegate void LogCallback(string line);
}
