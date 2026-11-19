using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TwinLink {
    internal sealed class Transfer : IDisposable {
        const int ConnectionsPerLane = 4;
        const int ConnectTimeout = 12000;
        readonly Uri url;
        string destination;
        readonly Func<HttpHead,string> chooseFile;
        readonly IPAddress primary;
        readonly Func<IPAddress> secondary;
        readonly Action<long,long,long> progress;
        readonly Action<int,int> received;
        readonly Action<long> discarded;
        readonly Action<string> note;
        readonly CancellationToken cancel;
        readonly HttpLane[] lanes = new HttpLane[2];
        readonly bool[] failedAtStart = new bool[2];
        readonly object discovery = new object();
        CookieContainer cookies;
        DateTime nextScan;
        long usefulBytes;

        Transfer(Uri url, IPAddress primary, Func<IPAddress> secondary, Func<HttpHead,string> chooseFile,
                Action<long,long,long> progress, CancellationToken cancel, Action<int,int> received,
                Action<long> discarded, Action<string> note) {
            this.url=url; this.primary=primary; this.secondary=secondary; this.chooseFile=chooseFile;
            this.progress=progress; this.cancel=cancel; this.received=received; this.discarded=discarded; this.note=note;
        }
        internal static void Download(Uri url, IPAddress primary, Func<IPAddress> secondary, Func<HttpHead,string> chooseFile,
                Action<long,long,long> progress, CancellationToken cancel, Action<int,int> received,
                Action<long> discarded, Action<string> note) {
            using (var job=new Transfer(url,primary,secondary,chooseFile,progress,cancel,received,discarded,note))
                job.Run();
        }
        public void Dispose() {
            foreach (var lane in lanes) if (lane != null) lane.Dispose();
        }
        HttpLane Lane(int index) {
            lock (discovery) {
                if (lanes[index] != null) return lanes[index];
                IPAddress ip;
                if (index == 0) ip=primary;
                else {
                    if (secondary==null) return null;
                    if (DateTime.UtcNow < nextScan) return null;
                    nextScan=DateTime.UtcNow.AddMilliseconds(650);
                    ip=secondary();
                    if (ip == null || ip.Equals(primary)) return null;
                }
                return lanes[index]=new HttpLane(ip,cookies,note);
            }
        }

        static string HttpError(HttpHead head) {
            string why;
            switch (head.Status) {
                case 401: why="Sign-in is required. Use the site's authorized download link or its signed-in browser download."; break;
                case 403: why="The host denied this download. Obtain a fresh authorized file link. A browser-only download may need your signed-in session."; break;
                case 404: case 410: why="This file link is missing or expired. Copy a fresh download link."; break;
                case 429: why="The host is limiting requests. Try again later."; break;
                default: why="The host could not serve this download."; break;
            }
            string later=string.IsNullOrWhiteSpace(head.RetryAfter) ? "" : " Retry-After: " + head.RetryAfter + ".";
            return "HTTP " + head.Status + " from " + head.Url.DnsSafeHost + ". " + why + later;
        }
        static bool TransientStatus(int code) {
            return code==408 || code==500 || code==502 || code==503 || code==504;
        }
        static TimeSpan RetryDelay(string value) {
            if (string.IsNullOrWhiteSpace(value)) return TimeSpan.FromMilliseconds(600);
            int seconds;
            if (int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out seconds))
                return TimeSpan.FromSeconds(seconds);
            DateTimeOffset when;
            if (DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out when))
                return when>DateTimeOffset.UtcNow ? when-DateTimeOffset.UtcNow : TimeSpan.Zero;
            return TimeSpan.MaxValue;
        }
        static bool IsPage(HttpHead h, string destination) {
            string type=(h.Type ?? "").Split(';')[0].Trim().ToLowerInvariant();
            string ext=Path.GetExtension(destination).ToLowerInvariant();
            if (type == "text/html" || type == "application/xhtml+xml")
                return ext != ".html" && ext != ".htm" && ext != ".xhtml";
            if (type == "application/vnd.apple.mpegurl" || type == "application/x-mpegurl" ||
                type == "audio/mpegurl" || type == "application/dash+xml")
                return ext != ".m3u8" && ext != ".m3u" && ext != ".mpd";
            return false;
        }

        // The ordinary GET stays open until a separate, version-checked range actually succeeds.
        // One-use links and servers that refuse Range can therefore finish with the original response.
        Fetch OpenFile(int preferred, out int laneIndex, bool openEnded=false) {
            int index=preferred;
            string last="";
            for (int attempt=0; ; attempt++) {
                cancel.ThrowIfCancellationRequested();
                HttpLane lane=Lane(index);
                if (lane == null) { index=0; lane=Lane(0); }
                Fetch response=null;
                try {
                    response=lane.Open(url,openEnded?0:-1,-1,null,cancel,ConnectTimeout);
                    var h=response.Head;
                    if (!openEnded && h.Status==416) {
                        response.Dispose();
                        response=lane.Open(url,0,-1,null,cancel,ConnectTimeout);
                        h=response.Head;
                    }
                    if (attempt==0 && TransientStatus(h.Status)) {
                        TimeSpan delay=RetryDelay(h.RetryAfter);
                        if (delay<=TimeSpan.FromSeconds(15)) {
                            response.Dispose(); response=null;
                            note("The host returned HTTP "+h.Status+". Retrying the download once.");
                            cancel.WaitHandle.WaitOne(delay>TimeSpan.Zero ? delay : TimeSpan.Zero);
                            continue;
                        }
                    }
                    if (h.Status != 200) {
                        if (h.Status != 206) throw new IOException(HttpError(h));
                        long start,end,total;
                        if (!ReadRange(h.Range,out start,out end,out total) || start!=0)
                            throw new IOException("This URL starts partway through a file. Use the host's full-file download link.");
                        if (h.Length>=0 && h.Length!=end+1) throw new IOException("The server gave conflicting file sizes.");
                        h.PartialOnly=end!=total-1;
                        h.Length=total;
                    }
                    if (destination != null && IsPage(h,destination))
                        throw new IOException("The link returned a webpage or streaming playlist. For a movie, copy its direct media download link. To save the page or playlist itself, choose its own extension.");
                    if (!Identity(h.Encoding)) throw new IOException("The server used an unsupported content encoding: " + h.Encoding + ".");
                    cookies=lane.Cookies();
                    failedAtStart[index]=false;
                    laneIndex=index;
                    return response;
                } catch (LinkFailure e) {
                    if (response != null) response.Dispose();
                    string message=lane.Address + ": " + e.Message;
                    if (attempt != 0)
                        throw new IOException((last.Length==0 || last==message ? "" : last+" ")+message+
                            " The download could not start. Try this link in your browser.");
                    last=message;
                    int other=1-index;
                    if (Lane(other) != null) { failedAtStart[index]=true; index=other; }
                    note(message + " Retrying on " + Lane(index).Address + ".");
                    cancel.WaitHandle.WaitOne(300);
                } catch {
                    if (response != null) response.Dispose();
                    throw;
                }
            }
        }

        static readonly Regex ByteRange=new Regex(@"^bytes\s+([0-9]+)-([0-9]+)/([0-9]+)$",RegexOptions.IgnoreCase);
        static bool ReadRange(string header,out long start,out long end,out long total) {
            start=end=total=0;
            var m=ByteRange.Match(header ?? "");
            return m.Success && long.TryParse(m.Groups[1].Value,out start) && long.TryParse(m.Groups[2].Value,out end)
                && long.TryParse(m.Groups[3].Value,out total) && start<=end && end<total;
        }
        static bool Identity(string encoding) {
            return string.IsNullOrWhiteSpace(encoding) || encoding.Equals("identity",StringComparison.OrdinalIgnoreCase);
        }
        class RangeFailure : IOException { public RangeFailure(string reason) : base(reason) {} }
        sealed class LinkRefused : RangeFailure { public LinkRefused(string reason) : base(reason) {} }

        static long VerifyRange(HttpHead h,HttpHead original,long offset,long end,long expectedTotal) {
            if (h.Status==429 || (h.Status==503 && h.RetryAfter!=null)) throw new ServerBusy(h);
            if (h.Status==401 || h.Status==403) throw new LinkRefused("The host refused ranged access on this connection (HTTP " + h.Status + ").");
            if (h.Status!=206) throw new RangeFailure("The host returned HTTP " + h.Status + " for a range request.");
            long a,b,total;
            if (!ReadRange(h.Range,out a,out b,out total) || a!=offset || b!=end ||
                (expectedTotal>=0 && total!=expectedTotal) || (h.Length>=0 && h.Length!=end-offset+1) || !Identity(h.Encoding))
                throw new RangeFailure("The host returned inconsistent range boundaries, size or encoding.");
            if ((original.ETag != null && h.ETag != null && original.ETag != h.ETag) ||
                (original.Modified != null && h.Modified != null && original.Modified != h.Modified))
                throw new RangeFailure("The file version changed between requests.");
            return total;
        }
        sealed class ServerBusy : IOException {
            public readonly HttpHead Head;
            public ServerBusy(HttpHead head):base(HttpError(head)) {Head=head;}
        }
        string CheckRanges(HttpHead initial,int baseLane,out long total,out byte firstByte) {
            total=initial.Length; firstByte=0;
            if (total==0 || total==1) return "The file fits in one request.";
            if (string.Equals(initial.Ranges,"none",StringComparison.OrdinalIgnoreCase)) return "The host disabled byte ranges.";
            if (initial.Validator==null && !string.Equals(initial.Ranges,"bytes",StringComparison.OrdinalIgnoreCase))
                return "The host supplied no file version for safely joining ranges.";
            try {
                using (var test=Lane(baseLane).Open(initial.Url,0,0,initial.Validator,cancel,5000)) {
                    long size=VerifyRange(test.Head,initial,0,0,total);
                    if (initial.Validator==null) {
                        if (test.Head.Validator==null) return "The host supplied no file version for safely joining ranges.";
                        initial.ETag=test.Head.ETag; initial.Modified=test.Head.Modified;
                    }
                    var data=test.Read(cancel);
                    if (data.Length!=1 || test.Read(cancel).Length!=0) return "The range test returned an unexpected body.";
                    total=size; firstByte=data[0];
                    return null;
                }
            } catch (RangeFailure e) { return e.Message; }
              catch (ServerBusy e) { return e.Message; }
              catch (LinkFailure e) { return e.Message; }
              catch (InvalidDataException e) { return e.Message; }
        }
        void Meter(int lane,int n) {
            Interlocked.Add(ref usefulBytes,n);
            received(lane,n);
        }
        void Forget(long bytes) {
            if (bytes<=0) return;
            Interlocked.Add(ref usefulBytes,-bytes);
            discarded(bytes);
        }
        void Reset() {
            long old=Interlocked.Exchange(ref usefulBytes,0);
            if (old>0) discarded(old);
            progress(0,0,-1);
        }

        void Run() {
            cancel.ThrowIfCancellationRequested();
            string work=null;
            int baseLane;
            using (var initial=OpenFile(0,out baseLane))
            try {
                var info=initial.Head;
                destination=chooseFile(info);
                if (destination==null) throw new OperationCanceledException();
                if (IsPage(info,destination))
                    throw new IOException("This link returned a webpage or streaming playlist. Use the site's file download button for the media, or choose the document's own extension to save it.");
                if (File.Exists(destination)) throw new IOException("Choose a new filename. A file already exists at that location.");
                work=Path.Combine(Path.GetDirectoryName(destination),".twinlink-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(work);
                string partial=Path.Combine(work,"download.partial");
                note("Checking whether the file can be split across connections.");
                long total; byte firstByte;
                string reason;
                reason=CheckRanges(info,baseLane,out total,out firstByte);
                if (reason!=null) {
                    note("Single connection on " + Lane(baseLane).Address + ". " + reason);
                    StreamFile(initial,baseLane,partial);
                } else {
                    initial.Dispose();
                    info.Length=total;
                    try {
                        note(failedAtStart[1-baseLane] ? "Downloading ranges on " + Lane(baseLane).Address + ". The other connection was unavailable." :
                            "Byte ranges enabled. A second adapter can join while ranges remain.");
                        Segments(info,baseLane,firstByte,partial);
                    } catch (IOException e) {
                        if (!(e is RangeFailure) && !(e is ServerBusy)) throw;
                        cancel.ThrowIfCancellationRequested();
                        var busy=e as ServerBusy;
                        if (busy!=null) {
                            var delay=RetryDelay(busy.Head.RetryAfter);
                            if (delay>TimeSpan.FromSeconds(15)) throw;
                            note("The host limited parallel requests. Waiting before restarting on one connection.");
                            cancel.WaitHandle.WaitOne(delay>TimeSpan.Zero ? delay : TimeSpan.Zero);
                            cancel.ThrowIfCancellationRequested();
                        }
                        note(e.Message + " Restarting as a single download.");
                        Reset();
                        var full=OpenFile(baseLane,out baseLane);
                        StreamFile(full,baseLane,partial);
                    }
                }
                cancel.ThrowIfCancellationRequested();
                File.Move(partial,destination);
            } finally {
                try { if (work!=null && Directory.Exists(work)) Directory.Delete(work,true); } catch (IOException) {}
            }
        }

        void StreamFile(Fetch first,int lane,string partial) {
            Fetch current=first;
            for (int attempt=0;;attempt++) {
                try {
                    if (current.Head.PartialOnly) {
                        current.Dispose();
                        current=OpenFile(lane,out lane,true);
                        if (current.Head.PartialOnly) {
                            current.Dispose();
                            throw new IOException("The host only provided a fragment and could not serve verifiable ranges. Get the full-file download link.");
                        }
                    }
                    using (current)
                    using (var output=new FileStream(partial,FileMode.Create,FileAccess.Write,FileShare.None)) {
                        long size=current.Head.Length, count=0;
                        progress(0,0,size);
                        while (true) {
                            var block=current.Read(cancel);
                            if (block.Length==0) break;
                            if (size>=0 && block.Length>size-count) throw new LinkFailure("The server sent more data than announced.");
                            output.Write(block,0,block.Length);
                            Meter(lane,block.Length);
                            count+=block.Length;
                            progress(lane==0?count:0,lane==1?count:0,size);
                        }
                        if (size>=0 && count!=size) throw new LinkFailure("The server closed before the complete file arrived.");
                        progress(lane==0?count:0,lane==1?count:0,count);
                    }
                    return;
                } catch (LinkFailure e) {
                    cancel.ThrowIfCancellationRequested();
                    if (attempt!=0) throw new IOException("The full-file transfer failed twice. " + e.Message);
                    Reset();
                    int other=1-lane;
                    int preferred=Lane(other)==null ? lane : other;
                    note("Single connection. The stream was interrupted. Restarting from byte zero on " + Lane(preferred).Address + ".");
                    current=OpenFile(preferred,out lane);
                }
            }
        }

        sealed class Piece {
            public long Offset, Length;
            public int[] Failures=new int[2];
        }
        void Segments(HttpHead info,int baseLane,byte firstByte,string partial) {
            long total=info.Length;
            long initialSize=Math.Max(65536L,Math.Min(1048576L,1+(total-1)/16));
            using (var file=new FileStream(partial,FileMode.Create,FileAccess.Write,FileShare.ReadWrite)) {
                file.SetLength(total);
                file.WriteByte(firstByte);
            }
            Meter(baseLane,1);
            long[] counts=new long[2]; counts[baseLane]=1;
            progress(counts[0],counts[1],total);
            if (total==1) return;
            long next=1;
            int inflight=0;
            object gate=new object();
            var pending=new Queue<Piece>();
            bool[] disabled=(bool[])failedAtStart.Clone();
            bool bothReported=false;
            using (var stop=CancellationTokenSource.CreateLinkedTokenSource(cancel)) {
                Action<int> worker=delegate(int i) {
                    // Each worker measures its own request rate, including setup and server latency.
                    long targetSize=initialSize;
                    try {
                        using (var file=new FileStream(partial,FileMode.Open,FileAccess.Write,FileShare.ReadWrite)) {
                            while (true) {
                                stop.Token.ThrowIfCancellationRequested();
                                lock (gate) {
                                    if (disabled[i] || (next==total && pending.Count==0 && inflight==0)) return;
                                }
                                HttpLane transport=Lane(i);
                                if (transport==null) { stop.Token.WaitHandle.WaitOne(250); continue; }
                                Piece piece;
                                lock (gate) {
                                    if (disabled[i]) return;
                                    if (pending.Count>0) piece=pending.Dequeue();
                                    else if (next<total) {
                                        long remaining=total-next;
                                        long tailLimit=Math.Max(initialSize/2,remaining/8);
                                        long length=Math.Min(remaining,Math.Min(targetSize,tailLimit));
                                        piece=new Piece { Offset=next,Length=length };
                                        next+=piece.Length;
                                    } else { Monitor.Wait(gate,100); continue; }
                                    inflight++;
                                }
                                var elapsed=System.Diagnostics.Stopwatch.StartNew();
                                long got=0;
                                try {
                                    long end=piece.Offset+piece.Length-1;
                                    using (var range=transport.Open(info.Url,piece.Offset,end,info.Validator,stop.Token,ConnectTimeout)) {
                                        VerifyRange(range.Head,info,piece.Offset,end,total);
                                        file.Position=piece.Offset;
                                        while (true) {
                                            byte[] data=range.Read(stop.Token);
                                            if (data.Length==0) break;
                                            if (data.Length>piece.Length-got) throw new RangeFailure("A range exceeded its assigned length.");
                                            file.Write(data,0,data.Length);
                                            got+=data.Length;
                                            Meter(i,data.Length);
                                        }
                                        if (got!=piece.Length) throw new LinkFailure("A range ended before all bytes arrived.");
                                    }
                                    lock (gate) {
                                        inflight--; counts[i]+=got;
                                        // Aim for roughly two seconds of work per request. Bound growth
                                        // so early bursts do not leave a slow link holding a large tail.
                                        long measured=(long)(got*2000.0/Math.Max(1L,elapsed.ElapsedMilliseconds));
                                        targetSize=Math.Max(131072L,Math.Min(8388608L,
                                            Math.Min(targetSize*2,(targetSize+measured)/2)));
                                        progress(counts[0],counts[1],total);
                                        if (!bothReported && counts[0]>0 && counts[1]>0 && !disabled[0] && !disabled[1]) {
                                            bothReported=true;
                                            note("Downloading over both connections.");
                                        }
                                        Monitor.PulseAll(gate);
                                    }
                                } catch (Exception e) {
                                    stop.Token.ThrowIfCancellationRequested();
                                    if (!(e is LinkFailure) && !(e is LinkRefused)) throw;
                                    if (e is LinkRefused && i==baseLane) throw;
                                    Forget(got);
                                    bool retire=false;
                                    lock (gate) {
                                        pending.Enqueue(piece); inflight--;
                                        if (++piece.Failures[i]>=2 || e is LinkRefused) {
                                            retire=!disabled[i];
                                            disabled[i]=true;
                                            if (disabled[1-i] || Lane(1-i)==null)
                                                throw new RangeFailure("The available connection could not finish ranged requests.");
                                        }
                                        Monitor.PulseAll(gate);
                                    }
                                    if (retire) note("Parallel ranges. " + transport.Address + " became unavailable. Continuing on the other connection.");
                                    stop.Token.WaitHandle.WaitOne(200);
                                }
                            }
                        }
                    } catch { stop.Cancel(); throw; }
                };
                var tasks=new List<Task>();
                for (int k=0;k<ConnectionsPerLane;k++) {
                    for (int index=0;index<(secondary==null ? 1 : 2);index++) {
                        int lane=index;
                        tasks.Add(Task.Factory.StartNew(()=>worker(lane),CancellationToken.None,
                            TaskCreationOptions.LongRunning,TaskScheduler.Default));
                    }
                }
                try { Task.WaitAll(tasks.ToArray()); }
                catch (AggregateException e) {
                    cancel.ThrowIfCancellationRequested();
                    throw e.Flatten().InnerExceptions.FirstOrDefault(ex=>!(ex is OperationCanceledException)) ?? e;
                }
            }
            if (counts.Sum()!=total) throw new IOException("The verified byte count did not match the file size.");
        }
    }
}
