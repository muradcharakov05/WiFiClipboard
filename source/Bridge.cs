using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Мост буфера — текст и файлы")]
[assembly: AssemblyVersion("7.3.0.0")]
[assembly: AssemblyFileVersion("7.3.0.0")]
namespace WifiClipboard {
public sealed class State {
    public readonly object Gate = new object();
    public string Text = "";
    public long Version;
    public bool Enabled = true;
    public Func<string, bool> WriteClipboard;
    public void Publish(string text) { lock (Gate) { Text = text; Version++; } }
}
public sealed partial class Server : IDisposable {
    public const int MaxText = 262144;
    readonly TcpListener listener;
    readonly State state;
    readonly HashSet<string> hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> tokens;
    readonly TrustStore trust;
    public string LocalHostName {get {return trust.HostName;}}
    public int TrustedCount {get {lock(gate)return tokens.Count;}}
    public void ForgetDevices() {lock(gate){trust.Save(new HashSet<string>());tokens.Clear();activity.Remove(null);}CancelFileTransfers();}
    readonly object gate = new object();
    readonly SemaphoreSlim slots = new SemaphoreSlim(12);
    readonly string html, js;
    int attempts;
    DateTime window = DateTime.UtcNow;
    volatile bool stopped;
    public readonly string Code;
    public readonly int Port;
    public Server(State s, int port, IEnumerable<string> addresses, string receiveDirectory = null, string trustDirectory = null) {
        trust=new TrustStore(trustDirectory);tokens=trust.Load();activity=new ActivityStore(trustDirectory,tokens);
        ReceiveDirectory=Path.GetFullPath(receiveDirectory ?? Path.Combine(Application.StartupPath,"Полученные файлы"));
        LoadUploads();
        state = s; listener = new TcpListener(IPAddress.Any, port); listener.Start(24);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        UpdateNetworkAddresses(addresses);
        byte[] bytes = new byte[4]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        Code = (BitConverter.ToUInt32(bytes, 0) % 100000000).ToString("D8");
        html = Resource("phone.html"); js = Resource("app.js");
        var t = new Thread(Accept); t.IsBackground = true; t.Start();
    }
    static string Resource(string name) { using (var r = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream(name))) return r.ReadToEnd(); }
    void Accept() {
        while (!stopped) {
            try {
                TcpClient client = listener.AcceptTcpClient();
                if (!slots.Wait(0)) { client.Close(); continue; }
                ThreadPool.QueueUserWorkItem(delegate { try { Handle(client); } finally { CloseGracefully(client); slots.Release(); } });
            } catch (SocketException) { if (stopped) return; }
              catch (ObjectDisposedException) { return; }
        }
    }
    static void CloseGracefully(TcpClient client) {
        try {
            // Drain a bounded rejected body before closing, so Winsock does not reset
            // the connection and discard an already written error response.
            client.ReceiveTimeout=300;
            var buffer=new byte[131072]; int total=0;
            DateTime deadline=DateTime.UtcNow.AddMilliseconds(1000);
            var stream=client.GetStream();
            while(total < Math.Max(ChunkSize,MaxText*6)+16384 && DateTime.UtcNow<deadline) {
                int n=stream.Read(buffer,0,buffer.Length); if(n==0)break;total+=n;
            }
        } catch {} finally {try{client.Client.Shutdown(SocketShutdown.Send);}catch{}client.Close();}
    }
    static string Line(Stream stream, ref int total) {
        var b = new List<byte>(); int c;
        while ((c = stream.ReadByte()) != -1) {
            if (++total > 16384) throw new InvalidDataException();
            if (c == 10) { if (b.Count == 0 || b[b.Count-1] != 13) throw new InvalidDataException(); b.RemoveAt(b.Count-1); return Encoding.ASCII.GetString(b.ToArray()); }
            b.Add((byte)c);
        }
        throw new EndOfStreamException();
    }
    void Handle(TcpClient client) {
        client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
        var stream = client.GetStream();
        try {
            int count = 0; string[] request = Line(stream, ref count).Split(' ');
            if (request.Length != 3 || request[2] != "HTTP/1.1") { Reply(stream,400,"Bad request"); return; }
            string method = request[0], path = request[1];
            var h = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            string line;
            while ((line = Line(stream, ref count)) != "") {
                int colon = line.IndexOf(':');
                if (colon <= 0 || h.ContainsKey(line.Substring(0,colon))) { Reply(stream,400,"Bad headers"); return; }
                h.Add(line.Substring(0,colon),line.Substring(colon+1).Trim());
            }
            string host; if (!h.TryGetValue("Host",out host) || !AcceptsHost(host)) { Reply(stream,403,"Host rejected"); return; }
            string origin;
            if (h.TryGetValue("Origin",out origin) && origin != "http://" + host) { Reply(stream,403,"Origin rejected"); return; }
            if (h.ContainsKey("Transfer-Encoding")) { Reply(stream,400,"Unsupported encoding"); return; }
            if (method == "GET" && path == "/") { Reply(stream,200,html,"text/html; charset=utf-8"); return; }
            if (method == "GET" && path == "/app.js") { Reply(stream,200,js,"application/javascript; charset=utf-8"); return; }
            if (method=="GET" && path=="/transfer.js") {Reply(stream,200,Resource("transfer.js"),"application/javascript; charset=utf-8");return;}
            if (HandleHome(stream,method,path,h)) return;
            if (HandleSafariDownloads(client,method,path,h)) return;
            if (HandleFileView(stream,method,path,h)) return;
            if (HandleFiles(client,method,path,h)) return;
            if (HandleUploads(client,method,path,h)) return;
            if (HandleActivity(stream,method,path,h)) return;
            if (method != "GET" && method != "POST") { Reply(stream,405,"Method rejected"); return; }
            if (path != "/pair" && path != "/api/text" && path != "/unpair" && path != "/session") { Reply(stream,404,"Not found"); return; }
            string token = null;
            if (path != "/pair") {
                bool valid; lock(gate) valid = tokens.Contains(TrustStore.Fingerprint(token=FileToken(h,true)));
                if (!valid) { Reply(stream,401,"Подключитесь заново."); return; }
            }
            if(method=="GET" && path=="/session") {activity.Seen(TrustStore.Fingerprint(token));Json(stream,new {authenticated=true,localUrl="http://"+LocalHostName+":"+Port+"/"},Cookie(token));return;}
            if (method == "GET" && path == "/api/text") {
                lock(state.Gate) Json(stream,new { text = state.Enabled ? state.Text : "", version = state.Enabled ? state.Version : -1, enabled = state.Enabled });
                return;
            }
            if (method != "POST") { Reply(stream,405,"Method rejected"); return; }
            string ct; if (!h.TryGetValue("Content-Type",out ct) || !ct.StartsWith("application/json",StringComparison.OrdinalIgnoreCase)) { Reply(stream,415,"JSON required"); return; }
            int length; string lengthText;
            if (!h.TryGetValue("Content-Length",out lengthText) || !int.TryParse(lengthText,out length) || length < 0 || length > MaxText * 6 + 1024) { Reply(stream,413,"Слишком большой запрос."); return; }
            if (path == "/pair" && length > 1024) { Reply(stream,413,"Слишком большой запрос."); return; }
            var body = new byte[length]; int offset = 0;
            while (offset < length) { int n = stream.Read(body,offset,length-offset); if (n == 0) return; offset += n; }
            Dictionary<string,object> data;
            try { data = new JavaScriptSerializer { MaxJsonLength = MaxText * 6 + 1024 }.Deserialize<Dictionary<string,object>>(new UTF8Encoding(false,true).GetString(body)); }
            catch { Reply(stream,400,"Некорректный JSON."); return; }
            if (data == null) { Reply(stream,400,"JSON object required"); return; }
            if (path == "/pair") {
                lock(gate) {
                    if ((DateTime.UtcNow - window).TotalSeconds >= 60) { attempts=0; window=DateTime.UtcNow; }
                    if (++attempts > 8) { Reply(stream,429,"Слишком много попыток. Подождите минуту."); return; }
                    object code;
                    if (!data.TryGetValue("code",out code) || !(code is string) || (string)code != Code) { Reply(stream,403,"Неверный код подключения."); return; }
                    if (tokens.Count >= 16) { Reply(stream,429,"Лимит 16 устройств. Отключите ненужное в окне Windows."); return; }
                    byte[] secret = new byte[32]; using(var rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
                    string issued = Convert.ToBase64String(secret);string fingerprint=TrustStore.Fingerprint(issued);
                    var saved=new HashSet<string>(tokens);saved.Add(fingerprint);trust.Save(saved);tokens.Add(fingerprint);object deviceName;activity.AddDevice(fingerprint,data.TryGetValue("name",out deviceName)&&deviceName is string?(string)deviceName:"iPhone");
                    Json(stream,new {token=issued},Cookie(issued));return;
                }
            }
            if (path == "/unpair") { RevokeDevice(TrustStore.Fingerprint(token)); Json(stream,new {ok=true},Cookie("",true)); return; }
            object value;
            if (!data.TryGetValue("text",out value) || !(value is string)) { Reply(stream,400,"Нужен текст."); return; }
            string text = (string)value;
            if (Encoding.UTF8.GetByteCount(text) > MaxText || text.IndexOf('\0') >= 0) { Reply(stream,413,"Текст больше 256 КБ или содержит нулевой символ."); return; }
            lock(state.Gate) { if (!state.Enabled) { Reply(stream,409,"Передача приостановлена на Windows."); return; } }
            if (state.WriteClipboard == null || !state.WriteClipboard(text)) { Reply(stream,503,"Буфер Windows занят или передача приостановлена. Повторите отправку."); return; }
            Record("iPhone → Windows","Текст",Encoding.UTF8.GetByteCount(text),token,"В буфере Windows");
            Json(stream,new {ok=true});
        } catch (IOException) {} catch (SocketException) {} catch (Exception) { try { Reply(stream,400,"Ошибка запроса."); } catch {} }
    }
    string Cookie(string token,bool clear=false) {return "Set-Cookie: BridgeSession"+Port+"="+token+"; HttpOnly; SameSite=Strict; Path=/; Max-Age="+(clear?"0":"31536000")+"\r\n";}
    static void Json(Stream s, object data, string extra = "") { Reply(s,200,new JavaScriptSerializer {MaxJsonLength=MaxText*6+1024}.Serialize(data),"application/json; charset=utf-8",extra); }
    static void Reply(Stream stream,int status,string body,string contentType=null,string extra="") {
        if (contentType == null) { contentType="application/json; charset=utf-8"; body=new JavaScriptSerializer().Serialize(new {error=body}); }
        byte[] data=Encoding.UTF8.GetBytes(body);
        string headers="HTTP/1.1 " + status + " Response\r\nContent-Type: " + contentType + "\r\nContent-Length: " + data.Length + "\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self'; media-src 'self'; frame-src 'self'; manifest-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'\r\n"+extra+"\r\n";
        byte[] head=Encoding.ASCII.GetBytes(headers); stream.Write(head,0,head.Length); stream.Write(data,0,data.Length);
    }
    public void Dispose() { stopped=true; listener.Stop(); StopFileTransfers(); lock(fileGate)files.Clear(); CleanupArchives(); }
}
public sealed partial class BridgeForm : Form {
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    readonly State state = new State();
    readonly Server server;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Label status = new Label();
    readonly Label fileStatus = new Label();
    readonly LocalNameResponder localName;
    uint sequence;
    public BridgeForm() : this(null) {}
    internal BridgeForm(string testRoot) {
        Icon=System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Text="Мост буфера · Текст и файлы"; ClientSize=new Size(700,800); MinimumSize=new Size(580,420);
        Font=new Font("Segoe UI",11); BackColor=Color.FromArgb(245,248,251); StartPosition=FormStartPosition.CenterScreen;
        var ips=NetworkAddresses.Current();networkAddresses.AddRange(ips);
        try { server=new Server(state,testRoot==null?8765:0,ips,testRoot==null?null:Path.Combine(testRoot,"received"),testRoot==null?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WiFiClipboard"):Path.Combine(testRoot,"trust")); }
        catch(SocketException) { throw new InvalidOperationException("Порт 8765 занят. Закройте другой экземпляр программы и запустите снова."); }
        localName=testRoot==null?new LocalNameResponder(server.LocalHostName):null;
        var layout=new FlowLayoutPanel {Name="mainScroll",Dock=DockStyle.Fill,Padding=new Padding(26),FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};
        Controls.Add(layout);
        layout.Controls.Add(new Label {Text="Мост буфера",Font=new Font("Segoe UI",24,FontStyle.Bold),AutoSize=true});
        layout.Controls.Add(new Label {Text="1. Подключите iPhone и компьютер к одному Wi-Fi.\n2. Откройте в Safari один из адресов:",AutoSize=true,Margin=new Padding(0,14,0,8)});
        var addresses = addressBox = new TextBox {Name="connectionAddresses",Multiline=true,ReadOnly=true,Height=78,Width=610,ScrollBars=ScrollBars.Vertical,Text=ips.Count==0?"Нет адреса сети. Подключите Wi-Fi; адреса обновятся автоматически.":"http://"+server.LocalHostName+":"+server.Port+"/"+Environment.NewLine+string.Join(Environment.NewLine,ips.ConvertAll(ip=>"http://"+ip+":"+server.Port).ToArray())};
        layout.Controls.Add(addresses);
        networkHint=new Label {Name="networkHint",Text="Первый адрес постоянный (.local)."+Environment.NewLine+"При смене сети адреса и QR обновятся автоматически."+Environment.NewLine+"В Safari: Поделиться → На экран «Домой». Выключите «Открывать как веб-приложение».",AutoSize=true,MaximumSize=new Size(610,0)};layout.Controls.Add(networkHint);
        layout.Controls.Add(new Label {Text="3. Код подключения:  " + server.Code,Font=new Font("Segoe UI",19,FontStyle.Bold),AutoSize=true,Margin=new Padding(0,16,0,12)});
        layout.Controls.Add(new Label {Text="Windows → iPhone: скопируйте текст, затем нажмите\n«Скопировать на iPhone» в Safari.\niPhone → Windows: вставьте текст на странице и отправьте.",AutoSize=true});
        layout.Controls.Add(new Label {Text="Файлы любых типов · до 10 ГиБ на файл",Font=new Font("Segoe UI",14,FontStyle.Bold),AutoSize=true,Margin=new Padding(0,20,0,8)});
        var pickFiles=new Button {Text="Выбрать файлы для iPhone…",Width=610,Height=42};
        pickFiles.Click+=delegate {
            using(var dialog=new OpenFileDialog {Multiselect=true,Filter="Все файлы (*.*)|*.*",Title="Выберите файлы для скачивания на iPhone"}) {
                if(dialog.ShowDialog(this)==DialogResult.OK) SharePaths(dialog.FileNames);
            }
        };layout.Controls.Add(pickFiles);
        var fileActions=new FlowLayoutPanel {Width=620,Height=48,WrapContents=false,Margin=new Padding(0)};
        var received=new Button {Text="Открыть полученные файлы",Width=310,Height=40};
        received.Click+=delegate {try {Directory.CreateDirectory(server.ReceiveDirectory);System.Diagnostics.Process.Start("explorer.exe","\""+server.ReceiveDirectory+"\"");}catch(Exception e){MessageBox.Show(this,e.Message,"Полученные файлы");}};
        var clearFiles=new Button {Text="Снять доступ к файлам",Width=290,Height=40};
        clearFiles.Click+=delegate {server.ClearSharedFiles();fileStatus.Text="Доступ к выбранным файлам снят. Полученные файлы сохранены.";};
        fileActions.Controls.Add(received);fileActions.Controls.Add(clearFiles);layout.Controls.Add(fileActions);
        fileStatus.AutoSize=true;fileStatus.MaximumSize=new Size(610,0);fileStatus.Text="Выберите файлы или скопируйте их в Проводнике (Ctrl+C).\nС iPhone: выберите файлы в Safari и нажмите «Отправить файлы».";layout.Controls.Add(fileStatus);
        server.FileNotice=delegate(string text) {try {BeginInvoke(new Action(delegate {fileStatus.Text=text;}));}catch(InvalidOperationException){}};
        var toggle=new Button {Text="Приостановить передачу",Height=43,Width=610,Margin=new Padding(0,16,0,8)};
        toggle.Click+=delegate {
            bool enabled;
            lock(state.Gate) { state.Enabled=!state.Enabled;enabled=state.Enabled;toggle.Text=enabled?"Приостановить передачу":"Возобновить передачу"; status.Text=enabled?"Передача включена. Скопируйте новый текст.":"Передача приостановлена."; }
            if(!enabled)server.CancelFileTransfers();
        }; layout.Controls.Add(toggle);
        status.Text="Передача включена. Скопируйте новый текст."; status.AutoSize=true; layout.Controls.Add(status);
        layout.Controls.Add(new Label {Text="Текст до 256 КБ; файлы до 10 ГиБ. Папки упаковываются в ZIP автоматически.\nВыбранные файлы доступны всем подключённым устройствам.\nHTTP без шифрования: используйте доверенную частную сеть.",ForeColor=Color.DimGray,AutoSize=true,MaximumSize=new Size(610,0),Margin=new Padding(0,12,0,0)});
        var forgetDevices=new Button {Name="forgetDevices",Text="Забыть все iPhone",Dock=DockStyle.Fill};
        forgetDevices.Click+=delegate {try {server.ForgetDevices();forgetDevices.Text="Все подключения забыты";}catch(Exception e){MessageBox.Show(this,e.Message,"Подключения");}};
        var footer=new Panel {Name="fixedFooter",Dock=DockStyle.Bottom,Height=70,Padding=new Padding(26,8,26,14),BackColor=BackColor};
        footer.Controls.Add(forgetDevices);Controls.Add(footer);
        AddFeatures(layout);
        ApplyDesktopTheme(layout,footer,forgetDevices);
        KeepScrollBottomReachable(layout);
        state.WriteClipboard=Write;
        sequence=GetClipboardSequenceNumber(); timer.Interval=500; timer.Tick+=Observe; if(testRoot==null){timer.Start();WatchNetwork();}

    }
    static void KeepScrollBottomReachable(FlowLayoutPanel layout) {
        bool updating=false;
        LayoutEventHandler update=delegate {
            if(updating)return;
            int bottom=layout.Padding.Top;
            foreach(Control child in layout.Controls)bottom=Math.Max(bottom,child.Bottom-layout.AutoScrollPosition.Y+child.Margin.Bottom);
            int height=bottom+layout.Padding.Bottom;
            if(layout.AutoScrollMinSize.Height==height)return;
            updating=true;
            try{layout.AutoScrollMinSize=new Size(0,height);}finally{updating=false;}
        };
        layout.Layout+=update;layout.PerformLayout();
    }
    protected override void OnShown(EventArgs e) {base.OnShown(e);FitToWorkingArea(Screen.FromControl(this).WorkingArea);}
    protected override void OnResizeEnd(EventArgs e) {base.OnResizeEnd(e);FitToWorkingArea(Screen.FromControl(this).WorkingArea);}
    internal void FitToWorkingArea(Rectangle area) {
        if(WindowState!=FormWindowState.Normal||area.Width<=0||area.Height<=0)return;
        // Measure the scaled outer window, including its title bar, against the usable screen.
        MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width),Math.Min(MinimumSize.Height,area.Height));
        int width=Math.Min(Width,area.Width),height=Math.Min(Height,area.Height);
        Bounds=new Rectangle(Math.Max(area.Left,Math.Min(Left,area.Right-width)),Math.Max(area.Top,Math.Min(Top,area.Bottom-height)),width,height);
    }
    protected override void Dispose(bool disposing) {if(disposing){StopWatchingNetwork();timer.Stop();timer.Dispose();if(localName!=null)localName.Dispose();if(server!=null)server.Dispose();}base.Dispose(disposing);}
    bool Write(string text) {
        try {
            return (bool)Invoke(new Func<bool>(delegate {
                lock(state.Gate) {
                    if(!state.Enabled) return false;
                    try { if(text.Length==0) Clipboard.Clear(); else Clipboard.SetText(text,TextDataFormat.UnicodeText); }
                    catch(ExternalException) {return false;}
                    sequence=GetClipboardSequenceNumber(); state.Publish(text);
                    status.Text="Получен текст с iPhone · " + DateTime.Now.ToString("HH:mm:ss"); return true;
                }
            }));
        } catch(InvalidOperationException) {return false;}
    }
    void Observe(object sender,EventArgs e) {
        uint current=GetClipboardSequenceNumber(); if(current==sequence) return;
        lock(state.Gate) {
            if(!state.Enabled) {sequence=current;return;}
            try {
                if(Clipboard.ContainsFileDropList()) {
                    var copied=Clipboard.GetFileDropList();
                    sequence=current;
                    var paths=new List<string>();foreach(string path in copied) paths.Add(path);
                    SharePaths(paths);return;
                }
                if(!Clipboard.ContainsText(TextDataFormat.UnicodeText)) {sequence=current;return;}
                string text=Clipboard.GetText(TextDataFormat.UnicodeText);
                if(Encoding.UTF8.GetByteCount(text)>Server.MaxText) {sequence=current;status.Text="Текст больше 256 КБ: передача пропущена.";return;}
                state.Publish(text);server.RecordText();sequence=current;status.Text="Текст доступен в Safari · " + DateTime.Now.ToString("HH:mm:ss");
            } catch(ExternalException) {} // Clipboard is busy: retry on next tick.
        }
    }
}
static class Program {
    [STAThread] static void Main(string[] args) {
        if(args.Length>0 && args[0]=="--self-test") { SelfTest.Run(args.Length>1?args[1]:"self-test.txt");return; }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length>1&&args[0]=="--ui-test"){FeatureTests.RenderUi(args[1]);return;}
        try { Application.Run(new BridgeForm()); }
        catch(Exception e) { MessageBox.Show(e.Message,"Мост буфера",MessageBoxButtons.OK,MessageBoxIcon.Error); }
    }
}
}
