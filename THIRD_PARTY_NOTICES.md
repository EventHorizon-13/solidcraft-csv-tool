# Third-party notices

The Windows application uses the .NET 10 Windows Forms runtime. The Linux command-line application uses the self-contained .NET 10 runtime.

## PDF developer preview

The PDF developer preview bundles the open-source Node.js 24 runtime and these open-source packages:

- `@mlightcad/data-model` 1.15.14; `@mlightcad/pdf-renderer` 1.7.8-preview.1; `@mlightcad/three-renderer` 1.7.8-preview.1; `@mlightcad/mtext-renderer` 0.13.6; `@mlightcad/mtext-parser` 1.5.5 — MIT, https://github.com/mlightcad/cad-viewer and https://github.com/mlightcad/realdwg-web
- pdf-lib 1.17.1 — MIT, https://github.com/Hopding/pdf-lib
- Three.js 0.186.1 — MIT, https://github.com/mrdoob/three.js
- @velipso/polybool 1.1.1 — 0BSD, https://github.com/velipso/polybool
- `pdf-glyph-provider.mjs` adapts MLightCAD's MIT-licensed SHX/BigFont glyph-provider implementation. Copyright (c) 2026 mlightcad. The applicable MIT notice is reproduced below.

The PDF renderer is bundled into `PdfExporter/pdf-exporter.bundle.mjs` to avoid shipping the entire npm package tree. The resolved dependency list and applicable license files are included under `PdfExporter/licenses`. Node.js license files are included under `PdfExporter`. esbuild 0.28.2 is an MIT-licensed build-time tool and is not included in the application runtime. The PDF engine is DXF-only and does not include LibreDWG or other GPL conversion packages. SHX font files are not bundled; users supply fonts they are licensed to use.

MIT License (MLightCAD glyph-provider adaptation)

Copyright (c) 2026 mlightcad

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

DXF 2010 output is written with netDxf 2023.11.10, https://github.com/haplokuon/netDxf .
Copyright (c) Daniel Carvajal. Licensed under the MIT License:

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## SolidCraft CLI 0.28.0

Windows and Linux release packages bundle the unmodified official SolidCraft CLI from https://github.com/tangency-labs/solidcraft/releases/tag/v0.28.0 . SolidCraft is an independent application by Tangency Labs. Its MIT/Apache-2.0 licenses, NOTICE and THIRD-PARTY-LICENSES.txt accompany the binary under SolidCraft/. Source: https://github.com/tangency-labs/solidcraft/tree/v0.28.0 .
