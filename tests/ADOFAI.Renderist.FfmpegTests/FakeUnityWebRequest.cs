using System;

// Only the UnityWebRequest surface used by the production L1 driver is modelled here.
// The production source itself is compiled into this test project.
namespace UnityEngine.Networking
{
    internal sealed class DownloadHandlerFile
    {
        public DownloadHandlerFile(string path) { Path = path; }
        public string Path { get; }
        public bool removeFileOnAbort { get; set; }
    }

    internal sealed class UnityWebRequestAsyncOperation
    {
        public bool Done { get; set; }
        public bool ThrowOnRead { get; set; }

        public bool isDone
        {
            get
            {
                if (ThrowOnRead)
                    throw new InvalidOperationException("operation state unavailable");
                return Done;
            }
        }
    }

    internal sealed class UnityWebRequest : IDisposable
    {
        internal enum Result { InProgress, Success, ConnectionError, ProtocolError }

        public const string kHttpVerbGET = "GET";
        public static UnityWebRequest Last { get; private set; }

        public UnityWebRequest(string targetUrl, string method)
        {
            url = targetUrl;
            Method = method;
            Operation = new UnityWebRequestAsyncOperation();
            Last = this;
        }

        public string Method { get; }
        public DownloadHandlerFile downloadHandler { get; set; }
        public int redirectLimit { get; set; }
        public int timeout { get; set; }
        public object certificateHandler { get; set; }
        public ulong downloadedBytes { get; set; }
        public long responseCode { get; set; }
        public string error { get; set; }
        public Result result { get; set; }
        public string url { get; set; }
        public bool isDone { get; set; }
        public string ContentLength { get; set; }
        public bool Aborted { get; private set; }
        public bool Disposed { get; private set; }
        public UnityWebRequestAsyncOperation Operation { get; }

        public UnityWebRequestAsyncOperation SendWebRequest() { return Operation; }
        public string GetResponseHeader(string name)
        {
            return string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                ? ContentLength : null;
        }

        public void Abort() { Aborted = true; isDone = true; }
        public void Dispose() { Disposed = true; }
    }
}
