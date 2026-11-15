import {test} from 'node:test';
import assert from 'node:assert/strict';
globalThis.document={addEventListener(){}};
const {sourceSearchState,sourceSearchView,configureSourceSearch,initializeSourceSearch}=await import('../dist/source-search.js');

test('multi-source results escape remote text and keep score provenance visible',()=>{
 Object.assign(sourceSearchState,{query:'" autofocus onfocus="bad',sources:[{id:'tmdb',name:'TMDB',status:'needs_key',message:'需要 API Key',url:'https://www.themoviedb.org/search?query=%22'}],items:[{id:'archive:fixture',title:'<img src=x onerror=bad>',originalTitle:'Fixture',source:'Internet Archive',ratingSource:'Internet Archive（5 分制折算至 10 分）',rating:8,voteCount:12,year:2020,type:'movie',genres:['Drama'],description:'<script>bad()</script>',sourceUrl:'https://archive.org/details/fixture'}]});
 const html=sourceSearchView((title,sub)=>`<h1>${title}</h1><p>${sub}</p>`,true);
 assert.ok(html.includes('&lt;img src=x onerror=bad&gt;'));
 assert.ok(html.includes('&lt;script&gt;bad()&lt;/script&gt;'));
 assert.ok(!html.includes('<img src=x'));
 assert.ok(!html.includes('value="" autofocus'));
 assert.ok(html.includes('Internet Archive（5 分制折算至 10 分）'));
 assert.ok(html.includes('需要 API Key'));
 assert.ok(html.includes('data-load-archive="fixture"'));
 assert.ok(html.includes('rel="noopener noreferrer"'));
});

test('empty library automatically synchronizes TMDB latest movies once',async()=>{
 Object.assign(sourceSearchState,{query:'',source:'all',type:'',mode:'search',minRating:0,includeUnrated:false,page:1,sources:[{id:'tmdb',status:'ready'}],items:[],searched:false,busy:false,batchBusy:false,automaticStarted:false,error:''});
 const requests=[],received=[];
 configureSourceSearch({api:async(path,method,body)=>{requests.push({path,method,body});return {items:[{id:'tmdb:movie:42'}],sources:[{id:'tmdb',status:'ok'}]};},refresh(){},toast(){},onResults(items,context){received.push({items,context});}});
 await initializeSourceSearch({view:'discover',catalogueEmpty:true});
 await initializeSourceSearch({view:'sources',catalogueEmpty:true});
 assert.equal(requests.length,1);
 assert.deepEqual(requests[0].body,{query:'',source:'tmdb',type:'movie',minRating:0,includeUnrated:false,page:1,mode:'latest'});
 assert.equal(received[0].context.mode,'latest');
 assert.equal(sourceSearchState.items[0].id,'tmdb:movie:42');
});

test('unconfigured or unrelated views do not trigger sync and failed loads do not loop',async()=>{
 Object.assign(sourceSearchState,{sources:[{id:'tmdb',status:'needs_key'}],items:[],searched:false,busy:false,batchBusy:false,automaticStarted:false,error:''});
 let calls=0;
 configureSourceSearch({api:async()=>{calls++;throw new Error('Fixture network failure');},refresh(){},toast(){},onResults(){throw new Error('Unexpected result');}});
 await initializeSourceSearch({view:'sources'});
 sourceSearchState.sources=[{id:'tmdb',status:'ready'}];
 await initializeSourceSearch({view:'settings',catalogueEmpty:true});
 assert.equal(calls,0);
 await initializeSourceSearch({view:'sources'});
 await initializeSourceSearch({view:'sources'});
 assert.equal(calls,1);
 assert.equal(sourceSearchState.error,'Fixture network failure');
 assert.deepEqual(sourceSearchState.items,[]);
});
