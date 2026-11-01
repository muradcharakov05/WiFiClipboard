'use strict';
// SHA-256 works on local HTTP too, where crypto.subtle is unavailable.
function sha256Bytes(bytes) {
  const k=[], initial=[];let candidate=2;
  while(k.length<64){let prime=true;for(let n=2;n*n<=candidate;n++)if(candidate%n===0){prime=false;break;}if(prime){if(initial.length<8)initial.push((Math.sqrt(candidate)%1*4294967296)|0);k.push((Math.cbrt(candidate)%1*4294967296)|0);}candidate++;}
  const length=bytes.length,total=Math.ceil((length+9)/64)*64,b=new Uint8Array(total);b.set(bytes);b[length]=128;
  const view=new DataView(b.buffer);view.setUint32(total-8,Math.floor(length/536870912));view.setUint32(total-4,(length*8)>>>0);
  const h=initial.slice(),w=new Int32Array(64),rotr=(x,n)=>(x>>>n)|(x<<(32-n));
  for(let start=0;start<total;start+=64){for(let i=0;i<16;i++)w[i]=view.getInt32(start+i*4);for(let i=16;i<64;i++){const x=w[i-15],y=w[i-2];w[i]=(w[i-16]+(rotr(x,7)^rotr(x,18)^(x>>>3))+w[i-7]+(rotr(y,17)^rotr(y,19)^(y>>>10)))|0;}
    let [a,c,d,e,f,g,j,l]=h;
    for(let i=0;i<64;i++){const t1=(l+(rotr(f,6)^rotr(f,11)^rotr(f,25))+((f&g)^(~f&j))+k[i]+w[i])|0,t2=((rotr(a,2)^rotr(a,13)^rotr(a,22))+((a&c)^(a&d)^(c&d)))|0;l=j;j=g;g=f;f=(e+t1)|0;e=d;d=c;c=a;a=(t1+t2)|0;}
    [a,c,d,e,f,g,j,l].forEach((x,i)=>h[i]=(h[i]+x)|0);
  }
  return h.map(x=>(x>>>0).toString(16).padStart(8,'0')).join('');
}
let uploadPaused=false,activeUploadId='',activeUploadKey='',retryWake=null;
const completedInSelection=new Set();
$('filePicker').addEventListener('change',()=>completedInSelection.clear());
function pauseError(){return Object.assign(Error('Передача приостановлена. Нажмите «Отправить / продолжить». Полученные части сохранены.'),{paused:true});}
async function retryTransfer(operation){let attempts=0;for(;;){if(uploadPaused||!authenticated)throw pauseError();try{return await operation();}catch(e){if(uploadPaused||!authenticated)throw pauseError();if(e.status&&![409,429,503].includes(e.status))throw e;
  $('fileMessage').textContent=e.status===409?'Передача приостановлена на Windows. Ожидаем включения…':'Нет связи или сервер занят. Сохранённые части остаются на Windows; повторяем подключение…';
  await new Promise(resolve=>{const timer=setTimeout(()=>{retryWake=null;resolve();},Math.min(2000*++attempts,10000));retryWake=()=>{clearTimeout(timer);retryWake=null;resolve();};});}}
}
function newUploadId(){const bytes=new Uint8Array(16);crypto.getRandomValues(bytes);return Array.from(bytes,x=>x.toString(16).padStart(2,'0')).join('');}
function transferKey(file){return 'bridge-upload-v7:'+JSON.stringify([file.name,file.size,file.lastModified]);}
function rememberUpload(key,id){try{if(id)localStorage.setItem(key,id);else localStorage.removeItem(key);}catch(_){}}
function sendChunk(id,offset,bytes,hash,file,index,count){return new Promise((resolve,reject)=>{
 const xhr=new XMLHttpRequest();currentUpload=xhr;xhr.open('POST','/api/uploads/'+id);xhr.timeout=35000;
 xhr.setRequestHeader('X-Bridge-Token',token);xhr.setRequestHeader('Content-Type','application/octet-stream');xhr.setRequestHeader('X-Upload-Offset',String(offset));xhr.setRequestHeader('X-Chunk-SHA256',hash);
 xhr.upload.onprogress=e=>{if(e.lengthComputable){const percent=Math.floor((offset+e.loaded)/Math.max(1,file.size)*100);$('fileProgress').value=percent;$('fileMessage').textContent=index+'/'+count+' · '+file.name+' · '+percent+'%';}};
 xhr.onload=()=>{currentUpload=null;let data;try{data=JSON.parse(xhr.responseText);}catch(_){reject(Error('Неожиданный ответ Windows.'));return;}if(xhr.status===401)forget();if(xhr.status>=200&&xhr.status<300)resolve(data);else reject(Object.assign(Error(data.error||'Не удалось сохранить часть файла.'),{status:xhr.status}));};
 xhr.onerror=xhr.ontimeout=()=>{currentUpload=null;reject(Error('Обрыв связи.'));};xhr.onabort=()=>{currentUpload=null;reject(pauseError());};xhr.send(bytes);
});}
async function uploadResumable(file,index,count){
 activeUploadKey=transferKey(file);let id='';try{id=localStorage.getItem(activeUploadKey)||'';}catch(_){}if(!/^[a-f0-9]{32}$/.test(id))id=newUploadId();activeUploadId=id;rememberUpload(activeUploadKey,id);
 let info=await retryTransfer(async()=>{try{return await api('/api/uploads/'+id);}catch(e){if(e.status!==404&&e.status!==410)throw e;if(e.status===410){id=newUploadId();activeUploadId=id;rememberUpload(activeUploadKey,id);}return api('/api/uploads/start',{method:'POST',body:JSON.stringify({id,name:file.name,size:file.size,modified:String(file.lastModified)})});}});
 if(info.size!==file.size)throw Error('Размер исходного файла изменился. Выберите исходный файл или отмените загрузку.');
 // Verify every already accepted chunk when a file is selected again, not just its name and timestamp.
 for(let i=0;i<info.hashes.length;i++){if(uploadPaused)throw pauseError();$('fileMessage').textContent='Проверяем сохранённую часть '+(i+1)+'/'+info.hashes.length+' · '+file.name;const bytes=new Uint8Array(await file.slice(i*info.chunkSize,Math.min((i+1)*info.chunkSize,file.size)).arrayBuffer());if(sha256Bytes(bytes)!==info.hashes[i])throw Error('Выбран другой или изменённый файл. Выберите прежний файл либо отмените незавершённую загрузку.');await new Promise(resolve=>setTimeout(resolve,0));}
 let offset=info.offset;
 while(offset<file.size){if(uploadPaused)throw pauseError();const bytes=new Uint8Array(await file.slice(offset,offset+info.chunkSize).arrayBuffer()),hash=sha256Bytes(bytes),start=offset;
  let needsStatus=false;const result=await retryTransfer(async()=>{if(needsStatus){const current=await api('/api/uploads/'+id);if(current.offset===start+bytes.length){if(current.hashes[Math.floor(start/info.chunkSize)]!==hash)throw Object.assign(Error('Часть файла изменилась. Отмените загрузку.'),{status:400});return current;}if(current.offset!==start)throw Object.assign(Error('Загрузка изменена в другой вкладке. Приостановите её и повторите отправку.'),{status:400});}needsStatus=true;return sendChunk(id,start,bytes,hash,file,index,count);});offset=result.offset;
 }
 const result=await retryTransfer(()=>api('/api/uploads/'+id+'/finish',{method:'POST',body:'{}'}));rememberUpload(activeUploadKey,'');activeUploadId='';activeUploadKey='';return result;
}
$('uploadFiles').addEventListener('click',async()=>{
 if(uploading)return;const files=Array.from($('filePicker').files).filter(f=>!completedInSelection.has(transferKey(f)));if(!files.length){$('fileMessage').textContent='Выберите файлы. Для продолжения после закрытия Safari выберите тот же файл.';return;}
 const large=files.find(f=>f.size>10737418240);if(large){$('fileMessage').textContent=large.name+': больше 10 ГиБ.';return;}
 uploading=true;uploadPaused=false;$('uploadFiles').disabled=true;$('filePicker').disabled=true;$('disconnect').disabled=true;$('cancelUpload').classList.remove('hidden');$('discardUpload').classList.add('hidden');let sent=0;
 try{for(let i=0;i<files.length;i++){await uploadResumable(files[i],i+1,files.length);completedInSelection.add(transferKey(files[i]));sent++;}$('fileProgress').value=100;$('fileMessage').textContent='Готово. Сохранено файлов: '+sent+'.';$('filePicker').value='';refreshHistory();}
 catch(e){$('fileMessage').textContent=e.message+' Завершено файлов: '+sent+'.';}
 finally{uploading=false;$('uploadFiles').disabled=!transferEnabled;$('filePicker').disabled=false;$('disconnect').disabled=false;$('cancelUpload').classList.add('hidden');$('discardUpload').classList.toggle('hidden',!activeUploadId);}
});
$('cancelUpload').addEventListener('click',()=>{uploadPaused=true;if(currentUpload)currentUpload.abort();if(retryWake)retryWake();});
$('discardUpload').addEventListener('click',async()=>{if(uploading||!activeUploadId)return;try{const result=await api('/api/uploads/'+activeUploadId+'/cancel',{method:'POST',body:'{}'});rememberUpload(activeUploadKey,'');activeUploadId='';activeUploadKey='';$('discardUpload').classList.add('hidden');$('fileProgress').value=0;$('fileMessage').textContent=result.done?'Файл уже был сохранён на Windows.':'Незавершённая загрузка отменена. Теперь можно отправить файл заново.';}catch(e){$('fileMessage').textContent=e.message;}});
window.addEventListener('online',()=>{if(retryWake)retryWake();});
