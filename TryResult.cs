using System;

namespace FalconUDP
{
    public delegate void TryCallback(TryResult result);

    /// <summary>
    /// Class stashes result of an operation, successful or not, so caller can consistantly
    /// proccess result.
    /// </summary>
    public class TryResult
    {
        private static TryResult successResult = new TryResult(true, null);

        private bool        success;
        private string      msg;        //} only populated on non-success
        private Exception   exception;  //}
        private object      tag;

        public bool         Success { get { return success; } }
        public string       NonSuccessMessage { get { return msg; } }
        public Exception    Code { get { return exception; } }
        public object       Tag { get { return tag; } }
        
        public static TryResult SuccessResult { get { return successResult; } }

        public TryResult(Exception ex)
            : this(false, ex.Message, ex, null)
        {
        }

        public TryResult(bool success, string msg)
            : this(success, msg, null, null)
        {
        }

        public TryResult(bool success, string msg, Exception ex, object tag)
        {
            this.success = success;
            this.msg = msg;
            this.exception = ex;
            this.tag = tag;
        }
    }
}
