"""Independent DXF audit and visual evidence for the synthetic C# regression fixture."""
from pathlib import Path
import html
import math
import sys
import ezdxf
from dxf_version_check import version_errors

path = Path(sys.argv[1])
raw = path.read_text(encoding='utf-8-sig')
assert not version_errors(raw), version_errors(raw)
# A mixed-version regression must fail even if a tolerant viewer can display it.
import re
legacy = re.sub(r'(?m)(^\s*9\n\$ACADVER\n\s*1\n)AC1024', r'\g<1>AC1009', raw, count=1)
errors = version_errors(legacy)
assert errors and any('$INSUNITS' in error for error in errors), errors
print('Strict DXF version check passed; downgraded-header regression detected')
doc = ezdxf.readfile(path)
audit = doc.audit()
print('DXF audit details:', [(e.code, e.message) for e in audit.errors + audit.fixes], flush=True)
assert not audit.has_errors and not audit.fixes, (audit.errors, audit.fixes)
assert doc.dxfversion == 'AC1024' and doc.header['$INSUNITS'] == 4
dimensions = list(doc.modelspace().query('DIMENSION'))
assert len(dimensions) >= 30
for dim in dimensions:
    labels = [e.dxf.text if e.dxftype()=='TEXT' else e.plain_text() for e in dim.virtual_entities() if e.dxftype() in ('TEXT','MTEXT')]
    assert len(labels) == 1 and abs(dim.get_measurement() - float(labels[0].replace('%%c','').replace('%%C','').replace('Ø','').replace('ø','').replace('⌀',''))) <= 0.00501
    assert dim.dxf.handle in doc.entitydb and dim.dxf.owner in doc.entitydb
    assert dim.dxf.dimstyle in doc.dimstyles
    assert dim.dxf.text == '<>'
    assert list(dim.virtual_entities()), 'Missing cached dimension graphics'
print(f'Independent DXF audit passed: {len(dimensions)} editable dimensions; {len(audit.fixes)} repairs')

# Show the first synthetic page with native dimension graphics expanded by ezdxf.
parts = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="-10 -859 1210 869" width="1680" height="1207">',
         '<rect x="-10" y="-859" width="1210" height="869" fill="white"/>']
colors = {'DIM': '#00a5b6', 'TEXT': '#099130', 'VISIBLE': '#141414', 'HIDDEN': '#777', 'TITLE': '#111'}
def add(entity):
    kind = entity.dxftype()
    if kind in ('DIMENSION','INSERT'):
        for child in entity.virtual_entities():
            add(child)
        return
    color = {3:'#099130',4:'#00a5b6'}.get(entity.dxf.color, colors.get(entity.dxf.layer, '#111'))
    if kind == 'LINE':
        a, b = entity.dxf.start, entity.dxf.end
        if max(a.y, b.y) < -1:
            return
        dash = ' stroke-dasharray="6 3"' if entity.dxf.layer == 'HIDDEN' else ''
        parts.append(f'<line x1="{a.x}" y1="{-a.y}" x2="{b.x}" y2="{-b.y}" stroke="{color}" stroke-width="0.5"{dash}/>')
    elif kind == 'ARC':
        c, radius = entity.dxf.center, entity.dxf.radius
        if c.y < -1:
            return
        a, b = map(math.radians, (entity.dxf.start_angle, entity.dxf.end_angle))
        x1, y1 = c.x + radius * math.cos(a), -c.y - radius * math.sin(a)
        x2, y2 = c.x + radius * math.cos(b), -c.y - radius * math.sin(b)
        large = int((entity.dxf.end_angle - entity.dxf.start_angle) % 360 > 180)
        parts.append(f'<path d="M{x1},{y1} A{radius},{radius} 0 {large} 0 {x2},{y2}" fill="none" stroke="{color}" stroke-width="0.6"/>')
    elif kind in ('TEXT','MTEXT'):
        p = (entity.dxf.align_point if entity.dxf.halign else entity.dxf.insert) if kind=='TEXT' else entity.dxf.insert
        if p.y < -1:
            return
        angle = entity.get_rotation() if kind=='MTEXT' else entity.dxf.get('rotation',0)
        text = html.escape(entity.dxf.text if kind=='TEXT' else entity.plain_text())
        text_height=entity.dxf.height if kind=='TEXT' else entity.dxf.char_height
        parts.append(f'<text x="{p.x}" y="{-p.y}" fill="{color}" font-size="{text_height}" font-family="sans-serif" text-anchor="middle" dominant-baseline="central" transform="rotate({-angle} {p.x} {-p.y})">{text}</text>')
    elif kind == 'LWPOLYLINE':
        for child in entity.virtual_entities(): add(child)
    elif kind == 'SOLID':
        vertices=[getattr(entity.dxf,'vtx'+str(i)) for i in range(3)]
        if max(p.y for p in vertices)<-1:return
        points=' '.join(f'{p.x},{-p.y}' for p in vertices)
        parts.append(f'<polygon points="{points}" fill="{color}"/>')
for entity in doc.modelspace():
    add(entity)
parts.append('</svg>')
path.with_suffix('.svg').write_text('\n'.join(parts), encoding='utf-8')

# User-visible dash spacing and template style are part of the output contract.
assert doc.layers.get('HIDDEN').dxf.linetype == 'SC_HIDDEN'
assert doc.linetypes.get('SC_HIDDEN').pattern_tags is not None
assert '_5' in doc.dimstyles and abs(doc.dimstyles.get('_5').dxf.dimscale-20)<1e-8
assert abs(doc.dimstyles.get('_5').dxf.dimtxt-.6)<1e-8
polys=list(doc.modelspace().query('LWPOLYLINE[layer=="HIDDEN"]'))
assert polys and all(p.dxf.flags & 128 for p in polys)

# Dimension text rectangles must remain distinct across and within the sheets.
rectangles=[]
for dim in dimensions:
    text=[e for e in dim.virtual_entities() if e.dxftype() in ('TEXT','MTEXT')][0]
    value=text.dxf.text if text.dxftype()=='TEXT' else text.plain_text()
    height=text.dxf.height if text.dxftype()=='TEXT' else text.dxf.char_height
    p=dim.dxf.text_midpoint
    value=value.replace('%%c','Ø').replace('%%C','Ø')
    width=max(1,len(value))*.60*height
    angle=math.radians(text.get_rotation() if text.dxftype()=='MTEXT' else text.dxf.get('rotation',0))
    dx=(abs(math.cos(angle))*width+abs(math.sin(angle))*height)/2
    dy=(abs(math.sin(angle))*width+abs(math.cos(angle))*height)/2
    box=(p.x-dx,p.y-dy,p.x+dx,p.y+dy)
    for other in rectangles:
        overlap=min(box[2],other[2])-max(box[0],other[0])>0.05 and min(box[3],other[3])-max(box[1],other[1])>0.05
        assert not overlap, ('Dimension label overlap',box,other)
    rectangles.append(box)
print('First-angle layout: framed sheets and dimension-label separation checks passed')
