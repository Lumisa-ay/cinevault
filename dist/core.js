export const DEFAULT_SETTINGS={directory:'D:\\Movies\\CineVault',autoRetry:true,maxRetries:3,concurrency:2,minRating:8,organize:true};
export function filterMedia(items,f){
  const q=(f.search||'').trim().toLocaleLowerCase();
  return items.filter(m=>(!q||`${m.title} ${m.originalTitle} ${(m.genres||[]).join(' ')}`.toLocaleLowerCase().includes(q))&&(!f.type||m.type===f.type)&&(!f.genre||m.genres.includes(f.genre))&&(Number(m.rating)||0)>=Number(f.minRating||0)&&(!f.year||String(m.year)===String(f.year))&&(!f.quality||m.qualities.includes(f.quality)))
    .sort((a,b)=>f.sort==='latest'?String(b.releaseDate||b.year||'').localeCompare(String(a.releaseDate||a.year||'')):f.sort==='year'?b.year-a.year:f.sort==='imdb'?(b.imdb||0)-(a.imdb||0):f.sort==='title'?a.title.localeCompare(b.title,'zh'):b.rating-a.rating);
}
export function toggleCompare(ids,id){if(ids.includes(id))return ids.filter(x=>x!==id);if(ids.length>=4)throw new Error('最多可同时比较 4 部内容');return [...ids,id];}
export function validateSettings(s){if(!String(s.directory||'').trim())throw new Error('请输入下载目录');if(!Number.isInteger(Number(s.maxRetries))||s.maxRetries<0||s.maxRetries>5)throw new Error('重试次数应为 0–5');if(!Number.isInteger(Number(s.concurrency))||s.concurrency<1||s.concurrency>4)throw new Error('并发任务数应为 1–4');if(s.minRating<0||s.minRating>10)throw new Error('评分应在 0–10 之间');return {...s,directory:s.directory.trim(),maxRetries:Number(s.maxRetries),concurrency:Number(s.concurrency),minRating:Number(s.minRating)};}
export const statusNames={Queued:'等待中',Downloading:'下载中',Paused:'已暂停',Retrying:'等待重试',Failed:'下载失败',Completed:'已完成',Cancelled:'已取消'};
export function progressOf(t){return t.totalBytes?Math.min(100,Math.round(t.bytesReceived/t.totalBytes*100)):Number(t.progress||0);}
export function bytes(n){if(!n)return '—';const u=['B','KB','MB','GB','TB'];let i=0;while(n>=1024&&i<u.length-1){n/=1024;i++;}return `${n.toFixed(i>1?1:0)} ${u[i]}`;}
