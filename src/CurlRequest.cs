using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Remoting;
using System.Security.Authentication;
using System.Text;

namespace TwinLink {
    // Used on a single adapter after the Framework client loses the connection before headers arrive.
    // Headers and body share one pipe so they always describe the same response.
    internal sealed class CurlRequest : AdapterRequest {
        internal static readonly string Executable=Path.Combine(Environment.SystemDirectory,"curl.exe");
        readonly IPAddress source;
        readonly CookieContainer cookies;
        readonly object gate=new object();
        readonly byte[] buffer=new byte[64*1024];
        Process process;
        Stream pipe;
        int offset, count;
        Uri current;
        bool aborted, finished, chunked, chunkEnd, bodyComplete;
        long chunkRemaining;

        internal CurlRequest(IPAddress source, CookieContainer cookies) {
            this.source=source; this.cookies=cookies;
        }

        static void Option(StringBuilder config,string name,string value) {
            if (value.IndexOfAny(new[] {'\0','\r','\n'})>=0)
                throw new InvalidDataException("A download request contained an invalid header or URL.");
            config.Append(name).Append(" = \"").Append(value.Replace("\\","\\\\").Replace("\"","\\\"")).Append("\"\n");
        }
        void Start(Uri url,long start,long end,string validator,int timeout) {
            current=url;
            var config=new StringBuilder();
            Option(config,"url",url.AbsoluteUri);
            Option(config,"interface",source.ToString());
            Option(config,"connect-timeout",Math.Max(1,(timeout+999)/1000).ToString(CultureInfo.InvariantCulture));
            Option(config,"header","Accept-Encoding: identity");
            if (start>=0) Option(config,"range",start.ToString(CultureInfo.InvariantCulture)+"-"+(end<0 ? "" : end.ToString(CultureInfo.InvariantCulture)));
            if (validator!=null) Option(config,"header","If-Range: "+validator);
            string cookie=cookies.GetCookieHeader(url);
            if (cookie.Length!=0) Option(config,"header","Cookie: "+cookie);
            var psi=new ProcessStartInfo(Executable,
                "--disable --globoff --ipv4 --silent --show-error --include --raw --no-buffer --suppress-connect-headers "+
                "--proto =http,https --speed-limit 1 --speed-time 30 --user-agent TwinLink/2.2 --config -") {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true
            };
            lock (gate) {
                if (aborted) throw new OperationCanceledException();
                try { process=Process.Start(psi); }
                catch (Win32Exception) { throw new LinkFailure("Windows could not start its curl download client."); }
                // Drain native diagnostics without retaining server text or signed URLs in the report.
                process.ErrorDataReceived+=delegate {};
                process.BeginErrorReadLine();
                pipe=process.StandardOutput.BaseStream;
                offset=count=0;
                byte[] data=new UTF8Encoding(false).GetBytes(config.ToString());
                Stream stdin=process.StandardInput.BaseStream;
                try { stdin.Write(data,0,data.Length); }
                catch (IOException) { /* An early curl error is reported from its exit code below. */ }
                finally { process.StandardInput.Close(); }
            }
        }
        static void Kill(Process p) {
            if (p==null) return;
            try { if (!p.HasExited) p.Kill(); }
            catch (InvalidOperationException) {}
            catch (Win32Exception) { if (!p.HasExited) throw; }
        }
        public override void Abort() {
            lock (gate) { aborted=true; Kill(process); }
        }
        void ReleaseProcess() {
            Process p;
            lock (gate) { p=process; process=null; }
            if (p!=null) {
                try { Kill(p); p.WaitForExit(); }
                finally { p.Dispose(); }
            }
            pipe=null;
        }
        public override void Close(bool consumed) {
            try { ReleaseProcess(); }
            finally { RemotingServices.Disconnect(this); }
        }
        int Fill() {
            if (finished) return 0;
            offset=0;
            try { count=pipe.Read(buffer,0,buffer.Length); }
            catch (IOException) { throw new LinkFailure("The Windows curl download stream was interrupted."); }
            if (count!=0) return count;
            process.WaitForExit();
            lock (gate) { if (aborted) throw new OperationCanceledException(); }
            int code=process.ExitCode;
            if (code==0) { finished=true; return 0; }
            string host=current.DnsSafeHost;
            switch (code) {
                case 35: case 51: case 58: case 59: case 60: case 77: case 83: case 90: case 91:
                    throw new AuthenticationException("Windows curl could not establish a trusted TLS connection to "+host+" (curl "+code+").");
                case 5: case 6: throw new LinkFailure("Windows curl could not resolve the host or proxy for "+host+" (curl "+code+").");
                case 7: throw new LinkFailure("Windows curl could not connect to "+host+" (curl 7).");
                case 28: throw new LinkFailure("Windows curl timed out waiting for "+host+" (curl 28).");
                case 45: throw new LinkFailure("Windows curl could not bind the selected adapter address "+source+" (curl 45).");
                case 52: case 55: case 56: throw new LinkFailure("The connection to "+host+" closed during the Windows curl request (curl "+code+").");
                default: throw new LinkFailure("Windows curl could not finish receiving from "+host+" (curl "+code+").");
            }
        }
        string HeaderLine(ref int budget) {
            var line=new StringBuilder();
            while (true) {
                if (offset==count && Fill()==0) throw new LinkFailure("Windows curl received no complete HTTP headers from "+current.DnsSafeHost+".");
                if (--budget<0) throw new InvalidDataException("The server sent oversized HTTP headers.");
                char c=(char)buffer[offset++];
                if (c=='\n') return line.ToString().TrimEnd('\r');
                line.Append(c);
            }
        }
        static string Field(Dictionary<string,string> fields,string name) {
            string value;
            return fields.TryGetValue(name,out value) ? value : null;
        }
        public override HttpHead Open(Uri url,long start,long end,string validator,int timeout) {
            for (int hop=0;hop<=8;hop++) {
                Start(url,start,end,validator,timeout);
                string location=null;
                int budget=128*1024;
                while (true) {
                    string[] prefix=HeaderLine(ref budget).Split(new[] {' '},StringSplitOptions.RemoveEmptyEntries);
                    int code;
                    if (prefix.Length<2 || !prefix[0].StartsWith("HTTP/",StringComparison.OrdinalIgnoreCase) ||
                        !int.TryParse(prefix[1],NumberStyles.None,CultureInfo.InvariantCulture,out code))
                        throw new InvalidDataException("Windows curl received an invalid HTTP status line.");
                    var fields=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                    var setCookies=new List<string>();
                    string line;
                    while ((line=HeaderLine(ref budget)).Length!=0) {
                        int sep=line.IndexOf(':');
                        if (sep<=0) continue;
                        string name=line.Substring(0,sep).Trim(), value=line.Substring(sep+1).Trim();
                        if (name.Equals("Set-Cookie",StringComparison.OrdinalIgnoreCase)) setCookies.Add(value);
                        else fields[name]=value;
                    }
                    if (code>=100 && code<200) {
                        if (code==101) throw new InvalidDataException("A protocol upgrade cannot be saved as a file.");
                        continue;
                    }
                    foreach (string cookie in setCookies) {
                        try { cookies.SetCookies(url,cookie); } catch (CookieException) {}
                    }
                    if (IsRedirect(code)) { location=Field(fields,"Location"); break; }
                    long length=-1;
                    string declared=Field(fields,"Content-Length");
                    if (declared!=null && !long.TryParse(declared,NumberStyles.None,CultureInfo.InvariantCulture,out length))
                        throw new InvalidDataException("The server sent an invalid Content-Length.");
                    string framing=Field(fields,"Transfer-Encoding");
                    chunked=string.Equals(framing,"chunked",StringComparison.OrdinalIgnoreCase);
                    if (framing!=null && !chunked) throw new InvalidDataException("Unsupported HTTP transfer encoding.");
                    return new HttpHead { Status=code,Url=url,Length=length,Range=Field(fields,"Content-Range"),
                        ETag=Field(fields,"ETag"),Modified=Field(fields,"Last-Modified"),Ranges=Field(fields,"Accept-Ranges"),
                        Type=Field(fields,"Content-Type"),Disposition=Field(fields,"Content-Disposition"),
                        Encoding=Field(fields,"Content-Encoding"),RetryAfter=Field(fields,"Retry-After") };
                }
                url=Redirect(url,location);
                ReleaseProcess();
            }
            throw new InvalidDataException("Too many redirects. Copy the host's final download link.");
        }
        public override byte[] Read() {
            if (bodyComplete) return new byte[0];
            if (chunked && chunkRemaining==0) {
                int budget=128*1024;
                if (chunkEnd && HeaderLine(ref budget).Length!=0) throw new LinkFailure("Invalid chunk terminator.");
                string line=HeaderLine(ref budget);
                int extension=line.IndexOf(';');
                string size=extension<0 ? line : line.Substring(0,extension);
                if (size.Length==0 || !long.TryParse(size,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out chunkRemaining) || chunkRemaining<0)
                    throw new LinkFailure("Invalid HTTP chunk size.");
                if (chunkRemaining==0) {
                    while (HeaderLine(ref budget).Length!=0) {}
                    bodyComplete=true;
                    return new byte[0];
                }
                chunkEnd=true;
            }
            if (offset==count && Fill()==0) {
                if (chunked) throw new LinkFailure("The HTTP chunk ended early.");
                return new byte[0];
            }
            int available=count-offset;
            if (chunked) {
                available=(int)Math.Min(chunkRemaining,available);
                chunkRemaining-=available;
            }
            if (available==buffer.Length) { offset=count; return buffer; }
            var data=new byte[available];
            Buffer.BlockCopy(buffer,offset,data,0,available);
            offset+=available;
            return data;
        }
    }
}
