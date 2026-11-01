using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
namespace WifiClipboard {
// QR Model 2, version 4, low ECC, byte mode. Fixed mask 0; URLs are ASCII and at most 78 bytes.
public static class ConnectionQr {
 static int Multiply(int x,int y){int r=0;for(int i=7;i>=0;i--){r=(r<<1)^((r>>7)*0x11D);r^=((y>>i)&1)*x;}return r;}
 static void Bits(List<bool> bits,int value,int length){for(int i=length-1;i>=0;i--)bits.Add(((value>>i)&1)!=0);}
 public static bool[,] Matrix(string text){
  byte[] payload=Encoding.UTF8.GetBytes(text);if(payload.Length>78)throw new ArgumentException("Адрес слишком длинный для QR.");
  var bits=new List<bool>();Bits(bits,4,4);Bits(bits,payload.Length,8);foreach(byte b in payload)Bits(bits,b,8);Bits(bits,0,Math.Min(4,640-bits.Count));while(bits.Count%8!=0)bits.Add(false);
  var words=new List<byte>();for(int i=0;i<bits.Count;i+=8){int b=0;for(int j=0;j<8;j++)b=(b<<1)|(bits[i+j]?1:0);words.Add((byte)b);}for(int pad=0;words.Count<80;pad++)words.Add((byte)(pad%2==0?236:17));
  byte[] divisor=new byte[20];divisor[19]=1;int root=1;for(int i=0;i<20;i++){for(int j=0;j<20;j++){divisor[j]=(byte)Multiply(divisor[j],root);if(j+1<20)divisor[j]^=divisor[j+1];}root=Multiply(root,2);}
  byte[] ecc=new byte[20];foreach(byte b in words){int factor=b^ecc[0];Array.Copy(ecc,1,ecc,0,19);ecc[19]=0;for(int j=0;j<20;j++)ecc[j]^=(byte)Multiply(divisor[j],factor);}words.AddRange(ecc);
  bool[,] m=new bool[33,33],reserved=new bool[33,33];Action<int,int,bool> set=(x,y,dark)=>{if(x>=0&&y>=0&&x<33&&y<33){m[x,y]=dark;reserved[x,y]=true;}};
  for(int i=0;i<33;i++){set(6,i,i%2==0);set(i,6,i%2==0);}
  Action<int,int> finder=(cx,cy)=>{for(int dy=-4;dy<=4;dy++)for(int dx=-4;dx<=4;dx++){int distance=Math.Max(Math.Abs(dx),Math.Abs(dy));set(cx+dx,cy+dy,distance!=2&&distance!=4);}};
  finder(3,3);finder(29,3);finder(3,29);
  for(int dy=-2;dy<=2;dy++)for(int dx=-2;dx<=2;dx++)set(26+dx,26+dy,Math.Max(Math.Abs(dx),Math.Abs(dy))!=1);
  int format=0x77C4;Func<int,bool> bit=i=>((format>>i)&1)!=0;
  for(int i=0;i<=5;i++)set(8,i,bit(i));set(8,7,bit(6));set(8,8,bit(7));set(7,8,bit(8));for(int i=9;i<15;i++)set(14-i,8,bit(i));
  for(int i=0;i<8;i++)set(32-i,8,bit(i));for(int i=8;i<15;i++)set(8,18+i,bit(i));set(8,25,true);
  int index=0;for(int right=32;right>=1;right-=2){if(right==6)right=5;for(int v=0;v<33;v++){int y=((right+1)&2)==0?32-v:v;for(int j=0;j<2;j++){int x=right-j;if(reserved[x,y])continue;bool dark=index<words.Count*8&&((words[index>>3]>>(7-(index&7)))&1)!=0;index++;m[x,y]=dark^((x+y)%2==0);}}}
  return m;
 }
 public static Bitmap Draw(string text,int scale=7){var m=Matrix(text);var bmp=new Bitmap(41*scale,41*scale);using(var g=Graphics.FromImage(bmp)){g.Clear(Color.White);for(int y=0;y<33;y++)for(int x=0;x<33;x++)if(m[x,y])g.FillRectangle(Brushes.Black,(x+4)*scale,(y+4)*scale,scale,scale);}return bmp;}
}
}
