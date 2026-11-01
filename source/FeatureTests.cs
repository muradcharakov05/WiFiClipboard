using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;
namespace WifiClipboard {
static class FeatureTests {
 static void Check(bool b,string n){SelfTest.Check(b,n);}
 static byte[] Json(object v){return Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(v));}
 static FileTests.Response Send(Server s,string route,object body,string token){return FileTests.Request(s,"POST",route,Json(body),token,"","application/json");}
 static FileTests.Response Get(Server s,string route,string token){return FileTests.Request(s,"GET",route,null,token);}
 static string Pair(Server s,string name){return (string)Send(s,"/pair",new{code=s.Code,name=name},null).Json["token"];}
 static FileTests.Response Start(Server s,string token,string id,string name,long size){return Send(s,"/api/uploads/start",new{id=id,name=name,size=size,modified="1700000000000"},token);}
 static FileTests.Response Chunk(Server s,string id,string token,long offset,byte[] bytes,string hash=null){return FileTests.Request(s,"POST","/api/uploads/"+id,bytes,token,"X-Upload-Offset: "+offset+"\r\nX-Chunk-SHA256: "+(hash??Server.Hash(bytes))+"\r\n");}
 static bool Same(byte[] a,byte[] b){return Server.Hash(a)==Server.Hash(b);}
 internal static void RenderUi(string output){string root=Path.Combine(Path.GetTempPath(),"wifi-ui-test-"+Guid.NewGuid().ToString("N"));try{using(var form=new BridgeForm(root)){form.Opacity=0;form.ShowInTaskbar=false;form.Show();System.Windows.Forms.Application.DoEvents();form.PerformLayout();using(var image=new System.Drawing.Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new System.Drawing.Rectangle(0,0,form.Width,form.Height));image.Save(output,System.Drawing.Imaging.ImageFormat.Png);}foreach(System.Windows.Forms.Control c in form.Controls){var panel=c as System.Windows.Forms.FlowLayoutPanel;if(panel!=null){panel.AutoScrollPosition=new System.Drawing.Point(0,650);using(var image=new System.Drawing.Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new System.Drawing.Rectangle(0,0,form.Width,form.Height));image.Save(Path.Combine(Path.GetDirectoryName(output),"ui-bottom.png"),System.Drawing.Imaging.ImageFormat.Png);}}}}}finally{if(Directory.Exists(root))Directory.Delete(root,true);}}
 internal static void Run(string reportDirectory){
  string root=Path.Combine(Path.GetTempPath(),"wifi-v7-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);string recv=Path.Combine(root,"received"),trust=Path.Combine(root,"trust"),first=null,second=null,id=Guid.NewGuid().ToString("N"),savedName=null;int port=0;
  byte[] block=new byte[Server.ChunkSize],tail=new byte[12345];new Random(44).NextBytes(block);new Random(55).NextBytes(tail);
  try{
   using(var s=new Server(new State(),0,new string[0],recv,trust)){
    Check(Get(s,"/transfer.js",null).Status==200&&Encoding.UTF8.GetString(Get(s,"/",null).Body).Contains("/transfer.js"),"Phone page loads the embedded resumable transfer script");
    port=s.Port;first=Pair(s,"Тестовый телефон");second=Pair(s,"Запасной iPhone");
    Check(s.Devices().Length==2&&s.Devices().Any(d=>d.Name=="Тестовый телефон"),"Named pairing records separate devices");
    Check(Get(s,"/api/device",first).Json["name"].ToString()=="Тестовый телефон","Phone reads its device name");
    Check(Send(s,"/api/device",new{name="Рабочий iPhone"},first).Status==200&&s.Devices().Any(d=>d.Name=="Рабочий iPhone"),"Phone can rename its own connection");
    s.RenameDevice(TrustStore.Fingerprint(second),"Запасной");Check(Get(s,"/api/device",second).Json["name"].ToString()=="Запасной","Windows renames a selected connection");
    Check(Get(s,"/api/history",null).Status==401,"Anonymous history is blocked");
    Check(Start(s,null,id,"test.bin",1).Status==401,"Anonymous resumable upload is blocked");
    Check(Start(s,first,"../escape","test.bin",1).Status==400,"Invalid resumable IDs cannot become paths");
    Check(Start(s,first,id,"Архив 😀.bin",block.Length+tail.Length).Status==200,"Resumable upload begins");
    Check(Start(s,first,id,"Архив 😀.bin",block.Length+tail.Length).Status==200&&Directory.GetFiles(Path.Combine(recv,".WiFiClipboard-resume"),"*.json").Length==1,"Repeating a lost start response is idempotent");
    Check(Start(s,second,id,"Архив 😀.bin",block.Length+tail.Length).Status==409,"Other device cannot claim a known upload ID");
    Check(Get(s,"/api/uploads/"+id,second).Status==404,"Resumable status is scoped to its owner");
    Check(Chunk(s,id,first,1,block).Status==409,"Incorrect chunk offset is rejected");
    Check(Chunk(s,id,first,0,block,new string('0',64)).Status==400&&(int)Get(s,"/api/uploads/"+id,first).Json["offset"]==0,"Corrupt chunk does not advance offset");
    Check(Chunk(s,id,first,0,block).Status==200,"First 2 MiB chunk is committed");
    Check(Chunk(s,id,first,0,block).Status==409&&(int)Get(s,"/api/uploads/"+id,first).Json["offset"]==block.Length,"Duplicate chunk cannot append duplicate data");
    Check(Send(s,"/api/uploads/"+id+"/finish",new{},first).Status==409,"Incomplete file cannot be finalized");
    using(var client=new TcpClient("127.0.0.1",s.Port)){
     client.ReceiveTimeout=5000;string request="POST /api/uploads/"+id+" HTTP/1.1\r\nHost: 127.0.0.1:"+s.Port+"\r\nX-Bridge-Token: "+first+"\r\nContent-Type: application/octet-stream\r\nX-Upload-Offset: "+block.Length+"\r\nContent-Length: "+tail.Length+"\r\n\r\npartial";byte[] bytes=Encoding.ASCII.GetBytes(request);var stream=client.GetStream();stream.Write(bytes,0,bytes.Length);client.Client.Shutdown(SocketShutdown.Send);while(stream.ReadByte()!=-1){}
    }
    Check((int)Get(s,"/api/uploads/"+id,first).Json["offset"]==block.Length&&!File.Exists(Path.Combine(recv,"Архив 😀.bin")),"Wi-Fi interruption retains previous chunks and hides incomplete file");
    string folder=Path.Combine(root,"Папка 😀");Directory.CreateDirectory(Path.Combine(folder,"вложенная","пустая"));File.WriteAllBytes(Path.Combine(folder,"вложенная","a.bin"),tail);File.WriteAllText(Path.Combine(folder,"текст.txt"),"Привет",Encoding.UTF8);
    var zipped=s.SharePath(folder);Check(zipped.Name.EndsWith(".zip")&&zipped.Size>0,"Selecting a folder creates and shares a ZIP");
    using(var zip=ZipFile.OpenRead(zipped.Path)){Check(zip.GetEntry("вложенная/пустая/")!=null&&zip.GetEntry("текст.txt")!=null,"ZIP preserves nested empty folders and Unicode names");using(var entry=zip.GetEntry("вложенная/a.bin").Open())using(var output=new MemoryStream()){entry.CopyTo(output);Check(Same(output.ToArray(),tail),"ZIP content matches original bytes");}}
    Check(Get(s,"/download/"+zipped.Id,first).Status==200,"Generated ZIP is downloadable");s.ClearSharedFiles();Check(!File.Exists(zipped.Path),"Removing shares removes owned temporary ZIP without deleting original folder");Check(File.Exists(Path.Combine(folder,"текст.txt")),"Original folder is preserved");
    var headFile=Path.Combine(root,"download.bin");File.WriteAllBytes(headFile,tail);var shared=s.ShareFile(headFile);
    var full=Get(s,"/download/"+shared.Id,first);string etag=full.Headers.Split('\n').First(line=>line.StartsWith("ETag:")).Substring(5).Trim();
    var resumed=FileTests.Request(s,"GET","/download/"+shared.Id,null,first,"Range: bytes=55-\r\nIf-Range: "+etag+"\r\n");Check(resumed.Status==206&&Same(resumed.Body,tail.Skip(55).ToArray()),"Matching ETag resumes Safari download from the requested byte");
    Check(FileTests.Request(s,"GET","/download/"+shared.Id,null,first,"Range: bytes=55-\r\nIf-Range: \"old\"\r\n").Status==200,"Stale If-Range returns whole file rather than mismatched bytes");
    Check(s.History().Length>0&&Get(s,"/api/history",first).Status==200,"Transfer history is available on Windows and authenticated phone");
   }
   string part=Path.Combine(recv,".WiFiClipboard-resume",id+".part");using(var append=new FileStream(part,FileMode.Append))append.WriteByte(99);
   using(var s=new Server(new State(),port,new string[0],recv,trust)){
    Check(s.Devices().Any(d=>d.Name=="Рабочий iPhone")&&s.Devices().Any(d=>d.Name=="Запасной"),"Device names persist across restart");
    Check(s.History().Length>0,"History persists across restart");
    Check(new FileInfo(part).Length==block.Length&&(int)Get(s,"/api/uploads/"+id,first).Json["offset"]==block.Length,"Restart restores committed offset and truncates uncommitted tail");
    Check(Chunk(s,id,first,block.Length,tail).Status==200,"Upload resumes after Windows restart");
    var done=Send(s,"/api/uploads/"+id+"/finish",new{},first);savedName=(string)done.Json["name"];
    Check(done.Status==200&&(bool)done.Json["done"]&&Same(File.ReadAllBytes(Path.Combine(recv,savedName)),block.Concat(tail).ToArray()),"Completed resumed upload SHA-256 matches entire source");
    Check((bool)Send(s,"/api/uploads/"+id+"/cancel",new{},first).Json["done"]&&File.Exists(Path.Combine(recv,savedName)),"Cancelling after a lost completion response preserves the completed file");
    var again=Send(s,"/api/uploads/"+id+"/finish",new{},first);Check(again.Status==200&&(string)again.Json["name"]==savedName&&Directory.GetFiles(recv).Length==1,"Lost finish response can be retried without duplicate files");
    string empty=Guid.NewGuid().ToString("N");Start(s,first,empty,"empty.bin",0);Check(Send(s,"/api/uploads/"+empty+"/finish",new{},first).Status==200&&new FileInfo(Path.Combine(recv,"empty.bin")).Length==0,"Resumable protocol supports empty files");
    string cancel=Guid.NewGuid().ToString("N");Start(s,first,cancel,"cancel.bin",block.Length);Chunk(s,cancel,first,0,block);Check(Send(s,"/api/uploads/"+cancel+"/cancel",new{},second).Status==404,"Other device cannot cancel a transfer");
    Check(Send(s,"/api/uploads/"+cancel+"/cancel",new{},first).Status==200&&!File.Exists(Path.Combine(recv,".WiFiClipboard-resume",cancel+".part")),"Cancel removes partial upload");
    s.RevokeDevice(TrustStore.Fingerprint(first));Check(Get(s,"/session",first).Status==401&&Get(s,"/session",second).Status==200,"Revoking one device leaves the other connected");Check(Get(s,"/api/uploads/"+id,first).Status==401,"Revocation blocks access to saved upload sessions");
    s.ClearHistory();Check(s.History().Length==0&&File.Exists(Path.Combine(recv,savedName)),"Clearing history keeps received files");
   }
   using(var s=new Server(new State(),port,new string[0],recv,trust)){Check(s.Devices().Length==1&&Get(s,"/session",second).Status==200&&Get(s,"/session",first).Status==401,"Individual revocation survives restart");Check(s.History().Length==0,"History clear survives restart");}
   using(var qr=ConnectionQr.Draw("http://wifi-clipboard-0123456789.local:8765/#code=12345678"))qr.Save(Path.Combine(reportDirectory,"QR-ПРОВЕРКА.png"),System.Drawing.Imaging.ImageFormat.Png);
   using(var qr=ConnectionQr.Draw("http://192.0.2.55:8765/#code=12345678"))qr.Save(Path.Combine(reportDirectory,"QR-IP-ПРОВЕРКА.png"),System.Drawing.Imaging.ImageFormat.Png);
   Check(ConnectionQr.Matrix("http://127.0.0.1:8765/").GetLength(0)==33,"Connection QR includes a four-module quiet zone in its bitmap");
  }finally{Directory.Delete(root,true);}
 }
}
}
