from pathlib import Path
from playwright.sync_api import sync_playwright, expect

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
        assert page.locator('#pdfBtn').inner_text().startswith('输出 PDF')
        assert page.locator('#dxfBtn').inner_text().startswith('输出 DXF')
        assert page.locator('.project-checks input[type=checkbox]').count()==5
        assert page.locator('#grid .delete').inner_text()=='×'
        assert page.locator('#grid tbody td').first.evaluate('(el) => getComputedStyle(el).textAlign')=='center'
        if name=='mobile':
            assert page.locator('#grid').bounding_box()['width'] < 1250
            assert page.locator('#grid tbody td').first.bounding_box()['width'] <= 36
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth'), 'body overflows viewport'
        # Verify what the controls submit without performing geometry jobs here.
        requests=[]
        def capture(route):
            requests.append(route.request.post_data_json)
            route.fulfill(status=400,content_type='application/json',body='{"error":"UI request check"}')
        page.route('**/api/jobs',capture)
        for checkbox in page.locator('.project-checks input').all():
            checkbox.check()
        for selector,kind in [('#dxfBtn','dxf'),('#pdfBtn','pdf'),('#stepBtn','project')]:
            page.locator(selector).click()
            expect(page.locator("#stepBtn")).to_be_enabled()
            assert page.locator('#progressDialog').is_visible()
            box=page.locator('#progressDialog').bounding_box()
            assert box['x'] >= 0 and box['y'] >= 0
            assert box['x']+box['width'] <= width and box['y']+box['height'] <= height
            page.get_by_role('button',name='关闭任务进度弹窗').click()
            assert page.locator('#progressDialog').is_hidden()
            page.locator('#showProgressBtn').click()
            assert page.locator('#progressDialog').is_visible()
            page.keyboard.press('Escape')
            assert page.locator('#progressDialog').is_hidden()
            assert requests[-1]['outputFormat']==kind
            for key in ['exportDxf','exportStep','combineStep','exportPdf','exportCsv']:
                assert requests[-1][key] == (kind=='project')
        for checkbox in page.locator('.project-checks input').all():
            checkbox.uncheck()
        count=len(requests)
        page.locator('#stepBtn').click()
        assert page.locator('#log').inner_text().endswith('请至少勾选一项项目输出。')
        assert len(requests)==count
        page.get_by_role('button',name='关闭任务进度弹窗').click()
        page.locator('#jobPanel').evaluate('(el) => el.open=false')
        page.locator('#addBtn').click()
        assert page.locator('#grid tbody tr').count()==2
        page.locator('#grid .delete').first.click()
        assert page.locator('#grid tbody tr').count()==1
        page.locator('#showProgressBtn').click()
        page.screenshot(path=f'../../work/ui-{name}.png',full_page=True)
        assert not errors,errors
        page.close()
    browser.close()
print('Desktop/mobile UI, hidden-line default 2, separate export actions and viewport checks passed.')


