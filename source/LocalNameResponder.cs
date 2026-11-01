using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
namespace WifiClipboard {
public sealed class LocalNameResponder : IDisposable {
    readonly string name;
    Socket socket;
    volatile bool stopped;
    public bool Active {get;private set;}
    readonly HashSet<string> joined=new HashSet<string>();
    static readonly IPAddress group=IPAddress.Parse("224.0.0.251");
    sealed class Interface {public IPAddress Address,Mask;}
    static List<Interface> Interfaces() {
        var list=new List<Interface>();
        foreach(var nic in NetworkInterface.GetAllNetworkInterfaces()) {
            if(nic.OperationalStatus!=OperationalStatus.Up||nic.NetworkInterfaceType==NetworkInterfaceType.Loopback||nic.NetworkInterfaceType==NetworkInterfaceType.Tunnel)continue;
            foreach(var ip in nic.GetIPProperties().UnicastAddresses)if(ip.Address.AddressFamily==AddressFamily.InterNetwork)list.Add(new Interface {Address=ip.Address,Mask=ip.IPv4Mask});
        }return list;
    }
    public LocalNameResponder(string name) {
        this.name=name;
        try {
            socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
            socket.ExclusiveAddressUse=false;socket.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);
            socket.Bind(new IPEndPoint(IPAddress.Any,5353));socket.ReceiveTimeout=1000;
            socket.SetSocketOption(SocketOptionLevel.IP,SocketOptionName.MulticastTimeToLive,255);
            Join();Active=joined.Count>0;
            var thread=new Thread(Run);thread.IsBackground=true;thread.Start();
        }catch(SocketException){if(socket!=null)socket.Close();Active=false;}
    }
    void Join() {
        foreach(var ip in Interfaces())if(!joined.Contains(ip.Address.ToString()))try {
            socket.SetSocketOption(SocketOptionLevel.IP,SocketOptionName.AddMembership,new MulticastOption(group,ip.Address));joined.Add(ip.Address.ToString());
        }catch(SocketException){}
    }
    static bool SameSubnet(IPAddress a,IPAddress b,IPAddress mask) {
        byte[] x=a.GetAddressBytes(),y=b.GetAddressBytes(),m=mask.GetAddressBytes();
        for(int i=0;i<4;i++)if((x[i]&m[i])!=(y[i]&m[i]))return false;return true;
    }
    void Run() {
        var buffer=new byte[4096];DateTime refresh=DateTime.MinValue;
        while(!stopped)try {
            if(DateTime.UtcNow>refresh){Join();refresh=DateTime.UtcNow.AddSeconds(20);Active=joined.Count>0;}
            EndPoint remote=new IPEndPoint(IPAddress.Any,0);int length=socket.ReceiveFrom(buffer,ref remote);var sender=(IPEndPoint)remote;
            var addresses=Interfaces().Where(ip=>SameSubnet(ip.Address,sender.Address,ip.Mask)).ToArray();
            if(addresses.Length==0)continue;
            bool unicast;byte[] response=Answer(buffer.Take(length).ToArray(),name,addresses[0].Address,sender.Port!=5353,out unicast);
            if(response==null)continue;
            socket.SetSocketOption(SocketOptionLevel.IP,SocketOptionName.MulticastInterface,addresses[0].Address.GetAddressBytes());
            socket.SendTo(response,unicast?remote:new IPEndPoint(group,5353));
        }catch(SocketException){}catch(ObjectDisposedException){return;}catch(Exception){}
    }
    static int Word(byte[] data,int offset){if(offset<0||offset+1>=data.Length)throw new InvalidDataException();return(data[offset]<<8)|data[offset+1];}
    static void PutWord(Stream output,int n){output.WriteByte((byte)(n>>8));output.WriteByte((byte)n);}
    static void PutName(Stream output,string name){foreach(string part in name.Split('.')){byte[] bytes=Encoding.ASCII.GetBytes(part);output.WriteByte((byte)bytes.Length);output.Write(bytes,0,bytes.Length);}output.WriteByte(0);}
    static string ReadName(byte[] data,ref int offset) {
        var labels=new List<string>();int pos=offset,jumps=0;bool jumped=false;
        while(true) {
            if(pos>=data.Length||++jumps>128)throw new InvalidDataException();int size=data[pos++];
            if(size==0){if(!jumped)offset=pos;return string.Join(".",labels.ToArray());}
            if((size&192)==192){if(pos>=data.Length)throw new InvalidDataException();int pointer=((size&63)<<8)|data[pos++];if(!jumped)offset=pos;jumped=true;pos=pointer;continue;}
            if(size>63||pos+size>data.Length)throw new InvalidDataException();labels.Add(Encoding.ASCII.GetString(data,pos,size));pos+=size;
            if(labels.Sum(x=>x.Length+1)>255)throw new InvalidDataException();
        }
    }
    internal static byte[] Answer(byte[] query,string expected,IPAddress address,bool legacy,out bool unicast) {
        unicast=legacy;
        try {
            if(query.Length<12||query.Length>4096||(query[2]&128)!=0)return null;
            int questions=Word(query,4),offset=12;if(questions>64)return null;
            for(int i=0;i<questions;i++) {
                string name=ReadName(query,ref offset);int type=Word(query,offset),cls=Word(query,offset+2);offset+=4;
                if(!string.Equals(name,expected,StringComparison.OrdinalIgnoreCase)||(type!=1&&type!=255)||(cls&32767)!=1)continue;
                unicast=legacy||(cls&32768)!=0;
                using(var output=new MemoryStream()) {
                    PutWord(output,legacy?Word(query,0):0);PutWord(output,0x8400);PutWord(output,legacy?1:0);PutWord(output,1);PutWord(output,0);PutWord(output,0);
                    if(legacy){PutName(output,expected);PutWord(output,type);PutWord(output,1);}
                    PutName(output,expected);PutWord(output,1);PutWord(output,legacy?1:0x8001);
                    PutWord(output,0);PutWord(output,legacy?10:120);PutWord(output,4);byte[] bytes=address.GetAddressBytes();output.Write(bytes,0,4);return output.ToArray();
                }
            }
        }catch(Exception){}return null;
    }
    public void Dispose(){stopped=true;Active=false;if(socket!=null)socket.Close();}
}
}
