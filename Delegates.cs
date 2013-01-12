namespace FalconUDP
{
    /// <summary>
    /// TODO
    /// </summary>
    /// <param name="id"></param>
    public delegate void PeerAdded(int id);

    /// <summary>
    /// TODO
    /// </summary>
    /// <param name="id"></param>
    public delegate void PeerDropped(int id);

    /// <summary>
    /// TODO
    /// </summary>
    /// <param name="line"></param>
    public delegate void LogCallback(string line);
}
