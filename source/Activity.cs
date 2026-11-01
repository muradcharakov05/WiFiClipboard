using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
namespace WifiClipboard {
public sealed class DeviceInfo {public string Id,Name;public DateTime Created,LastSeen;}
public sealed class ActivityEntry {public string Id,Direction,Name,Device,Status;public long Size;public DateTime Time;}
public sealed class ActivityStore {
 readonly object sync=new object(); readonly string folder; List<DeviceInfo> devices=new List<DeviceInfo>(); List<ActivityEntry> history=new List<ActivityEntry>();
 public ActivityStore(string directory,IEnumerable<string> trusted) {folder=directory;if(folder!=null){Directory.CreateDirectory(folder);devices=Read<List<DeviceInfo>>("devices.json")??devices;history=Read<List<ActivityEntry>>("history.json")??history;}devices=devices.Where(d=>d!=null&&trusted.Contains(d.Id)).Take(16).ToList();foreach(string id in trusted)if(!devices.Any(d=>d.Id==id))devices.Add(new DeviceInfo{Id=id,Name="iPhone (сохранённое подключение)",Created=DateTime.UtcNow});history=history.Where(h=>h!=null).Take(300).ToList();}
 T Read<T>(string name) where T:class {try {string f=Path.Combine(folder,name);if(!File.Exists(f)||new FileInfo(f).Length>1024*1024)return null;return new JavaScriptSerializer().Deserialize<T>(File.ReadAllText(f));}catch{return null;}}
 internal static void Atomic(string file,object value) {string tmp=file+"."+Guid.NewGuid().ToString("N")+".tmp";try{byte[] data=Encoding.UTF8.GetBytes(new JavaScriptSerializer{MaxJsonLength=2000000}.Serialize(value));using(var s=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){s.Write(data,0,data.Length);s.Flush(true);}if(File.Exists(file))File.Replace(tmp,file,null);else File.Move(tmp,file);}finally{if(File.Exists(tmp))File.Delete(tmp);}}
 void Save(string name,object value){if(folder!=null)Atomic(Path.Combine(folder,name),value);}
 public static string CleanName(string name) {name=new string((name??"").Where(c=>!char.IsControl(c)).ToArray()).Trim();return name.Length==0?"iPhone":name.Substring(0,Math.Min(60,name.Length));}
 public void AddDevice(string id,string name){lock(sync){devices.RemoveAll(d=>d.Id==id);devices.Add(new DeviceInfo{Id=id,Name=CleanName(name),Created=DateTime.UtcNow,LastSeen=DateTime.UtcNow});Save("devices.json",devices);}}
 public void Rename(string id,string name){lock(sync){var d=devices.Find(x=>x.Id==id);if(d==null)throw new IOException("Подключение уже удалено.");d.Name=CleanName(name);Save("devices.json",devices);}}
 public void Seen(string id){lock(sync){var d=devices.Find(x=>x.Id==id);if(d!=null&&(DateTime.UtcNow-d.LastSeen).TotalMinutes>=5){d.LastSeen=DateTime.UtcNow;Save("devices.json",devices);}}}
 public void Remove(string id){lock(sync){devices.RemoveAll(d=>id==null||d.Id==id);Save("devices.json",devices);}}
 public string Name(string id){lock(sync){var d=devices.Find(x=>x.Id==id);return d==null?"iPhone":d.Name;}}
 public DeviceInfo[] Devices(){lock(sync)return devices.Select(d=>new DeviceInfo{Id=d.Id,Name=d.Name,Created=d.Created,LastSeen=d.LastSeen}).ToArray();}
 public ActivityEntry[] History(){lock(sync)return history.ToArray();}
 public void Record(string direction,string name,long size,string device,string status,string id=null){lock(sync){id=id??Guid.NewGuid().ToString("N");history.RemoveAll(h=>h.Id==id);history.Insert(0,new ActivityEntry{Id=id,Direction=direction,Name=name,Size=size,Device=device,Status=status,Time=DateTime.UtcNow});if(history.Count>300)history.RemoveRange(300,history.Count-300);Save("history.json",history);}}
 public void Clear(){lock(sync){history.Clear();Save("history.json",history);}}
}
public sealed partial class Server {
 readonly ActivityStore activity;
 public DeviceInfo[] Devices(){lock(gate)return activity.Devices().Where(d=>tokens.Contains(d.Id)).ToArray();}
 public void RenameDevice(string id,string name){lock(gate){if(!tokens.Contains(id))throw new IOException("Подключение уже отключено.");activity.Rename(id,name);}}
 public void RevokeDevice(string id){lock(gate){var next=new HashSet<string>(tokens);next.Remove(id);trust.Save(next);tokens.Remove(id);activity.Remove(id);}lock(fileGate)foreach(var pair in fileOwners.Where(p=>p.Value==id).ToArray())try{pair.Key.Close();}catch{}}
 public ActivityEntry[] History(){return activity.History();}
 public void ClearHistory(){activity.Clear();}
 public void RecordText(){Record("Windows → iPhone","Текст",0,null,"Доступен на iPhone");}
 void Record(string direction,string name,long size,string token,string status,string id=null){try{activity.Record(direction,name,size,token==null?"Windows":activity.Name(TrustStore.Fingerprint(token)),status,id);}catch(IOException){Notice("Историю не удалось сохранить на диск.");}catch(UnauthorizedAccessException){Notice("Нет доступа для сохранения истории.");}}
 bool HandleActivity(System.IO.Stream stream,string method,string path,Dictionary<string,string> headers){
  if(path!="/api/history"&&path!="/api/device")return false;string token=FileToken(headers,true);string id=TrustStore.Fingerprint(token);lock(gate)if(!tokens.Contains(id)){Reply(stream,401,"Подключитесь заново.");return true;}
  if(path=="/api/history"&&method=="GET"){if(!Allowed(token)){Reply(stream,409,"Передача приостановлена.");return true;}Json(stream,new{entries=activity.History().Select(h=>new{time=h.Time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"),direction=h.Direction,name=h.Name,size=h.Size,device=h.Device,status=h.Status}).ToArray()});return true;}
  if(path=="/api/device"&&method=="GET"){Json(stream,new{name=activity.Name(id)});return true;}
  if(path=="/api/device"&&method=="POST"){var data=ReadSmallJson(stream,headers);object name;if(data==null||!data.TryGetValue("name",out name)||!(name is string)){Reply(stream,400,"Укажите название.");return true;}RenameDevice(id,(string)name);Json(stream,new{ok=true});return true;}
  Reply(stream,405,"Method rejected");return true;
 }
 static Dictionary<string,object> ReadSmallJson(Stream stream,Dictionary<string,string> h){string ct,sz;int n;if(!h.TryGetValue("Content-Type",out ct)||!ct.StartsWith("application/json",StringComparison.OrdinalIgnoreCase)||!h.TryGetValue("Content-Length",out sz)||!int.TryParse(sz,out n)||n<0||n>8192) return null;byte[] b=new byte[n];int offset=0;while(offset<n){int r=stream.Read(b,offset,n-offset);if(r==0)throw new EndOfStreamException();offset+=r;}try{return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(new UTF8Encoding(false,true).GetString(b));}catch{return null;}}
}
}
