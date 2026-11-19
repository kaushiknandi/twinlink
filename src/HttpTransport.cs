using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Remoting;
using System.Security.Authentication;
using System.Threading;

namespace TwinLink {
    [Serializable]
    public sealed class LinkFailure : IOException {
        public readonly bool ClosedBeforeResponse;
        public LinkFailure(string message, bool closedBeforeResponse=false) : base(message) { ClosedBeforeResponse=closedBeforeResponse; }
        private LinkFailure(SerializationInfo info, StreamingContext context) : base(info,context) {
            ClosedBeforeResponse=info.GetBoolean("ClosedBeforeResponse");
        }
        public override void GetObjectData(SerializationInfo info, StreamingContext context) {
            base.GetObjectData(info,context);
            info.AddValue("ClosedBeforeResponse",ClosedBeforeResponse);
        }
    }

    [Serializable]
    public sealed class HttpHead {
        public int Status;
        public long Length;
        public bool PartialOnly;
        public Uri Url;
        public string Range, ETag, Modified, Ranges, Type, Disposition, Encoding, RetryAfter;

        public string Validator {
            get {
                if (ETag != null && ETag.Length > 1 && ETag[0] == '"' && ETag[ETag.Length-1] == '"') return ETag;
                DateTimeOffset date;
                return DateTimeOffset.TryParseExact(Modified, "r", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,out date) ? Modified : null;
            }
        }
    }

    // The Framework HTTP connection pool and its socket-bind callback are shared per AppDomain.
    // A domain per adapter lets concurrent workers reuse connections without racing over that callback.
    public sealed class AdapterSession : MarshalByRefObject {
        IPAddress source;
        CookieContainer cookies;
        public override object InitializeLifetimeService() { return null; }
        public void Initialize(string address, CookieContainer initialCookies) {
            source = IPAddress.Parse(address);
            cookies = initialCookies ?? new CookieContainer();
            AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions",false);
            AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto",false);
        }
        public CookieContainer SnapshotCookies() { return cookies; }
        public AdapterRequest Create(bool native) {
            return native ? (AdapterRequest)new CurlRequest(source,cookies) : new FrameworkRequest(this);
        }

        internal HttpWebRequest Get(Uri url, long start, long end, string validator, int timeout) {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = null; // Each worker connects directly from its selected local interface.
            request.Method = "GET";
            request.UserAgent = "TwinLink/2.2";
            request.Accept = "*/*";
            request.CookieContainer = cookies;
            request.AllowAutoRedirect = false;
            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.BypassCache);
            request.KeepAlive = true;
            request.Timeout = timeout;
            request.ReadWriteTimeout = 30000;
            var point = request.ServicePoint;
            point.ConnectionLimit = 10;
            point.BindIPEndPointDelegate = Bind;
            if (start >= 0) {
                request.Headers[HttpRequestHeader.AcceptEncoding] = "identity";
                if (end<0) request.AddRange(start);
                else request.AddRange(start,end);
                if (validator != null) request.Headers["If-Range"] = validator;
            } else {
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }
            return request;
        }
        IPEndPoint Bind(ServicePoint point, IPEndPoint remote, int retry) {
            if (retry != 0 || remote.AddressFamily != source.AddressFamily)
                throw new SocketException((int)SocketError.AddressNotAvailable);
            return new IPEndPoint(source,0);
        }
    }

    public abstract class AdapterRequest : MarshalByRefObject {
        public override object InitializeLifetimeService() { return null; }
        public abstract HttpHead Open(Uri url, long start, long end, string validator, int timeout);
        public abstract byte[] Read();
        public abstract void Abort();
        public abstract void Close(bool consumed);
        internal static Uri Redirect(Uri current, string target) {
            Uri next;
            if (string.IsNullOrWhiteSpace(target) || !Uri.TryCreate(current,target,out next) ||
                (next.Scheme!="http" && next.Scheme!="https") || next.UserInfo.Length!=0)
                throw new InvalidDataException("The server returned an invalid download redirect.");
            if (current.Scheme=="https" && next.Scheme=="http")
                throw new InvalidDataException("The HTTPS download redirected to plain HTTP. Use the host's direct HTTPS link.");
            return next;
        }
        internal static bool IsRedirect(int code) {
            return code==301 || code==302 || code==303 || code==307 || code==308;
        }
    }

    public sealed class FrameworkRequest : AdapterRequest {
        readonly AdapterSession session;
        readonly object gate = new object();
        readonly byte[] buffer = new byte[64*1024];
        HttpWebRequest request;
        HttpWebResponse response;
        Stream body;
        bool aborted;
        public FrameworkRequest(AdapterSession session) { this.session = session; }
        public override void Abort() {
            lock (gate) {
                aborted = true;
                if (request != null) request.Abort();
            }
        }
        public override void Close(bool consumed) {
            try {
                if (!consumed) Abort();
                if (response != null) response.Close();
            } finally {
                body=null; response=null; request=null;
                // Each range has a short-lived remote object. Release it now, including its read buffer.
                RemotingServices.Disconnect(this);
            }
        }
        static Exception Explain(WebException error, Uri url, bool opening) {
            string host = url.DnsSafeHost;
            switch (error.Status) {
                case WebExceptionStatus.Timeout: return new LinkFailure("Connection to " + host + " timed out.");
                case WebExceptionStatus.NameResolutionFailure: return new LinkFailure("DNS could not resolve " + host + ".");
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure:
                    return new AuthenticationException("Windows could not establish a trusted HTTPS connection to " + host + ".");
                default:
                    var inner=error.GetBaseException() as SocketException;
                    string detail=error.Status.ToString()+(inner==null ? "" : ", "+inner.SocketErrorCode+" "+inner.NativeErrorCode);
                    bool closed=error.Status==WebExceptionStatus.ReceiveFailure || error.Status==WebExceptionStatus.ConnectionClosed ||
                        error.Status==WebExceptionStatus.KeepAliveFailure || error.Status==WebExceptionStatus.SendFailure;
                    return new LinkFailure("Connection to "+host+(opening && closed ? " closed before an HTTP response" : " failed")+
                        " ("+detail+").",opening && closed);
            }
        }
        public override HttpHead Open(Uri url, long start, long end, string validator, int timeout) {
            for (int hop=0; hop<=8; hop++) {
                HttpWebRequest next = session.Get(url,start,end,validator,timeout);
                lock (gate) {
                    if (aborted) throw new OperationCanceledException();
                    request = next;
                }
                try {
                    response = (HttpWebResponse)next.GetResponse();
                } catch (WebException e) {
                    response = e.Response as HttpWebResponse;
                    if (response == null) throw Explain(e,url,true);
                } catch (SocketException e) { throw new LinkFailure("Socket connection failed ("+e.SocketErrorCode+")."); }
                int status = (int)response.StatusCode;
                if (IsRedirect(status)) {
                    Uri location=Redirect(url,response.Headers["Location"]);
                    next.Abort();
                    response.Close(); response = null;
                    url = location;
                    continue;
                }
                body = response.GetResponseStream();
                var h = response.Headers;
                return new HttpHead { Status=status, Url=url, Length=response.ContentLength,
                    Range=h["Content-Range"], ETag=h["ETag"], Modified=h["Last-Modified"],
                    Ranges=h["Accept-Ranges"], Type=h["Content-Type"], Disposition=h["Content-Disposition"],
                    Encoding=h["Content-Encoding"], RetryAfter=h["Retry-After"] };
            }
            throw new InvalidDataException("Too many redirects. Copy the final download link from the host.");
        }
        public override byte[] Read() {
            try {
                int n = body.Read(buffer,0,buffer.Length);
                if (n == buffer.Length) return buffer;
                var data = new byte[n];
                Buffer.BlockCopy(buffer,0,data,0,n);
                return data;
            } catch (IOException) { throw new LinkFailure("The response stream was interrupted."); }
              catch (WebException e) { throw Explain(e,request.RequestUri,false); }
        }
    }

    internal sealed class HttpLane : IDisposable {
        readonly AppDomain domain;
        readonly AdapterSession session;
        readonly Action<string> note;
        int preferCurl;
        public readonly IPAddress Address;
        public HttpLane(IPAddress address, CookieContainer cookies, Action<string> note) {
            Address = address; this.note=note;
            string assembly = typeof(AdapterSession).Assembly.FullName;
            domain = AppDomain.CreateDomain("TwinLink " + address,null,
                new AppDomainSetup { ApplicationBase=Path.GetDirectoryName(typeof(AdapterSession).Assembly.Location),
                    TargetFrameworkName=".NETFramework,Version=v4.8" });
            try {
                session = (AdapterSession)domain.CreateInstanceAndUnwrap(assembly,typeof(AdapterSession).FullName);
                session.Initialize(address.ToString(),cookies);
            } catch { AppDomain.Unload(domain); throw; }
        }
        public CookieContainer Cookies() { return session.SnapshotCookies(); }
        public Fetch Open(Uri url, long start, long end, string validator, CancellationToken token, int timeout) {
            bool native=Volatile.Read(ref preferCurl)!=0;
            try { return OpenOnce(native,url,start,end,validator,token,timeout); }
            catch (LinkFailure e) {
                if (native || !e.ClosedBeforeResponse || !File.Exists(CurlRequest.Executable)) throw;
                token.ThrowIfCancellationRequested();
                if (Interlocked.Exchange(ref preferCurl,1)==0)
                    note(e.Message+" Trying Windows curl on the same adapter ("+Address+").");
                return OpenOnce(true,url,start,end,validator,token,timeout);
            }
        }
        Fetch OpenOnce(bool native, Uri url, long start, long end, string validator, CancellationToken token, int timeout) {
            token.ThrowIfCancellationRequested();
            var call = session.Create(native);
            var result = new Fetch(call,token.Register(call.Abort));
            try {
                result.Head = call.Open(url,start,end,validator,timeout);
                token.ThrowIfCancellationRequested();
                return result;
            } catch {
                result.Dispose();
                token.ThrowIfCancellationRequested();
                throw;
            }
        }
        public void Dispose() { AppDomain.Unload(domain); }
    }

    internal sealed class Fetch : IDisposable {
        readonly AdapterRequest call;
        CancellationTokenRegistration registration;
        bool consumed, closed;
        public HttpHead Head;
        public Fetch(AdapterRequest call, CancellationTokenRegistration registration) {
            this.call = call; this.registration = registration;
        }
        public byte[] Read(CancellationToken token) {
            token.ThrowIfCancellationRequested();
            try {
                byte[] data = call.Read();
                consumed = data.Length == 0;
                return data;
            } catch { token.ThrowIfCancellationRequested(); throw; }
        }
        public void Dispose() {
            if (closed) return;
            closed = true;
            registration.Dispose();
            call.Close(consumed);
        }
    }
}
