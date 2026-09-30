const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
// Isolated UI regression: static assets only, every API request is fulfilled with synthetic fixtures.
// Usage: node scripts/test-web-ui.cjs [wwwroot] [output-directory]
// Optional: PLAYWRIGHT_MODULE_PATH, SCANARCHIVE_BROWSER, SCANARCHIVE_UI_OUTPUT.
const os = require('node:os');
let playwright;
try { playwright = require('playwright'); }
catch (error) {
  const fallback = process.env.PLAYWRIGHT_MODULE_PATH || path.join(os.homedir(), '.cache', 'codex-runtimes', 'codex-primary-runtime', 'dependencies', 'node', 'node_modules', 'playwright');
  try { playwright = require(fallback); }
  catch { throw new Error('Playwright is required. Install it or set PLAYWRIGHT_MODULE_PATH.', {cause:error}); }
}
const {chromium} = playwright;
const root = path.resolve(process.argv[2] || path.join(__dirname, '..', 'ScanArchive.Server', 'wwwroot'));
const outputRoot = process.argv[3] || process.env.SCANARCHIVE_UI_OUTPUT || path.join(os.tmpdir(), 'scan-archive-web-ui-' + Date.now());
const output = path.join(outputRoot, 'screenshots'); fs.mkdirSync(output, {recursive:true});
const id = '0123456789abcdef0123456789abcdef', parentId = 'fedcba9876543210fedcba9876543210';
const fixtureText = `# 文件概览\n\n这是一份**合成保修资料**，仅用于界面验证。\n\n## 重要信息\n\n| 项目 | 内容 |\n| --- | --- |\n| 保修期限 | 24 个月 |\n| 日期 | 2026-09-30 |\n\n> 原件始终可以按页追溯。\n\n1. 准备购买凭证。\n2. 提供设备序列号。\n\n[安全链接](https://example.com/reference)\n[危险链接](javascript:alert(1))\n[本机文件链接](file:///C:/private)\n[数据链接](data:text/html,unsafe)\n![不应加载的远程图片](https://fixture-invalid.example/pixel.png)\n\n<img src=x onerror="window.__injected=true">\n<script>window.__injected=true</script>\n\n\`\`\`text\n${'LONG_UNBROKEN_TOKEN_'.repeat(70)}\n\`\`\`\n\n`;
const wideTable = '| '+Array.from({length:10},(_,i)=>'Column '+(i+1)).join(' | ')+' |\n| '+Array(10).fill('---').join(' | ')+' |\n| '+Array(10).fill('UNBROKEN_TABLE_CELL_'.repeat(15)).join(' | ')+' |\n\n';
const longMarkdown = fixtureText + wideTable + Array.from({length:100},(_,n)=>`### 第 ${n+1} 条线索\n\n这段合成说明用于验证长文档概括和对话的独立滚动。关键词包括**凭证**、售后与保修。` ).join('\n\n') + `\n\n[[${id}:0]]\n\n[[${id}:9999999999999999999999999999999999999999]]\n\n[[${id}:2]]`;
const now = '2026-09-30T10:24:00-04:00';
const options={libraryRoot:'C:\\Documents\\ScanArchive',model:'gpt-6-astra',embeddingModel:'text-embedding-3-large',autoOrganize:true,scheduleEnabled:true,dailyWakeTime:'05:00',dailyRequestLimit:1000,agentMaxSteps:20,documentConcurrency:3,pageConcurrency:2,instructions:'按内容整理，保留完整原件。'};
const documents = Array.from({length:14},(_,n)=>({id:n===0?id:n.toString(16).padStart(32,'0'),title:n===0?'设备保修凭证 · 合成演示资料':'合成文档 '+(n+1)+' · 学习笔记与家庭清单',category:n%2?'学习/物理':'生活/保修',summary:n===0?longMarkdown:'这份演示资料用于检查文件列表、筛选和状态呈现。',tags:'保修, 设备, 凭证',scanned:now,document_date:'2026-09-30',status:n===3?'analyzing':n===4?'error':'ready',error:n===4?'演示：网络暂时中断，请重试。':'',page_count:3,parent_id:n===0?parentId:'',source_pages:'1-3',locked:0}));
let chatDelayMs=0, postedChat=[], requestLog=[], unhandled=[], outside=[], errors=[], messages=[{role:'user',text:'这份设备凭证什么时候过保？',created:now},{role:'assistant',text:longMarkdown,created:now}], savedSettings=[];
const conversationMessages={'conversation-demo':messages,'conversation-other':[{role:'user',text:'另一段合成对话',created:now},{role:'assistant',text:'这是会话 B，用于验证发送期间切换不会被打断。',created:now}]};
const stats=()=>({configured:true,settings:options,overview:{counts:[{status:'ready',count:12},{status:'analyzing',count:1},{status:'error',count:1}],categories:[{category:'生活/保修',count:7},{category:'学习/物理',count:7}]},pending:[{kind:'index',status:'running',count:1}],usage:[],lastWake:now,addresses:['http://localhost:5278','http://192.168.1.20:5278']});
const record={document:documents[0],pages:[1,2,3].map(number=>({number,text:`合成资料，第 ${number} 页`,summary:JSON.stringify({summary:fixtureText,topics:['设备保修','售后服务'],entities:['合成机构'],dates_and_numbers:['24个月'],readability:'清晰'})})),children:[]};
const activity={counts:{running:1,pending:2,failed:1},jobs:[{id:'j1',kind:'index',status:'running',document_title:documents[3].title,updated:now},{id:'j2',kind:'organize',status:'pending',document_title:documents[1].title,updated:now},{id:'j3',kind:'index',status:'failed',document_title:documents[4].title,updated:now,error:'演示：网络连接暂时中断。已完成的页面保留，可继续分析。'}],operations:[{id:'o1',state:'applied',reason:'按文档内容归档',old_path:'C:\\Documents\\Inbox\\scan.pdf',new_path:'C:\\Documents\\生活\\保修\\设备凭证.pdf',created:now}],activity:[{kind:'analysis',message:'演示文档：已完成 2/3 页分析。',time:now}]};
const pageSvg=page=>`<svg xmlns="http://www.w3.org/2000/svg" width="700" height="960" viewBox="0 0 700 960"><rect width="700" height="960" fill="white"/><rect x="60" y="64" width="62" height="8" fill="#246b57"/><text x="60" y="140" font-size="36" fill="#193c36">SYNTHETIC WARRANTY</text><text x="60" y="205" font-size="22" fill="#70837d">Page ${page} · UI TEST FIXTURE</text>${Array.from({length:12},(_,i)=>`<rect x="60" y="${270+i*36}" width="${i%3?530:420}" height="7" fill="#dce6df"/>`).join('')}</svg>`;
const contentType={'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css; charset=utf-8','.svg':'image/svg+xml','.png':'image/png'};
const server=http.createServer((req,res)=>{
  const pathname=new URL(req.url,'http://localhost').pathname;
  if(pathname.startsWith('/api/')){res.writeHead(503);res.end('API must be mocked by Playwright');return;}
  const rel=pathname==='/'?'index.html':decodeURIComponent(pathname.slice(1));
  const file=path.resolve(root,rel);if(!file.startsWith(path.resolve(root)+path.sep)){res.writeHead(403);res.end();return;}
  try{res.setHeader('Content-Type',contentType[path.extname(file)]||'application/octet-stream');res.end(fs.readFileSync(file));}catch{res.writeHead(404);res.end();}
});
(async()=>{
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const port=server.address().port;assert.notEqual(port,5278);const base=`http://127.0.0.1:${port}`;
 const browser=await chromium.launch({headless:true,...(process.env.SCANARCHIVE_BROWSER?{executablePath:process.env.SCANARCHIVE_BROWSER}:{channel:'msedge'})});
 const context=await browser.newContext({viewport:{width:1440,height:1000},locale:'zh-CN'});
 await context.route('**/*',async route=>{
   const request=route.request(), url=new URL(request.url());
   if(url.origin!==base){outside.push(request.url());return route.abort();}
   if(!url.pathname.startsWith('/api/'))return route.continue();
   const endpoint=url.pathname.slice(4),method=request.method();requestLog.push({endpoint,method});
   const json=async data=>route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(data)});
   if(endpoint==='/session')return json({authenticated:true,local:true,setupRequired:false});
   if(endpoint==='/status')return json(stats());
   if(endpoint==='/categories')return json([{category:'生活/保修',count:7},{category:'学习/物理',count:7}]);
   if(endpoint==='/documents')return json(documents.filter(d=>(!url.searchParams.get('status')||d.status===url.searchParams.get('status'))&&(!url.searchParams.get('category')||d.category===url.searchParams.get('category'))).slice(Number(url.searchParams.get('offset'))||0));
   if(endpoint==='/search')return json({mode:'hybrid',results:[{...documents[0],doc_id:id,page:2,snippet:'设备保修期限为24个月，购买日期可在原文核对。'}]});
   if(/^\/documents\/[^/]+\/pages\/\d+$/.test(endpoint))return route.fulfill({status:200,contentType:'image/svg+xml',body:pageSvg(endpoint.split('/').at(-1))});
   if(/^\/documents\/[^/]+$/.test(endpoint)){const selected=documents.find(d=>d.id===endpoint.split('/').at(-1))||{...documents[0],id:parentId,title:'原始扫描合集',status:'superseded',parent_id:''};return json({...record,document:selected});}
   if(endpoint==='/conversations')return json([{id:'conversation-demo',title:'设备凭证什么时候过保？',created:now},{id:'conversation-other',title:'另一段合成对话',created:now}]);
   if(endpoint.startsWith('/conversations/'))return json({messages:conversationMessages[endpoint.split('/').at(-1)]||[],jobs:[{status:'done'}]});
   if(endpoint==='/chat'&&method==='POST'){const body=request.postDataJSON();postedChat.push(body);const conversation=body.conversation||'conversation-demo';if(chatDelayMs)await new Promise(resolve=>setTimeout(resolve,chatDelayMs));conversationMessages[conversation].push({role:'user',text:body.message,created:now},{role:'assistant',text:'已收到合成测试消息。**可以继续提问。**',created:now});return json({conversation,job:'mock-job'});}
   if(endpoint==='/activity')return json(activity);
   if(endpoint==='/trash')return json([{id:'trash-demo',title:'已删除的合成资料',deleted_at:now}]);
   if(endpoint==='/settings'&&method==='GET')return json({options,hasApiKey:true});
   if(endpoint==='/settings'&&method==='POST'){savedSettings.push(request.postDataJSON());return json({ok:true});}
   if(endpoint==='/memories')return json([{id:'memory-demo',text:'学习资料按学科整理。',created:now}]);
   if(method!=='GET')return json({ok:true,job:'mock-job',found:0});
   unhandled.push(endpoint);return route.fulfill({status:404,contentType:'application/json',body:JSON.stringify({error:'Unhandled mock endpoint: '+endpoint})});
 });
 const page=await context.newPage();page.setDefaultTimeout(8000);page.on('pageerror',e=>errors.push(e.message));
 const results=[];const check=async(name,fn)=>{try{await fn();results.push({name,pass:true});}catch(e){results.push({name,pass:false,error:e.message});}};
 const noOverflow=async()=>{const r=await page.evaluate(()=>({scroll:document.documentElement.scrollWidth,width:document.documentElement.clientWidth}));assert.ok(r.scroll<=r.width+1,JSON.stringify(r));};
 const screenshot=async name=>page.screenshot({path:path.join(output,name+'.png'),fullPage:false});
 const composerVisible=async()=>{const viewport=page.viewportSize();for(const selector of ['#chat-form','#chat-input','#send']){const box=await page.locator(selector).boundingBox();assert.ok(box&&box.height>0&&box.width>0&&box.y>=0&&box.x>=0&&box.y+box.height<=viewport.height+1&&box.x+box.width<=viewport.width+1,JSON.stringify({selector,box,viewport}));}};

 try {
   await check('standalone Markdown rendering and safe citation callback',async()=>{
     const probe=await context.newPage();try{
       await probe.setContent('<div id="markdown-probe"></div>');await probe.addScriptTag({path:path.join(root,'markdown.js')});
       await probe.evaluate(({source,id})=>window.renderMarkdown(document.querySelector('#markdown-probe'),source+'\n\n[['+id+':2]]',(documentId,page)=>window.fixtureCitation={documentId,page}),{source:fixtureText,id});
       assert.ok(await probe.locator('#markdown-probe h2,#markdown-probe h3').count());assert.ok(await probe.locator('#markdown-probe strong').count());assert.ok(await probe.locator('#markdown-probe table').count());assert.ok(await probe.locator('#markdown-probe ol li').count());assert.ok(await probe.locator('#markdown-probe blockquote').count());assert.ok(await probe.locator('#markdown-probe pre code').count());
       assert.equal(await probe.locator('#markdown-probe script,#markdown-probe img,#markdown-probe [onerror],#markdown-probe a[href^="javascript:"],#markdown-probe a[href^="file:"],#markdown-probe a[href^="data:"]').count(),0);assert.equal(await probe.evaluate(()=>!!window.__injected),false);
       await probe.locator('#markdown-probe .citation').click();assert.deepEqual(await probe.evaluate(()=>window.fixtureCitation),{documentId:id,page:2});
     }finally{await probe.close();}
   });
   await page.goto(base,{waitUntil:'networkidle'});await page.locator('#shell').waitFor({state:'visible'});await page.locator('.document-card').first().waitFor();
   await check('desktop library does not overflow horizontally',noOverflow);await screenshot('library-desktop');
   await page.locator('.document-card').first().click();await page.locator('#document-dialog').waitFor({state:'visible'});
   await check('summary renders heading, strong, table',async()=>{assert.ok(await page.locator('#doc-summary h2,#doc-summary h3,#doc-summary h4').count());assert.ok(await page.locator('#doc-summary strong').count());assert.ok(await page.locator('#doc-summary table').count());});
   await check('summary does not execute raw HTML or unsafe links/images',async()=>{assert.equal(await page.locator('#doc-summary script,#doc-summary img,#doc-summary [onerror],#doc-summary a[href^="javascript:"],#doc-summary a[href^="file:"],#doc-summary a[href^="data:"]').count(),0);assert.equal(await page.evaluate(()=>!!window.__injected),false);assert.equal(outside.length,0);});
   await check('wide tables and code remain accessible in their own scroll region',async()=>{
     const overflowing=await page.locator('#doc-summary .table-scroll,#doc-summary pre').evaluateAll(nodes=>nodes.filter(e=>e.scrollWidth>e.clientWidth+1).map(e=>({tag:e.tagName,overflow:getComputedStyle(e).overflowX,width:e.clientWidth,scroll:e.scrollWidth})));
     for(const item of overflowing)assert.ok(['auto','scroll'].includes(item.overflow),JSON.stringify(item));
   });
   await check('document reader does not overflow horizontally',noOverflow);await screenshot('reader-desktop');
   await check('invalid citation pages are inert and manual page jumps stay in bounds',async()=>{
     assert.equal(await page.locator('#doc-summary .citation').count(),1);assert.equal(await page.locator('#doc-summary .citation').innerText(),'原文 · 第 2 页');
     await page.locator('#page-number').fill('0');await page.locator('#page-number').press('Enter');assert.equal(await page.locator('#page-number').inputValue(),'1');
     await page.locator('#page-number').fill('99999999');await page.locator('#page-number').press('Enter');assert.equal(await page.locator('#page-number').inputValue(),'3');
     await page.locator('#page-number').fill('1');await page.locator('#page-number').press('Enter');
   });
   await page.locator('#next-page').click();await check('page navigation updates current page',async()=>{assert.equal(await page.locator('#page-number').inputValue(),'2');assert.ok((await page.locator('#page-preview').getAttribute('src')).includes('/pages/2'));});
   await page.locator('#close-document').click();await page.locator('[data-view="agent"]').click();await page.locator('#conversations button').first().click();
   await page.waitForFunction(()=>document.querySelector('#messages')?.textContent.includes('第 100 条线索'));
   await check('long reply preserves Markdown',async()=>{assert.ok(await page.locator('#messages strong').count());assert.ok(await page.locator('#messages table').count());});
   await check('desktop long reply keeps composer within viewport',async()=>{await composerVisible();const m=await page.locator('#messages').evaluate(e=>({client:e.clientHeight,scroll:e.scrollHeight}));assert.ok(m.scroll>m.client);});
   await check('desktop chat no horizontal overflow',noOverflow);await screenshot('chat-long-desktop');
   await check('reading position survives a background reply refresh',async()=>{
     await page.locator('#messages').evaluate(e=>{e.scrollTop=0;});messages.push({role:'assistant',text:'后台新增的合成回复，不应打断正在阅读的位置。',created:now});
     await page.waitForFunction(()=>document.querySelector('#messages')?.textContent.includes('后台新增的合成回复'),null,{timeout:6500});
     assert.ok((await page.locator('#messages').evaluate(e=>e.scrollTop))<3);
   });
   const citations=page.locator('#messages .citation');await check('citation opens correct original page',async()=>{assert.ok(await citations.count());await citations.last().click();await page.locator('#document-dialog').waitFor({state:'visible'});assert.equal(await page.locator('#page-number').inputValue(),'2');await page.locator('#close-document').click();});
   for(const size of [{width:1100,height:700},{width:390,height:844},{width:390,height:500}]){
     await page.setViewportSize(size);await page.waitForTimeout(100);
     await check(`${size.width}x${size.height} long chat composer remains visible`,async()=>{await composerVisible();});
     await check(`${size.width}x${size.height} chat no page overflow`,noOverflow);await screenshot('chat-'+size.width+'x'+size.height);
   }
   await check('ask document carries exact page context and Ctrl+Enter sends once',async()=>{
     await page.locator('#messages .citation').last().click();await page.locator('#document-dialog').waitFor({state:'visible'});await page.locator('#ask-document').click();
     assert.ok(await page.locator('#chat-context').isVisible());assert.ok((await page.locator('#context-label').textContent()).includes('第 2 页'));
     const before=postedChat.length;await page.locator('#chat-input').fill('请核对保修到期时间。');await page.locator('#chat-input').press('Control+Enter');
     await page.waitForFunction(()=>document.querySelector('#messages')?.textContent.includes('已收到合成测试消息'));
     assert.equal(postedChat.length,before+1);assert.ok(postedChat.at(-1).message.includes(id+':2'));assert.equal(await page.locator('#chat-input').inputValue(),'');
   });
   await check('switching conversation during delayed send preserves active chat and draft',async()=>{
     await page.setViewportSize({width:1440,height:1000});await page.locator('[data-conversation="conversation-demo"]').click();await page.waitForFunction(()=>!document.querySelector('#send').disabled);const before=postedChat.length;chatDelayMs=250;
     await page.locator('#chat-input').fill('会话 A 中尚未完成的请求。');
     await page.evaluate(()=>{const input=document.querySelector('#chat-input');document.querySelector('#chat-form').requestSubmit();input.value='会话 A 的第二条草稿 Q2';input.dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('[data-conversation="conversation-other"]').click();});
     await page.waitForTimeout(650);assert.equal(postedChat.length,before+1);assert.equal(postedChat.at(-1).conversation,'conversation-demo');assert.ok(await page.locator('[data-conversation="conversation-other"]').evaluate(e=>e.classList.contains('active')));assert.ok((await page.locator('#messages').innerText()).includes('这是会话 B'));
     await page.locator('[data-conversation="conversation-demo"]').click();assert.equal(await page.locator('#chat-input').inputValue(),'会话 A 的第二条草稿 Q2');chatDelayMs=0;
   });
   await check('IME composition Ctrl+Enter does not submit an unfinished message',async()=>{
     const before=postedChat.length;await page.locator('#chat-input').fill('尚未提交的中文合成词');
     await page.locator('#chat-input').evaluate(input=>input.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,isComposing:true,bubbles:true,cancelable:true})));
     await page.waitForTimeout(80);assert.equal(postedChat.length,before);
   });
   await page.setViewportSize({width:390,height:844});await page.locator('[data-view="library"]').click();await check('mobile library no horizontal overflow',noOverflow);await screenshot('library-mobile');
   await page.locator('.document-card').first().click();await page.locator('#document-dialog').waitFor({state:'visible'});await check('mobile reader no horizontal overflow',noOverflow);await screenshot('reader-mobile');await page.locator('#close-document').click();
   await check('search results open the matching document page',async()=>{
     await page.locator('#query').fill('保修期限');await page.locator('#search-form').evaluate(form=>form.requestSubmit());await page.waitForFunction(()=>document.querySelector('#search-note')?.textContent.includes('相关'));
     await page.locator('.document-card').first().click();await page.locator('#document-dialog').waitFor({state:'visible'});assert.equal(await page.locator('#page-number').inputValue(),'2');await page.locator('#close-document').click();
   });
   await page.locator('[data-view="activity"]').click();await check('mobile activity no horizontal overflow',noOverflow);await screenshot('activity-mobile');
   await page.locator('[data-view="settings"]').click();await check('mobile settings no horizontal overflow',noOverflow);await screenshot('settings-mobile');
   await check('settings field values preserved from fixture',async()=>{assert.equal(await page.locator('#document-concurrency').inputValue(),'3');assert.equal(await page.locator('#page-concurrency').inputValue(),'2');assert.equal(await page.locator('#wake-time').inputValue(),'05:00');});
   await check('no JavaScript page errors',async()=>assert.deepEqual(errors,[]));await check('all API and external requests isolated',async()=>{assert.deepEqual(unhandled,[]);assert.deepEqual(outside,[]);});
 } catch(e){results.push({name:'test flow',pass:false,error:e.stack});}
 finally{const report={base,outputRoot,results,errors,unhandled,outside,requestCount:requestLog.length,savedSettingsCount:savedSettings.length};fs.writeFileSync(path.join(outputRoot,'report.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report,null,2));await browser.close();server.close();if(results.some(r=>!r.pass))process.exitCode=1;}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
