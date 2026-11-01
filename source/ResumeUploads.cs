using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
namespace WifiClipboard {
public sealed class UploadSession {
 public string Id,Owner,Name,Modified,Destination;public long Size,Offset;public bool Done,Cancelled;public DateTime Updated;public List<string> Hashes=new List<string>();
 [ScriptIgnore] public object Sync=new object();
}
public sealed partial class Server {
 public const int ChunkSize=2*1024*1024;
 readonly object uploadGate=new object();readonly Dictionary<string,UploadSession> uploads=new Dictionary<string,UploadSession>();
 string ResumeDirectory {get{return Path.Combine(ReceiveDirectory,".WiFiClipboard-resume");}}
 string UploadPart(UploadSession u){return Path.Combine(ResumeDirectory,u.Id+".part");}
 string UploadMeta(UploadSession u){return Path.Combine(ResumeDirectory,u.Id+".json");}
 static bool UploadId(string id){return id!=null&&System.Text.RegularExpressions.Regex.IsMatch(id,"^[a-f0-9]{32}$");}
 internal static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
 void CheckReceiveDirectory(){Directory.CreateDirectory(ReceiveDirectory);if((File.GetAttributes(ReceiveDirectory)&FileAttributes.ReparsePoint)!=0)throw new IOException("Папка полученных файлов не должна быть ссылкой.");Directory.CreateDirectory(ResumeDirectory);if((File.GetAttributes(ResumeDirectory)&FileAttributes.ReparsePoint)!=0)throw new IOException("Папка временных загрузок не должна быть ссылкой.");}
 void SaveUpload(UploadSession u){ActivityStore.Atomic(UploadMeta(u),u);}
 void LoadUploads(){
  if(!Directory.Exists(ResumeDirectory))return;CheckReceiveDirectory();
  foreach(string file in Directory.GetFiles(ResumeDirectory,"*.json").Take(128))try{
   if(new FileInfo(file).Length>1000000)continue;var u=new JavaScriptSerializer{MaxJsonLength=1000000}.Deserialize<UploadSession>(File.ReadAllText(file));
   if(u==null||!UploadId(u.Id)||Path.GetFileName(file)!=u.Id+".json"||u.Name!=SafeName(u.Name)||u.Size<0||u.Size>MaxFile||u.Offset<0||u.Offset>u.Size||(u.Offset!=u.Size&&u.Offset%ChunkSize!=0)||u.Hashes==null||u.Hashes.Count!=(u.Offset+ChunkSize-1)/ChunkSize||u.Hashes.Any(h=>h==null||!System.Text.RegularExpressions.Regex.IsMatch(h,"^[a-f0-9]{64}$"))||u.Destination!=null&&u.Destination!=SafeName(u.Destination))continue;
   if((DateTime.UtcNow-u.Updated).TotalDays>7||!tokens.Contains(u.Owner)){File.Delete(file);if(File.Exists(UploadPart(u)))File.Delete(UploadPart(u));continue;}
   if(!u.Done){
    if(!File.Exists(UploadPart(u))){if(u.Offset!=u.Size||u.Destination==null||!VerifyDestination(u))continue;u.Done=true;SaveUpload(u);}
    else using(var part=new FileStream(UploadPart(u),FileMode.Open,FileAccess.ReadWrite,FileShare.None)){if(part.Length<u.Offset)continue;part.SetLength(u.Offset);}
   }
   uploads[u.Id]=u;
  }catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
 }
 bool VerifyDestination(UploadSession u){string f=Path.Combine(ReceiveDirectory,u.Destination);if(!File.Exists(f)||new FileInfo(f).Length!=u.Size)return false;using(var s=File.OpenRead(f)){foreach(string h in u.Hashes){byte[] b=new byte[(int)Math.Min(ChunkSize,s.Length-s.Position)];int o=0;while(o<b.Length){int n=s.Read(b,o,b.Length-o);if(n==0)return false;o+=n;}if(Hash(b)!=h)return false;}}return true;}
 object UploadStatus(UploadSession u){return new{id=u.Id,name=u.Done?u.Destination:u.Name,size=u.Size,offset=u.Offset,chunkSize=ChunkSize,done=u.Done,hashes=u.Hashes.ToArray()};}
 void PruneUploads(){foreach(var u in uploads.Values.ToArray()){if((DateTime.UtcNow-u.Updated).TotalDays<=7&&tokens.Contains(u.Owner))continue;if(!Monitor.TryEnter(u.Sync))continue;try{u.Cancelled=true;File.Delete(UploadPart(u));File.Delete(UploadMeta(u));uploads.Remove(u.Id);}finally{Monitor.Exit(u.Sync);}}}
 bool HandleUploads(TcpClient client,string method,string route,Dictionary<string,string> h){
  if(!route.StartsWith("/api/uploads/",StringComparison.Ordinal))return false;var stream=client.GetStream();string token=FileToken(h,true),owner=TrustStore.Fingerprint(token);lock(gate)if(!tokens.Contains(owner)){Reply(stream,401,"Подключитесь заново.");return true;}
  if(!Allowed(token)){Reply(stream,409,"Передача приостановлена.");return true;}
  if(route=="/api/uploads/start"){
   if(method!="POST"){Reply(stream,405,"Method rejected");return true;}var data=ReadSmallJson(stream,h);object name,size,modified;long length;
   if(data==null||!data.TryGetValue("name",out name)||!(name is string)||!data.TryGetValue("size",out size)||!long.TryParse(Convert.ToString(size,CultureInfo.InvariantCulture),out length)||length<0||length>MaxFile||!data.TryGetValue("modified",out modified)||!(modified is string)||((string)modified).Length>100){Reply(stream,400,"Некорректное описание файла (до 10 ГиБ).");return true;}
   object requestedId;string createId=data.TryGetValue("id",out requestedId)&&requestedId is string?(string)requestedId:Guid.NewGuid().ToString("N");if(!UploadId(createId)){Reply(stream,400,"Некорректный номер загрузки.");return true;}
   CheckReceiveDirectory();lock(gate)lock(uploadGate){PruneUploads();UploadSession existing;if(uploads.TryGetValue(createId,out existing)){if(existing.Owner!=owner||existing.Name!=SafeName((string)name)||existing.Size!=length||existing.Modified!=(string)modified){Reply(stream,409,"Номер загрузки уже используется.");return true;}if(!Monitor.TryEnter(existing.Sync)){Reply(stream,429,"Загрузка занята. Повторите запрос.");return true;}try{Json(stream,UploadStatus(existing));}finally{Monitor.Exit(existing.Sync);}return true;}
   if(uploads.Count>=128){var oldest=uploads.Values.Where(entry=>entry.Done).OrderBy(entry=>entry.Updated).FirstOrDefault();if(oldest!=null){File.Delete(UploadMeta(oldest));uploads.Remove(oldest.Id);}}if(uploads.Count>=128||uploads.Values.Count(entry=>entry.Owner==owner&&!entry.Done)>=16){Reply(stream,429,"Слишком много незавершённых загрузок. Завершите или отмените их.");return true;}
    var u=new UploadSession{Id=createId,Owner=owner,Name=SafeName((string)name),Size=length,Modified=(string)modified,Updated=DateTime.UtcNow};using(File.Create(UploadPart(u))){}SaveUpload(u);uploads.Add(u.Id,u);Json(stream,UploadStatus(u));return true;}
  }
  string[] segments=route.Substring(13).Split('/');string id=segments[0];UploadSession session;lock(uploadGate)uploads.TryGetValue(id,out session);
  if(!UploadId(id)||session==null||session.Owner!=owner){Reply(stream,404,"Загрузка не найдена.");return true;}
  lock(session.Sync){
   if(session.Cancelled||(DateTime.UtcNow-session.Updated).TotalDays>7){Reply(stream,410,"Срок хранения незавершённой загрузки истёк.");return true;}
   if(segments.Length==1&&method=="GET"){if(session.Done&&(session.Destination==null||!File.Exists(Path.Combine(ReceiveDirectory,session.Destination))||new FileInfo(Path.Combine(ReceiveDirectory,session.Destination)).Length!=session.Size)){Reply(stream,410,"Сохранённый файл был удалён или изменён на Windows.");return true;}Json(stream,UploadStatus(session));return true;}
   if(segments.Length==2&&segments[1]=="cancel"&&method=="POST"){if(session.Done){Json(stream,UploadStatus(session));return true;}session.Cancelled=true;File.Delete(UploadPart(session));File.Delete(UploadMeta(session));lock(uploadGate)uploads.Remove(id);Record("iPhone → Windows",session.Name,session.Size,token,"Отменён","upload-"+id);Json(stream,new{ok=true});return true;}
   if(segments.Length==2&&segments[1]=="finish"&&method=="POST"){
    if(!session.Done){if(session.Offset!=session.Size){Reply(stream,409,"Файл получен не полностью.");return true;}
     CheckReceiveDirectory();if(!Allowed(token)){Reply(stream,409,"Передача остановлена.");return true;}
     for(int i=0;i<10000;i++){session.Destination=SafeName(i==0?session.Name:Path.GetFileNameWithoutExtension(session.Name)+" ("+i+")"+Path.GetExtension(session.Name));string destination=Path.Combine(ReceiveDirectory,session.Destination);if(File.Exists(destination))continue;SaveUpload(session);try{File.Move(UploadPart(session),destination);session.Done=true;break;}catch(IOException){if(!File.Exists(destination))throw;}}
     if(!session.Done)throw new IOException("Не удалось выбрать свободное имя.");session.Updated=DateTime.UtcNow;SaveUpload(session);Notice("Получен файл: "+session.Destination);
    }
    Record("iPhone → Windows",session.Destination,session.Size,token,"Сохранён","upload-"+id);Json(stream,UploadStatus(session));return true;
   }
   if(segments.Length!=1||method!="POST"){Reply(stream,405,"Method rejected");return true;}
   string offsetText,lengthText,ct;long offset;int length;
   if(!h.TryGetValue("Content-Type",out ct)||ct!="application/octet-stream"||!h.TryGetValue("X-Upload-Offset",out offsetText)||!long.TryParse(offsetText,NumberStyles.None,CultureInfo.InvariantCulture,out offset)||!h.TryGetValue("Content-Length",out lengthText)||!int.TryParse(lengthText,out length)||length<=0||length>ChunkSize){Reply(stream,400,"Некорректная часть файла.");return true;}
   if(session.Done||offset!=session.Offset||length!=Math.Min(ChunkSize,session.Size-session.Offset)){Reply(stream,409,"Смещение изменилось. Запросите состояние загрузки.");return true;}
   if(!fileSlots.Wait(0)){Reply(stream,429,"Дождитесь завершения других передач.");return true;}
   lock(fileGate){fileClients.Add(client);fileOwners[client]=owner;filesFinished.Reset();}
   try{
    client.ReceiveTimeout=30000;CheckReceiveDirectory();if(new DriveInfo(Path.GetPathRoot(ReceiveDirectory)).AvailableFreeSpace<length+16*1024*1024){Reply(stream,507,"Недостаточно места на Windows.");return true;}
    byte[] bytes=new byte[length];int read=0;while(read<length){if(!Allowed(token))throw new IOException("Передача остановлена.");int n=stream.Read(bytes,read,length-read);if(n==0)throw new EndOfStreamException();read+=n;}
    string digest=Hash(bytes),expected;if(h.TryGetValue("X-Chunk-SHA256",out expected)&&expected!=digest){Reply(stream,400,"Проверка части файла не пройдена.");return true;}
    if(!Allowed(token)){Reply(stream,409,"Передача остановлена.");return true;}
    long old=session.Offset;using(var output=new FileStream(UploadPart(session),FileMode.Open,FileAccess.Write,FileShare.None)){output.SetLength(old);output.Position=old;output.Write(bytes,0,length);output.Flush(true);}
    session.Offset+=length;session.Hashes.Add(digest);session.Updated=DateTime.UtcNow;
    try{SaveUpload(session);}catch{session.Offset=old;session.Hashes.RemoveAt(session.Hashes.Count-1);using(var output=File.OpenWrite(UploadPart(session)))output.SetLength(old);throw;}
    Record("iPhone → Windows",session.Name,session.Size,token,"Получено "+session.Offset+" из "+session.Size+" Б","upload-"+id);Json(stream,new{id=session.Id,offset=session.Offset,done=false});
   }catch(IOException){Notice("Связь прервалась. Полученные части сохранены для продолжения.");}
   finally{lock(fileGate){fileClients.Remove(client);fileOwners.Remove(client);if(fileClients.Count==0)filesFinished.Set();}fileSlots.Release();}
   return true;
  }
 }
}
}
