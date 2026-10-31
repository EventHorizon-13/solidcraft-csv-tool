import { readdir, readFile, writeFile, mkdir } from 'node:fs/promises'
import { basename, dirname, extname, join, resolve } from 'node:path'
import {
  AcDbDatabase,
  AcDbFileType,
  acdbHostApplicationServices
} from '@mlightcad/data-model'
import { FontManager } from '@mlightcad/mtext-renderer'
import { AcTrMTextRenderer } from '@mlightcad/three-renderer'
import { exportDatabaseToPdf } from '@mlightcad/pdf-renderer'
import { PDFDocument } from 'pdf-lib'
import { fileURLToPath } from 'node:url'
import { createPdfGlyphProvider } from './pdf-glyph-provider.mjs'

const args = parseArgs(process.argv.slice(2))
const manifestPath = resolve(args.get('--manifest') ?? '')
if (!args.has('--manifest')) throw new Error('缺少 --manifest 参数。')
const manifest = JSON.parse(await readFile(manifestPath, 'utf8'))
const inputs = manifest.inputs
const fontDirectory = resolve(manifest.fontDirectory ?? '')
const output = resolve(manifest.output ?? '')
if (!Array.isArray(inputs) || inputs.length === 0) throw new Error('没有待转换的单页 DXF。')
await setupTextFonts(fontDirectory)

const combined = await PDFDocument.create()
combined.setTitle(basename(output))
combined.setProducer('MLightCAD pdf-renderer')
combined.setCreator('标准辊密封批量生成 2.0')
const glyphProvider = createPdfGlyphProvider()

for (let index = 0; index < inputs.length; index++) {
  const input = resolve(inputs[index])
  process.stdout.write(`正在渲染 A4 PDF ${index + 1}/${inputs.length}：${basename(input)}\n`)
  const bytes = await readFile(input)
  const buffer = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength)
  const database = new AcDbDatabase()
  acdbHostApplicationServices().workingDatabase = database
  await database.read(buffer, { readOnly: true, drawNoPlotLayers: false }, AcDbFileType.DXF)
  const pageBytes = await exportDatabaseToPdf(database, {
    title: basename(input),
    paper: { widthMm: 297, heightMm: 210 },
    marginMm: 0,
    fit: 'extents',
    background: 'none',
    showLineWeight: true,
    textMode: 'vector',
    glyphProvider
  })
  const pageDocument = await PDFDocument.load(pageBytes)
  if (pageDocument.getPageCount() !== 1) throw new Error(`${basename(input)} 未输出为单页 PDF。`)
  const page = pageDocument.getPage(0)
  if (Math.abs(page.getWidth() - 297 * 72 / 25.4) > 1 || Math.abs(page.getHeight() - 210 * 72 / 25.4) > 1)
    throw new Error(`${basename(input)} 的 PDF 页面不是 A4 横向。`)
  const [copy] = await combined.copyPages(pageDocument, [0])
  combined.addPage(copy)
}

await mkdir(dirname(output), { recursive: true })
await writeFile(output, await combined.save())
process.stdout.write(`PDF 已保存：${output}\n`)

function parseArgs(values) {
  const result = new Map()
  for (let index = 0; index < values.length; index++) {
    const name = values[index]
    if (name.startsWith('--') && values[index + 1] && !values[index + 1].startsWith('--')) {
      result.set(name, values[++index])
    } else {
      throw new Error(`参数无效：${name}`)
    }
  }
  return result
}

function fontStem(value) {
  return (extname(value) ? value.slice(0, -extname(value).length) : value).toLowerCase()
}

async function setupTextFonts(directory) {
  const builtIn = join(dirname(fileURLToPath(import.meta.url)), 'fonts', 'SourceHanSansSC-Regular.ttf')
  const entries = new Map()
  const add = (entry) => {
    for (const name of [entry.file, ...entry.name]) entries.set(fontStem(name), entry)
  }
  add({ name: ['sourcehansanssc-regular', 'Source Han Sans SC', 'SourceHanSansSC'], file: 'SourceHanSansSC-Regular.ttf', path: builtIn, type: 'mesh' })
  for (const root of [directory, join(directory, 'fonts')]) {
    let files
    try { files = await readdir(root) } catch { continue }
    for (const file of files) {
      if (!/^(gbenor|gbcbig|isocp)\.shx$/i.test(file)) continue
      add({ name: [fontStem(file)], file, path: join(root, file), type: 'shx', encoding: /^gbcbig\.shx$/i.test(file) ? 'gbk' : undefined })
    }
  }
  const defaults = ['sourcehansanssc-regular']
  const manager = FontManager.instance
  manager.enableFontCache = false
  manager.setDefaultFonts(defaults)
  manager.setSymbolFonts(defaults)
  manager.lazyFontLoading = true
  manager.awaitFontsBeforeDraw = true
  const renderer = AcTrMTextRenderer.getInstance()
  renderer.setRenderMode('main')
  renderer.initialize()
  renderer.setMeshGlyphInstancing(false)
  await renderer.setDefaultFonts(defaults)
  manager.setSymbolFonts(defaults)
  await renderer.setLazyFontLoading(true)
  await renderer.setAwaitFontsBeforeDraw(true)
  manager.setFontLoader({
    get baseUrl() { return directory },
    set baseUrl(_value) { },
    async getAvailableFonts() {
      return [...new Set(entries.values())].map(entry => ({ name: entry.name, file: entry.file, type: entry.type, url: '', source: 'cache', encoding: entry.encoding }))
    },
    async load(names) {
      const statuses = []
      for (const name of names) {
        const entry = entries.get(fontStem(name))
        if (!entry) { statuses.push({ fontName: fontStem(name), url: '', status: 'NotFound' }); continue }
        try {
          const file = await readFile(entry.path)
          const arrayBuffer = file.buffer.slice(file.byteOffset, file.byteOffset + file.byteLength)
          statuses.push(await manager.cacheFont(arrayBuffer, entry.file, entry.name, entry.encoding))
        } catch (error) {
          statuses.push({ fontName: fontStem(name), url: '', status: 'FailedToLoad', error: String(error) })
        }
      }
      return statuses
    }
  })
  const statuses = await manager.loadFontsByNames(defaults)
  if (statuses.some(status => status.status !== 'Success')) throw new Error('无法加载内置思源黑体 Source Han Sans SC。')
}
