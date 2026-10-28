import { build } from 'esbuild'
import { copyFile, mkdir, readdir, readFile } from 'node:fs/promises'
import { basename, dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { patchBigFontScale } from './font-scale-compat.mjs'

const projectDirectory = dirname(fileURLToPath(import.meta.url))
const outputDirectory = resolve(process.argv[2] ?? '')
if (!process.argv[2]) throw new Error('Usage: node build-bundle.mjs <output-directory>')
await mkdir(outputDirectory, { recursive: true })

await build({
  absWorkingDir: projectDirectory,
  entryPoints: [join(projectDirectory, 'pdf-exporter.mjs')],
  outfile: join(outputDirectory, 'pdf-exporter.bundle.mjs'),
  bundle: true,
  plugins: [{
    name: 'bigfont-own-cap-height',
    setup(builder) {
      builder.onLoad({ filter: /[\\/]@mlightcad[\\/]mtext-renderer[\\/]dist[\\/]index\.js$/ }, async ({ path }) => ({
        contents: patchBigFontScale(await readFile(path, 'utf8')), loader: 'js'
      }))
    }
  }],
  minify: true,
  platform: 'node',
  format: 'esm',
  target: 'node24',
  // @mlightcad/three-renderer imports this ESM file without its required .js suffix.
  alias: {
    'three/examples/jsm/controls/OrbitControls': 'three/examples/jsm/controls/OrbitControls.js'
  },
  sourcemap: false,
  legalComments: 'none'
})

for (const file of ['package.json', 'package-lock.json'])
  await copyFile(join(projectDirectory, file), join(outputDirectory, file))

const licenseDirectory = join(outputDirectory, 'licenses')
await mkdir(licenseDirectory, { recursive: true })
const packageLines = []
const visited = new Set()
const legalName = /^(LICENSE|LICENCE|COPYING|NOTICE|COPYRIGHT|PATENTS)(?:[._-].*)?$/i

await scanModules(join(projectDirectory, 'node_modules'))
packageLines.sort((a, b) => a.localeCompare(b))
await import('node:fs/promises').then(({ writeFile }) =>
  writeFile(join(licenseDirectory, 'DEPENDENCIES.txt'), packageLines.join('\n') + '\n', 'utf8'))

async function scanModules(directory) {
  let entries
  try { entries = await readdir(directory, { withFileTypes: true }) } catch { return }
  for (const entry of entries) {
    if (!entry.isDirectory() || entry.name === '.bin') continue
    const path = join(directory, entry.name)
    if (entry.name.startsWith('@')) {
      let scoped
      try { scoped = await readdir(path, { withFileTypes: true }) } catch { continue }
      for (const child of scoped) if (child.isDirectory()) await addPackage(join(path, child.name))
    } else {
      await addPackage(path)
    }
  }
}

async function addPackage(directory) {
  let metadata
  try { metadata = JSON.parse(await readFile(join(directory, 'package.json'), 'utf8')) } catch { return }
  const name = metadata.name ?? basename(directory)
  const version = metadata.version ?? 'unknown'
  const slug = `${name.replaceAll('/', '__')}@${version}`.replaceAll(/[^A-Za-z0-9_.@+-]/g, '_')
  packageLines.push(`${name}@${version}\t${typeof metadata.license === 'string' ? metadata.license : JSON.stringify(metadata.license ?? 'SEE LICENSE FILE')}`)
  if (!visited.has(slug)) {
    visited.add(slug)
    const files = await findLegalFiles(directory)
    if (files.length) {
      const target = join(licenseDirectory, slug)
      await mkdir(target, { recursive: true })
      for (const file of files) {
        const relative = file.slice(directory.length + 1)
        const destination = join(target, relative)
        await mkdir(dirname(destination), { recursive: true })
        await copyFile(file, destination)
      }
    }
  }
  await findNestedModules(directory)
}

async function findNestedModules(directory) {
  let entries
  try { entries = await readdir(directory, { withFileTypes: true }) } catch { return }
  for (const entry of entries) {
    if (!entry.isDirectory() || entry.name === '.bin') continue
    const path = join(directory, entry.name)
    if (entry.name === 'node_modules') await scanModules(path)
    else await findNestedModules(path)
  }
}

async function findLegalFiles(directory, relative = '') {
  let entries
  try { entries = await readdir(join(directory, relative), { withFileTypes: true }) } catch { return [] }
  const result = []
  for (const entry of entries) {
    if (entry.name === 'node_modules') continue
    const child = relative ? join(relative, entry.name) : entry.name
    if (entry.isFile() && legalName.test(entry.name)) result.push(join(directory, child))
    else if (entry.isDirectory() && !entry.name.startsWith('.')) result.push(...await findLegalFiles(directory, child))
  }
  return result
}

