import { MTextColor } from '@mlightcad/mtext-parser'
import { AcTrMTextRenderer } from '@mlightcad/three-renderer'

// Adapted from MLightCAD's MIT-licensed cad-pdf-plugin glyph provider.
const empty = {
    triangles: new Float32Array(0),
    polylines: new Float32Array(0)
  }
const emptyBox = { min: { x: 0, y: 0 }, max: { x: 0, y: 0 } }
export function createPdfGlyphProvider() {
  return {
    async renderMText(data, style, colorSettings) {
      try {
        const renderer = AcTrMTextRenderer.getInstance()
        const object = await renderer.asyncRenderMText(
          { ...data, meshGlyphOutputMode: 'merge' },
          style,
          toMTextColorSettings(colorSettings)
        )
        const contents = data.contents || data.text || ''
        const result = extractGlyphPrimitives(object, data.position?.x ?? 0, data.position?.y ?? 0)
        return { ...result, actualText: contents }
      } catch (error) {
        console.warn('[pdf-export] MTEXT vector rendering failed:', error instanceof Error ? error.message : error)
        return { primitives: empty, actualText: data.contents || data.text || '', box: emptyBox }
      }
    },
    async renderShape(shape, style) {
      try {
        const renderer = AcTrMTextRenderer.getInstance()
        const object = await renderer.asyncRenderShape(shape, style ?? {})
        return extractGlyphPrimitives(object, shape.position?.x ?? 0, shape.position?.y ?? 0)
      } catch (error) {
        console.warn('[pdf-export] SHAPE vector rendering failed:', error instanceof Error ? error.message : error)
        return { primitives: empty, box: emptyBox }
      }
    }
  }
}

function toMTextColorSettings(settings) {
  if (!settings) return undefined
  const color = new MTextColor()
  if (settings.entityIsByLayer) color.aci = 256
  else if (settings.entityIsByBlock) color.aci = 0
  else if (settings.entityIsForeground) color.aci = 7
  else if (typeof settings.entityAci === 'number') color.aci = settings.entityAci
  else if (typeof settings.entityRgb === 'number') color.rgbValue = settings.entityRgb
  else color.aci = 7
  return {
    color,
    byLayerColor: settings.byLayerColor,
    byBlockColor: settings.byBlockColor,
    layer: settings.layer
  }
}

function extractGlyphPrimitives(root, originX, originY) {
  root.updateMatrixWorld?.(true)
  const groups = new Map()
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity
  const addPoint = (group, point) => {
    group.points.push(point.x, point.y)
    minX = Math.min(minX, point.x); minY = Math.min(minY, point.y)
    maxX = Math.max(maxX, point.x); maxY = Math.max(maxY, point.y)
  }
  const getGroup = material => {
    const color = material?.color?.getHex?.() ?? 0xffffff
    let group = groups.get(color)
    if (!group) { group = { color, triangles: [], polylines: [], points: [] }; groups.set(color, group) }
    return group
  }
  const walk = node => {
    const geometry = node.geometry
    const position = geometry?.getAttribute?.('position')
    if (!position || position.count < 1) return
    const materials = Array.isArray(node.material) ? node.material : [node.material]
    const group = getGroup(materials[0])
    const elements = node.matrixWorld?.elements
    const world = index => {
      const x = position.getX(index), y = position.getY(index), z = position.getZ?.(index) ?? 0
      const w = elements ? elements[3] * x + elements[7] * y + elements[11] * z + elements[15] : 1
      const scale = w === 0 ? 1 : 1 / w
      return elements
        ? { x: (elements[0] * x + elements[4] * y + elements[8] * z + elements[12]) * scale - originX,
            y: (elements[1] * x + elements[5] * y + elements[9] * z + elements[13]) * scale - originY }
        : { x: x - originX, y: y - originY }
    }
    if (node.isMesh) {
      const index = geometry.getIndex?.()
      const count = index ? index.count : position.count
      for (let i = 0; i + 2 < count; i += 3) {
        for (let j = 0; j < 3; j++) {
          const p = world(index ? index.getX(i + j) : i + j)
          group.triangles.push(p.x, p.y)
          minX = Math.min(minX, p.x); minY = Math.min(minY, p.y)
          maxX = Math.max(maxX, p.x); maxY = Math.max(maxY, p.y)
        }
      }
      return
    }
    if (!(node.isLine || node.isLineSegments || node.isLineLoop)) return
    const index = geometry.getIndex?.()
    const at = i => world(index ? index.getX(i) : i)
    if (node.isLineSegments) {
      const count = index ? index.count : position.count
      for (let i = 0; i + 1 < count; i += 2) {
        const a = at(i), b = at(i + 1)
        const before = group.points.length
        addPoint(group, a); addPoint(group, b)
        group.polylines.push(2, ...group.points.slice(before))
        group.points.length = before
      }
    } else {
      const count = index ? index.count : position.count
      const vertices = Array.from({ length: count }, (_, i) => at(i))
      if (node.isLineLoop && vertices.length > 1) vertices.push(vertices[0])
      if (vertices.length > 1) {
        const flat = []
        for (const point of vertices) addPoint({ points: flat }, point)
        group.polylines.push(vertices.length, ...flat)
      }
    }
  }
  root.traverse(walk)
  const colorGroups = [...groups.values()].map(group => ({
    rgb: group.color,
    primitives: {
      triangles: new Float32Array(group.triangles),
      polylines: new Float32Array(group.polylines)
    }
  })).filter(item => item.primitives.triangles.length || item.primitives.polylines.length)
  const primitives = colorGroups.length === 1 ? colorGroups[0].primitives : empty
  const box = Number.isFinite(minX)
    ? { min: { x: minX, y: minY }, max: { x: maxX, y: maxY } }
    : emptyBox
  return { primitives, colorGroups: colorGroups.length > 1 ? colorGroups : undefined, box }
}

