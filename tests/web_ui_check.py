from pathlib import Path
from playwright.sync_api import sync_playwright

with sync_playwright() as p:
    browser = p.chromium.launch()
    for width, height, name in [(1440,1000,'desktop'),(390,844,'mobile')]:
        page = browser.new_page(viewport={'width':width,'height':height}, device_scale_factor=1, locale='zh-CN')
        errors=[]
        page.on('pageerror',lambda error: errors.append(str(error)))
        page.add_init_script("sessionStorage.setItem('rollerSealToken','upload-smoke-token')")
        page.goto('http://127.0.0.1:5080/',wait_until='networkidle')
        page.locator('#connectionState').filter(has_text='已连接').wait_for()
        page.evaluate('document.fonts.ready')
        assert page.locator('#hiddenLineScale').input_value()=='2'
        assert page.locator('#jobPanel').evaluate('(el) => !el.open')
        assert page.locator('#jobProgress').is_hidden()
        assert page.locator('#jobActions').is_hidden()
        assert page.locator('.eyebrow').count()==0
        assert page.locator('.table-wrap').bounding_box()['height'] < 180
        assert page.locator('#pdfBtn').inner_text().startswith('生成 PDF')
        assert page.locator('#dxfBtn').inner_text().startswith('生成 DXF')
        assert page.locator('#exportPdf').count()==0
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth'), 'body overflows viewport'
        page.locator('#addBtn').click()
        assert page.locator('#grid tbody tr').count()==2
        page.screenshot(path=f'../../work/ui-{name}.png',full_page=True)
        assert not errors,errors
        page.close()
    browser.close()
print('Desktop/mobile UI, hidden-line default 2, separate export actions and viewport checks passed.')
