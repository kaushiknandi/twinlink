using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using TwinLink;

// Test the actual .NET HTTP stack and observe local addresses on the server side.
internal sealed class RangeServer : IDisposable {
    readonly TcpListener listener = new TcpListener(IPAddress.Loopback,0);
    internal readonly byte[] Data;
    internal readonly byte[] NewData;
    readonly object gate=new object();
    readonly Dictionary<string,int> full=new Dictionary<string,int>();
    internal readonly ConcurrentDictionary<string,int> sources=new ConcurrentDictionary<string,int>();
    volatile bool closed;
    internal Uri Link(string path) { return new Uri("http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/"+path); }
    internal int FullCount(string key) { lock(gate) { int n;return full.TryGetValue(key,out n)?n:0; } }
    public RangeServer(bool benchmark=false) {
        Data=new byte[(benchmark ? 128 : 4)*1024*1024+113];
        new Random(9).NextBytes(Data); NewData=(byte[])Data.Clone(); Array.Reverse(NewData);
        listener.Start();
        var thread=new Thread(delegate() {
            while(!closed) {
                try { var client=listener.AcceptTcpClient(); ThreadPool.QueueUserWorkItem(_=>Serve(client)); }
                catch(SocketException) { if (!closed) throw; }
            }
        });
        thread.IsBackground=true;thread.Start();
    }
    static void Write(Stream stream,string text) {
        byte[] b=Encoding.ASCII.GetBytes(text);stream.Write(b,0,b.Length);
    }
    void Serve(TcpClient client) {
        using(client) try {
            client.NoDelay=true;
            string ip=((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
            var stream=client.GetStream();
            using(var reader=new StreamReader(stream,Encoding.ASCII,false,4096,true))
            while(!closed) {
                string first=reader.ReadLine();
                if(string.IsNullOrEmpty(first))return;
                string key=first.Split(' ')[1].TrimStart('/');
                var h=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                string line;
                while(!string.IsNullOrEmpty(line=reader.ReadLine())) { int ix=line.IndexOf(':');if(ix>0)h[line.Substring(0,ix)]=line.Substring(ix+1).Trim(); }
                bool compatibility=key.StartsWith("reset") || key=="native-cookie";
                if(compatibility) {
                    // Identify the two real clients in this fixture by the normal request's compression negotiation.
                    // The initial framework request asks for gzip; curl requests the identity representation.
                    string enc;
                    if(key=="reset-all" || (h.TryGetValue("Accept-Encoding",out enc) && enc.IndexOf("gzip",StringComparison.OrdinalIgnoreCase)>=0)) {
                        client.Client.LingerState=new LingerOption(true,0);
                        return;
                    }
                    if(key=="reset-cancel") { Thread.Sleep(3000);return; }
                    if(key=="reset-denied") { Write(stream,"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n");continue; }
                    if(key=="reset-redirect") {
                        Write(stream,"HTTP/1.1 302 Found\r\nLocation: /native-cookie\r\nSet-Cookie: link=ok; Path=/\r\nContent-Length: 0\r\n\r\n");continue;
                    }
                    if(key=="reset-hints") Write(stream,"HTTP/1.1 103 Early Hints\r\nLink: </data>\r\n\r\n");
                }
                string raw, cookie;
                bool ranged=h.TryGetValue("Range",out raw);
                int number;
                lock(gate) { full.TryGetValue(key,out number);if(!ranged)full[key]=++number; }
                if(key=="redirect") {
                    Write(stream,"HTTP/1.1 302 Found\r\nLocation: /cookie\r\nSet-Cookie: link=ok; Path=/\r\nContent-Length: 0\r\n\r\n"); continue;
                }
                if(key=="timeout" && ip=="127.0.0.1") {Thread.Sleep(13000);return;}
                if(key=="busy" && !ranged && number==1) {
                    Write(stream,"HTTP/1.1 503 Unavailable\r\nRetry-After: 0\r\nContent-Length: 0\r\n\r\n");continue;
                }
                if(key=="login") { Write(stream,"HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n");continue; }
                if(key=="missing") { Write(stream,"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");continue; }
                if(key=="unsatisfiable" && ranged) { Write(stream,"HTTP/1.1 416 Invalid Range\r\nContent-Length: 0\r\n\r\n");continue; }
                if(key=="openEnded" && !ranged) { Write(stream,"HTTP/1.1 416 Range Required\r\nContent-Length: 0\r\n\r\n");continue; }
                if(key=="denied" || (key=="range403" && ranged) || (key=="second403" && ranged && ip=="127.0.0.2") ||
                    ((key=="cookie"||key=="native-cookie") && (!h.TryGetValue("Cookie",out cookie)||!cookie.Contains("link=ok")))) {
                    Write(stream,"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n");continue;
                }
                if(key=="rate") {Write(stream,"HTTP/1.1 429 Too Many Requests\r\nRetry-After: 120\r\nContent-Length: 0\r\n\r\n");continue;}
                byte[] bytes=key=="empty"?new byte[0]:key=="one"?new byte[]{42}:key=="changed" && number>1?NewData:Data;
                int start=0,length=bytes.Length;
                string extra="", status="200 OK";
                bool chunked=key=="chunked"||key=="unknown"||key=="gzip"||key=="reset-chunked";
                bool noTag=key=="noversion"||key=="unknown"||key=="gzip"||key=="truncated"||key=="broken"||key=="eof"||(key=="rangeTag"&&!ranged);
                bool close=key=="eof"||key=="broken"||(key=="truncated" && number==1);
                bool closeFraming=key=="eof";
                bool useRange=ranged && key!="ignore" && key!="none";
                string etag=(key=="changed" && number>1)?"\"v2\"":"\"v1\"";
                if(!noTag) extra+="ETag: "+etag+"\r\n";
                if(key=="rangeTag")extra+="Accept-Ranges: bytes\r\n";
                if(key=="none"||key=="reset-stream")extra+="Accept-Ranges: none\r\n";
                if(key=="prefix" && !ranged) {
                    length=4096; status="206 Partial Content";
                    extra+="Content-Range: bytes 0-4095/"+bytes.Length+"\r\n";
                }
                if(useRange) {
                    var m=Regex.Match(raw,@"bytes=(\d+)-(\d*)");
                    start=int.Parse(m.Groups[1].Value);
                    int end=m.Groups[2].Value.Length==0?bytes.Length-1:int.Parse(m.Groups[2].Value);
                    length=end-start+1;
                    status="206 Partial Content";
                    if(key=="changed" && end>0) {extra="ETag: \"v2\"\r\n";}
                    extra+="Content-Range: bytes "+(key=="bad"&&end>0?start+1:start)+"-"+end+"/"+bytes.Length+"\r\n";
                    if(end>0) {
                        sources.AddOrUpdate(key+" "+ip,1,(k,n)=>n+1);
                        if(key=="benchmark")Thread.Sleep(ip=="127.0.0.2" ? 120 : 40);
                        else if(key=="late"||key=="reset-late")Thread.Sleep(450);
                        else if(key=="cancel")Thread.Sleep(1000);
                        else Thread.Sleep(35);
                    }
                }
                if(key=="gzip") {
                    using(var packed=new MemoryStream()) {
                        using(var gzip=new GZipStream(packed,CompressionMode.Compress,true))gzip.Write(bytes,0,bytes.Length);
                        bytes=packed.ToArray(); length=bytes.Length;
                    }
                    extra+="Content-Encoding: gzip\r\n";
                }
                string type=key=="page"?"text/html":key=="playlist"?"application/vnd.apple.mpegurl":"application/octet-stream";
                Write(stream,"HTTP/1.1 "+status+"\r\nContent-Type: "+type+"\r\n"+extra+
                    (chunked?"Transfer-Encoding: chunked\r\n":closeFraming?"":"Content-Length: "+length+"\r\n")+
                    (close?"Connection: close\r\n":"")+"\r\n");
                if(key=="broken" || (key=="truncated" && number==1)) {stream.Write(bytes,start,Math.Min(10000,length)); return;}
                if(key=="dropUSB" && useRange && length>1 && ip=="127.0.0.2") {
                    stream.Write(bytes,start,Math.Min(10000,length));return;
                }
                int endPos=start+length;
                var pacing=System.Diagnostics.Stopwatch.StartNew();
                for(int pos=start;pos<endPos;) {
                    int n=Math.Min(32768,endPos-pos);
                    if(chunked)Write(stream,n.ToString("x",CultureInfo.InvariantCulture)+";test=ok\r\n");
                    stream.Write(bytes,pos,n);
                    if(chunked)Write(stream,"\r\n");
                    pos+=n;
                    if(key=="benchmark" && useRange) {
                        long due=(pos-start)*1000L/(ip=="127.0.0.2" ? 1024*1024 : 4*1024*1024);
                        int wait=(int)(due-pacing.ElapsedMilliseconds);
                        if(wait>0)Thread.Sleep(wait);
                    }
                }
                if(chunked)Write(stream,"0\r\nX-Check: ok\r\n\r\n");
                if(close)return;
            }
        } catch(IOException) {} catch(SocketException) {} catch(ObjectDisposedException) {}
    }
    public void Dispose() {closed=true;listener.Stop();}
}

internal static class Tests {
    static readonly IPAddress primary=IPAddress.Loopback, usb=IPAddress.Parse("127.0.0.2");
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
    static void Main(string[] args) {
        // Local test traffic stays on loopback even on developer PCs with a corporate proxy.
        Environment.SetEnvironmentVariable("NO_PROXY","127.0.0.1,127.0.0.2,localhost");
        ThreadPool.SetMinThreads(40,40);
        string dir=Path.Combine(Path.GetTempPath(),"TwinLink-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        bool benchmark=args.Contains("--benchmark");
        using(var server=new RangeServer(benchmark)) try {
            foreach(string key in benchmark ? new[]{"benchmark"} : new[]{"reset-wifi","reset-stream","reset-chunked","reset-redirect","reset-hints","reset-late","reset-denied","reset-cancel","reset-all","parallel","late","ignore","range403","second403","chunked","rangeTag","noversion","none","unknown","eof","gzip","redirect","changed","bad","truncated","broken","empty","one","page","playlist","denied","login","missing","unsatisfiable","prefix","openEnded","dropUSB","rate","cancel","timeout","busy","exists"}) {
                string file=Path.Combine(dir,"test.mp4"), lastNote="";
                bool nativeUsed=false, autoWaiting=key.StartsWith("reset") && key!="reset-late";
                if(key=="exists")File.WriteAllText(file,"keep me");
                var elapsed=System.Diagnostics.Stopwatch.StartNew();
                long left=0,right=0; bool unknown=false;
                using(var c=new CancellationTokenSource(TimeSpan.FromSeconds(45))) {
                    try {
                        Transfer.Download(server.Link(key),primary,
                            ()=>autoWaiting || ((key=="late" || key=="reset-late") && Interlocked.Read(ref left)<=1) ? null:usb,
                            h=>file,(a,b,total)=>{Interlocked.Exchange(ref left,a);Interlocked.Exchange(ref right,b);
                                if(total<0)unknown=true; if(key=="cancel" && a+b>1)c.Cancel();},c.Token,
                            (lane,n)=>{},n=>{},m=>{lastNote=m;if(m.Contains("Trying Windows curl")) { nativeUsed=true;if(key=="reset-cancel")c.CancelAfter(150); }});
                        Check(!new[]{"page","playlist","denied","login","missing","rate","cancel","broken","exists","reset-denied","reset-cancel","reset-all"}.Contains(key),"Expected a clean failure");
                        byte[] expected=key=="changed"?server.NewData:key=="empty"?new byte[0]:key=="one"?new byte[]{42}:server.Data;
                        Check(File.ReadAllBytes(file).SequenceEqual(expected),"File contents differ");
                        Check(left+right==expected.Length,"Final counters differ");
                        if(key=="parallel"||key=="late"||key=="chunked"||key=="redirect"||key=="rangeTag") {
                            string k=key=="redirect"?"cookie":key;
                            Check(left>1 && right>0,"Missing network contribution");
                            Check(server.sources.ContainsKey(k+" 127.0.0.1") && server.sources.ContainsKey(k+" 127.0.0.2"),
                                "Server saw only one source address");
                        }
                        if(new[]{"ignore","range403","unsatisfiable","noversion","none","unknown","eof","gzip"}.Contains(key))
                            Check(right==0 && server.FullCount(key)==1,"Single-stream fallback repeated the ordinary GET");
                        if(key=="unknown")Check(unknown,"Unknown-size progress missing");
                        if(key=="timeout")Check(right>0,"Primary timeout did not switch adapters");
                        if(benchmark) {
                            Check(left>0 && right>0,"Expected both benchmark lanes to contribute");
                            Console.WriteLine("Benchmark: {0} ms, {1} Wi-Fi requests, {2} USB requests", elapsed.ElapsedMilliseconds, server.sources[key+" 127.0.0.1"], server.sources[key+" 127.0.0.2"]);
                        }
                        if(autoWaiting)Check(left==expected.Length && right==0,"Optional USB received credit during Wi-Fi-only download");
                        if(key=="reset-late")Check(left>1 && right>0 && server.sources.ContainsKey(key+" 127.0.0.1") && server.sources.ContainsKey(key+" 127.0.0.2"),"Second connection did not join the recovered download");
                        if(key=="second403")Check(left==expected.Length && right==0,"A refused secondary was credited");
                        if(key=="dropUSB")Check(left==expected.Length && right==0 && server.sources.ContainsKey("dropUSB 127.0.0.2"),"Interrupted USB range was not recovered");
                        File.Delete(file);
                    } catch(OperationCanceledException) { if(key!="cancel" && key!="reset-cancel")throw; }
                      catch(IOException e) {
                        if(key=="page"||key=="playlist")Check(e.Message.Contains("webpage"),e.Message);
                        else if(key=="denied")Check(e.Message.Contains("403") && server.FullCount(key)==1,e.Message);
                        else if(key=="reset-denied")Check(e.Message.Contains("403"),e.Message);
                        else if(key=="reset-all")Check(e.Message.Contains("curl"),e.Message);
                        else if(key=="login")Check(e.Message.Contains("401") && server.FullCount(key)==1,e.Message);
                        else if(key=="missing")Check(e.Message.Contains("404") && server.FullCount(key)==1,e.Message);
                        else if(key=="exists") {Check(File.ReadAllText(file)=="keep me","Existing file was overwritten"); File.Delete(file);}
                        else if(key=="rate")Check(e.Message.Contains("429") && server.FullCount(key)==1,e.Message);
                        else if(key=="broken")Check(server.FullCount(key)==2,"Unbounded retry or missing retry");
                        else throw;
                    }
                }
                if(key.StartsWith("reset"))Check(nativeUsed,"Expected recovery through Windows curl");
                Check(Directory.GetFileSystemEntries(dir).Length==0,"A partial file or scratch folder survived: "+key);
                Console.WriteLine("PASS "+key+"  "+left+" / "+right+" bytes  "+lastNote);
            }
        } finally {Directory.Delete(dir,true);}
    }
}
