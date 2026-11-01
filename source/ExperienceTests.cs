using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
namespace WifiClipboard {
static class ExperienceTests {
    static void Check(bool value,string title){SelfTest.Check(value,title);}
    static string Text(FileTests.Response response){return Encoding.UTF8.GetString(response.Body);}
    static string Pair(Server server) {
        var response=FileTests.Request(server,"POST","/pair",Encoding.UTF8.GetBytes("{\"code\":\""+server.Code+"\"}"),null,"","application/json");
        Check(response.Status==200&&response.Headers.Contains("Max-Age=31536000"),"Pairing creates a persistent one-year cookie");
        return(string)response.Json["token"];
    }
    static byte[] Query(string host,int type,int cls) {
        var data=new List<byte>(new byte[]{0,9,0,0,0,1,0,0,0,0,0,0});
        foreach(string part in host.Split('.')){data.Add((byte)part.Length);data.AddRange(Encoding.ASCII.GetBytes(part));}
        data.Add(0);data.Add((byte)(type>>8));data.Add((byte)type);data.Add((byte)(cls>>8));data.Add((byte)cls);return data.ToArray();
    }
    public static void Run() {
        string root=Path.Combine(Path.GetTempPath(),"wifi-experience-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string trust=Path.Combine(root,"trust"),received=Path.Combine(root,"received"),token,host;int port;
        try {
            new TrustStore(trust).Save(new HashSet<string>());
            using(var s=new Server(new State(),0,new string[0],received,trust)) {
                token=Pair(s);port=s.Port;host=s.LocalHostName;
                Check(s.TrustedCount==1,"Windows records the paired browser");
                byte[] encrypted=File.ReadAllBytes(Path.Combine(trust,"trusted-devices.dat"));
                Check(!Encoding.UTF8.GetString(encrypted).Contains(token)&&Encoding.UTF8.GetString(encrypted).Contains(TrustStore.Fingerprint(token)),"Saved device store contains only SHA-256 fingerprints, never raw tokens");
                string cookie="Cookie: BridgeSession"+port+"="+token+"\r\n";
                Check(FileTests.Request(s,"GET","/session",null,null,cookie).Status==200,"Returning browser restores its session using the cookie alone");
                Check(FileTests.Request(s,"GET","/api/text",null,null,cookie).Status==200&&FileTests.Request(s,"GET","/api/files",null,null,cookie).Status==200,"Cookie-only sessions can use text and file APIs");
                Check(FileTests.Request(s,"GET","/manifest.webmanifest").Status==200&&Text(FileTests.Request(s,"GET","/manifest.webmanifest")).Contains("browser"),"Home-screen manifest has a stable root start URL");
                var icon=FileTests.Request(s,"GET","/icon.png");Check(icon.Status==200&&icon.Body[0]==137&&icon.Body[1]==80,"Home-screen PNG icon is embedded and served");
                string pdf=Path.Combine(root,"document.pdf");File.WriteAllText(pdf,"%PDF-1.4 test");var file=s.ShareFile(pdf);
                var page=FileTests.Request(s,"GET","/file/"+file.Id,null,null,cookie);string html=Text(page);
                Check(page.Status==200&&html.Contains("href=\"/\"")&&html.Contains("← К обмену"),"File viewer has an explicit root Back link independent of browser history");
                Check(html.Contains("http://")&&html.Contains("/safari/")&&!html.Contains("x-safari-"),"Download page uses valid HTTP links without a custom application scheme");
                Check(html.Contains("/preview/"+file.Id),"PDF preview is embedded inside the page with its Back control");
                var preview=FileTests.Request(s,"GET","/preview/"+file.Id,null,null,cookie);
                Check(preview.Status==200&&preview.Headers.Contains("Content-Type: application/pdf")&&preview.Headers.Contains("Content-Disposition: inline;"),"PDF preview uses inline MIME while downloads remain attachments");
                string executable=Path.Combine(root,"unsafe.html");File.WriteAllText(executable,"<script>alert(1)</script>");var unsafeFile=s.ShareFile(executable);
                Check(FileTests.Request(s,"GET","/preview/"+unsafeFile.Id,null,null,cookie).Status==415&&!Text(FileTests.Request(s,"GET","/file/"+unsafeFile.Id,null,null,cookie)).Contains("<iframe"),"HTML cannot run as an inline preview");
                unsafeFile.Name="<script>alert(1)</script>.html";
                Check(!Text(FileTests.Request(s,"GET","/file/"+unsafeFile.Id,null,null,cookie)).Contains("<script>"),"Untrusted file titles are HTML escaped");
                Check(Text(FileTests.Request(s,"GET","/file/missing",null,null,cookie)).Contains("href=\"/\""),"Missing file page still offers a way back");
            }
            using(var s=new Server(new State(),port,new string[0],received,trust)) {
                Check(s.TrustedCount==1&&s.LocalHostName==host,"Trusted browser and local hostname survive a Windows server restart");
                Check(FileTests.Request(s,"GET","/session",null,token).Status==200,"Old device token is accepted after restart without entering a code");
                Check(FileTests.Request(s,"GET","/session",null,"invalid").Status==401,"Unknown token still requires pairing");
                FileTests.Request(s,"POST","/unpair",Encoding.UTF8.GetBytes("{}"),token,"","application/json");
            }
            using(var s=new Server(new State(),port,new string[0],received,trust)) {
                Check(s.TrustedCount==0&&FileTests.Request(s,"GET","/session",null,token).Status==401,"Forget on iPhone persists revocation after restart");
                token=Pair(s);s.ForgetDevices();
                Check(s.TrustedCount==0&&FileTests.Request(s,"GET","/session",null,token).Status==401,"Windows Forget All immediately revokes trusted access");
            }
            using(var s=new Server(new State(),port,new string[0],received,trust)) Check(s.TrustedCount==0,"Windows Forget All remains revoked after restart");
            string savedStore=Path.Combine(trust,"trusted-devices.dat");File.WriteAllText(savedStore,"corrupted");
            bool rejected=false;try{new TrustStore(trust).Load();}catch(IOException){rejected=true;}Check(rejected,"Corrupted device store fails closed");
            bool unicast;var answer=LocalNameResponder.Answer(Query(host,1,1),host,IPAddress.Parse("192.0.2.55"),false,out unicast);
            Check(answer!=null&&!unicast&&answer.Skip(answer.Length-4).SequenceEqual(new byte[]{192,0,2,55}),"mDNS name resolves to current interface IPv4 address");
            answer=LocalNameResponder.Answer(Query(host,1,32769),host,IPAddress.Parse("192.0.2.56"),false,out unicast);
            Check(answer!=null&&unicast&&answer.Last()==56,"mDNS unicast-response requests and address changes are supported");
            Check(LocalNameResponder.Answer(Query("other.local",1,1),host,IPAddress.Loopback,false,out unicast)==null,"mDNS does not answer for unrelated names");
            Check(LocalNameResponder.Answer(new byte[]{0,0,0,0,0,1,0,0,0,0,0,0,192,12},host,IPAddress.Loopback,false,out unicast)==null,"Malformed cyclic DNS compression is rejected");
        }finally{Directory.Delete(root,true);}
    }
}
}
