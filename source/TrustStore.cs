using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
namespace WifiClipboard {
public sealed class TrustStore {
    readonly string directory, file;
    public readonly string HostName;
    public TrustStore(string directory) {
        this.directory=directory;
        if(directory==null) {HostName="wifi-clipboard-test.local";return;}
        Directory.CreateDirectory(directory);file=Path.Combine(directory,"trusted-devices.dat");
        string hostFile=Path.Combine(directory,"local-name.txt");
        if(!File.Exists(hostFile))File.WriteAllText(hostFile,"wifi-clipboard-"+Guid.NewGuid().ToString("N").Substring(0,10)+".local",Encoding.ASCII);
        HostName=File.ReadAllText(hostFile,Encoding.ASCII).Trim();
        if(!System.Text.RegularExpressions.Regex.IsMatch(HostName,@"^wifi-clipboard-[a-z0-9]{10}\.local$"))throw new IOException("Повреждено локальное имя компьютера: "+hostFile);
    }
    public static string Fingerprint(string token) {
        if(string.IsNullOrEmpty(token)||token.Length>256)return "";
        using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(token)));
    }
    public HashSet<string> Load() {
        if(file==null||!File.Exists(file))return new HashSet<string>();
        try {
            if(new FileInfo(file).Length>65536)throw new InvalidDataException();
            byte[] plain=File.ReadAllBytes(file);
            var items=new JavaScriptSerializer().Deserialize<string[]>(Encoding.UTF8.GetString(plain));
            if(items==null||items.Length>16)throw new InvalidDataException();
            foreach(string item in items)if(item==null||Convert.FromBase64String(item).Length!=32)throw new InvalidDataException();
            return new HashSet<string>(items);
        }catch(Exception e){throw new IOException("Не удалось прочитать подключения. Закройте программу и переименуйте "+file+", затем подключите iPhone заново.",e);}
    }
    public void Save(HashSet<string> hashes) {
        if(file==null)return;
        byte[] payload=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(hashes.ToArray()));
        string temporary=Path.Combine(directory,".trust-"+Guid.NewGuid().ToString("N")+".tmp");
        try {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(payload,0,payload.Length);stream.Flush(true);}
            if(File.Exists(file))File.Replace(temporary,file,null);else File.Move(temporary,file);
        }finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
}
