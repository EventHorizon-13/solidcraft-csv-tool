// MLightCAD mtext-renderer 0.13.6 applies the primary SHX em/cap ratio to
// fallback BigFont glyphs. Use the fallback face's own ratio at the original
// drawing height. This affects BigFont only, preserving Latin/numeric glyphs.
export function patchBigFontScale(source) {
  const call = /(!s && r && \(s = this\.fontManager\.getCharShape\(\s*e,\s*r,\s*)this\.currentFontSize(\s*\), s && \(i = r\))/g
  let count = 0
  const result = source.replace(call, (_match, before, after) => {
    count++
    return `${before}((size) => { const glyph = this.fontManager.getCharShape(e, r, size); const box = glyph?.shape?.bbox; const inkHeight = box ? box.maxY - box.minY : 0; return inkHeight > size * 1.05 ? size * size / inkHeight : size; })(this.currentLayoutFontSize * this.fontManager.getFontScaleFactor(r))${after}`
  })
  if (count !== 1) throw new Error(`BigFont compatibility patch expected one call, found ${count}; review renderer version before building.`)
  return result
}
