"""Validate header and dimension field versions before permissive DXF loading."""
from collections import defaultdict
from ezdxf.entities import factory
from ezdxf.sections.headervars import HEADER_VAR_MAP


def version_errors(text):
    lines = text.splitlines()
    if len(lines) % 2:
        return ['Incomplete DXF group pair']
    tags = [(int(lines[i].strip()), lines[i + 1].strip(), i + 1) for i in range(0, len(lines), 2)]
    version = next((tags[i + 1][1] for i, tag in enumerate(tags[:-1]) if tag[:2] == (9, '$ACADVER')), None)
    if version is None:
        return ['Missing $ACADVER']
    schemas = {}
    for kind in ('DIMENSION', 'DIMSTYLE'):
        attributes = factory.cls(kind).DXFATTRIBS
        codes = defaultdict(list)
        for code, name in attributes.build_group_code_items():
            codes[code].append(attributes.get(name).dxfversion)
        schemas[kind] = codes
    errors = []
    section = kind = ''
    for i, (code, value, line) in enumerate(tags):
        if code == 0:
            kind = value
            if value == 'SECTION' and i + 1 < len(tags):
                section = tags[i + 1][1]
            elif value == 'ENDSEC':
                section = ''
            continue
        if section == 'HEADER' and code == 9:
            definition = HEADER_VAR_MAP.get(value)
            if definition is not None and definition.mindxf > version:
                errors.append(f'Line {line}: {value} requires {definition.mindxf}, declared {version}')
        elif kind in schemas and code in schemas[kind] and min(schemas[kind][code]) > version:
            errors.append(f'Line {line}: {kind} group {code} requires {min(schemas[kind][code])}, declared {version}')
    return errors
