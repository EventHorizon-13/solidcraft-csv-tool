"""Targeted upload/API checks. Synthetic SHX signatures only; no licensed font data."""
import io
import json
import os
from pathlib import Path
import time
import urllib.error
import urllib.request
import uuid
import zipfile

import ezdxf

BASE = 'http://127.0.0.1:5080'
TOKEN = 'upload-smoke-token'

def call(path, data=None, content_type=None, auth=True, status=200):
    headers = {'X-RollerSeal-Token': TOKEN} if auth else {}
    if content_type:
        headers['Content-Type'] = content_type
    request = urllib.request.Request(BASE + path, data=data, headers=headers)
    try:
        response = urllib.request.urlopen(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    body = response.read()
    assert response.status == status, (path, response.status, body[:500])
    if 'application/json' in response.headers.get('Content-Type', ''):
        return json.loads(body)
    return body

def upload(files, resource_id=None, **options):
    boundary = 'boundary' + uuid.uuid4().hex
    body = bytearray()
    if resource_id:
        body.extend(f'--{boundary}\r\nContent-Disposition: form-data; name="resourceId"\r\n\r\n{resource_id}\r\n'.encode())
    for field, name, content in files:
        body.extend(f'--{boundary}\r\nContent-Disposition: form-data; name="{field}"; filename="{name}"\r\nContent-Type: application/octet-stream\r\n\r\n'.encode())
        body.extend(content)
        body.extend(b'\r\n')
    body.extend(f'--{boundary}--\r\n'.encode())
    return call('/api/resources', bytes(body), 'multipart/form-data; boundary=' + boundary, **options)

# Genuine structural DXF fixture with a distinct dimension style demonstrates that
# a job uses the selected resource set instead of the package's/default template.
doc = ezdxf.new('R2010')
doc.dimstyles.new('upload-test')
block = doc.blocks.new('A4_FRAME')
block.add_lwpolyline([(0, 0), (297, 0), (297, 210), (0, 210)], close=True)
doc.modelspace().add_blockref('A4_FRAME', (0, 0))
stream = io.StringIO()
doc.write(stream)
dxf = stream.getvalue().encode('utf-8')
frame = ('drawingTemplate', 'A4.dxf', dxf)
# These exercise transfer and signature checks only; never passed to the PDF renderer.
font = ('fonts', 'gbenor.shx', b'AutoCAD-86 shapes 1.0\r\n\x1a' + bytes(32))
bigfont = ('fonts', 'gbcbig.shx', b'AutoCAD-86 bigfont 1.0\r\n\x1a' + bytes(32))

call('/api/bootstrap', auth=False, status=401)
upload([frame], auth=False, status=401)
upload([('drawingTemplate', '../A4.dxf', dxf)], status=400)
upload([('fonts', 'other.shx', font[2])], status=400)
upload([('fonts', 'gbenor.shx', b'not SHX')], status=400)
upload([('drawingTemplate', 'A4.dxf', b'not DXF')], status=400)
upload([frame, frame], status=400)
upload([('drawingTemplate', 'A4.dxf', b'x' * (8 * 1024 * 1024 + 1))], status=400)
upload([frame], resource_id='../escape', status=400)
call('/api/resources/' + 'a' * 32, status=404)

first = upload([frame])
assert first['drawingTemplate'] and first['pdf']
assert first['customFonts'] is False, first
isocp = ('fonts', 'isocp.shx', font[2])
second = upload([font, bigfont, isocp], first['resourceId'])
assert second['fonts']['isocp']
assert second['customFonts'] is True, second
assert second['pdf'] and second['resourceId'] != first['resourceId']
preserved = upload([frame], second['resourceId'])
assert preserved['customFonts'] is True, preserved
assert not call('/api/resources/' + first['resourceId'])['fonts']['gbenor'], 'previous set mutated'
assert call('/api/resources/' + second['resourceId'])['pdf']
call('/WebData/resources/' + second['resourceId'] + '/frame.dxf', status=404)
boot = call('/api/bootstrap')
payload = {'rows': [boot['defaultRow']], 'combineStep': False, 'exportCsv': True,
           'exportDxf': True, 'resourceId': first['resourceId'], 'dimensionStyle': 'upload-test'}
missing = dict(payload, resourceId='b' * 32)
call('/api/jobs', json.dumps(missing).encode(), 'application/json', status=400)
call('/api/jobs', json.dumps(dict(payload, outputFormat='exe')).encode(), 'application/json', status=400)
job = call('/api/jobs', json.dumps(payload).encode(), 'application/json')
for _ in range(180):
    result = call('/api/jobs/' + job['id'])
    if result['status'] in ('completed', 'failed', 'cancelled'):
        break
    time.sleep(1)
assert result['status'] == 'completed', result
archive = call('/api/jobs/' + job['id'] + '/download')
names = zipfile.ZipFile(io.BytesIO(archive)).namelist()
assert any(name.endswith('.dxf') for name in names), names
print('Targeted uploads, authentication, invalid files, size/path limits, immutable sets and selected-frame DXF job passed.')


# New Web downloads: no ZIP for DXF/PDF; no-frame DXF contains a part label and
# no FRAME entities. Two differently sized parts share the same PDF scale.
from pypdf import PdfReader

def run_job(payload):
    job = call('/api/jobs', json.dumps(payload).encode(), 'application/json')
    for _ in range(180):
        result = call('/api/jobs/' + job['id'])
        if result['status'] in ('completed', 'failed', 'cancelled'):
            break
        time.sleep(1)
    assert result['status'] == 'completed', result
    return result, call('/api/jobs/' + job['id'] + '/download')

row = boot['defaultRow'].copy()
row[0] = '2'
row[5] = str(float(row[5]) * 10)
rows = [boot['defaultRow'], row]
result, raw = run_job({'rows': rows, 'outputFormat': 'dxf', 'includeFrame': False})
assert result['contentType'] == 'application/dxf' and result['downloadName'].endswith('.dxf')
assert not raw.startswith(b'PK')
drawing = ezdxf.read(io.StringIO(raw.decode('utf-8-sig')))
assert not list(drawing.modelspace().query('*[layer=="FRAME"]'))
labels = [entity.dxf.text for entity in drawing.modelspace().query('TEXT') if '编号' in entity.dxf.text]
assert len(labels) == 2 and labels[0] != labels[1], labels
assert any('SourceHanSansSC-Regular.ttf' in style.dxf.font for style in drawing.styles)
result, raw = run_job({'rows': rows, 'outputFormat': 'pdf', 'includeFrame': False})
assert result['contentType'] == 'application/pdf' and result['downloadName'].endswith('.pdf')
assert raw.startswith(b'%PDF-') and not raw.startswith(b'PK')
Path('../../work/export-no-frame.pdf').write_bytes(raw)
pdf = PdfReader(io.BytesIO(raw))
assert len(pdf.pages) == 2
for page in pdf.pages:
    assert abs(float(page.mediabox.width) - 841.89) < 1
    assert abs(float(page.mediabox.height) - 595.28) < 1
# Compare the effective geometry CTM emitted into each actual PDF page.
# PDF polylines may be emitted as separate stroke segments, so a closed-path
# detector would incorrectly measure a glyph or a semicircle instead of a box.
from collections import Counter
def geometry_transform(page):
    transforms = Counter()
    def visit(operator, operands, cm, tm):
        if operator in (b'm', b'l'):
            transforms[tuple(round(float(v),5) for v in cm)] += 1
    page.extract_text(visitor_operand_before=visit)
    assert transforms, 'no vector geometry in PDF'
    return transforms.most_common(1)[0][0]
a,b = geometry_transform(pdf.pages[0]), geometry_transform(pdf.pages[1])
assert a == b and a[0] > 0 and a[3] > 0, (a,b)
assert pdf.pages[0].get_contents().get_data() != pdf.pages[1].get_contents().get_data()
print('Direct no-frame DXF/PDF, labels, Source Han Sans rendering, A4 pagination and identical geometry transforms passed:',a)
