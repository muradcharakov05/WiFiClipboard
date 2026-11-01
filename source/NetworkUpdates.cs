using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
namespace WifiClipboard {
internal static class NetworkAddresses {
 internal static List<string> Current(){
  var result=new List<string>();
  foreach(var nic in NetworkInterface.GetAllNetworkInterfaces().OrderBy(n=>n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211?0:n.NetworkInterfaceType==NetworkInterfaceType.Ethernet?1:2)){
   if(nic.OperationalStatus!=OperationalStatus.Up||nic.NetworkInterfaceType==NetworkInterfaceType.Loopback||nic.NetworkInterfaceType==NetworkInterfaceType.Tunnel)continue;
   try{foreach(var ip in nic.GetIPProperties().UnicastAddresses)if(ip.Address.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(ip.Address)&&!result.Contains(ip.Address.ToString()))result.Add(ip.Address.ToString());}catch(NetworkInformationException){}
  }return result;
 }
}
public sealed partial class Server {
 readonly object hostGate=new object();
 public void UpdateNetworkAddresses(IEnumerable<string> addresses){
  var next=new HashSet<string>(StringComparer.OrdinalIgnoreCase){LocalHostName+":"+Port,"127.0.0.1:"+Port,"localhost:"+Port};
  foreach(string value in addresses){IPAddress ip;if(IPAddress.TryParse(value,out ip)&&ip.AddressFamily==AddressFamily.InterNetwork)next.Add(ip+":"+Port);}
  lock(hostGate){hosts.Clear();hosts.UnionWith(next);}
 }
 bool AcceptsHost(string host){lock(hostGate)return host!=null&&hosts.Contains(host);}
}
public sealed partial class BridgeForm {
 readonly List<string> networkAddresses=new List<string>();
 readonly System.Windows.Forms.Timer networkTimer=new System.Windows.Forms.Timer();
 TextBox addressBox;Label networkHint;int networkDirty;
 void WatchNetwork(){
  NetworkChange.NetworkAddressChanged+=NetworkAddressChanged;
  networkTimer.Interval=2000;networkTimer.Tick+=delegate{RefreshNetwork(false);};networkTimer.Start();
 }
 void NetworkAddressChanged(object sender,EventArgs e){Interlocked.Exchange(ref networkDirty,1);}
 void RefreshNetwork(bool force){
  bool signalled=Interlocked.Exchange(ref networkDirty,0)!=0;
  try{var current=NetworkAddresses.Current();if(!force&&!signalled&&current.SequenceEqual(networkAddresses))return;ApplyNetworkAddresses(current,!force);}
  catch(NetworkInformationException){networkHint.Text="Сеть ещё подключается. Адреса обновятся автоматически.";}
 }
 internal void ApplyNetworkAddresses(IEnumerable<string> current,bool changed){
  var next=current.ToList();server.UpdateNetworkAddresses(next);networkAddresses.Clear();networkAddresses.AddRange(next);
  if(localName!=null)localName.RefreshNetwork();
  addressBox.Text=next.Count==0?"Нет адреса сети. Подключите Wi-Fi; адреса появятся автоматически.":"http://"+server.LocalHostName+":"+server.Port+"/"+Environment.NewLine+string.Join(Environment.NewLine,next.Select(ip=>"http://"+ip+":"+server.Port+"/").ToArray());
  networkHint.Text=(changed?"Сеть изменилась. Откройте QR-код или постоянный адрес заново.\n":"")+"Первый адрес .local остаётся тем же при смене Wi-Fi.\nЕсли он не открывается, выберите в QR-коде новый IP.\nWindows и iPhone должны быть в одной сети; VPN и гостевая сеть могут блокировать связь.";
 }
 internal string[] ConnectionUrls(){return new[]{"http://"+server.LocalHostName+":"+server.Port+"/"}.Concat(networkAddresses.Select(ip=>"http://"+ip+":"+server.Port+"/")).ToArray();}
 void StopWatchingNetwork(){networkTimer.Stop();networkTimer.Dispose();NetworkChange.NetworkAddressChanged-=NetworkAddressChanged;}
}
}
