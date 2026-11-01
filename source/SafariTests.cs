using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
namespace WifiClipboard {
static class SafariTests {
    static string Text(FileTests.Response r){return Encoding.UTF8.GetString(r.Body);}
    static void Check(bool condition,string title){SelfTest.Check(condition,title);}
    public static void Run() {
        string root=Path.Combine(Path.GetTempPath(),"wifi-safari-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            var state=new State();using(var s=new Server(state,0,new string[0],Path.Combine(root,"received"))) {
                var pair=FileTests.Request(s,"POST","/pair",Encoding.UTF8.GetBytes("{\"code\":\""+s.Code+"\"}"),null,"","application/json");string token=(string)pair.Json["token"];
                byte[] bytes=new byte[1024*1024+19];new Random(88).NextBytes(bytes);string source=Path.Combine(root,"064.JPG");File.WriteAllBytes(source,bytes);var file=s.ShareFile(source);
                string page=Text(FileTests.Request(s,"GET","/file/"+file.Id,null,token));
                var match=Regex.Match(page,@"href=""http://[^""]+/safari/([A-Za-z0-9_-]+)""");
                Check(match.Success,"iPhone file page has a valid ordinary HTTP download link");string key=match.Groups[1].Value;
                Check(!page.Contains(token)&&key.Length==43,"Safari handoff does not expose the trusted browser token");
                Check(page.Contains("id=downloadLink")&&page.Contains("id=regularDownload"),"Safari handoff has a copyable URL and normal-browser fallback");
                var landing=FileTests.Request(s,"GET","/safari/"+key);
                Check(landing.Status==200&&Text(landing).Contains("064.JPG")&&Text(landing).Contains("/safari-file/"+key),"Safari landing works without cookies from the Home Screen app");
                var back=Regex.Match(Text(landing),@"<header><a href=""([^""]+)""");
                Check(back.Success&&back.Groups[1].Value=="/safari-view/"+key,"Back points to a distinct file page rather than reloading the download landing");
                var returned=FileTests.Request(s,"GET",back.Groups[1].Value);
                Check(returned.Status==200&&Text(returned).Contains("064.JPG")&&Text(returned).Contains("Это страница выбранного файла"),"Following Back really returns file details without requiring Safari cookies");
                Check(Text(returned).Contains("href=\"/\"")&&Text(returned).Contains("/safari/"+key),"File details offer exchange navigation and a route to downloading");
                var imagePreview=FileTests.Request(s,"GET","/safari-preview/"+key);
                Check(imagePreview.Status==200&&imagePreview.Headers.Contains("Content-Type: image/jpeg")&&imagePreview.Body.SequenceEqual(bytes),"Returned file page can preview its exact JPEG without sharing session cookies");
                Check(!page.Contains("x-safari-")&&!Text(landing).Contains("x-safari-"),"No generated download page contains the rejected custom URL scheme");
                Check(new Uri(Server.SafariLink("127.0.0.1:"+s.Port,key)).Scheme=="http","Download link parses as a standard browser HTTP URL");
                Check(!landing.Headers.Contains("Set-Cookie:"),"Single-file handoff never pairs Safari or issues a full-access cookie");
                var download=FileTests.Request(s,"GET","/safari-file/"+key);
                Check(download.Status==200&&bytes.SequenceEqual(download.Body),"Safari can download the exact file with its scoped URL and no session cookie");
                Check(download.Headers.Contains("Content-Disposition: attachment;"),"Safari transfer retains attachment download headers");
                var range=FileTests.Request(s,"GET","/safari-file/"+key,null,null,"Range: bytes=7-20\r\n");
                Check(range.Status==206&&range.Body.SequenceEqual(bytes.Skip(7).Take(14)),"Safari handoff supports ranged requests for the selected file");
                var head=FileTests.Request(s,"HEAD","/safari-file/"+key);
                Check(head.Status==200&&head.Headers.Contains("Content-Length: "+bytes.Length),"Safari handoff supports HEAD metadata");
                Check(FileTests.Request(s,"GET","/api/text",null,key).Status==401&&FileTests.Request(s,"GET","/api/files",null,key).Status==401,"Download-only grant cannot access clipboard or file list");
                Check(FileTests.Request(s,"GET","/download/"+file.Id,null,key).Status==401,"Download-only grant is not a device authentication token");
                Check(FileTests.Request(s,"GET","/safari/unknown").Status==410,"Unknown Safari links are refused with a recoverable HTML page");
                string expired=s.GrantDownload(file.Id,token,TimeSpan.FromSeconds(-1));
                Check(FileTests.Request(s,"GET","/safari-file/"+expired).Status==410,"Expired Safari file link is rejected");
                state.Enabled=false;Check(FileTests.Request(s,"GET","/safari-file/"+key).Status==410,"Pause also blocks Safari download links");state.Enabled=true;
                s.ClearSharedFiles();Check(FileTests.Request(s,"GET","/safari-file/"+key).Status==404,"Removing shared file revokes existing Safari grants");
                file=s.ShareFile(source);string first=s.GrantDownload(file.Id,token);string latest=null;
                for(int i=0;i<129;i++)latest=s.GrantDownload(file.Id,token);
                Check(FileTests.Request(s,"GET","/safari/"+first).Status==410&&FileTests.Request(s,"GET","/safari/"+latest).Status==200,"Safari grant cache is bounded and retains current links");
                s.ForgetDevices();Check(FileTests.Request(s,"GET","/safari-file/"+latest).Status==410,"Forgetting the source iPhone immediately revokes Safari download grants");
            }
        }finally{Directory.Delete(root,true);}
    }
}
}
