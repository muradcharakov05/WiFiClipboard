using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace WifiClipboard {
public sealed class SharedFile {
    public string Id, Name, Path;
    public long Size;
    public DateTime Modified;
}
public sealed partial class Server {
    public const long MaxFile = 10L * 1024 * 1024 * 1024;
    public readonly string ReceiveDirectory;
    public Action<string> FileNotice;
    readonly object fileGate = new object();
    readonly List<SharedFile> files = new List<SharedFile>();
    readonly Dictionary<TcpClient,string> fileOwners = new Dictionary<TcpClient,string>();
    readonly HashSet<TcpClient> downloadClients = new HashSet<TcpClient>();
    readonly HashSet<TcpClient> fileClients = new HashSet<TcpClient>();
    readonly SemaphoreSlim fileSlots = new SemaphoreSlim(3);
    readonly ManualResetEventSlim filesFinished = new ManualResetEventSlim(true);
    public int SharedCount { get { lock(fileGate) return files.Count; } }
    public SharedFile ShareFile(string path) {
        var info=new FileInfo(path);
        if(!info.Exists) throw new IOException("Файл не найден.");
        if((info.Attributes & FileAttributes.Directory)!=0) throw new IOException("Выберите файл. Папку сначала упакуйте в ZIP.");
        if(info.Length>MaxFile) throw new IOException("Файл больше 10 ГиБ: "+info.Name);
        lock(fileGate) {
            var existing=files.Find(f=>string.Equals(f.Path,info.FullName,StringComparison.OrdinalIgnoreCase));
            if(existing!=null) return existing;
            if(files.Count>=100) throw new IOException("Можно открыть доступ к 100 файлам. Сначала снимите доступ к прежним.");
            var item=new SharedFile {Id=Guid.NewGuid().ToString("N"),Name=info.Name,Path=info.FullName,Size=info.Length,Modified=info.LastWriteTimeUtc};
            files.Add(item);return item;
        }
    }
    public void ClearSharedFiles() { lock(fileGate) {files.Clear();foreach(var client in downloadClients)try{client.Close();}catch{}}for(int i=0;i<30;i++){lock(fileGate)if(downloadClients.Count==0)break;Thread.Sleep(10);}CleanupArchives(); }
    bool Allowed(string token) {
        if(stopped) return false;
        lock(state.Gate) if(!state.Enabled) return false;
        lock(gate) return token!=null && tokens.Contains(TrustStore.Fingerprint(token));
    }
    void Notice(string text) { var callback=FileNotice; if(callback!=null) try {callback(text);} catch {} }
    public void CancelFileTransfers() {
        lock(fileGate) foreach(var client in fileClients) try {client.Close();} catch {}
    }
    void StopFileTransfers() {
        CancelFileTransfers();
        filesFinished.Wait(3000);
    }
    string FileToken(Dictionary<string,string> headers,bool allowCookie) {
        string token;
        if(headers.TryGetValue("X-Bridge-Token",out token) && !string.IsNullOrEmpty(token)) return token;
        string cookie;
        if(allowCookie && headers.TryGetValue("Cookie",out cookie)) {
            string prefix="BridgeSession"+Port+"=";
            foreach(string part in cookie.Split(';')) if(part.Trim().StartsWith(prefix,StringComparison.Ordinal)) return part.Trim().Substring(prefix.Length);
        }
        return null;
    }
    bool HandleFiles(TcpClient client,string method,string path,Dictionary<string,string> headers) {
        bool preview=path.StartsWith("/preview/",StringComparison.Ordinal);
        bool download=preview||path.StartsWith("/download/",StringComparison.Ordinal);
        if(path!="/api/files" && !download) return false;
        var stream=client.GetStream();
        string token=FileToken(headers,true);
        bool valid;lock(gate)valid=token!=null && tokens.Contains(TrustStore.Fingerprint(token));
        if(!valid) {Reply(stream,401,"Подключитесь заново.");return true;}
        if(!Allowed(token)) {Reply(stream,409,"Передача приостановлена на Windows.");return true;}
        if(path=="/api/files" && method=="GET") {
            object[] list;
            lock(fileGate) list=files.Select(f=>(object)new {id=f.Id,name=f.Name,size=f.Size}).ToArray();
            Json(stream,new {files=list,maxFileSize=MaxFile});return true;
        }
        if((download && method!="GET" && method!="HEAD") || (!download && method!="POST")) {Reply(stream,405,"Method rejected");return true;}
        if(!fileSlots.Wait(0)) {Reply(stream,429,"Уже выполняются три передачи файлов. Дождитесь завершения.");return true;}
        lock(fileGate) {if(stopped) {fileSlots.Release();return true;}fileClients.Add(client);if(download)downloadClients.Add(client);fileOwners[client]=TrustStore.Fingerprint(token);filesFinished.Reset();}
        try {
            client.ReceiveTimeout=30000;client.SendTimeout=30000;
            if(download) DownloadFile(stream,method,path.Substring(preview?9:10),headers,token,preview);
            else UploadFile(stream,headers,token);
        } catch(IOException) { Notice("Передача файла прервана или файл недоступен."); }
          catch(UnauthorizedAccessException) { try {Reply(stream,507,"Нет доступа к файлу или папке полученных файлов.");}catch {} }
          finally {lock(fileGate) {fileClients.Remove(client);downloadClients.Remove(client);fileOwners.Remove(client);if(fileClients.Count==0)filesFinished.Set();}fileSlots.Release();}
        return true;
    }
    internal static string SafeName(string name) {
        // Client filenames never become paths. Keep the visible basename, stripping Windows hazards.
        name=(name??"").Replace('\\','/');name=name.Substring(name.LastIndexOf('/')+1);
        var clean=new StringBuilder();
        foreach(char c in name) if(!char.IsControl(c) && Array.IndexOf(Path.GetInvalidFileNameChars(),c)<0) clean.Append(c);
        name=clean.ToString().Trim().TrimEnd('.',' ');
        if(name.Length>160) name=name.Substring(0,120)+Path.GetExtension(name).Substring(0,Math.Min(32,Path.GetExtension(name).Length));
        name=name.TrimEnd('.',' ');
        string root=name.Split('.')[0].ToUpperInvariant();
        if(root=="CON"||root=="PRN"||root=="AUX"||root=="NUL"||root=="CLOCK$"||root=="CONIN$"||root=="CONOUT$"||System.Text.RegularExpressions.Regex.IsMatch(root,@"^(COM|LPT)[1-9¹²³]$")) name="_"+name;
        if(name.Length==0) name="file";
        return name;
    }
    void UploadFile(Stream stream,Dictionary<string,string> headers,string token) {
        long length;string encoded,ct,lengthHeader;
        if(!headers.TryGetValue("Content-Type",out ct)||ct!="application/octet-stream") {Reply(stream,415,"Нужен двоичный файл.");return;}
        if(!headers.TryGetValue("Content-Length",out lengthHeader)||!long.TryParse(lengthHeader,NumberStyles.None,CultureInfo.InvariantCulture,out length)||length<0||length>MaxFile) {Reply(stream,413,"Допускается до 10 ГиБ на файл.");return;}
        if(!headers.TryGetValue("X-File-Name",out encoded)||encoded.Length>4096) {Reply(stream,400,"Не указано имя файла.");return;}
        string name;
        try {name=SafeName(Uri.UnescapeDataString(encoded));} catch {Reply(stream,400,"Неверное имя файла.");return;}
        Directory.CreateDirectory(ReceiveDirectory);
        // Never accept uploads through a receive folder redirected to an unrelated directory.
        if((File.GetAttributes(ReceiveDirectory)&FileAttributes.ReparsePoint)!=0) {Reply(stream,507,"Папка полученных файлов не должна быть ссылкой.");return;}
        var drive=new DriveInfo(Path.GetPathRoot(ReceiveDirectory));
        if(drive.AvailableFreeSpace<length+16*1024*1024) {Reply(stream,507,"На компьютере недостаточно свободного места.");return;}
        string temporary=Path.Combine(ReceiveDirectory,".wifi-"+Guid.NewGuid().ToString("N")+".part");
        bool complete=false;
        try {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,FileOptions.SequentialScan)) {
                byte[] buffer=new byte[131072];long remaining=length;
                while(remaining>0) {
                    if(!Allowed(token)) {Reply(stream,409,"Передача остановлена. Отправьте файл заново.");return;}
                    int n=stream.Read(buffer,0,(int)Math.Min(buffer.Length,remaining));
                    if(n==0) throw new EndOfStreamException();
                    output.Write(buffer,0,n);remaining-=n;
                }
                output.Flush(true);
            }
            if(!Allowed(token)) {Reply(stream,409,"Передача остановлена.");return;}
            string destination=null;
            for(int i=0;i<10000;i++) {
                destination=Path.Combine(ReceiveDirectory,i==0?name:Path.GetFileNameWithoutExtension(name)+" ("+i+")"+Path.GetExtension(name));
                try {File.Move(temporary,destination);complete=true;break;}
                catch(IOException) {if(!File.Exists(destination))throw;}
            }
            if(!complete) throw new IOException("Не удалось выбрать свободное имя.");
            string saved=Path.GetFileName(destination);
            Notice("Получен файл: "+saved);
            Record("iPhone → Windows",saved,length,token,"Сохранён");
            Json(stream,new {ok=true,name=saved,size=length});
        } catch(IOException) {try {Reply(stream,507,"Файл не сохранён: обрыв связи, ошибка диска или недостаточно места. Повторите передачу.");}catch {}}
          finally {if(!complete)try {File.Delete(temporary);}catch {}}
    }
    void DownloadFile(Stream stream,string method,string id,Dictionary<string,string> headers,string token,bool preview=false) {
        SharedFile item;lock(fileGate)item=files.Find(f=>f.Id==id);
        if(item==null) {Reply(stream,404,"Файл больше не доступен.");return;}
        string mime=preview?PreviewMime(item.Name):"application/octet-stream";
        if(mime==null){Reply(stream,415,"Для этого типа доступно только скачивание.");return;}
        FileStream input;
        try {input=new FileStream(item.Path,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.SequentialScan);}
        catch(IOException) {Reply(stream,404,"Файл перемещён, занят или удалён. Выберите его на Windows заново.");return;}
        using(input) {
            if(input.Length!=item.Size||File.GetLastWriteTimeUtc(item.Path)!=item.Modified) {Reply(stream,409,"Файл изменён. Снимите доступ и выберите его на Windows заново.");return;}
            string etag="\""+item.Id+"-"+item.Size+"-"+item.Modified.Ticks+"\"";
            long start=0,end=input.Length-1;string range,ifRange;bool partial=headers.TryGetValue("Range",out range);
            if(partial&&headers.TryGetValue("If-Range",out ifRange)&&ifRange!=etag)partial=false;
            if(partial && !ParseRange(range,input.Length,out start,out end)) {Reply(stream,416,"Недопустимый диапазон.",null,"Content-Range: bytes */"+input.Length+"\r\n");return;}
            long count=input.Length==0?0:end-start+1;
            string extra="ETag: "+etag+"\r\nAccept-Ranges: bytes\r\nContent-Disposition: "+(preview?"inline":"attachment")+"; filename=\"download\"; filename*=UTF-8''"+Uri.EscapeDataString(SafeName(item.Name))+"\r\n";
            if(partial) extra+="Content-Range: bytes "+start+"-"+end+"/"+input.Length+"\r\n";
            string head="HTTP/1.1 "+(partial?"206 Partial Content":"200 OK")+"\r\nContent-Type: "+mime+"\r\nContent-Length: "+count+"\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'\r\n"+extra+"\r\n";
            byte[] headBytes=Encoding.ASCII.GetBytes(head);stream.Write(headBytes,0,headBytes.Length);
            if(method=="HEAD")return;
            if(!preview)Record("Windows → iPhone",item.Name,item.Size,token,partial?"Продолжение скачивания":"Скачивание начато","download-"+item.Id+"-"+TrustStore.Fingerprint(token));
            input.Position=start;byte[] buffer=new byte[131072];long remaining=count;
            try { while(remaining>0) {
                if(!Allowed(token))return;
                lock(fileGate) if(!files.Contains(item)) return;
                int n=input.Read(buffer,0,(int)Math.Min(buffer.Length,remaining));if(n==0)throw new EndOfStreamException();
                stream.Write(buffer,0,n);remaining-=n;
            }
            } finally {if(!preview)Record("Windows → iPhone",item.Name,item.Size,token,remaining>0?"Прервано · можно продолжить":partial?"Диапазон передан Safari":"Передан Safari","download-"+item.Id+"-"+TrustStore.Fingerprint(token));}
        }
    }
    internal static bool ParseRange(string value,long size,out long start,out long end) {
        start=0;end=size-1;
        if(size==0||!value.StartsWith("bytes=",StringComparison.Ordinal)||value.Contains(","))return false;
        string[] parts=value.Substring(6).Split('-');long a,b;
        if(parts.Length!=2)return false;
        if(parts[0]=="") {
            if(!long.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out b)||b<=0)return false;
            start=Math.Max(0,size-b);return true;
        }
        if(!long.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out a)||a>=size)return false;
        start=a;
        if(parts[1]!="") {if(!long.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out b)||b<a)return false;end=Math.Min(b,end);}
        return true;
    }
}
}
