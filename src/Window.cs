using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TwinLink {
    internal sealed class Adapter {
        public string Id, Name;
        public IPAddress Address;
        public bool Wireless, Usb, Virtual;
        public override string ToString() { return Name + "   (" + Address + ")"; }
    }

    internal sealed class Window : Form {
        readonly TextBox url = new TextBox { Dock = DockStyle.Fill };
        readonly ComboBox first = Selector(), second = Selector();
        readonly Button refresh = new Button { Text = "Refresh", AutoSize = true };
        readonly Button start = new Button { Text = "Download...", AutoSize = true };
        readonly Button cancel = new Button { Text = "Cancel", Enabled = false, AutoSize = true };
        readonly ProgressBar bar = new ProgressBar { Dock = DockStyle.Fill, Maximum = 1000 };
        readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(560,0) };
        
        
        
        readonly Label mode = new Label { AutoSize = true, MaximumSize = new Size(595,0) };
        readonly Button copy = new Button { Text = "Copy details", AutoSize = true, Enabled = false };
        string report = "";
        const string FirstOnly = "Use first connection only";
        static string Clock(double seconds) {
            long whole = (long)seconds;
            return string.Format("{0:00}:{1:00}:{2:00}", whole/3600, whole/60%60, whole%60);
        }
        static string Remaining(long total, long received, long recentBytes, double interval) {
            if (total <= 0) return "Estimating...";
            if (received >= total) return "Finishing...";
            if (recentBytes <= 0 || interval <= 0) return "Estimating...";
            double seconds = Math.Ceiling((total - received) / (double)recentBytes * interval);
            return seconds < long.MaxValue ? Clock(seconds) : "Estimating...";
        }
        CancellationTokenSource active;
        static string SizeText(long bytes) {
            if (bytes < 0) return "Unknown";
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "N0" : "N2") + " " + units[unit];
        }
        static string LeftText(long size,long saved,bool complete) {
            return complete ? "0 B" : size < 0 ? "Unknown" : SizeText(Math.Max(0,size-saved));
        }
        static string Rate(long bytes,double seconds) {
            return (bytes*8.0/1000000/Math.Max(seconds,0.001)).ToString("N2")+" Mbps";
        }
        bool closing;
        static readonly Color Ink=Color.FromArgb(24,43,63), Accent=Color.FromArgb(0,106,99);
        static ComboBox Selector() { return new ComboBox { Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList }; }
        static Label Caption(string text) { return new Label { Text=text,AutoSize=true,Margin=new Padding(3,5,3,3) }; }
        static TableLayoutPanel Grid(int columns) {
            var grid=new TableLayoutPanel { AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Dock=DockStyle.Top,ColumnCount=columns,Margin=new Padding(0) };
            for(int i=0;i<columns;i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/columns));
            return grid;
        }
        static void Cell(TableLayoutPanel grid,int column,int row,string heading,Control value) {
            var stack=Grid(1);
            stack.Margin=new Padding(0,0,10,5);
            stack.Controls.Add(Caption(heading),0,0);
            stack.Controls.Add(value,0,1);
            grid.Controls.Add(stack,column,row);
        }
        static Label Metric() {
            return new Label { Text="-",AutoSize=true,Font=new Font("Segoe UI",14,FontStyle.Bold),Margin=new Padding(3,0,3,5) };
        }
        readonly Label combined=Metric(), remainingSize=Metric(), elapsed=Metric(), eta=Metric();
        readonly Label rateCaption=Caption("Combined speed");
        readonly Label[] laneNames={Caption("First connection"),Caption("Second connection")};
        readonly Label[] laneRates={Metric(),Metric()}, laneSizes={Caption("0 B"),Caption("0 B")};
        void SetRates(long x,long y,double seconds) {
            laneRates[0].Text=Rate(x,seconds); laneRates[1].Text=Rate(y,seconds);
            combined.Text=Rate(x+y,seconds);
        }
        public Window() {
            Text="TwinLink Downloader 2.3";
            Font=new Font("Segoe UI",10);
            AutoScaleMode=AutoScaleMode.Dpi;
            BackColor=Color.FromArgb(240,245,246); ForeColor=Ink;
            ClientSize=new Size(820,680); MinimumSize=new Size(620,470);
            StartPosition=FormStartPosition.CenterScreen;
            var scroll=new Panel { Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(20,12,20,14) };
            Controls.Add(scroll);
            var panel=Grid(1); scroll.Controls.Add(panel);
            var title=Caption("TwinLink Downloader"); title.Font=new Font("Segoe UI",22,FontStyle.Bold);
            panel.Controls.Add(title,0,0);
            var setup=Grid(2); setup.Padding=new Padding(14,6,6,8); setup.BackColor=Color.White;
            setup.Margin=new Padding(0,6,0,12); panel.Controls.Add(setup,0,1);
            Cell(setup,0,0,"Direct file URL",url); setup.SetColumnSpan(setup.GetControlFromPosition(0,0),2);
            Cell(setup,0,1,"First connection",first); Cell(setup,1,1,"Second connection",second);
            var buttons=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Fill,Margin=new Padding(0),WrapContents=true };
            foreach(Button button in new[]{start,cancel,refresh,copy}) {
                button.FlatStyle=FlatStyle.Flat; button.Padding=new Padding(8,3,8,3);
                button.FlatAppearance.BorderColor=Color.FromArgb(192,208,211);
                button.BackColor=Color.White; buttons.Controls.Add(button);
            }
            start.Text="Download."; start.BackColor=Accent; start.ForeColor=Color.White;
            setup.Controls.Add(buttons,0,2); setup.SetColumnSpan(buttons,2);
            var overview=Grid(2); overview.BackColor=Ink; overview.ForeColor=Color.White; overview.Padding=new Padding(14,8,14,10);
            panel.Controls.Add(overview,0,2);
            overview.Controls.Add(rateCaption,0,0); overview.Controls.Add(combined,0,1);
            combined.ForeColor=Color.FromArgb(114,237,210);
            overview.Controls.Add(Caption("Remaining size"),1,0); overview.Controls.Add(remainingSize,1,1);
            Cell(overview,0,2,"Elapsed time",elapsed); Cell(overview,1,2,"Time remaining",eta);
            bar.Height=10; bar.Dock=DockStyle.Top; overview.Controls.Add(bar,0,3); overview.SetColumnSpan(bar,2);
            var lanes=Grid(2); lanes.BackColor=Color.White; lanes.Padding=new Padding(14,6,14,8);
            panel.Controls.Add(lanes,0,3);
            for(int i=0;i<2;i++) {
                lanes.Controls.Add(laneNames[i],i,0); lanes.Controls.Add(laneRates[i],i,1); lanes.Controls.Add(laneSizes[i],i,2);
                laneRates[i].ForeColor=Accent;
            }
            panel.Controls.Add(mode,0,4); panel.Controls.Add(status,0,5);
            mode.Margin=new Padding(3,12,3,3); status.ForeColor=Accent;
            SetRates(0,0,1); elapsed.Text=Clock(0);
            copy.Click += delegate {
                try { Clipboard.SetText(report); }
                catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this,"The clipboard is busy. Try again."); }
            };
            refresh.Click += delegate { LoadAdapters(); };
            start.Click += async delegate { await Run(); };
            cancel.Click += delegate { if (active != null) active.Cancel(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (active != null) { closing = true; e.Cancel = true; active.Cancel(); }
            };
            LoadAdapters();
        }

        const string AutoUsb = "Automatically add USB tethering";
        static bool IsTether(NetworkInterface adapter) {
            string text = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
            return new[] { "rndis", "remote ndis", "internet sharing", "tether", "iphone", "apple mobile device", "mobile ncm", "usb ncm" }
                .Any(text.Contains);
        }
        static bool IsVirtual(NetworkInterface adapter) {
            string text = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
            return new[] { "vmware", "virtualbox", "hyper-v", "vethernet", "bluetooth", "tap-", "wireguard", "vpn", "npcap", "tailscale", "zerotier", "docker", "wsl", "loopback", "pseudo" }
                .Any(text.Contains);
        }
        static Adapter[] Adapters() {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => {
                        var props = n.GetIPProperties();
                        bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                        return props.UnicastAddresses.Where(a => gateway && a.Address.AddressFamily == AddressFamily.InterNetwork)
                            .Select(a => new Adapter { Id = n.Id, Name = n.Name, Address = a.Address, Usb = IsTether(n), Virtual = IsVirtual(n), Wireless = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 });
                    }).OrderBy(a => a.Virtual).ThenByDescending(a => a.Wireless).ThenBy(a => a.Name).ToArray();
        }

        void LoadAdapters() {
            try {
                var adapters = Adapters();
                first.Items.Clear(); second.Items.Clear();
                first.Items.AddRange(adapters); second.Items.Add(AutoUsb); second.Items.Add(FirstOnly); second.Items.AddRange(adapters);
                if (adapters.Length > 0) first.SelectedIndex = 0;
                second.SelectedIndex = 0;
                start.Enabled = first.SelectedIndex >= 0;
                status.Text = start.Enabled ? "Start now. USB tethering can join during the download." : "Connect a network, then click Refresh.";
            } catch (Exception e) { start.Enabled = false; status.Text = e.Message; }
        }

        static string SuggestedName(HttpHead info, Uri original) {
            string value = null;
            if (!string.IsNullOrEmpty(info.Disposition)) {
                var encoded = System.Text.RegularExpressions.Regex.Match(info.Disposition,
                    @"(?:^|;)\s*filename\*\s*=\s*UTF-8'[^']*'([^;]+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (encoded.Success) value = Uri.UnescapeDataString(encoded.Groups[1].Value.Trim().Trim('"'));
                if (string.IsNullOrWhiteSpace(value)) {
                    var quoted = System.Text.RegularExpressions.Regex.Match(info.Disposition,
                        @"(?:^|;)\s*filename\s*=\s*""((?:\\.|[^""\\])*)""",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (quoted.Success) value=quoted.Groups[1].Value.Replace("\\\"","\"").Replace("\\\\","\\");
                    else {
                        try { value = new System.Net.Mime.ContentDisposition(info.Disposition).FileName; }
                        catch (FormatException) {}
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(value)) {
                value=Uri.UnescapeDataString(info.Url.Segments.Last());
                if (string.IsNullOrWhiteSpace(Path.GetExtension(SafeName(value))))
                    value=Uri.UnescapeDataString(original.Segments.Last());
            }
            string name=SafeName(value);
            if (name.Length==0) name="download";
            if (!Path.HasExtension(name)) {
                switch ((info.Type ?? "").Split(';')[0].Trim().ToLowerInvariant()) {
                    case "video/mp4": return name+".mp4";
                    case "video/webm": return name+".webm";
                    case "video/x-matroska": return name+".mkv";
                    case "audio/mpeg": return name+".mp3";
                    case "application/pdf": return name+".pdf";
                    case "application/zip": return name+".zip";
                    default: return name+".bin";
                }
            }
            return name;
        }
        static string SafeName(string value) {
            string name=(value ?? "").Replace('\\','/').Split('/').Last().Trim();
            char[] invalid=Path.GetInvalidFileNameChars();
            name=new string(name.Where(c=>!char.IsControl(c) && !invalid.Contains(c)).ToArray()).TrimEnd('.',' ');
            if (System.Text.RegularExpressions.Regex.IsMatch(name,@"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) name="_"+name;
            return name.Length>180 ? name.Substring(0,180).TrimEnd('.',' ') : name;
        }

        async Task Run() {
            Uri link;
            if (!Uri.TryCreate(url.Text.Trim(),UriKind.Absolute,out link) ||
                (link.Scheme!="http" && link.Scheme!="https") || link.UserInfo.Length!=0) {
                status.Text="Enter a direct HTTP or HTTPS file URL."; return;
            }
            var a=first.SelectedItem as Adapter; var b=second.SelectedItem as Adapter;
            if (a==null || (b!=null && (a.Id==b.Id || a.Address.Equals(b.Address)))) {
                status.Text="Choose two different network adapters."; return;
            }
            bool useSecond=(second.SelectedItem as string)!=FirstOnly;
            active=new CancellationTokenSource();
            var token=active.Token;
            first.Enabled=second.Enabled=start.Enabled=refresh.Enabled=url.Enabled=false;
            cancel.Enabled=true; copy.Enabled=false; report="";
            status.Text="Connecting to "+link.DnsSafeHost+".";
            rateCaption.Text="Combined speed"; bar.Value=0;
            mode.Text="";
            string secondName=b!=null ? b.Name : useSecond ? "USB (optional)" : "Second connection off";
            Func<IPAddress> discover=delegate {
                if (!useSecond) return null;
                Adapter found=b;
                if (found==null) {
                    try {
                        found=Adapters().Where(n=>n.Id!=a.Id && !n.Address.Equals(a.Address) && !n.Virtual && (n.Usb || !n.Wireless))
                            .OrderByDescending(n=>n.Usb).FirstOrDefault();
                    } catch (NetworkInformationException) { return null; }
                }
                if (found==null) return null;
                Interlocked.Exchange(ref secondName,found.Name);
                return found.Address;
            };
            var watch=Stopwatch.StartNew();
            long[] bytes=new long[2];
            long discarded=0, savedA=0, savedB=0, total=-1;
            string stage="Connecting to "+link.DnsSafeHost+".", target=null;
            object gate=new object();
            var events=new List<string>();
            Action<string> notify=delegate(string line) {
                lock (gate) {
                    stage=line;
                    if (events.Count>=32) events.RemoveAt(0);
                    events.Add(Clock(watch.Elapsed.TotalSeconds)+"  "+line);
                }
            };
            long lastA=0,lastB=0;
            double lastTime=0;
            var history=new Queue<Tuple<double,long>>();
            history.Enqueue(Tuple.Create(0.0,0L));
            Action render=delegate {
                double now=watch.Elapsed.TotalSeconds;
                long x=Interlocked.Read(ref bytes[0]), y=Interlocked.Read(ref bytes[1]);
                long size,left,right;
                lock (gate) { size=total; left=savedA; right=savedB; mode.Text=stage; }
                bar.Style=size<0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
                if (size>=0) bar.Value=size==0?0:(int)Math.Min(1000,(left+right)/(double)size*1000);
                laneNames[0].Text=a.Name; laneNames[1].Text=secondName;
                laneSizes[0].Text=SizeText(left)+" downloaded"; laneSizes[1].Text=SizeText(right)+" downloaded";
                remainingSize.Text=LeftText(size,left+right,false);
                SetRates(x-lastA,y-lastB,now-lastTime);
                while (history.Count>1 && history.Peek().Item1<now-5) history.Dequeue();
                var old=history.Peek();
                elapsed.Text=Clock(now); eta.Text=size<0 ? "Unknown" :
                    Remaining(size,Math.Max(0,x+y-Interlocked.Read(ref discarded)),x+y-old.Item2,now-old.Item1);
                history.Enqueue(Tuple.Create(now,x+y));
                lastA=x; lastB=y; lastTime=now;
                status.Text=size<0 ? "Waiting for size or downloading an unmeasured stream." :
                    string.Format("{0:P0} downloaded",size==0?0:(left+right)/(double)size);
            };
            Func<HttpHead,string> choose=delegate(HttpHead head) {
                return (string)Invoke((Func<string>)delegate {
                    if (closing || token.IsCancellationRequested) return null;
                    watch.Stop();
                    try {
                        string type=(head.Type??"").Split(';')[0];
                        string title=type.IndexOf("html",StringComparison.OrdinalIgnoreCase)>=0 ? "This link returned a webpage. Save the page itself?" :
                            type.IndexOf("mpegurl",StringComparison.OrdinalIgnoreCase)>=0 || type=="application/dash+xml" ? "This link returned a playlist. Save the playlist itself?" : "Save download as a new file";
                        using (var picker=new SaveFileDialog { FileName=SuggestedName(head,link),Title=title,OverwritePrompt=false }) {
                            if (picker.ShowDialog(this)!=DialogResult.OK) return null;
                            target=picker.FileName;
                            return target;
                        }
                    } finally { watch.Start(); }
                });
            };
            bool done=false;
            string outcome="";
            using (var timer=new System.Windows.Forms.Timer { Interval=500 }) {
                timer.Tick+=delegate { render(); };
                render(); timer.Start();
                try {
                    await Task.Run(()=>Transfer.Download(link,a.Address,useSecond ? discover : null,choose,
                        (x,y,n)=>{ lock (gate) {savedA=x; savedB=y; total=n;} },token,
                        (i,n)=>Interlocked.Add(ref bytes[i],n),n=>Interlocked.Add(ref discarded,n),notify));
                    done=true;
                    outcome="Saved to "+target;
                } catch (OperationCanceledException) { outcome="Download canceled."; }
                  catch (Exception e) { outcome=e.GetBaseException().Message; }
                finally {
                    timer.Stop(); watch.Stop(); render();
                    bar.Style=ProgressBarStyle.Continuous;
                    if (done) bar.Value=1000;
                    var seconds=watch.Elapsed.TotalSeconds;
                    rateCaption.Text=done ? "Average combined speed" : "Combined speed";
                    SetRates(done?savedA:0,done?savedB:0,seconds);
                    elapsed.Text=Clock(seconds); eta.Text=done ? Clock(0) : "-";
                    remainingSize.Text=LeftText(total,savedA+savedB,done);
                    status.Text=outcome;
                    lock (gate) {
                        report="TwinLink 2.3 | "+link.DnsSafeHost+Environment.NewLine+"First connection: "+a+
                            Environment.NewLine+"Second connection: "+secondName+Environment.NewLine+
                            string.Join(Environment.NewLine,events)+Environment.NewLine+laneNames[0].Text+": "+laneSizes[0].Text+", "+laneRates[0].Text+Environment.NewLine+
                            laneNames[1].Text+": "+laneSizes[1].Text+", "+laneRates[1].Text+Environment.NewLine+
                            rateCaption.Text+": "+combined.Text+Environment.NewLine+"Elapsed: "+elapsed.Text+"   ETA: "+eta.Text+"   Remaining size: "+remainingSize.Text+Environment.NewLine+(done?"Download completed.":outcome);
                    }
                    active.Dispose(); active=null;
                    cancel.Enabled=false; copy.Enabled=true;
                    first.Enabled=second.Enabled=start.Enabled=refresh.Enabled=url.Enabled=true;
                    if (closing) Close();
                }
            }
        }

        [STAThread] internal static void Main() {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new Window());
        }
    }
}
