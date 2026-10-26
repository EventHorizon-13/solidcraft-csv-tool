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
    labels = [e.dxf.text for e in dim.virtual_entities() if e.dxftype() == 'TEXT']
    assert len(labels) == 1 and abs(dim.get_measurement() - float(labels[0])) <= 0.00501
    assert dim.dxf.handle in doc.entitydb and dim.dxf.owner in doc.entitydb
    assert dim.dxf.dimstyle in doc.dimstyles
    assert dim.dxf.text == '<>'
    assert list(dim.virtual_entities()), 'Missing cached dimension graphics'
print(f'Independent DXF audit passed: {len(dimensions)} editable dimensions; {len(audit.fixes)} repairs')

# Show the first synthetic page with native dimension graphics expanded by ezdxf.
parts = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="-90 -190 700 490" width="1680" height="1176">',
         '<rect x="-90" y="-190" width="700" height="490" fill="white"/>']
colors = {'DIM': '#00a5b6', 'TEXT': '#099130', 'VISIBLE': '#141414', 'HIDDEN': '#777', 'TITLE': '#111'}
def add(entity):
    kind = entity.dxftype()
    if kind == 'DIMENSION':
        for child in entity.virtual_entities():
            add(child)
        return
    color = colors.get(entity.dxf.layer, '#111')
    if kind == 'LINE':
        a, b = entity.dxf.start, entity.dxf.end
        if min(a.y, b.y) < -300:
            return
        dash = ' stroke-dasharray="4 2"' if entity.dxf.layer == 'HIDDEN' else ''
        parts.append(f'<line x1="{a.x}" y1="{-a.y}" x2="{b.x}" y2="{-b.y}" stroke="{color}" stroke-width="0.5"{dash}/>')
    elif kind == 'ARC':
        c, radius = entity.dxf.center, entity.dxf.radius
        if c.y < -300:
            return
        a, b = map(math.radians, (entity.dxf.start_angle, entity.dxf.end_angle))
        x1, y1 = c.x + radius * math.cos(a), -c.y - radius * math.sin(a)
        x2, y2 = c.x + radius * math.cos(b), -c.y - radius * math.sin(b)
        large = int((entity.dxf.end_angle - entity.dxf.start_angle) % 360 > 180)
        parts.append(f'<path d="M{x1},{y1} A{radius},{radius} 0 {large} 0 {x2},{y2}" fill="none" stroke="{color}" stroke-width="0.6"/>')
    elif kind == 'TEXT':
        p = entity.dxf.align_point if entity.dxf.halign else entity.dxf.insert
        if p.y < -300:
            return
        angle, text = entity.dxf.rotation, html.escape(entity.dxf.text)
        parts.append(f'<text x="{p.x}" y="{-p.y}" fill="{color}" font-size="{entity.dxf.height}" font-family="sans-serif" text-anchor="middle" dominant-baseline="central" transform="rotate({-angle} {p.x} {-p.y})">{text}</text>')
for entity in doc.modelspace():
    add(entity)
parts.append('</svg>')
path.with_suffix('.svg').write_text('\n'.join(parts), encoding='utf-8')
