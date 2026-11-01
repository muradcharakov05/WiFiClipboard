using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
namespace WifiClipboard {
internal sealed class RoundedFlowPanel : FlowLayoutPanel {
 public Color Surface=Color.White,Border=Color.FromArgb(223,232,236);
 public RoundedFlowPanel(){DoubleBuffered=true;ResizeRedraw=true;}
 protected override void OnSizeChanged(EventArgs e){
  base.OnSizeChanged(e);if(Width<2||Height<2)return;
  var previous=Region;using(var shape=DesktopTheme.RoundPath(new Rectangle(0,0,Width,Height),14))Region=new Region(shape);
  if(previous!=null)previous.Dispose();
 }
 protected override void OnPaintBackground(PaintEventArgs e){
  e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
  using(var back=new SolidBrush(Parent==null?Color.FromArgb(242,246,248):Parent.BackColor))e.Graphics.FillRectangle(back,ClientRectangle);
  var bounds=new Rectangle(0,0,Math.Max(1,Width-1),Math.Max(1,Height-1));
  using(var p=DesktopTheme.RoundPath(bounds,14))using(var fill=new SolidBrush(Surface))using(var pen=new Pen(Border)){e.Graphics.FillPath(fill,p);e.Graphics.DrawPath(pen,p);}
 }
}
internal static class DesktopTheme {
 internal static readonly Color Canvas=Color.FromArgb(242,246,248),Ink=Color.FromArgb(24,42,54),Muted=Color.FromArgb(91,111,122),Green=Color.FromArgb(119,218,177),Deep=Color.FromArgb(16,38,49),Soft=Color.FromArgb(235,243,245);
 internal static GraphicsPath RoundPath(Rectangle r,int radius){var p=new GraphicsPath();int d=Math.Min(radius*2,Math.Min(r.Width,r.Height));if(d<2){p.AddRectangle(r);return p;}p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
 internal static void RoundButton(Button b){
  Action set=delegate{if(b.Width<2||b.Height<2)return;var old=b.Region;using(var p=RoundPath(new Rectangle(0,0,b.Width,b.Height),10))b.Region=new Region(p);if(old!=null)old.Dispose();};
  b.SizeChanged+=delegate{set();};set();
 }
 internal static void Button(Button b,bool primary){
  b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderSize=0;b.BackColor=primary?Green:Soft;b.ForeColor=primary?Color.FromArgb(15,51,40):Ink;
  b.FlatAppearance.MouseOverBackColor=primary?Color.FromArgb(151,232,195):Color.FromArgb(218,232,235);b.FlatAppearance.MouseDownBackColor=primary?Color.FromArgb(99,200,157):Color.FromArgb(207,225,228);
  b.Font=new Font("Segoe UI",10,FontStyle.Bold);b.Cursor=Cursors.Hand;b.Margin=new Padding(0,6,0,5);RoundButton(b);
 }
 internal static void Dialog(Form form){
  form.BackColor=Canvas;form.ForeColor=Ink;
  Action<Control> style=null;style=delegate(Control parent){foreach(Control c in parent.Controls){
   var button=c as Button;var label=c as Label;var flow=c as FlowLayoutPanel;var list=c as ListView;var input=c as TextBox;var combo=c as ComboBox;
   if(button!=null)Button(button,false);
   if(label!=null){label.ForeColor=Ink;label.BackColor=Canvas;}
   if(flow!=null)flow.BackColor=Canvas;
   if(list!=null){list.BackColor=Color.White;list.ForeColor=Ink;list.BorderStyle=BorderStyle.FixedSingle;}
   if(input!=null){input.BackColor=Color.White;input.ForeColor=Ink;input.BorderStyle=BorderStyle.FixedSingle;}
   if(combo!=null){combo.BackColor=Color.White;combo.ForeColor=Ink;}
   style(c);
  }};style(form);
 }
 internal static Label Eyebrow(string text){return new Label{Text=text,AutoSize=true,Font=new Font("Segoe UI",9,FontStyle.Bold),ForeColor=Color.FromArgb(40,133,104),BackColor=Color.White,Margin=new Padding(0,0,0,9)};}
}
public sealed partial class BridgeForm {
 void ApplyDesktopTheme(FlowLayoutPanel layout,Panel footer,Button forget){
  var original=layout.Controls.Cast<Control>().ToArray();if(original.Length!=17)return;
  SuspendLayout();layout.SuspendLayout();layout.Controls.Clear();
  BackColor=DesktopTheme.Canvas;layout.BackColor=DesktopTheme.Canvas;layout.Padding=new Padding(22,20,22,16);
  var hero=NewCard();hero.Surface=DesktopTheme.Deep;hero.Border=DesktopTheme.Deep;hero.Padding=new Padding(20,18,20,17);
  var tag=new Label{Text="WINDOWS  ↔  IPHONE   ·   WI-FI",AutoSize=true,ForeColor=DesktopTheme.Green,BackColor=DesktopTheme.Deep,Font=new Font("Segoe UI",9,FontStyle.Bold),Margin=new Padding(0,0,0,4)};
  var title=(Label)original[0];title.Text="Мост буфера";title.ForeColor=Color.White;title.BackColor=DesktopTheme.Deep;title.Font=new Font("Segoe UI",24,FontStyle.Bold);title.Margin=new Padding(0,0,0,2);
  var subtitle=new Label{Text="Ваши файлы и текст рядом",AutoSize=true,ForeColor=Color.FromArgb(181,208,213),BackColor=DesktopTheme.Deep,Font=new Font("Segoe UI",10),Margin=new Padding(0)};
  hero.Controls.Add(tag);hero.Controls.Add(title);hero.Controls.Add(subtitle);layout.Controls.Add(hero);
  var connection=NewCard();connection.Controls.Add(DesktopTheme.Eyebrow("ПОДКЛЮЧЕНИЕ"));foreach(int i in new[]{1,2,3,4,5})connection.Controls.Add(original[i]);layout.Controls.Add(connection);
  var clipboard=NewCard();clipboard.Controls.Add(DesktopTheme.Eyebrow("БУФЕР ОБМЕНА"));foreach(int i in new[]{6,13,15})clipboard.Controls.Add(original[i]);layout.Controls.Add(clipboard);
  var files=NewCard();files.Controls.Add(DesktopTheme.Eyebrow("ФАЙЛЫ И ПАПКИ"));foreach(int i in new[]{7,8,9,10,11,12})files.Controls.Add(original[i]);layout.Controls.Add(files);
  var manage=NewCard();manage.Controls.Add(DesktopTheme.Eyebrow("УПРАВЛЕНИЕ"));foreach(int i in new[]{14,16})manage.Controls.Add(original[i]);layout.Controls.Add(manage);
  foreach(var label in original.OfType<Label>()){label.BackColor=Color.White;label.ForeColor=DesktopTheme.Muted;label.Font=new Font("Segoe UI",10);label.Margin=new Padding(0,3,0,8);}
  title.BackColor=DesktopTheme.Deep;title.ForeColor=Color.White;title.Font=new Font("Segoe UI",24,FontStyle.Bold);
  var code=(Label)original[5];code.ForeColor=DesktopTheme.Ink;code.Font=new Font("Segoe UI",16,FontStyle.Bold);code.Margin=new Padding(0,13,0,0);
  var filesTitle=(Label)original[7];filesTitle.ForeColor=DesktopTheme.Ink;filesTitle.Font=new Font("Segoe UI",14,FontStyle.Bold);filesTitle.Margin=new Padding(0,0,0,8);
  var drop=(Label)original[11];drop.BackColor=Color.FromArgb(231,247,239);drop.ForeColor=Color.FromArgb(34,103,78);drop.Font=new Font("Segoe UI",10,FontStyle.Bold);drop.Margin=new Padding(0,8,0,8);
  addressBox.BackColor=Color.FromArgb(244,248,249);addressBox.ForeColor=DesktopTheme.Ink;addressBox.BorderStyle=BorderStyle.FixedSingle;addressBox.Font=new Font("Consolas",10);addressBox.Margin=new Padding(0,5,0,9);
  DesktopTheme.Button((Button)original[4],true);DesktopTheme.Button((Button)original[8],true);DesktopTheme.Button((Button)original[10],false);DesktopTheme.Button((Button)original[13],false);
  foreach(var row in new[]{(FlowLayoutPanel)original[9],(FlowLayoutPanel)original[14]}){row.BackColor=Color.White;row.Margin=new Padding(0,0,0,4);foreach(Button b in row.Controls.OfType<Button>())DesktopTheme.Button(b,false);}
  fileStatus.ForeColor=DesktopTheme.Muted;status.ForeColor=Color.FromArgb(40,133,104);
  footer.BackColor=DesktopTheme.Canvas;DesktopTheme.Button(forget,false);forget.ForeColor=Color.FromArgb(138,78,79);
  foreach(RoundedFlowPanel card in new[]{hero,connection,clipboard,files,manage}){
   card.AllowDrop=true;card.DragEnter+=delegate(object sender,DragEventArgs e){e.Effect=e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;};
   card.DragDrop+=delegate(object sender,DragEventArgs e){var paths=e.Data.GetData(DataFormats.FileDrop) as string[];if(paths!=null)SharePaths(paths);};
  }
  Action resize=delegate {
   int width=Math.Max(390,Math.Min(620,layout.ClientSize.Width-layout.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth-8));
   foreach(RoundedFlowPanel card in new[]{hero,connection,clipboard,files,manage}){
    if(card.Width!=width){card.MaximumSize=Size.Empty;card.MinimumSize=Size.Empty;card.Width=width;card.MinimumSize=new Size(width,0);card.MaximumSize=new Size(width,0);}
    int inner=Math.Max(200,width-card.Padding.Horizontal-4);
    foreach(Control child in card.Controls){
     var label=child as Label;if(label!=null){if(label.AutoSize)label.MaximumSize=new Size(inner,0);else label.Width=inner;continue;}
     var row=child as FlowLayoutPanel;if(row!=null){row.Width=inner;int half=Math.Max(80,(inner-12)/2);foreach(Button button in row.Controls.OfType<Button>()){button.Width=half;button.Margin=new Padding(0,3,8,3);}continue;}
     if(child is TextBox || child is Button)child.Width=inner;
    }
   }
  };
  layout.SizeChanged+=delegate{resize();};resize();
  layout.ResumeLayout(true);ResumeLayout(true);
 }
 RoundedFlowPanel NewCard(){return new RoundedFlowPanel{Width=620,MinimumSize=new Size(620,0),MaximumSize=new Size(620,0),AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(20,17,20,17),Margin=new Padding(0,0,0,12)};}
}
}
