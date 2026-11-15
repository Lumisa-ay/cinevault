const esc=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
export const sourceSearchState={query:'',source:'all',type:'',minRating:0,includeUnrated:false,page:1,sources:[],items:[],busy:false,loading:'',searched:false,error:'',mode:'search',automaticStarted:false,outcomes:[],batchBusy:false};
const labels={ready:'可站内检索',ok:'查询完成',needs_key:'需要 API Key',needs_data:'需要数据集',needs_query:'需要片名',external:'网页检索',error:'检索未完成'};
let hooks;
export function configureSourceSearch(options){hooks=options;}
export async function loadSourceOptions(){const s=sourceSearchState;s.sources=await hooks.api('/sources');if(!s.searched&&!s.busy)s.source=s.sources.some(x=>x.id==='tmdb'&&x.status==='ready')?'tmdb':'archive';}
export async function loadLatestTmdb(){
 const s=sourceSearchState;if(s.busy||s.batchBusy)return;
 Object.assign(s,{query:'',source:'tmdb',type:'movie',page:1,mode:'latest',automaticStarted:true,outcomes:[]});await run();
}
export async function initializeSourceSearch({view,catalogueEmpty=false}){
 const s=sourceSearchState;if(s.automaticStarted||s.searched||s.busy||s.batchBusy||!(view==='sources'||view==='discover'&&catalogueEmpty)||!s.sources.some(x=>x.id==='tmdb'&&x.status==='ready'))return;
 await loadLatestTmdb();
}
export function sourceSearchView(heading,live){
 const s=sourceSearchState;
 return heading('从五个来源，找到好故事。','最新电影按 TMDB 首映日期同步；资源名称中的 4K 与中字标注供优先筛选。',live?`<button class="btn primary" data-sync-latest ${s.busy||s.batchBusy?'disabled':''}>${s.busy?'正在同步…':'同步最新电影'}</button><button class="btn" data-sync-download ${s.busy||s.batchBusy?'disabled':''}>${s.batchBusy?'正在匹配资源…':'同步并下载 4K 中字（最多 3 部）'}</button>`:'')+`
 <section class="source-search-panel">
 <form id="source-search-form" class="source-search-form">
 <label class="form-field"><span>片名 / 原名</span><input name="query" maxlength="120" value="${esc(s.query)}" placeholder="例如 Night of the Living Dead；留空浏览热门内容"></label>
 <label class="form-field"><span>查询来源</span><select name="source">${[['all','全部来源'],['archive','Internet Archive'],['tmdb','TMDB'],['douban','豆瓣电影'],['justwatch','JustWatch'],['imdb','IMDb']].map(([v,n])=>`<option value="${v}" ${v===s.source?'selected':''}>${n}</option>`).join('')}</select></label>
 <label class="form-field"><span>内容类别</span><select name="type"><option value="">全部</option><option value="movie" ${s.type==='movie'?'selected':''}>电影</option><option value="tv" ${s.type==='tv'?'selected':''}>电视剧</option></select></label>
 <label class="form-field"><span>最低评分 / 10</span><input name="minRating" type="number" min="0" max="10" step="0.1" value="${s.minRating}"></label>
 <label class="source-unrated"><input name="includeUnrated" type="checkbox" ${s.includeUnrated?'checked':''}>同时显示暂无评分的内容</label>
 <button class="btn primary" type="submit" ${s.busy||s.batchBusy||!live?'disabled':''}>${s.busy?'正在检索…':'检索来源'}</button>
 </form>
 <div class="notice"><span>磁力检索需管理员配置 Prowlarr 与 qBittorrent。评分按来源独立显示。TMDB / 豆瓣 / IMDb 提供影片资料，JustWatch 查询观看平台；这些资料页本身不提供影片文件。${!live?' 请运行本地 MVC 服务使用站内检索。':''}</span></div>
 ${s.error?`<div class="notice warning" role="alert">${esc(s.error)}</div>`:''}
 <div class="source-status-grid">${s.sources.map(source=>`<article class="source-status-card"><div><strong>${esc(source.name)}</strong><span class="source-badge ${source.status==='error'?'failed':''}">${esc(labels[source.status]||source.status)}</span></div><p>${esc(source.message)}</p>${source.status==='ok'?`<small>本页匹配 ${source.count} 项</small>`:''}<a class="resource-link" href="${esc(source.url)}" target="_blank" rel="noopener noreferrer">打开 ${esc(source.name)} 网页 ↗</a></article>`).join('')}</div>
 </section>
 ${s.outcomes.length?`<div class="source-status-grid">${s.outcomes.map(x=>`<article class="source-status-card"><strong>${esc(x.title)}</strong><p>${esc(x.message)}</p><small>${esc(x.status)}</small></article>`).join('')}</div>`:''}<section class="source-result-section" aria-live="polite"><div class="section-heading"><h2>检索结果 <small>${s.items.length} 项 · 第 ${s.page} 页</small></h2><a class="btn small" href="#compare">比较已选内容</a></div>
 ${s.items.length?`<div class="source-result-grid">${s.items.map(m=>`<article class="source-result-card"><div class="source-result-title"><div><span class="source-badge">${esc(m.source)}</span><h3>${esc(m.title)}</h3><small>${esc(m.originalTitle!==m.title?m.originalTitle:'')} · ${esc(m.releaseDate||m.year||'年份未知')} · ${m.type==='tv'?'电视剧':m.type==='movie'?'电影':'类别未知'}</small></div><button class="btn small" data-compare="${esc(m.id)}" aria-label="加入或移出比较：${esc(m.title)}">± 比较</button></div><p class="source-score"><strong>${m.rating?m.rating.toFixed(1):'—'}</strong><span>${esc(m.ratingSource||m.source)}${m.voteCount?` · ${m.voteCount.toLocaleString()} 人评分`:''}</span></p><p class="muted">${esc(m.genres.join(' / ')||'来源未提供类型')}</p><p class="source-description">${esc(m.description?.slice(0,200)||'来源未提供简介。')}</p><div class="source-result-actions"><a class="resource-link" href="${esc(m.sourceUrl)}" target="_blank" rel="noopener noreferrer">原始页面 ↗</a>${m.id.startsWith('archive:')?`<button class="btn primary small" data-load-archive="${esc(m.id.slice(8))}" ${s.loading?'disabled':''}>${s.loading===m.id?'正在读取…':'读取下载文件'}</button>`:`<button class="btn small" data-details="${esc(m.id)}">资料详情</button><button class="btn primary small" data-find-magnets="${esc(m.id)}" ${s.loading?'disabled':''}>${s.loading===m.id?'正在检索…':'搜索 4K 中字磁力'}</button>`}</div></article>`).join('')}</div>`:`<div class="empty"><h3>${s.busy?'正在读取来源…':s.searched?'本页暂无站内结果':'开始一次多来源检索'}</h3><p>${s.searched?'查看上方来源状态；网页入口可直接打开。也可尝试英文原名、下一页或调整评分。':'输入片名，或留空查看 Internet Archive 热门影片。IMDb 本地 / OMDb 需要片名，官方 API 请输入 tt 编号。'}</p></div>`}
 ${s.searched?`<div class="source-pagination"><button class="btn" data-source-page="${s.page-1}" ${s.busy||s.page<=1?'disabled':''}>上一页</button><span>第 ${s.page} 页 · 每来源独立分页；评分筛选针对当前页</span><button class="btn" data-source-page="${s.page+1}" ${s.busy||s.page>=20?'disabled':''}>下一页</button></div>`:''}</section>
 <div class="notice"><span>TMDB 接入使用官方 API。This product uses the TMDB API but is not endorsed or certified by TMDB. <a class="resource-link" href="https://www.themoviedb.org/" target="_blank" rel="noopener noreferrer">TMDB</a> · IMDb 本地数据限个人及非商业用途。</span></div>`;
}
async function run(){
 const s=sourceSearchState;if(s.busy||s.batchBusy)return;s.busy=true;s.error='';hooks.refresh();
 try{const result=await hooks.api('/search','POST',{query:s.query,source:s.source,type:s.type,minRating:s.minRating,includeUnrated:s.includeUnrated,page:s.page,mode:s.mode});s.items=result.items;s.sources=result.sources;s.searched=true;hooks.onResults(result.items,{mode:s.mode});}
 catch(error){s.error=error.message;hooks.toast(error.message,true);}
 finally{s.busy=false;hooks.refresh();}
}
document.addEventListener('submit',async event=>{
 if(event.target.id!=='source-search-form')return;event.preventDefault();if(sourceSearchState.busy||sourceSearchState.batchBusy)return;
 const data=new FormData(event.target);Object.assign(sourceSearchState,{query:String(data.get('query')||'').trim(),source:String(data.get('source')),type:String(data.get('type')||''),minRating:Number(data.get('minRating')||0),includeUnrated:data.has('includeUnrated'),page:1,mode:'search',automaticStarted:true,outcomes:[]});await run();
});
document.addEventListener('click',async event=>{
 if(event.target.closest('[data-sync-latest]')){await loadLatestTmdb();return;}
 if(event.target.closest('[data-sync-download]')){
  const s=sourceSearchState;if(s.busy||s.batchBusy)return;s.batchBusy=true;s.error='';s.outcomes=[];hooks.refresh();
  try{const result=await hooks.api('/sync/latest/download','POST',{page:1,minRating:s.minRating,limit:3});s.items=result.catalogue.items;s.sources=result.catalogue.sources;s.mode='latest';s.source='tmdb';s.type='movie';s.query='';s.page=1;s.searched=true;s.outcomes=result.outcomes;hooks.onResults(s.items,{mode:'latest'});hooks.toast(`已同步 ${s.items.length} 部，${s.outcomes.filter(x=>x.status==='Queued').length} 项已加入 BT 队列`);}
  catch(error){s.error=error.message;hooks.toast(error.message,true);}finally{s.batchBusy=false;hooks.refresh();}return;
 }
 const find=event.target.closest('[data-find-magnets]');if(find){const s=sourceSearchState;if(s.loading)return;s.loading=find.dataset.findMagnets;s.error='';hooks.refresh();
  try{const result=await hooks.api('/media/'+encodeURIComponent(find.dataset.findMagnets)+'/magnets','POST',{});s.items=s.items.map(m=>m.id===result.item.id?result.item:m);hooks.onResource(result.item);hooks.toast(`找到 ${result.candidates.length} 个磁力候选，已按匹配度与 4K 中字优先排序`);}
  catch(error){s.error=error.message;hooks.toast(error.message,true);}finally{s.loading='';hooks.refresh();}return;
 }
 const page=event.target.closest('[data-source-page]');if(page&&!page.disabled){sourceSearchState.page=Number(page.dataset.sourcePage);await run();return;}
 const archive=event.target.closest('[data-load-archive]');if(!archive||archive.disabled||sourceSearchState.loading)return;
 const s=sourceSearchState,id=archive.dataset.loadArchive;s.loading='archive:'+id;hooks.refresh();
 try{const item=await hooks.api('/resources/archive/'+encodeURIComponent(id),'POST',{});s.items=s.items.map(m=>m.id===item.id?item:m);hooks.onResource(item);}
 catch(error){s.error=error.message;hooks.toast('文件列表读取失败：'+error.message,true);}
 finally{s.loading='';hooks.refresh();}
});
