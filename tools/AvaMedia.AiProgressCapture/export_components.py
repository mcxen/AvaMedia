"""从指定评审 HTML 提取 gallery 布局及示例内容；组件仍由 Avalonia 实控件绘制。"""
import json
from pathlib import Path
from playwright.sync_api import sync_playwright

root = Path('/workspace/AvaMedia-sidebar-review/components')
with sync_playwright() as p:
    browser = p.chromium.launch(executable_path='/usr/bin/google-chrome', args=['--no-sandbox'])
    page = browser.new_page(viewport={'width':1344,'height':900}, device_scale_factor=1)
    page.goto((root/'components.html').as_uri())
    data=page.evaluate('''() => [...document.querySelectorAll('section')].map(section => {
      const sr=section.getBoundingClientRect();
      const rect=e=>{const r=e.getBoundingClientRect();return {x:r.x-sr.x,y:r.y-sr.y,w:r.width,h:r.height}};
      const leaf='.btn,.pop,.combo,.chk,.sw,.num,.tf,.sl,.pb,.ring,.stages,.subs,.seg,.tabs,.chip,.badge,.dotb,.th,.tip,.toast,.state,.split,.menu,.spin,.sbar,.lr,.ai';
      const selected=[...section.querySelectorAll(leaf)].filter(e=>!e.parentElement.closest(leaf));
      const shell=[...section.querySelectorAll('.card,.group,.popv,.sheet,.scrollbox,.gr')];
      const nodes=[];
      const inks={};
      for (const [css,key] of Object.entries({'--text':'UiText','--text2':'UiTextSecondary','--text3':'AcTextTertiary','--accent':'UiAccent','--red':'UiDanger','--bannerT':'AcBannerText'})){
        const test=document.createElement('span');test.style.color=getComputedStyle(document.body).getPropertyValue(css);document.body.appendChild(test);inks[getComputedStyle(test).color]=key;test.remove();
      }
      const node=e=>{const c=getComputedStyle(e);return {...rect(e),cls:e.className,text:e.value||e.innerText||'',tag:e.tagName,html:e.innerHTML,value:e.querySelector('input')?.value,icon:e.querySelector('use')?.getAttribute('href'),
        font:parseFloat(c.fontSize),weight:c.fontWeight,opacity:parseFloat(c.opacity),background:c.backgroundImage,items:[...e.querySelectorAll('b,.mi,.mhd,.msep,.sc,.sr')].map(i=>({cls:i.className,text:i.innerText,html:i.innerHTML,title:i.querySelector('.tt,.l1')?.innerText,detail:i.querySelector('.ds,.l2')?.innerText,count:i.querySelector('.tm,.rt')?.innerText}))}};
      shell.forEach(e=>nodes.push({...node(e),kind:'shell'}));
      selected.forEach(e=>nodes.push({...node(e),kind:'control'}));
      const walker=document.createTreeWalker(section,NodeFilter.SHOW_TEXT);
      let t;while(t=walker.nextNode()){
        if(!t.textContent.trim() || selected.some(e=>e.contains(t)))continue;
        const parent=t.parentElement,c=getComputedStyle(parent);const range=document.createRange();range.selectNodeContents(t);
        const r=range.getBoundingClientRect();nodes.push({kind:'text',text:t.textContent.trim(),x:r.x-sr.x,y:r.y-sr.y,w:r.width,h:r.height,font:parseFloat(c.fontSize),weight:c.fontWeight,ink:inks[c.color]||"UiText",cls:parent.className});
      }
      return {width:sr.width,height:sr.height,nodes};
    })''')
    browser.close()
Path(__file__).with_name('component-layout.json').write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n')
