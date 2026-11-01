using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace WifiClipboard {
static class FileTests {
    internal sealed class Response {
        public int Status;
        public string Headers;
        public byte[] Body;
        public Dictionary<string,object> Json {get {return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(Body));}}
    }
    static void Check(bool condition,string name) {SelfTest.Check(condition,name);}
    internal static Response Read(NetworkStream stream,bool head) {
        var bytes=new List<byte>();
        while(true) {int c=stream.ReadByte();if(c<0)throw new EndOfStreamException();bytes.Add((byte)c);int n=bytes.Count;if(n>32768)throw new Exception("Headers too large");if(n>=4&&bytes[n-4]==13&&bytes[n-3]==10&&bytes[n-2]==13&&bytes[n-1]==10)break;}
        string headers=Encoding.ASCII.GetString(bytes.ToArray());int size=0;
        foreach(string line in headers.Split('\n')) if(line.StartsWith("Content-Length:")) size=int.Parse(line.Substring(15).Trim());
        byte[] body=new byte[head?0:size];int offset=0;
        while(offset<body.Length) {int n=stream.Read(body,offset,body.Length-offset);if(n==0)throw new EndOfStreamException();offset+=n;}
        return new Response {Status=int.Parse(headers.Split(' ')[1]),Headers=headers,Body=body};
    }
    internal static Response Request(Server server,string method,string path,byte[] body=null,string token=null,string extra="",string contentType="application/octet-stream",long declared=-1) {
        using(var client=new TcpClient("127.0.0.1",server.Port)) {
            client.ReceiveTimeout=10000;var stream=client.GetStream();
            body=body??new byte[0];
            string headers=method+" "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+server.Port+"\r\nContent-Length: "+(declared>=0?declared:body.LongLength)+"\r\nContent-Type: "+contentType+"\r\n";
            if(token!=null) headers+="X-Bridge-Token: "+token+"\r\n";
            headers+=extra+"Connection: close\r\n\r\n";
            byte[] h=Encoding.ASCII.GetBytes(headers);stream.Write(h,0,h.Length);if(body.Length>0)stream.Write(body,0,body.Length);
            return Read(stream,method=="HEAD");
        }
    }
    static bool Equal(byte[] a,byte[] b) {using(var hash=SHA256.Create())return hash.ComputeHash(a).SequenceEqual(hash.ComputeHash(b));}
    public static void Run() {
        string root=Path.Combine(Path.GetTempPath(),"wifi-file-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var state=new State();
            using(var s=new Server(state,0,new string[0],Path.Combine(root,"received"))) {
                var pairing=Request(s,"POST","/pair",Encoding.UTF8.GetBytes("{\"code\":\""+s.Code+"\"}"),null,"","application/json");
                string token=(string)pairing.Json["token"];
                Check(pairing.Headers.Contains("HttpOnly; SameSite=Strict"),"Native Safari downloads get an HttpOnly pairing cookie");
                Check(Request(s,"GET","/api/files").Status==401,"Anonymous file list blocked");
                Check(Request(s,"POST","/api/files").Status==401,"Anonymous file upload blocked");
                Check(Request(s,"GET","/download/unknown").Status==401,"Anonymous file download blocked");
                byte[] data=new byte[5*1024*1024+37];new Random(99).NextBytes(data);
                string binary=Path.Combine(root,"Фото 😀 и видео.bin");File.WriteAllBytes(binary,data);
                var share=s.ShareFile(binary);
                Check(s.ShareFile(binary).Id==share.Id && s.SharedCount==1,"Repeated file selection does not duplicate shares");
                var listing=Request(s,"GET","/api/files",null,token);
                Check(listing.Status==200&&Encoding.UTF8.GetString(listing.Body).Contains(share.Id),"Shared files listed with opaque IDs");
                Check(!Encoding.UTF8.GetString(listing.Body).Contains(root.Replace("\\","\\\\")),"Local file paths not exposed");
                var download=Request(s,"GET","/download/"+share.Id,null,token);
                Check(download.Status==200&&Equal(data,download.Body),"5 MiB binary download SHA-256 matches source");
                Check(download.Headers.Contains("Content-Type: application/octet-stream")&&download.Headers.Contains("Content-Disposition: attachment;")&&download.Headers.Contains("filename*=UTF-8''"),"Files forced to download with UTF-8 attachment names");
                var cookieDownload=Request(s,"GET","/download/"+share.Id,null,null,"Cookie: BridgeSession"+s.Port+"="+token+"\r\n");
                Check(cookieDownload.Status==200&&Equal(data,cookieDownload.Body),"Safari navigation downloads authenticate via session cookie");
                var head=Request(s,"HEAD","/download/"+share.Id,null,token);
                Check(head.Status==200&&head.Headers.Contains("Content-Length: "+data.Length),"HEAD supports Safari download metadata");
                var range=Request(s,"GET","/download/"+share.Id,null,token,"Range: bytes=17-300\r\n");
                Check(range.Status==206&&Equal(data.Skip(17).Take(284).ToArray(),range.Body),"Byte range download matches selected bytes");
                var suffix=Request(s,"GET","/download/"+share.Id,null,token,"Range: bytes=-31\r\n");
                Check(suffix.Status==206&&Equal(data.Skip(data.Length-31).ToArray(),suffix.Body),"Suffix range download matches source");
                Check(Request(s,"GET","/download/"+share.Id,null,token,"Range: bytes=9999999999-\r\n").Status==416,"Out of bounds range rejected");
                Check(Request(s,"GET","/download/"+share.Id,null,token,"Range: bytes=1-2,4-5\r\n").Status==416,"Multiple ranges rejected safely");
                string uploadHeaders="X-File-Name: "+Uri.EscapeDataString("Архив 😀.anything")+"\r\n";
                var upload=Request(s,"POST","/api/files",data,token,uploadHeaders);
                Check(upload.Status==200&&Equal(data,File.ReadAllBytes(Path.Combine(s.ReceiveDirectory,(string)upload.Json["name"]))),"5 MiB binary upload SHA-256 matches original");
                var duplicate=Request(s,"POST","/api/files",new byte[]{1,2,3},token,uploadHeaders);
                Check(duplicate.Status==200&&(string)duplicate.Json["name"]!=(string)upload.Json["name"]&&Equal(data,File.ReadAllBytes(Path.Combine(s.ReceiveDirectory,(string)upload.Json["name"]))),"Duplicate upload preserves existing file and chooses a new name");
                var traversal=Request(s,"POST","/api/files",new byte[]{9},token,"X-File-Name: "+Uri.EscapeDataString("../../escape.exe")+"\r\n");
                Check(traversal.Status==200&&(string)traversal.Json["name"]=="escape.exe"&&File.Exists(Path.Combine(s.ReceiveDirectory,"escape.exe"))&&!File.Exists(Path.Combine(root,"escape.exe")),"Traversal filenames cannot escape received folder; arbitrary extension preserved");
                Check(Server.SafeName("CON.txt")=="_CON.txt"&&Server.SafeName("LPT1")=="_LPT1"&&Server.SafeName("a:b?.zip")=="ab.zip", "Windows reserved and invalid filenames sanitized");
                var empty=Request(s,"POST","/api/files",new byte[0],token,"X-File-Name: empty.bin\r\n");
                Check(empty.Status==200&&new FileInfo(Path.Combine(s.ReceiveDirectory,"empty.bin")).Length==0,"Zero-byte upload supported");
                var emptyShare=s.ShareFile(Path.Combine(s.ReceiveDirectory,"empty.bin"));
                Check(Request(s,"GET","/download/"+emptyShare.Id,null,token).Body.Length==0,"Zero-byte download supported");
                Check(Request(s,"POST","/api/files",null,token,uploadHeaders,"application/octet-stream",Server.MaxFile+1).Status==413,"10 GiB upload size limit enforced before reading body");
                long start,end;
                Check(Server.ParseRange("bytes=4294967296-",8L*1024*1024*1024,out start,out end)&&start==4294967296L&&end==8589934591L,"64-bit file offsets above 4 GiB supported");
                state.Enabled=false;
                Check(Request(s,"POST","/api/files",null,token,uploadHeaders).Status==409&&Request(s,"GET","/download/"+share.Id,null,token).Status==409,"Pause blocks file uploads and downloads");
                state.Enabled=true;
                // Disconnect halfway through a declared upload; partial data must never become a received file.
                using(var client=new TcpClient("127.0.0.1",s.Port)) {
                    byte[] raw=Encoding.ASCII.GetBytes("POST /api/files HTTP/1.1\r\nHost: 127.0.0.1:"+s.Port+"\r\nX-Bridge-Token: "+token+"\r\nContent-Type: application/octet-stream\r\nX-File-Name: interrupted.dat\r\nContent-Length: 100000\r\n\r\nabc");
                    var partialStream=client.GetStream();partialStream.Write(raw,0,raw.Length);client.Client.Shutdown(SocketShutdown.Send);
                    Read(partialStream,false);
                }
                Check(!File.Exists(Path.Combine(s.ReceiveDirectory,"interrupted.dat"))&&Directory.GetFiles(s.ReceiveDirectory,"*.part").Length==0,"Disconnected uploads clean partial files and never expose incomplete data");
                File.AppendAllText(binary,"changed");
                Check(Request(s,"GET","/download/"+share.Id,null,token).Status==409,"Changed source file rejected rather than silently serving different data");
                s.ClearSharedFiles();
                Check(Request(s,"GET","/download/"+share.Id,null,token).Status==404,"Removing shares revokes download access");
                Request(s,"POST","/unpair",Encoding.UTF8.GetBytes("{}"),token,"","application/json");
                Check(Request(s,"GET","/download/"+emptyShare.Id,null,null,"Cookie: BridgeSession"+s.Port+"="+token+"\r\n").Status==401,"Unpair revokes native download cookies");
            }
        } finally {Directory.Delete(root,true);}
    }
}
}
