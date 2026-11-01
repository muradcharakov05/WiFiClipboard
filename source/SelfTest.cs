using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;

namespace WifiClipboard {
static class SelfTest {
    sealed class Response { public int Status; public string Body; }
    static int checks;
    static readonly List<string> results=new List<string>();
    internal static void Check(bool condition,string name) { if(!condition) throw new Exception("FAIL: "+name); checks++;results.Add("PASS: "+name); }
    static Response Send(Server server,string method,string path,string body,string token,string extra,string host) {
        using(var client=new TcpClient("127.0.0.1",server.Port)) {
            client.ReceiveTimeout=5000; var stream=client.GetStream();
            byte[] data=Encoding.UTF8.GetBytes(body??"");
            string req=method+" "+path+" HTTP/1.1\r\nHost: "+(host??"127.0.0.1:"+server.Port)+"\r\nContent-Type: application/json\r\nContent-Length: "+data.Length+"\r\n";
            if(token!=null) req+="X-Bridge-Token: "+token+"\r\n";
            req+=(extra??"")+"Connection: close\r\n\r\n";
            byte[] header=Encoding.ASCII.GetBytes(req);stream.Write(header,0,header.Length);stream.Write(data,0,data.Length);
            var headBytes=new List<byte>();
            while(true) { int next=stream.ReadByte(); if(next<0) throw new EndOfStreamException(); headBytes.Add((byte)next); int n=headBytes.Count; if(n>=4 && headBytes[n-4]==13 && headBytes[n-3]==10 && headBytes[n-2]==13 && headBytes[n-1]==10) break; }
            string responseHead=Encoding.ASCII.GetString(headBytes.ToArray()); int size=0;
            foreach(string row in responseHead.Split(new string[]{"\r\n"},StringSplitOptions.RemoveEmptyEntries)) if(row.StartsWith("Content-Length:")) size=int.Parse(row.Substring(15).Trim());
            var responseBytes=new byte[size]; int received=0;
            while(received<size) {int n=stream.Read(responseBytes,received,size-received);if(n==0)throw new EndOfStreamException();received+=n;}
            return new Response {Status=int.Parse(responseHead.Split(' ')[1]),Body=Encoding.UTF8.GetString(responseBytes)};
        }
    }
    static Response Send(Server s,string method,string path,string body=null,string token=null) {return Send(s,method,path,body,token,null,null);}
    public static void Run(string output) {
        try {
            var state=new State();string clipboard="";
            state.WriteClipboard=delegate(string text) {clipboard=text;state.Publish(text);return true;};
            using(var s=new Server(state,0,new string[0])) {
                Check(Send(s,"GET","/").Body.Contains("Windows → iPhone"),"Embedded phone page");
                Check(Send(s,"GET","/app.js").Body.Contains("execCommand"),"HTTP clipboard fallback included");
                Check(Send(s,"GET","/api/text").Status==401,"Anonymous clipboard access blocked");
                Check(Send(s,"GET","/api/text",null,"invalid").Status==401,"Invalid token blocked");
                Check(Send(s,"POST","/pair","{\"code\":\"wrong\"}").Status==403,"Wrong pairing code blocked");
                var pair=Send(s,"POST","/pair","{\"code\":\""+s.Code+"\"}");
                Check(pair.Status==200,"Pairing works");
                string token=(string)new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(pair.Body)["token"];
                state.Publish("Привет 👋\nhttps://example.com/?a=1&b=2");
                var get=Send(s,"GET","/api/text",null,token);
                var snapshot=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(get.Body);
                Check((string)snapshot["text"]==state.Text,"Windows to phone Unicode roundtrip");
                var post=Send(s,"POST","/api/text","{\"text\":\"С iPhone 😀\\nстрока 2\"}",token);
                Check(post.Status==200 && clipboard=="С iPhone 😀\nстрока 2","Phone to clipboard adapter Unicode roundtrip");
                state.Enabled=false;
                Check(Send(s,"POST","/api/text","{\"text\":\"blocked\"}",token).Status==409,"Pause blocks clipboard write");
                Check((string)new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Send(s,"GET","/api/text",null,token).Body)["text"]=="","Pause hides current text");
                state.Enabled=true;
                Check(Send(s,"POST","/api/text","{\"text\":12}",token).Status==400,"Wrong text type rejected");
                Check(Send(s,"POST","/api/text","{invalid",token).Status==400,"Malformed JSON rejected");
                Check(Send(s,"POST","/api/text","null",token).Status==400,"Null JSON rejected");
                Check(Send(s,"POST","/api/text","{\"text\":\"\\u0000\"}",token).Status==413,"NUL rejected");
                string large=new string('x',Server.MaxText+1);
                Check(Send(s,"POST","/api/text","{\"text\":\""+large+"\"}",token).Status==413,"Oversized text rejected");
                Check(Send(s,"POST","/api/text","{}",token,"Origin: http://evil.invalid\r\n",null).Status==403,"Cross-origin request rejected");
                Check(Send(s,"GET","/",null,null,null,"evil.invalid").Status==403,"DNS rebinding host rejected");
                Check(Send(s,"GET","/unknown").Status==404,"Unknown path rejected");
                Check(Send(s,"POST","/api/text","{\"text\":\"\"}",token).Status==200 && clipboard=="","Explicit empty clipboard supported");
                Check(Send(s,"POST","/unpair","{}",token).Status==200,"Unpair works");
                Check(Send(s,"GET","/api/text",null,token).Status==401,"Revoked token rejected");
                for(int i=0;i<7;i++) Send(s,"POST","/pair","{\"code\":\"wrong\"}");
                Check(Send(s,"POST","/pair","{\"code\":\""+s.Code+"\"}").Status==429,"Pairing rate limit enforced");
            }
            FileTests.Run();
            ExperienceTests.Run();
            NetworkTests.Run();
            SafariTests.Run();
            FeatureTests.Run(Path.GetDirectoryName(Path.GetFullPath(output)));
            results.Add("All "+checks+" checks passed. Native clipboard replaced by an in-memory adapter; no user clipboard changes.");
        } catch(Exception e) {results.Add(e.ToString());Environment.ExitCode=1;}
        File.WriteAllLines(output,results.ToArray(),Encoding.UTF8);
    }
}
}
