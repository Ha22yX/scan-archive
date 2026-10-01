'use strict';
const $=id=>document.getElementById(id);
const state={view:'library',options:null,conversation:'',document:null,page:1,offset:0,lastMessages:'',searching:false,listToken:0,docToken:0,authenticated:false,settingsDirty:false,settingsLoaded:false,context:null,sending:false,chatVersion:0,chatRequest:0,conversationsRequest:0,chatWorking:false,chatDraftKey:'new:0',newDraftCounter:0,chatDrafts:new Map()};
const labels={queued:'等待分析',analyzing:'正在分析',indexing:'建立索引',analyzed:'等待整理',ready:'已归档',superseded:'已整合 · 保留原件',error:'需要处理',pending:'等待执行',running:'执行中',done:'已完成',failed:'需要处理',index:'内容分析',organize:'扫描后整理',review:'文档库巡检',chat:'秘书对话',embeddings:'语义索引',reindex:'重建索引',reanalyze:'重新分析',format_summary:'整理摘要'};
const titles={library:['YOUR DOCUMENTS','文档库','每一份原件与线索，都在这里。'],agent:['YOUR PERSONAL SECRETARY','文档秘书','用一句话找到资料，或让秘书帮你整理。'],activity:['EVERY CHANGE, RECORDED','处理记录','处理进度、文件调整与原件，一切可追溯。'],settings:['MAKE IT YOURS','设置','让文档工作台按你的习惯运行。']};
const el=(tag,text='',cls='')=>{const n=document.createElement(tag);n.textContent=String(text??'');n.className=cls;return n;};
const date=value=>{if(!value)return '—';const d=new Date(value);return Number.isNaN(d.getTime())?String(value):d.toLocaleString('zh-CN',{hour12:false});};
const shortDate=value=>{if(!value)return '—';const d=new Date(value);return Number.isNaN(d.getTime())?String(value):d.toLocaleDateString('zh-CN');};
const show=(id,visible)=>$(id).classList.toggle('hidden',!visible);
const badge=status=>el('span',labels[status]||status,'badge status-'+status);
function toast(message){$('toast').textContent=message;show('toast',true);clearTimeout(toast.timer);toast.timer=setTimeout(()=>show('toast',false),6500);}
function failed(error){if(error?.name!=='AbortError')toast(error?.message||'操作未完成，请稍后重试');}
function on(id,event,fn){$(id).addEventListener(event,e=>{try{Promise.resolve(fn(e)).catch(failed);}catch(error){failed(error);}});}
function button(text,fn,cls='secondary compact'){const b=el('button',text,cls);b.type='button';b.addEventListener('click',()=>Promise.resolve().then(fn).catch(failed));return b;}
function markdown(target,text){renderMarkdown(target,text,(id,page)=>openDoc(id,page).catch(failed));}
function connection(connected){$('service-state').textContent=connected?'文档库已连接':'连接已断开';$('service-dot').classList.toggle('offline',!connected);show('connection-banner',!connected&&state.authenticated);}
async function busy(control,action,label){if(control.disabled)return;const old=control.textContent;control.disabled=true;if(label)control.textContent=label;try{return await action();}finally{control.disabled=false;control.textContent=old;}}
async function api(path,method='GET',body){
 const options={method,headers:{'X-ScanArchive':'1'}};
 if(body instanceof FormData)options.body=body;else if(body!==undefined){options.body=JSON.stringify(body);options.headers['Content-Type']='application/json';}
 let r;try{r=await fetch('/api'+path,options);}catch{connection(false);throw Error('无法连接文档库，请确认扫描电脑上的服务正在运行。');}
 const raw=await r.text();let data={};try{data=JSON.parse(raw);}catch{}
 if(!r.ok){if(r.status===401&&!['/login','/session'].includes(path)){state.authenticated=false;show('shell',false);show('login',true);$('document-dialog').close();}throw Error(data.error||(r.status===401?'登录已过期，请重新登录。':'请求未完成，请稍后重试。'));}
 connection(true);return data;
}
async function confirmAction(title,copy,accept='确认'){$('confirm-title').textContent=title;$('confirm-copy').textContent=copy;$('confirm-accept').textContent=accept;const d=$('confirm-dialog');d.returnValue='';d.showModal();return new Promise(resolve=>d.addEventListener('close',()=>resolve(d.returnValue==='confirm'),{once:true}));}
async function session(){const s=await api('/session');state.authenticated=s.authenticated;show('shell',s.authenticated);show('login',!s.authenticated);$('login-copy').textContent=s.setupRequired?(s.local?'首次使用，请创建至少 10 个字符的访问密码。':'请先在扫描电脑上打开 localhost:5278 设置密码。'):'登录你的私人文档库';return s.authenticated;}
on('login-form','submit',async e=>{e.preventDefault();$('login-error').textContent='';await busy(e.submitter,async()=>{try{await api('/login','POST',{password:$('password').value});$('password').value='';await start();}catch(error){$('login-error').textContent=error.message;}},'正在连接…');});
on('logout','click',async()=>{if(!await mayLeaveSettings())return;await api('/logout','POST',{});state.authenticated=false;await session();});
async function mayLeaveSettings(){if(!state.settingsDirty)return true;if(!await confirmAction('设置尚未保存','离开后，未保存的修改将被放弃。','放弃修改'))return false;state.settingsDirty=false;state.settingsLoaded=false;return true;}
async function view(name){
 if(!titles[name])name='library';if(name!==state.view&&state.view==='settings'&&!await mayLeaveSettings()){history.replaceState(null,'','#settings');return;}
 state.view=name;history.replaceState(null,'','#'+name);document.body.classList.toggle('agent-view',name==='agent');
 document.querySelectorAll('.view').forEach(x=>x.classList.toggle('hidden',x.id!=='view-'+name));
 document.querySelectorAll('[data-view]').forEach(x=>{const active=x.dataset.view===name;x.classList.toggle('active',active);x.setAttribute('aria-current',active?'page':'false');});
 [$('view-eyebrow').textContent,$('view-title').textContent,$('view-description').textContent]=titles[name];
 if(name==='library')await library();if(name==='settings')await settings();if(name==='activity')await activity();if(name==='agent'){if(!state.conversation)welcome();await conversations();$('chat-input').focus();}
}
document.querySelectorAll('[data-view]').forEach(b=>b.addEventListener('click',()=>view(b.dataset.view).catch(failed)));
on('config-badge','click',()=>view('settings'));
on('refresh','click',()=>busy($('refresh'),async()=>{await status();if(state.view==='settings'&&state.settingsDirty){toast('未保存的设置已保留。');return;}await view(state.view);},'刷新中…'));
async function status(){
 const data=await api('/status');state.options=data.settings;$('config-badge').textContent=data.configured?'✦ 智能服务已配置':'配置智能服务';$('config-badge').className='badge'+(data.configured?'':' warn');
 const counts=Object.fromEntries(data.overview.counts.map(x=>[x.status,Number(x.count)]));const total=Object.values(counts).reduce((a,b)=>a+b,0);const pending=(data.pending||[]).filter(x=>['pending','running'].includes(x.status)).reduce((a,b)=>a+Number(b.count),0);
 $('stats').replaceChildren(...[[total,'份文档'],[counts.ready||0,'已归档'],[pending,'处理中']].map(([n,t])=>{const item=el('span','','stat');item.append(el('strong',n),el('span',t));return item;}));
 $('nav-pending').textContent=String(pending);show('nav-pending',pending>0);$('addresses').replaceChildren(...[...new Set(data.addresses||[])].map(url=>{const a=el('a',url);a.href=url;a.target='_blank';a.rel='noopener';return a;}));
}
async function categories(){const list=await api('/categories');const selected=$('category').value;$('category').replaceChildren(new Option('全部分类',''),...list.map(c=>new Option(c.category+' · '+c.count,c.category)));if([...$('category').options].some(x=>x.value===selected))$('category').value=selected;}
function emptyState(title,copy,action){const box=el('div','','empty');box.append(el('span','▤','empty-icon'),el('h3',title),el('p',copy));if(action)box.append(action);return box;}
function documentCard(doc,hit=false){
 const card=el('button','','document-card');card.type='button';card.title=doc.title;card.dataset.docId=hit?doc.doc_id:doc.id;
 const icon=el('span','DOC','doc-icon'),content=el('div','','card-content');content.append(el('h3',doc.title),el('p',plainMarkdown(hit?doc.snippet:doc.summary)||'原件已保存，内容分析完成后会显示摘要。','card-description'));
 const meta=el('div','','card-meta');meta.append(el('span',doc.category||'等待分类','card-category'),el('span','扫描于 '+shortDate(doc.scanned)));if(!hit&&doc.page_count)meta.append(el('span',doc.page_count+' 页'));content.append(meta);
 const tail=el('div','','card-tail');tail.append(hit?el('span','第 '+doc.page+' 页','badge'):badge(doc.status),el('span','↗','card-arrow'));card.append(icon,content,tail);card.addEventListener('click',()=>openDoc(hit?doc.doc_id:doc.id,doc.page||1).catch(failed));return card;
}
function filterState(){const from=$('date-from').value,to=$('date-to').value;if(from&&to&&from>to)throw Error('开始日期不能晚于结束日期。');show('reset-filters',!!($('category').value||$('status-filter').value||from||to));show('clear-search',!!$('query').value);$('date-filter-label').textContent=from||to?(from||'不限')+' — '+(to||'不限'):'扫描日期 ⌄';return {from,to};}
function loadingCards(){$('documents').replaceChildren(...Array.from({length:4},()=>{const n=el('div','','skeleton-card');n.setAttribute('aria-hidden','true');return n;}));}
async function library(append=false,silent=false){
 if(state.searching&&!append){if(!silent)return search();return;}
 const token=++state.listToken,{from,to}=filterState();if(!append)state.offset=0;const params=new URLSearchParams({status:$('status-filter').value,category:$('category').value,from,to,offset:state.offset});
 if(!silent&&!append&&!$('documents').querySelector('.document-card'))loadingCards();$('documents').setAttribute('aria-busy','true');
 try{
  if(!silent)await categories();const list=await api('/documents?'+params);if(token!==state.listToken)return;const signature=JSON.stringify(list);if(!append&&signature===state.listSignature)return;state.listSignature=signature;
  if(!append)$('documents').replaceChildren();$('documents').append(...list.map(d=>documentCard(d)));
  if(!list.length&&!append)$('documents').append($('category').value||$('status-filter').value||from||to?emptyState('这个范围内还没有文档','换一个分类或扫描日期，或者重置筛选。',button('重置筛选',resetFilters)):emptyState('把第一份资料放进来','在桌面端开始扫描，或导入已有的 PDF、JPG 和 PNG。',button('导入文件',()=>$('upload').click(),'primary')));
  show('load-more',list.length===100);$('library-heading').textContent='最近扫描';$('search-note').textContent='按扫描时间排序';$('library-foot').textContent=list.length?'已显示 '+$('documents').querySelectorAll('.document-card').length+' 份文档':'';
 }catch(error){if(token===state.listToken&&!silent)$('documents').replaceChildren(emptyState('文档暂时无法载入',error.message,button('重新加载',()=>library())));throw error;}
 finally{if(token===state.listToken)$('documents').setAttribute('aria-busy','false');}
}
on('load-more','click',()=>busy($('load-more'),async()=>{state.offset+=100;try{await library(true);}catch(e){state.offset-=100;throw e;}},'正在载入…'));
async function search(){
 const q=$('query').value.trim(),{from,to}=filterState();state.listSignature='';if(!q){state.searching=false;$('status-filter').disabled=false;await library();return;}
 state.searching=true;$('status-filter').disabled=true;$('status-filter').title='内容搜索按相关页面排序；清除搜索后可筛选处理状态。';
 const token=++state.listToken;$('search-note').textContent='正在结合原文、关键词与语义查找…';$('library-heading').textContent='搜索结果';$('search-button').disabled=true;show('load-more',false);$('documents').setAttribute('aria-busy','true');
 try{
  const data=await api('/search?'+new URLSearchParams({q,category:$('category').value,from,to}));if(token!==state.listToken)return;
  $('documents').replaceChildren(...data.results.map(d=>documentCard(d,true)));if(!data.results.length)$('documents').append(emptyState('暂时没有找到相关页面','试试文件中的编号、姓名或近义词。尚未完成分析的文档不会出现在内容搜索里。',button('让秘书深入查找',askSearch)));
  $('search-note').textContent=data.results.length+' 个相关页面 · '+(data.mode==='hybrid'?'全文与语义检索':'全文与关键词检索')+(data.warning?' · 语义检索暂不可用':'');$('library-foot').textContent='结果按相关程度排序，点击即可定位原文页码。';
 }catch(error){if(token===state.listToken){$('documents').replaceChildren(emptyState('搜索未完成',error.message,button('重试搜索',search)));$('search-note').textContent='';}throw error;}
 finally{if(token===state.listToken){$('search-button').disabled=false;$('documents').setAttribute('aria-busy','false');}}
}
on('search-form','submit',e=>{e.preventDefault();return search();});on('query','input',()=>show('clear-search',!!$('query').value));
on('clear-search','click',async()=>{$('query').value='';state.searching=false;$('status-filter').disabled=false;state.listSignature='';await library();$('query').focus();});
async function resetFilters(){$('category').value='';$('status-filter').value='';$('date-from').value='';$('date-to').value='';state.listSignature='';await(state.searching?search():library());}
on('reset-filters','click',resetFilters);on('reset-dates','click',async()=>{$('date-from').value='';$('date-to').value='';state.listSignature='';await(state.searching?search():library());});
for(const id of ['category','status-filter','date-from','date-to'])on(id,'change',()=>{state.listSignature='';return state.searching?search():library();});
async function askSearch(){const q=$('query').value.trim();await view('agent');$('chat-input').value=q?'请深入查找：'+q+'。尝试不同关键词，核对原文，并提供页码引用。':'';$('chat-input').focus();}
on('ask-search','click',askSearch);
function setLayout(mode){$('documents').classList.toggle('list-view',mode==='list');for(const m of ['list','grid']){$('layout-'+m).classList.toggle('active',m===mode);$('layout-'+m).setAttribute('aria-pressed',String(m===mode));}}
for(const mode of ['list','grid'])on('layout-'+mode,'click',()=>{setLayout(mode);try{localStorage.setItem('scanarchive-library-layout',mode);}catch{}});
try{setLayout(localStorage.getItem('scanarchive-library-layout')||'list');}catch{}
async function uploadFiles(files){
 if(state.uploading)return;const selected=[...files];if(!selected.length)return;if(selected.some(f=>!/\.(pdf|png|jpe?g)$/i.test(f.name)))throw Error('请选择 PDF、JPG 或 PNG 文件。');if(selected.reduce((s,f)=>s+f.size,0)>99*1024*1024)throw Error('一次导入请控制在 99 MB 内，可分批导入。');
 state.uploading=true;try{await busy($('upload-button'),async()=>{const body=new FormData();selected.forEach(f=>body.append('files',f));await api('/upload','POST',body);$('upload').value='';toast(selected.length+' 份文件已进入分析队列。');state.listSignature='';await status();if(!state.searching)await library();},'正在导入…');}finally{state.uploading=false;}
}
on('upload-button','click',()=>$('upload').click());on('upload','change',()=>uploadFiles($('upload').files));
let dragDepth=0;
document.addEventListener('dragenter',e=>{if(state.authenticated&&state.view==='library'&&e.dataTransfer?.types.includes('Files')){e.preventDefault();dragDepth++;show('drop-zone',true);}});
document.addEventListener('dragover',e=>{if(state.authenticated&&state.view==='library'&&e.dataTransfer?.types.includes('Files'))e.preventDefault();});
document.addEventListener('dragleave',()=>{if(--dragDepth<=0){dragDepth=0;show('drop-zone',false);}});
document.addEventListener('drop',e=>{if(!state.authenticated||state.view!=='library')return;e.preventDefault();dragDepth=0;show('drop-zone',false);uploadFiles(e.dataTransfer.files).catch(failed);});
function detailTab(name){document.querySelectorAll('[data-detail]').forEach(b=>{const active=b.dataset.detail===name;b.classList.toggle('active',active);b.setAttribute('aria-selected',active);});document.querySelectorAll('.detail-tab').forEach(x=>x.classList.toggle('hidden',x.id!=='detail-'+name));}
document.querySelectorAll('[data-detail]').forEach(b=>b.addEventListener('click',()=>detailTab(b.dataset.detail)));
async function openDoc(id,page=1){
 const token=++state.docToken,result=await api('/documents/'+encodeURIComponent(id));if(token!==state.docToken)return;state.document=result;state.page=page;const d=result.document;
 $('doc-title').textContent=d.title;$('doc-eyebrow').textContent=(d.category||'等待分类')+' · '+(d.page_count||0)+' 页';markdown($('doc-summary'),d.summary||'内容概括正在准备中。分析完成后，可在这里查看完整摘要与关键信息。');
 const tags=String(d.tags||'').split(/[,，;\n]/).map(x=>x.trim()).filter(Boolean);$('doc-tags').replaceChildren(...tags.map(t=>el('span',t,'tag')));if(!tags.length)$('doc-tags').append(el('p','分析完成后会提取可用于搜索的关键词。','subtle'));
 $('doc-meta').replaceChildren(badge(d.status),el('span','扫描于 '+date(d.scanned)),el('span',d.locked?'分类已锁定':'秘书自动管理'));if(d.error)$('doc-meta').append(el('p',d.error,'error'));
 const provenance=$('doc-provenance');provenance.replaceChildren(el('h3','文档来源'),el('p','原始扫描时间：'+date(d.scanned)),el('p','文档所载日期：'+(d.document_date||'未识别')),el('p','固定编号：'+d.id,'fixed-id'));
 if(d.parent_id)provenance.append(el('p','从原始扫描的第 '+d.source_pages+' 页拆分。'),button('查看原始扫描',()=>openDoc(d.parent_id)));
 const sources=(result.sources||[]).filter(s=>s.source_doc_id!==d.id);
 if(sources.length){provenance.append(el('h3','每页扫描来源'),el('p','保留每次扫描的时间与原页码；点击可核对原始扫描。','subtle'));
  for(const source of sources){const row=el('div','','source-record');row.append(button('本文件第 '+source.merged_page+' 页 ← '+source.title+' · 原第 '+source.source_page+' 页',()=>openDoc(source.source_doc_id,Number(source.source_page)),'child-link'),el('p','扫描于 '+date(source.scanned),'subtle'));provenance.append(row);}
 }
 const children=(result.children||[]).filter(c=>c.status!=='deleted');if(children.length){provenance.append(el('h3','拆分后的文档'));children.forEach(c=>provenance.append(button(c.title,()=>openDoc(c.id),'child-link')));}
 $('original-file').href='/api/documents/'+id+'/file';$('metadata-file').href='/api/documents/'+id+'/metadata';$('move-title').value=d.title;$('move-category').value=d.category||'';show('unlock',!!d.locked);
 const archived=['deleted','superseded'].includes(d.status);$('doc-trash').disabled=archived;$('doc-retry').disabled=archived||['analyzing','indexing'].includes(d.status);$('doc-organize').disabled=!['ready','analyzed'].includes(d.status);[...$('move-form').elements].forEach(c=>c.disabled=archived);
 detailTab('summary');renderPage();if(!$('document-dialog').open)$('document-dialog').showModal();
}
function renderPage(){
 if(!state.document)return;const d=state.document.document,total=Math.max(Number(d.page_count)||1,1);state.page=Math.min(Math.max(1,Math.trunc(Number(state.page))||1),total);
 $('page-number').value=state.page;$('page-number').max=total;$('page-label').textContent='/ '+total;$('prev-page').disabled=state.page===1;$('next-page').disabled=state.page===total;
 $('page-preview').alt=d.title+'，第 '+state.page+' 页';show('page-preview',false);show('preview-state',true);$('preview-state').replaceChildren(el('span','正在载入原件…'));$('page-preview').src='/api/documents/'+d.id+'/pages/'+state.page;$('preview-scroll').scrollTop=0;
 const page=state.document.pages.find(x=>Number(x.number)===state.page);$('page-text').textContent=page?.text||'本页尚未完成文字识别。';let analysis={};try{analysis=JSON.parse(page?.summary||'{}');}catch{}
 const description=[analysis.summary,...[['主题',analysis.topics],['人物与机构',analysis.entities],['日期与数字',analysis.dates_and_numbers],['识别说明',analysis.readability],['跨扫描衔接线索',analysis.continuity]].filter(x=>x[1]).map(([t,v])=>'### '+t+'\n'+(typeof v==='string'?v:JSON.stringify(v)))].filter(Boolean).join('\n\n');
 markdown($('page-summary'),description||'此页尚未完成内容分析。完成后会显示摘要、主题与关键线索。');
}
on('page-preview','load',()=>{show('page-preview',true);show('preview-state',false);});on('page-preview','error',()=>{show('page-preview',false);show('preview-state',true);$('preview-state').replaceChildren(el('p','这一页暂时无法预览。'),button('重新载入',renderPage));});
on('prev-page','click',()=>{state.page--;renderPage();});on('next-page','click',()=>{state.page++;renderPage();});on('page-number','change',()=>{state.page=$('page-number').value;renderPage();});on('page-number','keydown',e=>{if(e.key==='Enter'){e.preventDefault();state.page=$('page-number').value;renderPage();}});
for(const mode of ['page','width'])on('fit-'+mode,'click',()=>{$('preview-scroll').classList.toggle('fit-width',mode==='width');$('fit-page').classList.toggle('active',mode==='page');$('fit-width').classList.toggle('active',mode==='width');});
on('close-document','click',()=>$('document-dialog').close());
on('document-dialog','close',()=>{state.docToken++;});
on('document-dialog','click',e=>{if(e.target===$('document-dialog')){const r=$('document-dialog').getBoundingClientRect();if(e.clientX<r.left||e.clientX>r.right||e.clientY<r.top||e.clientY>r.bottom)$('document-dialog').close();}});
on('move-form','submit',async e=>{e.preventDefault();const id=state.document.document.id,page=state.page,token=state.docToken;await busy(e.submitter,async()=>{await api('/documents/'+id+'/move','POST',{title:$('move-title').value,category:$('move-category').value});toast('归档位置已更新并锁定。');if(token===state.docToken&&$('document-dialog').open){await openDoc(id,page);detailTab('manage');}state.listSignature='';},'保存中…');});
on('unlock','click',async()=>{const id=state.document.document.id,token=state.docToken;await api('/documents/'+id+'/unlock','POST',{});if(token===state.docToken)show('unlock',false);toast('已允许秘书再次调整分类。');});
on('doc-retry','click',()=>busy($('doc-retry'),async()=>{await api('/documents/'+state.document.document.id+'/retry','POST',{});toast('已加入内容分析队列。');},'正在提交…'));
on('doc-organize','click',()=>busy($('doc-organize'),async()=>{await api('/documents/'+state.document.document.id+'/organize','POST',{});toast('已交给秘书重新整理，原始扫描会保留。');},'正在提交…'));
on('doc-trash','click',async()=>{const d=state.document.document;if(!await confirmAction('移至回收站？','「'+d.title+'」将从文档库和搜索中移除，可在处理记录的回收站恢复。','移至回收站'))return;await api('/documents/'+d.id+'/trash','POST',{});$('document-dialog').close();state.listSignature='';toast('文档已移至回收站。');await status();await(state.searching?search():library());});
function setContext(context){state.context=context;show('chat-context',!!context);$('context-label').textContent=context?'附带文档：'+context.title+' · 第 '+context.page+' 页':'';}
on('ask-document','click',async()=>{const d=state.document.document;setContext({id:d.id,title:d.title,page:state.page});$('document-dialog').close();await view('agent');$('chat-input').focus();});on('remove-context','click',()=>setContext(null));
function welcome(){
 const box=el('div','','chat-welcome');box.append(el('div','✦','welcome-mark'),el('p','从一个问题开始','eyebrow'),el('h2','你的资料，我来找。'),el('p','查找某份原件、回顾最近扫描，或者一起把文档库整理得更好。'));
 const suggestions=el('div','','suggestions');[['查找一份文件','帮我查找一份文件，我会描述它的内容。'],['回顾最近扫描','查看最近扫描的文档，告诉我分别讲了什么，并提供原文引用。'],['优化文档分类','检查当前文档库，看看分类和搜索索引有什么可以改善的，并执行有价值的优化。']].forEach(([title,prompt])=>suggestions.append(button(title+' ↗',()=>{$('chat-input').value=prompt;$('chat-input').focus();},'suggestion')));box.append(suggestions);$('messages').replaceChildren(box);
}
function saveChatDraft(){state.chatDrafts.set(state.chatDraftKey,{text:$('chat-input').value,context:state.context?{...state.context}:null});}
function updateChatSend(){const send=$('send');send.disabled=state.sending||state.chatWorking;send.textContent=state.sending?'正在发送…':state.chatWorking?'等待回复':'发送 ↑';}
async function changeConversation(id,fresh=false){
 if(id===state.conversation&&!fresh){if(id)await chatUpdate();return;}
 saveChatDraft();state.chatVersion++;state.chatRequest++;state.conversation=id;state.chatDraftKey=id||'new:'+(++state.newDraftCounter);state.lastMessages='';
 const draft=state.chatDrafts.get(state.chatDraftKey);$('chat-input').value=draft?.text||'';setContext(draft?.context||null);selectConversation();
 state.chatWorking=!!id;show('agent-state',!!id);$('agent-state').classList.remove('error');$('agent-state').textContent=id?'正在读取对话记录…':'';
 if(id)$('messages').replaceChildren(el('p','正在载入这段对话…','conversation-empty'));else welcome();
 $('messages').scrollTop=0;updateChatSend();$('chat-input').focus();
 if(id)await chatUpdate();
}
async function conversations(refreshActive=true){
 const request=++state.conversationsRequest,list=await api('/conversations');if(request!==state.conversationsRequest)return;
 $('conversations').replaceChildren(...list.map(c=>{const b=button('',()=>changeConversation(c.id),'conversation'+(state.conversation===c.id?' active':''));b.dataset.conversation=c.id;b.title=c.title;b.append(el('span',c.title),el('time',shortDate(c.created)));return b;}));
 if(!list.length)$('conversations').append(el('p','你的对话会保存在这里。','conversation-empty'));if(refreshActive&&state.conversation)await chatUpdate();
}
function selectConversation(){document.querySelectorAll('[data-conversation]').forEach(b=>b.classList.toggle('active',b.dataset.conversation===state.conversation));}
function messageNode(m){
 const article=el('article','','message '+m.role),heading=el('div','','message-heading');heading.append(el('span',m.role==='user'?'你':'文档秘书'),el('time',date(m.created)));article.append(heading);const body=el('div','','message-body');markdown(body,m.text);article.append(body);
 if(m.role==='assistant')article.append(button('复制回答',async()=>{try{await navigator.clipboard.writeText(m.text);toast('回答已复制。');}catch{const range=document.createRange();range.selectNodeContents(body);const selection=window.getSelection();selection.removeAllRanges();selection.addRange(range);toast('已选中回答，可按 Ctrl + C 复制。');}},'copy-message link'));return article;
}
async function chatUpdate(){
 const id=state.conversation;if(!id)return;const version=state.chatVersion,request=++state.chatRequest,data=await api('/conversations/'+id);if(id!==state.conversation||version!==state.chatVersion||request!==state.chatRequest)return;
 const json=JSON.stringify(data.messages),container=$('messages');if(state.lastMessages!==json){const selection=window.getSelection(),readingSelection=selection&&!selection.isCollapsed&&container.contains(selection.anchorNode),nearBottom=!state.lastMessages||(!readingSelection&&container.scrollHeight-container.scrollTop-container.clientHeight<100),oldTop=container.scrollTop;container.replaceChildren(...data.messages.map(messageNode));state.lastMessages=json;if(nearBottom)container.scrollTop=container.scrollHeight;else container.scrollTop=oldTop;}
 const job=data.jobs[0],working=job&&['pending','running'].includes(job.status);show('agent-state',!!working||job?.status==='failed');$('agent-state').classList.toggle('error',job?.status==='failed');
 $('agent-state').textContent=working?(job.status==='pending'?'消息已送达，正在等待秘书处理。':'秘书正在查阅原文并整理答案…'):job?.status==='failed'?'本次回复未完成：'+(job.error||'请在处理记录中重试任务。'):'';state.chatWorking=!!working;updateChatSend();
}
on('chat-form','submit',async e=>{
 e.preventDefault();const text=$('chat-input').value.trim();if(!text||state.sending||state.chatWorking)return;
 const conversation=state.conversation,draftKey=state.chatDraftKey,c=state.context?{...state.context}:null;saveChatDraft();state.sending=true;state.chatVersion++;state.chatRequest++;updateChatSend();let accepted=false;
 try{
  const message=c?text+'\n\n附带文档：'+c.title+'，文档编号 '+c.id+'，当前第 '+c.page+' 页。请基于这份文档核对，引用格式 [['+c.id+':'+c.page+']]。':text;
  const data=await api('/chat','POST',{conversation,message});accepted=true;
  // A follow-up typed while this request was in flight belongs to this draft, even after switching away.
  if(state.chatDraftKey===draftKey)saveChatDraft();const saved=state.chatDrafts.get(draftKey);
  const remaining=saved?{...saved,text:saved.text.trim()===text?'':saved.text}:{text:'',context:c};
  if(data.conversation!==draftKey)state.chatDrafts.delete(draftKey);state.chatDrafts.set(data.conversation,remaining);
  if(state.chatDraftKey===draftKey){
   state.conversation=data.conversation;state.chatDraftKey=data.conversation;state.chatVersion++;state.chatRequest++;state.lastMessages='';state.chatWorking=true;
   $('chat-input').value=remaining.text;setContext(remaining.context);selectConversation();show('agent-state',true);$('agent-state').textContent='消息已送达，正在准备回复。';updateChatSend();
  }
  await conversations(false);if(state.conversation===data.conversation)await chatUpdate();
 }catch(error){
  if(accepted){if(state.chatDraftKey===draftKey||state.conversation===conversation){show('agent-state',true);$('agent-state').textContent='消息已送达，正在重新连接以接收回复。';}return;}
  throw error;
 }finally{state.sending=false;updateChatSend();}
});
on('chat-input','input',saveChatDraft);
on('chat-input','keydown',e=>{if(!e.isComposing&&(e.ctrlKey||e.metaKey)&&e.key==='Enter'){e.preventDefault();$('chat-form').requestSubmit();}});
on('new-chat','click',()=>changeConversation('',true));
function activityTab(name){for(const b of document.querySelectorAll('[data-activity]')){const active=b.dataset.activity===name;b.classList.toggle('active',active);b.setAttribute('aria-selected',active);}for(const id of ['jobs','operations','trash'])show(id,id===name);}
document.querySelectorAll('[data-activity]').forEach(b=>b.addEventListener('click',()=>activityTab(b.dataset.activity)));
async function activity(){
 const [trash,data]=await Promise.all([api('/trash'),api('/activity')]);
 $('trash').replaceChildren(...trash.map(d=>{const box=el('div','','operation');box.append(el('strong',d.title),el('p','移除于 '+date(d.deleted_at),'subtle'),button('恢复到文档库',async()=>{await api('/documents/'+d.id+'/restore','POST',{});toast('文件已恢复。');await activity();await status();}));return box;}));if(!trash.length)$('trash').append(emptyState('回收站是空的','从文档库移除的文件，可以在这里恢复。'));
 const jobs=[...data.jobs].sort((a,b)=>({running:0,pending:1,failed:2,done:3}[a.status]??4)-({running:0,pending:1,failed:2,done:3}[b.status]??4));
 $('jobs').replaceChildren(...jobs.slice(0,40).map(j=>{const box=el('div','','job'),top=el('div','','record-heading');top.append(el('strong',labels[j.kind]||j.kind),badge(j.status));box.append(top);if(j.document_title)box.append(el('p',j.document_title,'job-document'));box.append(el('p','更新于 '+date(j.updated),'subtle'));if(j.error){const details=el('details','','record-details');details.append(el('summary',j.status==='failed'?'查看问题':'查看处理说明'),el('p',j.error,'error'));box.append(details);}if(j.status==='failed')box.append(button('重试任务',async()=>{await api('/jobs/'+j.id+'/retry','POST',{});await activity();toast('任务已重新排队。');}));return box;}));if(!jobs.length)$('jobs').append(emptyState('所有任务都已就绪','开始扫描或导入文件后，可以在这里查看处理进度。'));
 $('operations').replaceChildren(...data.operations.map(o=>{
  const merged=o.kind==='merge',box=el('div','','operation');box.append(el('strong',(merged?'跨扫描合并 · ':'')+(o.reason||'文档位置调整')),el('p',o.state==='undone'?'已撤销 · '+date(o.created):date(o.created),'subtle'));
  const details=el('details','','record-details');details.append(el('summary',merged?'查看合并结果与撤销说明':'查看调整前后的位置'));
  if(merged)details.append(el('p','生成文件：'+o.new_path),el('p','撤销后合并结果移入回收站，原始来源重新显示。扫描原件始终保留。'));else details.append(el('p','调整前：'+o.old_path),el('p','调整后：'+o.new_path));box.append(details);
  if(o.state==='applied')box.append(button(merged?'撤销这次合并':'撤销这次调整',async()=>{if(!await confirmAction(merged?'撤销跨扫描合并？':'撤销文件调整？',merged?'合并结果将移入应用回收站，原始来源重新显示。所有扫描原件保留。':'文件将恢复到本次调整前的位置。',merged?'撤销合并':'撤销调整'))return;await api('/operations/'+o.id+'/undo','POST',{});await activity();toast(merged?'合并已撤销，原始来源已恢复。':'已撤销文件调整。');}));return box;
 }));if(!data.operations.length)$('operations').append(emptyState('还没有文件调整','秘书的合并、整理与手动归档会记录在这里。'));
 $('activity').replaceChildren(...data.activity.slice(0,60).map(a=>{const box=el('div','','activity-item');box.append(el('time',date(a.time)),el('p',a.message));return box;}));if(!data.activity.length)$('activity').append(el('p','文档库的最新动态会显示在这里。','subtle'));
}
on('wake-now','click',()=>busy($('wake-now'),async()=>{await api('/maintenance','POST',{});toast('秘书已加入整理队列。');await activity();await status();},'正在提交…'));
async function settings(){
 if(state.settingsLoaded&&state.settingsDirty)return;const [data,memories]=await Promise.all([api('/settings'),api('/memories')]);state.options=data.options;const o=data.options;
 $('key-state').textContent=data.hasApiKey?'已加密保存密钥；输入新密钥可替换。':'尚未配置，分析与秘书任务将等待密钥。';
 for(const [id,key] of [['model','model'],['embedding-model','embeddingModel'],['library-root','libraryRoot'],['wake-time','dailyWakeTime'],['daily-limit','dailyRequestLimit'],['max-steps','agentMaxSteps'],['instructions','instructions'],['document-concurrency','documentConcurrency'],['page-concurrency','pageConcurrency']])$(id).value=o[key]??'';
 $('auto-organize').checked=o.autoOrganize;$('schedule-enabled').checked=o.scheduleEnabled;$('wake-time').disabled=!o.scheduleEnabled;
 $('memories').replaceChildren(...memories.map(m=>{const box=el('div','','memory');box.append(el('p',m.text),button('移除',async()=>{if(!await confirmAction('移除这条记忆？',m.text,'移除记忆'))return;await api('/memories/'+m.id,'DELETE');box.remove();toast('记忆已移除。');},'link danger'));return box;}));if(!memories.length)$('memories').append(el('p','还没有保存的长期记忆。你可以在对话中告诉秘书自己的整理偏好。','subtle'));
 state.settingsLoaded=true;state.settingsDirty=false;$('settings-status').textContent='设置在保存后生效';
}
on('settings-form','input',()=>{state.settingsDirty=true;$('settings-status').textContent='有未保存的修改';});on('settings-form','change',()=>{state.settingsDirty=true;$('settings-status').textContent='有未保存的修改';});on('schedule-enabled','change',()=>{$('wake-time').disabled=!$('schedule-enabled').checked;});
on('settings-form','submit',async e=>{
 e.preventDefault();await busy($('save-settings'),async()=>{const options={...state.options,model:$('model').value.trim(),embeddingModel:$('embedding-model').value.trim(),libraryRoot:$('library-root').value.trim(),autoOrganize:$('auto-organize').checked,scheduleEnabled:$('schedule-enabled').checked,dailyWakeTime:$('wake-time').value,dailyRequestLimit:Number($('daily-limit').value),agentMaxSteps:Number($('max-steps').value),documentConcurrency:Number($('document-concurrency').value),pageConcurrency:Number($('page-concurrency').value),instructions:$('instructions').value};await api('/settings','POST',{options,apiKey:$('api-key').value});$('api-key').value='';state.settingsDirty=false;await status();await settings();$('settings-status').textContent='所有设置已保存';toast('设置已保存，等待中的任务将自动继续。');},'保存中…');
});
on('test-api','click',()=>busy($('test-api'),async()=>{await api('/test-api','POST',{});toast('OpenAI 连接成功。');},'正在测试…'));
on('discover','click',()=>busy($('discover'),async()=>{const r=await api('/discover','POST',{});toast('检查完成，发现 '+r.found+' 份文件。');},'正在检查…'));
window.addEventListener('beforeunload',e=>{if(state.settingsDirty){e.preventDefault();e.returnValue='';}});
document.addEventListener('keydown',e=>{if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='k'&&state.authenticated){e.preventDefault();if($('document-dialog').open)$('document-dialog').close();view('library').then(()=>$('query').focus()).catch(failed);}if($('document-dialog').open&&!['INPUT','TEXTAREA','SELECT'].includes(document.activeElement?.tagName)){if(e.key==='ArrowLeft'&&!$('prev-page').disabled){e.preventDefault();state.page--;renderPage();}if(e.key==='ArrowRight'&&!$('next-page').disabled){e.preventDefault();state.page++;renderPage();}}});
window.addEventListener('hashchange',()=>{if(state.authenticated)view(location.hash.slice(1)||'library').catch(failed);});
async function start(){if(!await session())return;await status();await view(location.hash.slice(1)||'library');}start().catch(failed);
let polling=false;
setInterval(async()=>{if(!state.authenticated||document.hidden||polling)return;polling=true;try{if(state.view==='agent')await chatUpdate();if(state.view==='library'&&!state.searching&&state.offset===0&&!$('document-dialog').open)await library(false,true);}catch{}finally{polling=false;}},4000);
setInterval(async()=>{if(!state.authenticated||document.hidden)return;try{await status();if(state.view==='activity')await activity();}catch{}},15000);
