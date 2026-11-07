const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const source=fs.readFileSync(require('node:path').join(__dirname,'../extension/content.js'),'utf8');
const context=vm.createContext({window:{},document:{}});
vm.runInContext(source.slice(source.indexOf('  function inspectReactFiberMedia('),source.indexOf('  function findOuterInstagramPostContainer(')),context);
const element=props=>({parentElement:null,querySelectorAll:()=>[],__reactProps$fixture:props});
let passed=0;
function check(props,id,name){assert.equal(context.inspectReactFiberMedia(element(props),true).videoId,id,name);console.log('PASS: '+name);passed++;}
check({author:{id:'7999999999999999999',uniqueId:'alice'},itemStruct:{id:'7381234567890123456',video:{},desc:'video'}},'7381234567890123456','React author ID cannot be mistaken for video ID');
check({videoId:7381234567890123456},'','unsafe numeric video ID is not rounded into another identity');
check({videoId:'7381234567890123456'},'7381234567890123456','explicit video ID string remains supported');
console.log(`RESULT: ${passed} passed`);
