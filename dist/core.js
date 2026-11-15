export const DEFAULT_SETTINGS={directory:'D:\\Movies\\CineVault',autoRetry:true,maxRetries:3,concurrency:2,minRating:8,organize:true};
const standardGenres=['动作','冒险','动画','喜剧','犯罪','纪录','剧情','家庭','奇幻','历史','恐怖','音乐','悬疑','爱情','科幻','电视电影','惊悚','战争','西部','动作冒险','儿童','新闻','真人秀','科幻奇幻','肥皂剧','脱口秀','战争政治'];
export function mediaFilterOptions(items){
  const genres=new Set(standardGenres),years=new Set();
  for(const item of items){
    for(const genre of Array.isArray(item.genres)?item.genres:[])if(typeof genre==='string'&&genre.trim())genres.add(genre.trim());
    const year=Number(item.year);if(Number.isInteger(year)&&year>=1880&&year<=2100)years.add(year);
  }
  return {genres:[...genres].sort((a,b)=>a.localeCompare(b,'zh-CN')),years:[...years].sort((a,b)=>b-a)};
}
export function paginateMedia(items,page=1,pageSize=12){
  const size=Number.isInteger(pageSize)&&pageSize>0?pageSize:12;
  const total=items.length,totalPages=Math.max(1,Math.ceil(total/size));
  const current=Math.min(totalPages,Math.max(1,Number.isFinite(Number(page))?Math.trunc(Number(page)):1));
  const offset=(current-1)*size;
  return {items:items.slice(offset,offset+size),page:current,total,totalPages,start:total?offset+1:0,end:Math.min(offset+size,total)};
}
export function filterMedia(items,f){
  const q=(f.search||'').trim().toLocaleLowerCase();
  return items.filter(m=>(!q||`${m.title} ${m.originalTitle} ${(m.genres||[]).join(' ')}`.toLocaleLowerCase().includes(q))&&(!f.type||m.type===f.type)&&(!f.genre||(m.genres||[]).includes(f.genre))&&(Number(m.rating)||0)>=Number(f.minRating||0)&&(!f.year||String(m.year)===String(f.year))&&(!f.quality||(m.qualities||[]).includes(f.quality)))
    .sort((a,b)=>f.sort==='latest'?String(b.releaseDate||b.year||'').localeCompare(String(a.releaseDate||a.year||'')):f.sort==='year'?b.year-a.year:f.sort==='imdb'?(b.imdb||0)-(a.imdb||0):f.sort==='title'?a.title.localeCompare(b.title,'zh'):b.rating-a.rating);
}
export function toggleCompare(ids,id){if(ids.includes(id))return ids.filter(x=>x!==id);if(ids.length>=4)throw new Error('最多可同时比较 4 部内容');return [...ids,id];}
export function validateSettings(s){if(!String(s.directory||'').trim())throw new Error('请输入下载目录');if(!Number.isInteger(Number(s.maxRetries))||s.maxRetries<0||s.maxRetries>5)throw new Error('重试次数应为 0–5');if(!Number.isInteger(Number(s.concurrency))||s.concurrency<1||s.concurrency>4)throw new Error('并发任务数应为 1–4');if(s.minRating<0||s.minRating>10)throw new Error('评分应在 0–10 之间');return {...s,directory:s.directory.trim(),maxRetries:Number(s.maxRetries),concurrency:Number(s.concurrency),minRating:Number(s.minRating)};}
export const statusNames={Queued:'等待中',Downloading:'下载中',Paused:'已暂停',Retrying:'等待重试',Failed:'下载失败',Completed:'已完成',Cancelled:'已取消'};
export function progressOf(t){return t.totalBytes?Math.min(100,Math.round(t.bytesReceived/t.totalBytes*100)):Number(t.progress||0);}
export function bytes(n){if(!n)return '—';const u=['B','KB','MB','GB','TB'];let i=0;while(n>=1024&&i<u.length-1){n/=1024;i++;}return `${n.toFixed(i>1?1:0)} ${u[i]}`;}
