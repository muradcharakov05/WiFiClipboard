using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
namespace WifiClipboard {
public sealed partial class Server {
    internal static string PreviewMime(string name) {
        switch(Path.GetExtension(name).ToLowerInvariant()) {
            case ".jpg":case ".jpeg":return "image/jpeg";
            case ".png":return "image/png";
            case ".gif":return "image/gif";
            case ".webp":return "image/webp";
            case ".pdf":return "application/pdf";
            case ".mp4":case ".m4v":return "video/mp4";
            case ".mov":return "video/quicktime";
            case ".mp3":return "audio/mpeg";
            case ".m4a":return "audio/mp4";
            case ".wav":return "audio/wav";
            default:return null;
        }
    }
    static string ViewShell(string title,string content,string backUrl="/",string backText="← К обмену") {
        return "<!doctype html><html lang=ru><head><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1,viewport-fit=cover\"><meta name=apple-mobile-web-app-capable content=no><title>"+WebUtility.HtmlEncode(title)+"</title><style>html{color-scheme:dark}body{margin:0;background:#101821;color:#edf4fa;font:17px -apple-system,BlinkMacSystemFont,sans-serif}header{position:sticky;top:0;z-index:10;padding:max(18px,env(safe-area-inset-top)) 18px 18px;background:#1a2734;border-bottom:1px solid #486074}a{color:#83e0bf;display:inline-block;padding:12px 0;min-height:24px}main{padding:20px;max-width:960px;margin:auto}h1{font-size:22px;overflow-wrap:anywhere}p{line-height:1.6;color:#b3c4d3}.action{display:block;border-radius:12px;background:#83e0bf;color:#112a24;text-align:center;text-decoration:none;padding:16px;font-weight:600;margin:18px 0}.preview{width:100%;min-height:55vh;border:1px solid #314456;border-radius:12px;background:#fff}img{max-width:100%;height:auto}video,audio{width:100%}</style></head><body><header><a href=\""+WebUtility.HtmlEncode(backUrl)+"\">"+WebUtility.HtmlEncode(backText)+"</a></header><main>"+content+"<a href=\""+WebUtility.HtmlEncode(backUrl)+"\">"+WebUtility.HtmlEncode(backText)+"</a></main></body></html>";
    }
    bool HandleFileView(Stream stream,string method,string path,Dictionary<string,string> headers) {
        if(!path.StartsWith("/file/",StringComparison.Ordinal))return false;
        if(method!="GET"){Reply(stream,405,"Method rejected");return true;}
        string token=FileToken(headers,true);
        if(!Allowed(token)) {Reply(stream,200,ViewShell("Подключение","<h1>Откройте страницу обмена</h1><p>Возможно, передача приостановлена или нужно подключиться заново.</p>"),"text/html; charset=utf-8");return true;}
        SharedFile item;lock(fileGate)item=files.Find(f=>f.Id==path.Substring(6));
        if(item==null){Reply(stream,200,ViewShell("Файл недоступен","<h1>Доступ к файлу снят</h1><p>Выберите файл на Windows заново.</p>"),"text/html; charset=utf-8");return true;}
        string id=Uri.EscapeDataString(item.Id),name=WebUtility.HtmlEncode(item.Name),mime=PreviewMime(item.Name);
        string grant=GrantDownload(item.Id,token),host=headers["Host"],normal="http://"+host+"/safari/"+grant,safari=SafariLink(host,grant);
        string content="<h1>"+name+"</h1><a id=safariDownload class=action href=\""+WebUtility.HtmlEncode(safari)+"\" target=\"_blank\" rel=\"noopener noreferrer\">Открыть скачивание</a><p>Откройте страницу скачивания обычной ссылкой. Чтобы сохранить кнопки Safari, запускайте сайт как закладку, а не как веб-приложение.</p><details><summary>Ссылка для копирования</summary><p>Скопируйте ссылку ниже и вставьте её в адресную строку Safari. Она действует до 7 дней только для этого файла, пока программа открыта и доступ не отозван.</p><textarea id=downloadLink readonly aria-label=\"Ссылка для Safari\" style=\"width:100%;height:100px;font-size:16px\">"+WebUtility.HtmlEncode(normal)+"</textarea><p><a id=regularDownload href=\""+WebUtility.HtmlEncode(normal)+"\" target=\"_blank\" rel=\"noopener noreferrer\">Открыть обычную ссылку ↗</a></p></details><script src=\"/file-view.js\"></script>";
        if(mime!=null) {
            string src="/preview/"+id;
            if(mime.StartsWith("image/"))content+="<img src=\""+src+"\" alt=\""+name+"\">";
            else if(mime.StartsWith("video/"))content+="<video controls playsinline preload=metadata src=\""+src+"\"></video>";
            else if(mime.StartsWith("audio/"))content+="<audio controls preload=metadata src=\""+src+"\"></audio>";
            else content+="<iframe class=preview title=\"Просмотр PDF\" src=\""+src+"\"></iframe><p>Если PDF не отображается, откройте скачивание кнопкой выше.</p>";
        }else content+="<p>Для этого формата нет встроенного просмотра. Откройте скачивание кнопкой выше.</p>";
        Reply(stream,200,ViewShell(item.Name,content),"text/html; charset=utf-8");return true;
    }
    bool HandleHome(Stream stream,string method,string path,Dictionary<string,string> headers) {
        if(method!="GET")return false;
        if(path=="/file-view.js") {Reply(stream,200,Resource("file-view.js"),"application/javascript; charset=utf-8");return true;}
        if(path=="/manifest.webmanifest") {
            Reply(stream,200,"{\"name\":\"Мост буфера\",\"short_name\":\"Мост буфера\",\"id\":\"/\",\"start_url\":\"/\",\"scope\":\"/\",\"display\":\"browser\",\"background_color\":\"#101821\",\"theme_color\":\"#101821\",\"icons\":[{\"src\":\"/icon.png\",\"sizes\":\"1254x1254\",\"type\":\"image/png\"}]}","application/manifest+json; charset=utf-8");return true;
        }
        if(path=="/icon.png") {
            using(var input=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("app-icon.png")) {
                byte[] h=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: "+input.Length+"\r\nCache-Control: public, max-age=86400\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");stream.Write(h,0,h.Length);input.CopyTo(stream);
            }return true;
        }
        return false;
    }
}
}
