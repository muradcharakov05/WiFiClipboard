using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace WifiClipboard {
public sealed partial class BridgeForm {
 readonly Queue<string> shareQueue=new Queue<string>();bool preparing;
 void AddFeatures(FlowLayoutPanel layout){
  var qr=new Button{Text="QR-код подключения…",Width=610,Height=42};qr.Click+=delegate{ShowQr();};layout.Controls.Add(qr);layout.Controls.SetChildIndex(qr,4);
  var folder=new Button{Text="Выбрать папку → отправить ZIP…",Width=610,Height=42};folder.Click+=delegate{using(var d=new FolderBrowserDialog{Description="Выберите папку для упаковки в ZIP",ShowNewFolderButton=false})if(d.ShowDialog(this)==DialogResult.OK)SharePaths(new[]{d.SelectedPath});};layout.Controls.Add(folder);layout.Controls.SetChildIndex(folder,10);
  var drop=new Label{Text="Перетащите сюда файлы или папки из Проводника.\nПапки упакуются в ZIP автоматически.",AutoSize=false,Width=610,Height=65,BackColor=Color.FromArgb(223,240,233),TextAlign=ContentAlignment.MiddleCenter};layout.Controls.Add(drop);layout.Controls.SetChildIndex(drop,11);
  var features=new FlowLayoutPanel{Width=620,Height=48,WrapContents=false,Margin=new Padding(0,12,0,0)};
  var history=new Button{Text="История передачи…",Width=300,Height=42};history.Click+=delegate{ShowHistory();};features.Controls.Add(history);
  var devices=new Button{Text="Устройства…",Width=300,Height=42};devices.Click+=delegate{ShowDevices();};features.Controls.Add(devices);layout.Controls.Add(features);layout.Controls.SetChildIndex(features,14);
  AttachDrop(this);
 }
 void AttachDrop(Control control){control.AllowDrop=true;control.DragEnter+=delegate(object sender,DragEventArgs e){e.Effect=e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;};control.DragDrop+=delegate(object sender,DragEventArgs e){var paths=e.Data.GetData(DataFormats.FileDrop) as string[];if(paths!=null)SharePaths(paths);};foreach(Control child in control.Controls)AttachDrop(child);}
 void SharePaths(IEnumerable<string> paths){foreach(string p in paths){if(shareQueue.Count>=100){fileStatus.Text="В очереди уже 100 файлов или папок.";break;}shareQueue.Enqueue(p);}PrepareNext();}
 void PrepareNext(){if(preparing||shareQueue.Count==0||IsDisposed||Disposing)return;preparing=true;string next=shareQueue.Dequeue();fileStatus.Text="Подготовка: "+Path.GetFileName(next);
  Task.Factory.StartNew(delegate{string error=null;try{server.SharePath(next);}catch(Exception e){error=e.Message;}try{BeginInvoke(new Action(delegate{preparing=false;fileStatus.Text=error??("Доступно на iPhone: "+server.SharedCount+" файлов. Откройте список в Safari.");if(error!=null)MessageBox.Show(this,error,"Не удалось подготовить файл");PrepareNext();}));}catch(InvalidOperationException){}});
 }
 void ShowQr(){using(var dialog=new Form{Text="Подключение по QR",ClientSize=new Size(530,500),StartPosition=FormStartPosition.CenterParent,Font=Font}){
  var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(18),AutoScroll=true};dialog.Controls.Add(panel);
  panel.Controls.Add(new Label{Text="Откройте камеру iPhone и наведите на QR-код.",AutoSize=true});
  var addresses=new ComboBox{Width=485,DropDownStyle=ComboBoxStyle.DropDownList};panel.Controls.Add(addresses);
  var picture=new PictureBox{Width=287,Height=287,SizeMode=PictureBoxSizeMode.Normal,Margin=new Padding(96,12,0,12)};panel.Controls.Add(picture);
  addresses.SelectedIndexChanged+=delegate{if(addresses.SelectedItem==null)return;var old=picture.Image;picture.Image=ConnectionQr.Draw(addresses.SelectedItem+"#code="+server.Code);if(old!=null)old.Dispose();};
  Action refresh=delegate{
   RefreshNetwork(false);string selected=addresses.SelectedItem as string;
   var urls=ConnectionUrls().ToList();
   if(addresses.Items.Cast<string>().SequenceEqual(urls))return;
   addresses.Items.Clear();addresses.Items.AddRange(urls.Cast<object>().ToArray());addresses.SelectedIndex=selected!=null&&urls.Contains(selected)?urls.IndexOf(selected):0;
  };refresh();
  panel.Controls.Add(new Label{Text="При смене Wi-Fi адреса обновятся автоматически. Если .local\nне открывается, выберите новый IP в списке выше.",AutoSize=true});
  using(var refreshTimer=new System.Windows.Forms.Timer{Interval=2000}){refreshTimer.Tick+=delegate{refresh();};refreshTimer.Start();DesktopTheme.Dialog(dialog);dialog.ShowDialog(this);}
  if(picture.Image!=null)picture.Image.Dispose();
 }}
 static ListView Table(){return new ListView{Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false};}
 void ShowDevices(){using(var dialog=new Form{Text="Сохранённые устройства",ClientSize=new Size(740,440),StartPosition=FormStartPosition.CenterParent,Font=Font}){
  var list=Table();list.Columns.Add("Название",260);list.Columns.Add("Добавлено",180);list.Columns.Add("Последнее подключение",230);dialog.Controls.Add(list);
  var bar=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=100,Padding=new Padding(8)};dialog.Controls.Add(bar);var name=new TextBox{Width=450,MaxLength=60};bar.Controls.Add(name);
  Action refresh=delegate{list.Items.Clear();foreach(var device in server.Devices()){var row=new ListViewItem(device.Name){Tag=device.Id};row.SubItems.Add(device.Created==DateTime.MinValue?"—":device.Created.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));row.SubItems.Add(device.LastSeen==DateTime.MinValue?"—":device.LastSeen.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));list.Items.Add(row);}};
  list.SelectedIndexChanged+=delegate{if(list.SelectedItems.Count>0)name.Text=list.SelectedItems[0].Text;};
  var rename=new Button{Text="Переименовать",AutoSize=true};rename.Click+=delegate{if(list.SelectedItems.Count==0)return;try{server.RenameDevice((string)list.SelectedItems[0].Tag,name.Text);refresh();}catch(Exception e){MessageBox.Show(dialog,e.Message);}};bar.Controls.Add(rename);
  var revoke=new Button{Text="Отключить выбранное",AutoSize=true};revoke.Click+=delegate{if(list.SelectedItems.Count==0)return;try{server.RevokeDevice((string)list.SelectedItems[0].Tag);refresh();name.Clear();}catch(Exception e){MessageBox.Show(dialog,e.Message);}};bar.Controls.Add(revoke);
  var reload=new Button{Text="Обновить",AutoSize=true};reload.Click+=delegate{refresh();};bar.Controls.Add(reload);refresh();DesktopTheme.Dialog(dialog);dialog.ShowDialog(this);
 }}
 void ShowHistory(){using(var dialog=new Form{Text="История · последние 300 событий",ClientSize=new Size(1050,500),StartPosition=FormStartPosition.CenterParent,Font=Font}){
  var list=Table();list.Columns.Add("Время",165);list.Columns.Add("Направление",165);list.Columns.Add("Файл / событие",260);list.Columns.Add("Устройство",170);list.Columns.Add("Результат",250);dialog.Controls.Add(list);
  var bar=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=52,Padding=new Padding(8)};dialog.Controls.Add(bar);
  Action refresh=delegate{list.BeginUpdate();list.Items.Clear();foreach(var item in server.History()){var row=new ListViewItem(item.Time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"));row.SubItems.Add(item.Direction);row.SubItems.Add(item.Name);row.SubItems.Add(item.Device);row.SubItems.Add(item.Status);list.Items.Add(row);}list.EndUpdate();};
  var reload=new Button{Text="Обновить",AutoSize=true};reload.Click+=delegate{refresh();};bar.Controls.Add(reload);var clear=new Button{Text="Очистить историю",AutoSize=true};clear.Click+=delegate{try{server.ClearHistory();refresh();}catch(Exception e){MessageBox.Show(dialog,e.Message);}};bar.Controls.Add(clear);refresh();DesktopTheme.Dialog(dialog);dialog.ShowDialog(this);
 }}
}
}
