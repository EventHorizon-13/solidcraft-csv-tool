const {chromium} = require('playwright');
const fs = require('fs');
(async()=>{
 const browser=await chromium.launch({headless:true,args:['--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader']});
 const page=await browser.newPage({acceptDownloads:true,viewport:{width:1440,height:1000}});
 page.on('pageerror',e=>console.log('PAGE ERROR',e.message));
 // Serve the candidate JS while testing against the deployed template and WASM.
 await page.route('**/roller-seal/client-app.js',r=>r.fulfill({path:'src/SolidCraftWeb/wwwroot/client-app.js',contentType:'text/javascript'}));
 await page.route('**/api/generation-records',r=>r.fulfill({status:201,contentType:'application/json',body:'{"ok":true}'}));
 // CI uses WebGL software rendering; the user-facing URL keeps automatic GPU selection.
 await page.route('**/roller-seal/solidcraft/?*',r=>{
  const url=new URL(r.request().url());url.searchParams.set('webgl','');
  return r.continue({url:url.href});
 });
 await page.goto('https://iobyte.cn/roller-seal/',{waitUntil:'domcontentloaded'});
 await page.waitForFunction(()=>document.querySelector('#connectionState').textContent.includes('已就绪'),{},{timeout:240000});
 console.log('Browser engine ready');
 for(let n=0;n<2;n++){
  const downloadPromise=page.waitForEvent('download',{timeout:240000});
  await page.click('#stepBtn');
  const download=await downloadPromise;
  await download.saveAs('browser-result-'+n+'.zip');
  await page.waitForFunction(()=>document.querySelector('#jobState').textContent.includes('完成'),{},{timeout:30000});
  const b=fs.readFileSync('browser-result-'+n+'.zip');
  if(b.readUInt32LE(0)!==0x04034b50)throw Error('Not a ZIP');
  console.log('Generation',n+1,'downloaded',b.length);
 }
 await page.screenshot({path:'browser-result.png',fullPage:true});
 await browser.close();
})().catch(e=>{console.error(e);process.exit(1)});
