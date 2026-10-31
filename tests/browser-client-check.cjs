const {chromium} = require('playwright');
const fs = require('fs');
const {spawn}=require('child_process');
(async()=>{
 const server=spawn('python3',['-m','http.server','8765','--bind','127.0.0.1','--directory','browser-site']);
 process.on('exit',()=>server.kill());
 await new Promise(r=>setTimeout(r,1000));
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
 await page.addInitScript(()=>localStorage.setItem('solidcraft.webgl','1'));
 await page.goto('http://127.0.0.1:8765/roller-seal/',{waitUntil:'domcontentloaded'});
 await page.waitForFunction(()=>document.querySelector('#connectionState').textContent.includes('已就绪'),{},{timeout:240000}).catch(async e=>{console.log(await page.locator('#connectionState').textContent());await page.screenshot({path:'browser-result-failed.png',fullPage:true});throw e;});
 console.log('Browser engine ready');
 for(let n=0;n<2;n++){
  if(n===1)await page.setViewportSize({width:390,height:844});
  const downloadPromise=page.waitForEvent('download',{timeout:240000});
  await page.click('#stepBtn');
  if(!await page.locator('#progressDialog').isVisible())throw Error('Progress dialog did not open');
  const download=await Promise.race([downloadPromise,page.waitForFunction(()=>document.querySelector('#jobState').textContent==='失败',{},{timeout:240000}).then(async()=>{throw Error(await page.locator('#log').textContent())})]);
  await download.saveAs('browser-result-'+n+'.zip');
  await page.waitForFunction(()=>document.querySelector('#jobState').textContent.includes('完成'),{},{timeout:30000});
  const b=fs.readFileSync('browser-result-'+n+'.zip');
  if(b.readUInt32LE(0)!==0x04034b50)throw Error('Not a ZIP');
  console.log('Generation',n+1,'downloaded',b.length);
  const box=await page.locator('#progressDialog').boundingBox(), viewport=page.viewportSize();
  if(box.x<0||box.y<0||box.x+box.width>viewport.width||box.y+box.height>viewport.height)throw Error('Dialog overflows viewport');
  await page.screenshot({path:'browser-result-dialog-'+n+'.png',fullPage:true});
  await page.getByRole('button',{name:'关闭任务进度弹窗'}).click();
  await page.click('#showProgressBtn');
  if(!await page.locator('#progressDialog').isVisible())throw Error('Cannot reopen progress');
  await page.keyboard.press('Escape');
 }
 await page.screenshot({path:'browser-result.png',fullPage:true});
 await browser.close();server.kill();
})().catch(e=>{console.error(e);process.exit(1)});
