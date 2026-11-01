using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
namespace WifiClipboard {
public sealed partial class Server {
    sealed class DownloadGrant {public string FileId,SourceToken;public DateTime Expires;}
    readonly object grantGate=new object();
    readonly Dictionary<string,DownloadGrant> grants=new Dictionary<string,DownloadGrant>();
    internal string GrantDownload(string fileId,string token,TimeSpan? lifetime=null) {
        if(!Allowed(token))throw new InvalidOperationException("Подключение неактивно.");
        lock(fileGate)if(!files.Any(f=>f.Id==fileId))throw new FileNotFoundException();
        byte[] bytes=new byte[32];using(var random=RandomNumberGenerator.Create())random.GetBytes(bytes);
        string key=Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
        lock(grantGate) {
            foreach(string stale in grants.Where(g=>g.Value.Expires<=DateTime.UtcNow).Select(g=>g.Key).ToArray())grants.Remove(stale);
            if(grants.Count>=128)grants.Remove(grants.OrderBy(g=>g.Value.Expires).First().Key);
            grants.Add(key,new DownloadGrant {FileId=fileId,SourceToken=token,Expires=DateTime.UtcNow.Add(lifetime??TimeSpan.FromDays(7))});
        }
        return key;
    }
    internal static string SafariLink(string host,string key) {return "http://"+host+"/safari/"+Uri.EscapeDataString(key);}
    string DownloadHelp(string title,string text,string back="/") {
        return ViewShell(title,"<h1>"+WebUtility.HtmlEncode(title)+"</h1><p>"+WebUtility.HtmlEncode(text)+"</p><p>Для обмена текстом и файлами откройте значок «Мост буфера» на экране «Домой».</p>",back,"← К обмену");
    }
    bool HandleSafariDownloads(TcpClient client,string method,string path,Dictionary<string,string> headers) {
        bool preview=path.StartsWith("/safari-preview/",StringComparison.Ordinal),viewer=path.StartsWith("/safari-view/",StringComparison.Ordinal);
        bool binary=preview||path.StartsWith("/safari-file/",StringComparison.Ordinal);
        if(!binary&&!viewer&&!path.StartsWith("/safari/",StringComparison.Ordinal))return false;
        string prefix=preview?"/safari-preview/":viewer?"/safari-view/":binary?"/safari-file/":"/safari/";
        var stream=client.GetStream();string key=path.Substring(prefix.Length);
        if(method!="GET"&&method!="HEAD"){Reply(stream,405,"Method rejected");return true;}
        DownloadGrant grant;
        lock(grantGate)grants.TryGetValue(key,out grant);
        if(grant==null||grant.Expires<=DateTime.UtcNow||!Allowed(grant.SourceToken)) {
            Reply(stream,410,DownloadHelp("Ссылка недоступна","Вернитесь в «Мост буфера» и откройте страницу файла ещё раз. Возможно, срок ссылки истёк или передача остановлена."),"text/html; charset=utf-8");return true;
        }
        SharedFile item;lock(fileGate)item=files.Find(f=>f.Id==grant.FileId);
        if(item==null){Reply(stream,404,DownloadHelp("Доступ к файлу снят","Выберите файл на Windows заново."),"text/html; charset=utf-8");return true;}
        string landing="/safari/"+Uri.EscapeDataString(key),filePage="/safari-view/"+Uri.EscapeDataString(key);
        if(viewer) {
            string name=WebUtility.HtmlEncode(item.Name),mime=PreviewMime(item.Name),src="/safari-preview/"+Uri.EscapeDataString(key);
            string content="<h1>"+name+"</h1><a class=action href=\""+landing+"\">Перейти к скачиванию</a><p>Это страница выбранного файла.</p>";
            if(mime!=null) {
                if(mime.StartsWith("image/"))content+="<img src=\""+src+"\" alt=\""+name+"\">";
                else if(mime.StartsWith("video/"))content+="<video controls playsinline preload=metadata src=\""+src+"\"></video>";
                else if(mime.StartsWith("audio/"))content+="<audio controls preload=metadata src=\""+src+"\"></audio>";
                else content+="<iframe class=preview title=\"Просмотр PDF\" src=\""+src+"\"></iframe>";
            }else content+="<p>Для этого типа доступно скачивание без предпросмотра.</p>";
            Reply(stream,200,ViewShell(item.Name,content),"text/html; charset=utf-8");return true;
        }
        if(!binary) {
            string target="/safari-file/"+Uri.EscapeDataString(key);
            string content="<h1>"+WebUtility.HtmlEncode(item.Name)+"</h1><p>Скачайте файл кнопкой ниже. Страница файла доступна по ссылке сверху.</p><a id=saveFile class=action href=\""+target+"\" target=\"_blank\" rel=\"noopener noreferrer\">Скачать файл</a><p>Если iOS откроет системный просмотрщик, используйте навигацию Safari или вернитесь в исходную вкладку.</p>";
            Reply(stream,200,ViewShell(item.Name,content,filePage,"← К странице файла"),"text/html; charset=utf-8");return true;
        }
        if(!fileSlots.Wait(0)){Reply(stream,429,"Дождитесь завершения других передач.");return true;}
        lock(fileGate){if(stopped){fileSlots.Release();return true;}fileClients.Add(client);downloadClients.Add(client);fileOwners[client]=TrustStore.Fingerprint(grant.SourceToken);filesFinished.Reset();}
        try {client.SendTimeout=30000;DownloadFile(stream,method,grant.FileId,headers,grant.SourceToken,preview);}
        finally {lock(fileGate){fileClients.Remove(client);downloadClients.Remove(client);fileOwners.Remove(client);if(fileClients.Count==0)filesFinished.Set();}fileSlots.Release();}
        return true;
    }
}
}
