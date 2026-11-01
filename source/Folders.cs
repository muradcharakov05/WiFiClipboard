using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
namespace WifiClipboard {
public sealed partial class Server {
 readonly List<string> ownedArchives=new List<string>();
 public SharedFile SharePath(string path){if(Directory.Exists(path))return ShareFolder(path);return ShareFile(path);}
 public SharedFile ShareFolder(string directory){
  directory=Path.GetFullPath(directory);if(stopped)throw new IOException("Программа закрывается.");
  string cache=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ReceiveDirectory),".WiFiClipboard-archives"));
  if(cache.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||string.Equals(cache,directory,StringComparison.OrdinalIgnoreCase))throw new IOException("Выберите отдельную папку, не содержащую временные архивы программы.");
  Directory.CreateDirectory(cache);if((File.GetAttributes(cache)&FileAttributes.ReparsePoint)!=0)throw new IOException("Папка архивов не должна быть ссылкой.");
  string work=Path.Combine(cache,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);string zip=Path.Combine(work,SafeName(new DirectoryInfo(directory).Name+".zip"));
  long total=0;int count=0;bool success=false;
  try{
   Notice("Упаковка папки: "+new DirectoryInfo(directory).Name);
   using(var output=new FileStream(zip,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var archive=new ZipArchive(output,ZipArchiveMode.Create,false,System.Text.Encoding.UTF8))PackFolder(archive,directory,"",ref total,ref count,0);
   if(stopped)throw new IOException("Упаковка остановлена.");var item=ShareFile(zip);lock(fileGate)ownedArchives.Add(zip);success=true;Record("Windows → iPhone",item.Name,item.Size,null,"Папка упакована и доступна");return item;
  }finally{if(!success){try{File.Delete(zip);Directory.Delete(work);}catch{}}}
 }
 void PackFolder(ZipArchive zip,string directory,string prefix,ref long total,ref int count,int depth){
  if(depth>64)throw new IOException("Слишком много вложенных папок (больше 64).");if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("Папка содержит ссылку: "+directory);
  foreach(string file in Directory.EnumerateFiles(directory)){
   if(stopped)throw new IOException("Упаковка остановлена.");var info=new FileInfo(file);if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Папка содержит ссылку: "+info.Name);
   if(++count>100000||info.Length>MaxFile-total)throw new IOException("Папка превышает 10 ГиБ или 100 000 файлов.");
   var entry=zip.CreateEntry(prefix+info.Name,CompressionLevel.Fastest);using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=entry.Open()){
    byte[] buffer=new byte[131072];int n;while((n=input.Read(buffer,0,buffer.Length))>0){if(stopped)throw new IOException("Упаковка остановлена.");total+=n;if(total>MaxFile)throw new IOException("Папка больше 10 ГиБ.");output.Write(buffer,0,n);}
   }
  }
  foreach(string child in Directory.EnumerateDirectories(directory)){if(++count>100000)throw new IOException("Слишком много элементов.");string name=prefix+Path.GetFileName(child)+"/";zip.CreateEntry(name);PackFolder(zip,child,name,ref total,ref count,depth+1);}
 }
 void CleanupArchives(){lock(fileGate)foreach(string file in ownedArchives.ToArray())try{if(files.Exists(f=>string.Equals(f.Path,file,StringComparison.OrdinalIgnoreCase)))continue;File.Delete(file);Directory.Delete(Path.GetDirectoryName(file));ownedArchives.Remove(file);}catch(IOException){}catch(UnauthorizedAccessException){}}
}
}
