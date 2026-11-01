const fs=require('node:fs'),vm=require('node:vm'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const {File}=require('node:buffer');const path=require('node:path');const source=fs.readFileSync(path.join(__dirname,'transfer.js'),'utf8');let checks=0;
function check(value,message){assert.ok(value,message);checks++;console.log('PASS: '+message);}
function harness(options={}){
 const saved=new Map(),sessions=new Map(),nodes=new Map();const stats={posts:0,finishes:0,stored:0,statusGets:0};let lost=false,finishLost=false;
 const sha=bytes=>crypto.createHash('sha256').update(bytes).digest('hex');
 const state={authenticated:true,token:'test-token',currentUpload:null,uploading:false,transferEnabled:true,crypto:crypto.webcrypto,Uint8Array,Int32Array,DataView,Math,Promise,URLSearchParams,console,
  localStorage:{getItem:k=>saved.get(k)||null,setItem:(k,v)=>saved.set(k,v),removeItem:k=>saved.delete(k)},
  $:id=>{if(!nodes.has(id))nodes.set(id,{textContent:'',value:0,disabled:false,files:[],classList:{add(){},remove(){},toggle(){}},handlers:{},addEventListener(event,handler){this.handlers[event]=handler;}});return nodes.get(id);},
  window:{addEventListener(){}},refreshHistory(){},forget(){state.authenticated=false;},setTimeout:(f,ms)=>setTimeout(f,Math.min(ms,1)),clearTimeout};
 function snapshot(s){return {id:s.id,name:s.name,size:s.size,offset:s.offset,chunkSize:2097152,hashes:s.hashes.slice(),done:s.done};}
 state.api=async(route,options={})=>{
  if(route==='/api/uploads/start'){const data=JSON.parse(options.body);if(!sessions.has(data.id))sessions.set(data.id,{...data,offset:0,hashes:[],chunks:[],done:false});return snapshot(sessions.get(data.id));}
  const parts=route.slice('/api/uploads/'.length).split('/'),s=sessions.get(parts[0]);if(!s)throw Object.assign(Error('Not found'),{status:404});
  if(parts[1]==='finish'){stats.finishes++;if(!s.done){s.done=true;stats.stored++;}if(options&&options.method&&harnessOptions.loseFinish&&!finishLost){finishLost=true;throw Error('Response lost');}return snapshot(s);}
  if(parts[1]==='cancel'){sessions.delete(s.id);return {ok:true};}stats.statusGets++;return snapshot(s);
 };
 const harnessOptions=options;
 state.XMLHttpRequest=class{
  constructor(){this.upload={};this.headers={};}open(method,url){this.url=url;}setRequestHeader(k,v){this.headers[k]=v;}abort(){if(this.onabort)this.onabort();}
  send(bytes){stats.posts++;const postNumber=stats.posts;const s=sessions.get(this.url.split('/').pop());setTimeout(()=>{
   if(harnessOptions.failAtPost===postNumber){this.status=507;this.responseText=JSON.stringify({error:'Disk temporarily unavailable'});this.onload();return;}
   if(harnessOptions.loseBefore&&!lost){lost=true;this.onerror();return;}
   assert.equal(Number(this.headers['X-Upload-Offset']),s.offset);assert.equal(sha(bytes),this.headers['X-Chunk-SHA256']);s.offset+=bytes.length;s.hashes.push(sha(bytes));s.chunks.push(Buffer.from(bytes));
   if(harnessOptions.loseAfter&&!lost){lost=true;this.onerror();return;}this.status=200;this.responseText=JSON.stringify({offset:s.offset});this.onload();
  },0);}
 };
 const context=vm.createContext(state);vm.runInContext(source,context);return {context,state,saved,sessions,stats,nodes};
}
(async()=>{
 for(const n of ['app.js','transfer.js','file-view.js'])new vm.Script(fs.readFileSync(path.join(__dirname,n),'utf8'));check(true,'All browser scripts parse');
 const html=fs.readFileSync(path.join(__dirname,'phone.html'),'utf8'),ids=[...html.matchAll(/\bid="([^"]+)"/g)].map(m=>m[1]);check(new Set(ids).size===ids.length,'No duplicate HTML IDs');
 for(const n of ['app.js','transfer.js'])for(const m of fs.readFileSync(path.join(__dirname,n),'utf8').matchAll(/\$\('([^']+)'\)/g))assert.ok(ids.includes(m[1]),'Missing DOM element '+m[1]);check(true,'Every browser control exists in HTML');
 const bytes=crypto.randomBytes(2097152+321);const file=new File([bytes],'Фото 😀.bin',{lastModified:1700000000000});
 for(const scenario of [{loseAfter:true},{loseBefore:true},{loseFinish:true}]){
  const h=harness(scenario);await h.context.uploadResumable(file,1,1);const s=[...h.sessions.values()][0];check(Buffer.concat(s.chunks).equals(bytes),'Recovery preserves file bytes: '+JSON.stringify(scenario));check(h.stats.stored===1&&h.saved.size===0,'Completion stores once and clears checkpoint: '+JSON.stringify(scenario));
  if(scenario.loseAfter)check(h.stats.posts===2,'Lost chunk acknowledgement reconciles offset without retransmitting accepted data');
  if(scenario.loseBefore)check(h.stats.posts===3,'Lost connection before acceptance resends only the missing chunk');
  if(scenario.loseFinish)check(h.stats.finishes===2,'Lost finish acknowledgement is retried');
 }
 const h=harness(),id='a'.repeat(32),prefix=bytes.subarray(0,2097152),key='bridge-upload-v7:'+JSON.stringify([file.name,file.size,file.lastModified]);h.saved.set(key,id);h.sessions.set(id,{id,name:file.name,size:file.size,offset:prefix.length,hashes:[crypto.createHash('sha256').update(prefix).digest('hex')],chunks:[prefix],done:false});
 const changed=Buffer.from(bytes);changed[777]^=1;let rejected=false;try{await h.context.uploadResumable(new File([changed],file.name,{lastModified:file.lastModified}),1,1);}catch(e){rejected=e.message.includes('изменённый');}check(rejected&&h.stats.posts===0,'Reselection rejects changed content even with identical name, length and timestamp');
 await h.context.uploadResumable(file,1,1);check(h.stats.posts===1&&Buffer.concat(h.sessions.get(id).chunks).equals(bytes),'Reselection of original file verifies prefix and sends only the remaining part');
 const batch=harness({failAtPost:3});batch.state.$('filePicker').files=[new File([Buffer.from('first')],'first.txt',{lastModified:2}),file];await batch.state.$('uploadFiles').handlers.click();check(batch.stats.stored===1,'Multi-file batch preserves completed files when a later transfer fails');await batch.state.$('uploadFiles').handlers.click();check(batch.stats.stored===2&&batch.stats.posts===4,'Continuing a batch skips completed files and resumes the interrupted file');
 const empty=harness();await empty.context.uploadResumable(new File([],'empty.bin',{lastModified:1}),1,1);check(empty.stats.posts===0&&empty.stats.stored===1,'Empty browser file completes without a chunk');
 const paused=harness();vm.runInContext('uploadPaused=true',paused.context);let pause=false;try{await paused.context.uploadResumable(file,1,1);}catch(e){pause=e.paused;}check(pause&&paused.stats.posts===0,'Pause prevents further transfer');
 for(const len of [0,1,55,56,64,65536,2097152]){const data=crypto.randomBytes(len);check(h.context.sha256Bytes(data)===crypto.createHash('sha256').update(data).digest('hex'),'HTTP-compatible SHA-256 matches Node at '+len+' bytes');}
 console.log('All '+checks+' frontend checks passed.');
})().catch(e=>{console.error(e);process.exitCode=1;});
