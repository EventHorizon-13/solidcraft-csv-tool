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
import { createPdfGlyphProvider } from './pdf-glyph-provider.mjs'

const args = parseArgs(process.argv.slice(2))
const manifestPath = resolve(args.get('--manifest') ?? '')
if (!args.has('--manifest')) throw new Error('缺少 --manifest 参数。')
const manifest = JSON.parse(await readFile(manifestPath, 'utf8'))
const inputs = manifest.inputs
const fontDirectory = resolve(manifest.fontDirectory ?? '')
const output = resolve(manifest.output ?? '')
if (!Array.isArray(inputs) || inputs.length === 0) throw new Error('没有待转换的单页 DXF。')
if (!manifest.fontDirectory) throw new Error('请在“出图设置”中选择包含 gbenor.shx 和 gbcbig.shx 的外置字体文件夹。')
await setupTextFonts(fontDirectory)

const combined = await PDFDocument.create()
combined.setTitle(basename(output))
combined.setProducer('MLightCAD pdf-renderer')
combined.setCreator('标准辊密封批量生成 Dev Preview')
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
  const roots = [directory, join(directory, 'fonts')]
  let root = directory
  let catalog
  for (const candidate of roots) {
    try {
      const parsed = JSON.parse(await readFile(join(candidate, 'fonts.json'), 'utf8'))
      if (Array.isArray(parsed)) { root = candidate; catalog = parsed; break }
    } catch { }
  }
  if (!catalog) {
    for (const candidate of roots) {
      try {
        const files = await readdir(candidate)
        if (files.some(file => /^gbenor\.shx$/i.test(file)) && files.some(file => /^gbcbig\.shx$/i.test(file))) {
          root = candidate
          break
        }
      } catch { }
    }
  }
  const entries = new Map()
  const add = (info) => {
    if (!info?.file) return
    const names = Array.isArray(info.name) ? info.name : [info.name ?? fontStem(info.file)]
    const entry = { ...info, name: names, type: info.type ?? (extname(info.file).toLowerCase() === '.shx' ? 'shx' : 'mesh') }
    for (const alias of [info.file, ...names]) entries.set(fontStem(alias), entry)
  }
  for (const item of catalog ?? []) add(item)
  for (const file of await readdir(root)) {
    if (!['.shx', '.ttf', '.otf', '.woff', '.woff2'].includes(extname(file).toLowerCase())) continue
    if (!entries.has(fontStem(file))) add({ name: [fontStem(file)], file, type: extname(file).toLowerCase() === '.shx' ? 'shx' : 'mesh', encoding: /^gbcbig\.shx$/i.test(file) ? 'gbk' : undefined })
  }
  for (const required of ['gbenor.shx', 'gbcbig.shx']) {
    if (!entries.has(fontStem(required))) throw new Error(`外置字体文件夹缺少 ${required}。可参照 tools/pdf-export/fonts.json.example 配置 fonts.json。`)
  }

  const manager = FontManager.instance
  manager.enableFontCache = false
  manager.setDefaultFonts('modern')
  manager.lazyFontLoading = true
  manager.awaitFontsBeforeDraw = true
  const renderer = AcTrMTextRenderer.getInstance()
  renderer.setRenderMode('main')
  renderer.initialize()
  renderer.setMeshGlyphInstancing(false)
  await renderer.setDefaultFonts('modern')
  await renderer.setLazyFontLoading(true)
  await renderer.setAwaitFontsBeforeDraw(true)
  manager.setFontLoader({
    get baseUrl() { return root },
    set baseUrl(_value) { },
    async getAvailableFonts() {
      return [...new Map([...entries.values()].map(entry => [entry.file, {
        name: entry.name, file: entry.file, type: entry.type, url: '', source: 'cache', encoding: entry.encoding
      }])).values()]
    },
    async load(names) {
      const statuses = []
      for (const name of names) {
        const entry = entries.get(fontStem(name))
        if (!entry) { statuses.push({ fontName: fontStem(name), url: '', status: 'NotFound' }); continue }
        try {
          const file = await readFile(join(root, entry.file))
          const arrayBuffer = new ArrayBuffer(file.byteLength)
          new Uint8Array(arrayBuffer).set(file)
          statuses.push(await manager.cacheFont(arrayBuffer, entry.file, entry.name, entry.encoding))
        } catch (error) {
          statuses.push({ fontName: fontStem(name), url: '', status: 'FailedToLoad', error: String(error) })
        }
      }
      return statuses
    }
  })
  const required = ['gbenor.shx', 'gbcbig.shx']
  const statuses = await manager.loadFontsByNames(required)
  const missing = statuses.filter(status => status.status !== 'Success')
  if (missing.length) throw new Error(`无法加载外置 SHX 字体：${missing.map(item => item.fontName).join('、')}`)
}

