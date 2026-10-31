"""Targeted upload/API checks. Synthetic SHX signatures only; no licensed font data."""
import io
import json
import os
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
assert first['drawingTemplate'] and not first['pdf']
second = upload([font, bigfont], first['resourceId'])
assert second['pdf'] and second['resourceId'] != first['resourceId']
assert not call('/api/resources/' + first['resourceId'])['pdf'], 'previous set mutated'
assert call('/api/resources/' + second['resourceId'])['pdf']
call('/WebData/resources/' + second['resourceId'] + '/frame.dxf', status=404)
boot = call('/api/bootstrap')
payload = {'rows': [boot['defaultRow']], 'combineStep': False, 'exportCsv': True,
           'exportDxf': True, 'resourceId': first['resourceId'], 'dimensionStyle': 'upload-test'}
missing = dict(payload, resourceId='b' * 32)
call('/api/jobs', json.dumps(missing).encode(), 'application/json', status=400)
call('/api/jobs', json.dumps(dict(payload, exportPdf=True)).encode(), 'application/json', status=400)
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
