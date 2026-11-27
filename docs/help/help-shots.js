// Annotated screenshots for the help guide. Run the app in Development, run tests/e2e/smoke.js once
// to seed it, then: node docs/help/help-shots.js <out-dir>. Request #16 = a teacher field trip
// waiting on admin@example.org (true for a fresh smoke-test database).
const { chromium } = require('playwright');
const base='http://127.0.0.1:5080', OUT=process.argv[2];
const CSS = `.sim-banner{display:none!important}
.ann-box{position:absolute;border:3px solid #ea580c;border-radius:10px;box-shadow:0 0 0 3px rgba(255,255,255,.85);pointer-events:none;z-index:9998}
.ann-n{position:absolute;width:30px;height:30px;border-radius:50%;background:#ea580c;color:#fff;font:800 16px/30px system-ui,sans-serif;text-align:center;box-shadow:0 0 0 3px #fff,0 2px 6px rgba(0,0,0,.3);z-index:9999}`;
async function login(p, who){ await p.goto(base+'/Account/DevLogin'); await p.click(`button[value="${who}"]`); await p.waitForURL(base+'/'); }
async function annotate(p, items){
  await p.addStyleTag({content:CSS});
  const boxes=[];
  for (const it of items){ const bs=[]; for (const sel of [].concat(it.sel)){ const l=p.locator(sel).first(); await l.waitFor({timeout:5000}); bs.push(await l.boundingBox()); }
    const x=Math.min(...bs.map(b=>b.x)), y=Math.min(...bs.map(b=>b.y)), r=Math.max(...bs.map(b=>b.x+b.width)), btm=Math.max(...bs.map(b=>b.y+b.height));
    boxes.push({x,y,width:r-x,height:btm-y, n:it.n, pad:it.pad??6, at:it.at||'tl'}); }
  await p.evaluate((boxes)=>{ boxes.forEach(b=>{
    const x=b.x+scrollX-b.pad, y=b.y+scrollY-b.pad, w=b.width+2*b.pad, h=b.height+2*b.pad;
    const d=document.createElement('div'); d.className='ann-box'; Object.assign(d.style,{left:x+'px',top:y+'px',width:w+'px',height:h+'px'}); document.body.append(d);
    const n=document.createElement('div'); n.className='ann-n'; n.textContent=b.n;
    const nx = b.at==='tr' ? x+w-15 : b.at==='l' ? x-38 : x-15; const ny = b.at==='l' ? y+h/2-15 : b.at==='bl' ? y+h-15 : y-15;
    Object.assign(n.style,{left:nx+'px',top:ny+'px'}); document.body.append(n); }); }, boxes);
}
async function shot(p, name, clip){ await p.waitForTimeout(300); await p.screenshot({path:`${OUT}/${name}.png`, clip, fullPage: !clip}); }
(async()=>{
 const b=await chromium.launch();
 const ctx=await b.newContext({viewport:{width:1280,height:860},deviceScaleFactor:2}); const p=await ctx.newPage();
 await login(p,'teacher@example.org');
 // 1 Home
 await p.goto(base+'/'); await p.waitForTimeout(500);
 await annotate(p,[{sel:'.sidebar nav',n:1,pad:4},{sel:'.stat-row',n:2},{sel:'.home-main details.form-group',n:3},{sel:'.home-side',n:4}]);
 await shot(p,'1-home',{x:0,y:0,width:1280,height:760});
 // 2 Form
 await p.setViewportSize({width:1280,height:1400});
 await p.goto(base+'/Start/4'); await p.waitForTimeout(800);
 await annotate(p,[{sel:'button:has-text("Start from a previous request")',n:1},{sel:'.tip-btn >> nth=1',n:2,pad:5,at:'tr'},{sel:'button:has-text("Save as my chain")',n:3},{sel:'button:has-text("Save for later")',n:4},{sel:'button:has-text("Send for approval")',n:5}]);
 const fh=await p.evaluate(()=>document.body.scrollHeight);
 await shot(p,'2-form',{x:236,y:0,width:830,height:1300});
 // 3 Request page (teacher)
 await p.setViewportSize({width:1280,height:860});
 await p.goto(base+'/Requests/Detail/16'); await p.waitForTimeout(600);
 await annotate(p,[{sel:'.hero-ring',n:1},{sel:['.hero-now','.hero .sub'],n:2,pad:6,at:'bl'},{sel:'.hero .actions',n:3,at:'tr'},{sel:'.journey',n:4}]);
 await shot(p,'3-request',{x:236,y:0,width:1044,height:540});
 // 5a Notifications: email timing and alerts
 await p.setViewportSize({width:1280,height:2600});
 await p.goto(base+'/Notifications'); await p.waitForTimeout(600);
 await p.evaluate(()=>{ for (const el of document.querySelectorAll('main p')) if (/not switched on/.test(el.textContent)) el.remove(); });
 await annotate(p,[{sel:'main select >> nth=-1',n:1,pad:5},{sel:'main section:has-text("Alerts on this device"), main .panel:has-text("Alerts on this device")',n:2,pad:2}]);
 const e = await p.locator('main :is(section,.panel):has-text("Also send my notifications by email")').last().boundingBox();
 const a = await p.locator('main :is(section,.panel):has-text("Alerts on this device")').last().boundingBox();
 await shot(p,'5a-notifications',{x:e.x-24,y:e.y-24,width:e.width+48,height:(a.y+a.height)-e.y+48});
 // 5b Away
 await p.setViewportSize({width:1280,height:860});
 await p.goto(base+'/Away'); await p.waitForTimeout(600);
 await annotate(p,[{sel:'main input[type=email], main input[placeholder*="colleague"]',n:1,pad:4,at:'bl'},{sel:'main input[type=date] >> nth=0',n:2,pad:4,at:'bl'},{sel:'main input[type=date] >> nth=1',n:3,pad:4,at:'bl'},{sel:'main button:has-text("Save")',n:4,pad:4,at:'tr'}]);
 await shot(p,'5b-away',{x:236,y:0,width:1044,height:300});
 // 4 Approver view
 await login(p,'admin@example.org');
 await p.setViewportSize({width:1280,height:1100});
 await p.goto(base+'/Requests/Detail/16'); await p.waitForTimeout(600);
 await annotate(p,[{sel:'.approve-box textarea',n:1,pad:4,at:'tr'},{sel:'.approve-box button:has-text("Approve")',n:2,pad:4},{sel:'.approve-box button:has-text("Send back for changes")',n:3,pad:4},{sel:'.approve-box button:has-text("Reject")',n:4,pad:4},{sel:'.approve-box :text("Pass to someone else")',n:5,pad:4,at:'tr'}]);
 const ab = await p.locator('.approve-box').first().boundingBox();
 await shot(p,'4-approve',{x:236,y:ab.y-20,width:1044,height:ab.height+40});
 await b.close();
})();
