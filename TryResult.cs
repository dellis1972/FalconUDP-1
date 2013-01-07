namespace FalconUDP
{
    public delegate void TryCallback(TryResult result);

    public class TryResult
    {
        private bool success;
        private string msg;
        private object tag;
        private static TryResult successResult = new TryResult(true, null);

        public bool Success { get { return success; } }
        public string NonSuccessMessage { get { return msg; } }
        public object Tag { get { return tag; } }


        public static TryResult SuccessResult { get { return successResult; } }


        public TryResult(bool success, string msg)
            : this(success, msg, null)
        {
        }

        public TryResult(bool success, string msg, object tag)
        {
            this.success = success;
            this.msg = msg;
            this.tag = tag;
        }
    }
}
