import {test} from 'node:test';
import assert from 'node:assert/strict';
import {filterMedia,mediaFilterOptions,paginateMedia,toggleCompare,validateSettings,DEFAULT_SETTINGS,progressOf} from '../dist/core.js';
import {demoMedia} from '../dist/catalog.js';
test('combined search, type, genre, year, rating and quality filters',()=>{
 const results=filterMedia(demoMedia,{search:'Interstellar',type:'movie',genre:'科幻',year:'2014',minRating:9,quality:'4K'});
 assert.deepEqual(results.map(x=>x.id),['interstellar']);
 assert.equal(filterMedia(demoMedia,{search:'does not exist'}).length,0);
 assert.equal(filterMedia(demoMedia,{type:'tv',quality:'720P'}).length,0);
});
test('score floor and alternate ranking remain independent',()=>{
 const tv=filterMedia(demoMedia,{type:'tv',minRating:9,sort:'imdb'});assert.equal(tv[0].id,'breakingbad');
 assert.ok(tv.every(x=>x.rating>=9&&x.type==='tv'));
 const years=filterMedia(demoMedia,{sort:'year'}).map(x=>x.year);assert.deepEqual(years,[...years].sort((a,b)=>b-a));
});
test('comparison permits four unique items and permits removal at capacity',()=>{
 let ids=[];for(const id of ['a','b','c','d'])ids=toggleCompare(ids,id);
 assert.throws(()=>toggleCompare(ids,'e'),/4/);assert.deepEqual(toggleCompare(ids,'b'),['a','c','d']);
});
test('settings reject blank paths, invalid retries and out of range concurrency',()=>{
 assert.throws(()=>validateSettings({...DEFAULT_SETTINGS,directory:' '}));
 assert.throws(()=>validateSettings({...DEFAULT_SETTINGS,concurrency:5}));
 assert.throws(()=>validateSettings({...DEFAULT_SETTINGS,maxRetries:1.5}));
 assert.equal(validateSettings({...DEFAULT_SETTINGS,directory:' D:\\Movies '}).directory,'D:\\Movies');
});
test('progress handles unknown size and caps overrun',()=>{
 assert.equal(progressOf({bytesReceived:20,totalBytes:10}),100);assert.equal(progressOf({bytesReceived:10}),0);
});

test('latest ordering uses full release dates independently of ratings',()=>{
 const items=[{...demoMedia[0],id:'old',releaseDate:'2026-01-02',rating:9.9},{...demoMedia[0],id:'new',releaseDate:'2026-11-03',rating:5}];
 assert.deepEqual(filterMedia(items,{sort:'latest',minRating:0}).map(m=>m.id),['new','old']);
});

test('works filters include standard and source genres and only valid years',()=>{
 const opts=mediaFilterOptions([{genres:['动作','舞台剧','动作'],year:2020},{genres:['爱情'],year:'2023'},{genres:null,year:0},{genres:[],year:2101}]);
 assert.ok(opts.genres.includes('动作')&&opts.genres.includes('爱情')&&opts.genres.includes('恐怖')&&opts.genres.includes('舞台剧'));
 assert.equal(opts.genres.filter(x=>x==='动作').length,1);
 assert.deepEqual(opts.years,[2023,2020]);
});

test('works pagination follows filtered results and clamps when the result set changes',()=>{
 const items=Array.from({length:29},(_,i)=>({...demoMedia[0],id:String(i),year:i<15?2023:2022,genres:i<15?['恐怖']:['动作']}));
 const filtered=filterMedia(items,{genre:'恐怖',year:2023});
 const first=paginateMedia(filtered,1,12),second=paginateMedia(filtered,2,12);
 assert.equal(first.total,15);assert.equal(first.totalPages,2);assert.equal(first.items.length,12);
 assert.deepEqual([second.start,second.end,second.items.length],[13,15,3]);
 assert.equal(new Set([...first.items,...second.items].map(m=>m.id)).size,15);
 const narrower=paginateMedia(filterMedia(items,{genre:'动作',year:2022}),99,12);
 assert.deepEqual([narrower.page,narrower.totalPages,narrower.total],[2,2,14]);
 assert.deepEqual([paginateMedia([],3).page,paginateMedia([],3).start,paginateMedia([],3).end],[1,0,0]);
});
