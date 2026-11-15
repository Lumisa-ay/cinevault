import {test} from 'node:test';
import assert from 'node:assert/strict';
import {demoMedia} from '../dist/catalog.js';

test('works page lets users paginate and resets the page when filters change',async()=>{
 const originalLength=demoMedia.length,oldInterval=globalThis.setInterval;
 const handlers=new Map(),elements=new Map();
 for(let i=0;i<27;i++)demoMedia.push({...demoMedia[0],id:'works-fixture-'+i,title:'分页测试 '+i,year:2021,genres:['恐怖'],rating:8});
 function element(selector){
  if(!elements.has(selector))elements.set(selector,{innerHTML:'',textContent:'',className:'',classList:{remove(){},toggle(){}},scrollIntoView(){},addEventListener(){}});
  return elements.get(selector);
 }
 globalThis.document={
  querySelector(selector){return selector.startsWith('meta[')?null:element(selector);},
  querySelectorAll(){return [];},
  addEventListener(type,fn){handlers.set(type,[...(handlers.get(type)||[]),fn]);}
 };
 globalThis.window={addEventListener(){},scrollTo(){}};
 globalThis.location={hash:'',pathname:'/'};
 globalThis.localStorage={getItem(key){return key==='cinevault-settings'?JSON.stringify({minRating:0}):null;},setItem(){}};
 globalThis.setInterval=()=>0;
 try{
  await import('../dist/app.js');
  assert.match(element('#app').innerHTML,/影片题材/);
  assert.match(element('#app').innerHTML,/>恐怖<\/option>/);
  assert.match(element('#app').innerHTML,/>2021<\/option>/);
  assert.match(element('#works-pagination').innerHTML,/每页 12 部/);
  const click=handlers.get('click').at(-1);
  const page={disabled:false,dataset:{worksPage:'2'}};
  await click({target:{closest(selector){return selector==='[data-works-page]'?page:null;}}});
  assert.match(element('#works-pagination').innerHTML,/第 13–24 部/);
  const change=handlers.get('change').at(-1);
  change({target:{dataset:{filter:'genre'},value:'恐怖'}});
  assert.match(element('#works-pagination').innerHTML,/第 1–12 部/);
  change({target:{dataset:{filter:'year'},value:'2021'}});
  assert.match(element('#result-count').innerHTML,/<strong>27<\/strong>/);
  assert.match(element('#works-pagination').innerHTML,/第 1–12 部，共 27 部/);
 } finally {demoMedia.splice(originalLength);globalThis.setInterval=oldInterval;}
});
