using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
namespace WifiClipboard {
static class NetworkTests {
 static void Check(bool ok,string message){SelfTest.Check(ok,message);}
 static FileTests.Response Request(Server server,string host,string path="/",string method="GET",string body="",string token=null,string origin=null){
  using(var client=new TcpClient("127.0.0.1",server.Port)){
   client.ReceiveTimeout=5000;var stream=client.GetStream();byte[] content=Encoding.UTF8.GetBytes(body);
   string request=method+" "+path+" HTTP/1.1\r\nHost: "+host+":"+server.Port+"\r\nContent-Type: application/json\r\nContent-Length: "+content.Length+"\r\n";
   if(token!=null)request+="X-Bridge-Token: "+token+"\r\n";if(origin!=null)request+="Origin: "+origin+"\r\n";
   byte[] header=Encoding.ASCII.GetBytes(request+"Connection: close\r\n\r\n");stream.Write(header,0,header.Length);stream.Write(content,0,content.Length);return FileTests.Read(stream,false);
  }
 }
 internal static void Run(){
  using(var server=new Server(new State(),0,new[]{"192.0.2.10"})){
   int port=server.Port;string host=server.LocalHostName;
   Check(Request(server,"192.0.2.10").Status==200,"Initial LAN address opens the phone page");
   var pair=Request(server,"192.0.2.10","/pair","POST","{\"code\":\""+server.Code+"\"}");string token=(string)pair.Json["token"];
   server.UpdateNetworkAddresses(new[]{"198.51.100.20"});
   Check(Request(server,"198.51.100.20").Status==200,"New LAN address opens immediately without restarting the listener");
   Check(Request(server,"192.0.2.10").Status==403,"Previous LAN address is removed from the Host allowlist");
   Check(Request(server,"198.51.100.20","/session","GET","",token).Status==200&&server.Port==port&&server.TrustedCount==1,"Changing Wi-Fi preserves pairing, port and session");
   Check(Request(server,host,"/session","GET","",token).Status==200,"Stable local hostname keeps working after an address change");
   Check(Request(server,"evil.invalid").Status==403,"Network refresh does not allow arbitrary Host names");
   Check(Request(server,"198.51.100.20","/session","GET","",token,"http://evil.invalid").Status==403,"Origin protection remains enabled after a network change");
   server.UpdateNetworkAddresses(new string[0]);
   Check(Request(server,"198.51.100.20").Status==403&&Request(server,"127.0.0.1").Status==200,"Disconnect removes old addresses while keeping loopback management");
   server.UpdateNetworkAddresses(new[]{"203.0.113.30","203.0.113.31"});
   Check(Request(server,"203.0.113.30").Status==200&&Request(server,"203.0.113.31").Status==200,"Reconnect accepts the current addresses of multiple interfaces");
   Check(Request(server,host,"/session","GET","",token).Status==200,"Saved device survives disconnect and reconnect");
   server.UpdateNetworkAddresses(new[]{"not-an-ip","evil.invalid"});
   Check(Request(server,"evil.invalid").Status==403,"Address updates cannot add non-IP Host names");
  }
  var announcement=LocalNameResponder.Announcement("wifi-clipboard-test.local",IPAddress.Parse("198.51.100.20"));
  Check(announcement[0]==0&&announcement[1]==0&&announcement[2]==0x84&&announcement[6]==0&&announcement[7]==1,"Unsolicited mDNS update is an authoritative single-answer response");
  int record=12+"wifi-clipboard-test".Length+1+"local".Length+1+1;
  Check(announcement[record]==0&&announcement[record+1]==1&&announcement[record+2]==0x80&&announcement[record+3]==1,"mDNS update marks the address for cache flush");
  Check(announcement[record+7]==120&&announcement.Skip(announcement.Length-4).SequenceEqual(new byte[]{198,51,100,20}),"mDNS update contains the new address and a bounded TTL");
  string root=Path.Combine(Path.GetTempPath(),"wifi-network-ui-"+Guid.NewGuid().ToString("N"));
  try{using(var form=new BridgeForm(root)){
   form.ApplyNetworkAddresses(new[]{"192.0.2.10"},false);string stable=form.ConnectionUrls()[0];
   form.ApplyNetworkAddresses(new[]{"198.51.100.20"},true);
   var addresses=(TextBox)form.Controls.Find("connectionAddresses",true)[0];
   Check(addresses.Text.Contains("198.51.100.20")&&!addresses.Text.Contains("192.0.2.10"),"Window replaces the previous network address");
   Check(form.ConnectionUrls()[0]==stable&&form.ConnectionUrls().Any(url=>url.Contains("198.51.100.20"))&&!form.ConnectionUrls().Any(url=>url.Contains("192.0.2.10")),"QR address choices use the refreshed network and retain the stable URL");
   form.ApplyNetworkAddresses(new string[0],true);
   Check(!addresses.Text.Contains("198.51.100.20")&&form.ConnectionUrls().Length==1,"Offline state hides stale IP links and QR choices");
  }}finally{if(Directory.Exists(root))Directory.Delete(root,true);}
 }
}
}
