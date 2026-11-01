'use strict';
const $ = id => document.getElementById(id);
let authenticated = false;
let token = '', version = -1, polling = false, sending = false;
let uploading = false, currentUpload = null, filesBusy = false, filesSignature = '', transferEnabled = false;
try { token = localStorage.getItem('bridge-token') || sessionStorage.getItem('bridge-token') || ''; } catch (_) {}
function message(s) { $('status').textContent = s; }
function paired(on) { $('pairCard').classList.toggle('hidden', on); $('connected').classList.toggle('hidden', !on); }
function forget() { if(currentUpload) currentUpload.abort(); authenticated = false; token = ''; version = -1; filesSignature = ''; $('fileList').replaceChildren(); try { localStorage.removeItem('bridge-token'); sessionStorage.removeItem('bridge-token'); } catch (_) {} paired(false); }
async function api(path, options = {}) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 6500);
  try {
    const r = await fetch(path, { ...options, cache: 'no-store', signal: controller.signal,
      headers: { 'X-Bridge-Token': token, 'Content-Type': 'application/json', ...(options.headers || {}) } });
    if (r.status === 401) { forget(); throw Object.assign(Error('Это подключение не сохранено или доступ отозван. Введите код Windows.'),{status:401}); }
    const data = await r.json();
    if (!r.ok) throw Object.assign(Error(data.error || 'Не удалось выполнить запрос.'),{status:r.status});
    return data;
  } finally { clearTimeout(timeout); }
}
async function poll() {
  if (!authenticated || polling || document.hidden) return;
  polling = true;
  try {
    const d = await api('/api/text');
    if (d.version !== version) { $('fromPc').value = d.text; version = d.version; }
    $('copy').disabled = !d.enabled || !d.text;
    $('send').disabled = !d.enabled || sending;
    transferEnabled = d.enabled;
    $('uploadFiles').disabled = !d.enabled || uploading;
    $('fileList').querySelectorAll('a').forEach(a => { a.hidden = !d.enabled; });
    message(d.enabled ? '● Соединение активно' : 'Передача приостановлена на Windows');
  } catch (e) { message('Нет связи. Проверьте Wi-Fi и окно программы.'); }
  finally { polling = false; }
}
$('pairForm').addEventListener('submit', async event => {
  event.preventDefault(); $('pairButton').disabled = true; $('pairError').textContent = '';
  try {
    const d = await api('/pair', { method: 'POST', body: JSON.stringify({ code: $('code').value.trim(), name: $('pairName').value.trim() }) });
    authenticated=true; token = d.token; try { localStorage.setItem('bridge-token', token); } catch (_) {}
    filesSignature=''; $('code').value = ''; paired(true);
    const info=await api('/session');$('localAddress').href=info.localUrl;$('localAddress').textContent=info.localUrl;await poll();await refreshFiles();
  } catch (e) { $('pairError').textContent = e.message; }
  finally { $('pairButton').disabled = false; }
});
$('copy').addEventListener('click', async () => {
  const area = $('fromPc');
  try {
    if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(area.value);
    else { area.focus(); area.select(); area.setSelectionRange(0, area.value.length); if (!document.execCommand('copy')) throw Error('manual'); }
    message('Скопировано. Можно вставлять в другое приложение.');
  } catch (_) { area.focus(); area.select(); area.setSelectionRange(0, area.value.length); message('Удерживайте выделенный текст → Скопировать.'); }
});
$('paste').addEventListener('click', async () => {
  const area = $('toPc'); area.focus();
  try {
    if (navigator.clipboard && window.isSecureContext) area.value = await navigator.clipboard.readText();
    else { document.execCommand('paste'); message('Если текст не появился: удерживайте поле → Вставить.'); }
  } catch (_) { message('Удерживайте поле → Вставить, затем нажмите «Отправить».'); }
});
$('send').addEventListener('click', async () => {
  if (sending) return;
  sending = true; $('send').disabled = true;
  try {
    const text = $('toPc').value;
    if (new TextEncoder().encode(text).length > 262144) throw Error('Текст больше 256 КБ.');
    await api('/api/text', { method: 'POST', body: JSON.stringify({ text }) });
    message('Текст в буфере Windows. Нажмите Ctrl+V на компьютере.');
  } catch (e) { message(e.name === 'AbortError' ? 'Нет ответа. Проверьте связь перед повторной отправкой.' : e.message); }
  finally { sending = false; $('send').disabled = false; }
});
$('disconnect').addEventListener('click', async () => {
  try { await api('/unpair', { method: 'POST', body: '{}' }); forget(); }
  catch (_) { message('Нет связи. Чтобы отозвать доступ, нажмите «Забыть все iPhone» на Windows.'); }
});
function sizeLabel(size) {
  if(size < 1024) return size + ' Б';
  if(size < 1048576) return (size/1024).toFixed(1) + ' КиБ';
  if(size < 1073741824) return (size/1048576).toFixed(1) + ' МиБ';
  return (size/1073741824).toFixed(2) + ' ГиБ';
}
async function refreshFiles() {
  if(!authenticated || filesBusy || document.hidden) return;
  filesBusy=true;
  try {
    const data=await api('/api/files');
    const signature=JSON.stringify(data.files);
    if(signature===filesSignature) return;
    filesSignature=signature;
    const list=$('fileList'); list.replaceChildren();
    if(!data.files.length) {list.textContent='Пока нет файлов. Выберите их на Windows.';return;}
    for(const file of data.files) {
      const row=document.createElement('div');row.className='file-row';
      const title=document.createElement('strong');title.textContent=file.name;row.appendChild(title);
      const size=document.createElement('small');size.textContent=sizeLabel(file.size);row.appendChild(size);
      // A normal navigation streams directly into Safari's download manager, without a JS Blob.
      const link=document.createElement('a');link.textContent='Открыть / скачать';link.href='/file/'+encodeURIComponent(file.id);link.hidden=!transferEnabled;
      // The viewer has its own Back link; raw files never replace the exchange page.
      row.appendChild(link);list.appendChild(row);
    }
  } catch(e) { if(transferEnabled) $('fileMessage').textContent='Не удалось обновить список файлов. Проверьте соединение.'; }
  finally {filesBusy=false;}
}
$('refreshFiles').addEventListener('click',refreshFiles);
async function restoreConnection() {
  if(authenticated || document.hidden) return;
  try {
    const data=await api('/session'); authenticated=true; paired(true);
    $('restoreStatus').textContent='';
    const link=$('localAddress');link.href=data.localUrl;link.textContent=data.localUrl;
    await poll();await refreshFiles();
  }catch(e){$('restoreStatus').textContent=e.name==='AbortError'?'Компьютер не отвечает. Запустите программу Windows.':e.message;}
}
setInterval(refreshFiles,4000);paired(false);restoreConnection();setInterval(restoreConnection,7000);setInterval(poll,1800);
document.addEventListener('visibilitychange', () => { if (!document.hidden) {if(authenticated){poll();refreshFiles();}else restoreConnection();} });

async function refreshHistory(){if(!authenticated)return;try{const data=await api('/api/history');const list=$('historyList');list.replaceChildren();if(!data.entries.length){list.textContent='История пока пуста.';return;}for(const entry of data.entries){const row=document.createElement('div');row.className='file-row';const title=document.createElement('strong');title.textContent=entry.name;row.appendChild(title);const detail=document.createElement('small');detail.textContent=entry.time+' · '+entry.direction+' · '+entry.device+' · '+entry.status;row.appendChild(detail);list.appendChild(row);}}catch(e){$('historyList').textContent=e.message;}}
$('refreshHistory').addEventListener('click',refreshHistory);
$('historySection').addEventListener('toggle',()=>{if($('historySection').open)refreshHistory();});
$('deviceSection').addEventListener('toggle',async()=>{if(!$('deviceSection').open||!authenticated)return;try{const d=await api('/api/device');$('deviceName').value=d.name;}catch(e){$('deviceMessage').textContent=e.message;}});
$('saveDeviceName').addEventListener('click',async()=>{try{await api('/api/device',{method:'POST',body:JSON.stringify({name:$('deviceName').value})});$('deviceMessage').textContent='Название сохранено.';}catch(e){$('deviceMessage').textContent=e.message;}});
try{const params=new URLSearchParams(location.hash.slice(1));const code=params.get('code');if(code&&/^[0-9]{8}$/.test(code)){$('code').value=code;history.replaceState(null,'',location.pathname);}}catch(_){}
