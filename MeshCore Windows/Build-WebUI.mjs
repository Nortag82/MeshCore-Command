import { createRequire } from 'node:module';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const nativeRoot = dirname(fileURLToPath(import.meta.url));
const web = resolve(nativeRoot, '../NeonPocket/examples/companion_radio/webui');
const require = createRequire(join(web, 'package.json'));
const esbuild = require('esbuild');
const pkg = JSON.parse(await readFile(join(web, 'package.json'), 'utf8'));
const versionFile = await readFile(join(nativeRoot, 'NativeVersion.props'), 'utf8');
const version = versionFile.match(/<MccVersion>(\d+\.\d+\.\d+)<\/MccVersion>/)?.[1];
if (!version) throw Error('Native version is missing or invalid.');
const js = await esbuild.build({
  entryPoints: [join(web, 'src/app.js')], bundle: true, write: false, format: 'iife',
  platform: 'browser', target: ['safari15', 'chrome100', 'firefox100'], minify: true,
  charset: 'utf8', legalComments: 'none',
  define: { __APP_VERSION__: JSON.stringify(version), __MESHCORE_JS_VERSION__: JSON.stringify(pkg.dependencies['@liamcottle/meshcore.js']) },
});
const css = await esbuild.transform(await readFile(join(web, 'node_modules/leaflet/dist/leaflet.css'), 'utf8') + '\n' + await readFile(join(web, 'src/styles.css'), 'utf8'), { loader: 'css', minify: true });
const template = await readFile(join(web, 'src/index.html'), 'utf8');
const placeholder = '<span class="app-build" id="desktop-build" hidden></span>';
const mark = '<svg class="brand-mark" aria-hidden="true"><use href="#brand-pocket"/></svg>';
if (!template.includes(placeholder) || !template.includes(mark)) throw Error('Desktop branding placeholders are missing.');
const icon = (await readFile(join(nativeRoot, 'assets/meshcore-command-small.png'))).toString('base64');
const branded = template.replace(placeholder, `<span class="app-build" id="desktop-build">Version ${version}</span>`)
  .replaceAll('NeonPocketMC', 'MeshCore Command')
  .replaceAll(mark, `<img class="brand-mark" alt="" src="data:image/png;base64,${icon}">`)
  .replace('</head>', '<style>.ultimate-hero,.ultimate-metrics,.ultimate-grid,.ultimate-settings-grid,.device-details .device-card,.device-details .settings-stack > :not(:first-child),#network-setup-button{display:none!important}</style></head>');
const html = branded.replace('/*__RCC6_CSS__*/', () => css.code.trim()).replace('/*__RCC6_JS__*/', () => js.outputFiles[0].text.trim());
if (html.includes('/*__RCC6_') || /<script\s+[^>]*src=|<link\s+[^>]*rel=["']stylesheet/i.test(html)) throw Error('Unresolved or external WebUI assets.');
const output = join(nativeRoot, 'artifacts');
await mkdir(output, { recursive: true });
await writeFile(join(output, 'radio.html'), html);
const mapJs = await esbuild.build({
  entryPoints: [join(nativeRoot, 'assets/hflink-contacts-map.mjs')], nodePaths: [join(web, 'node_modules')],
  bundle: true, write: false, format: 'esm', platform: 'browser', target: 'chrome100', minify: true,
});
const leafletCss = await readFile(join(web, 'node_modules/leaflet/dist/leaflet.css'), 'utf8');
await writeFile(join(output, 'hflink-contacts-map.html'), `<!doctype html><html lang="en"><meta charset="utf-8">
<title>HFLink contact map</title><style>${leafletCss}
html,body{height:100%;margin:0;background:#0b1218;color:#e8f2fa;font:14px Segoe UI,sans-serif}
body{display:flex;flex-direction:column}#map{flex:1;background:#14232e}#status{margin:0;padding:8px 12px}
</style><p id="status">Contact markers show grid-square centres.</p><div id="map" aria-label="HFLink contacts map"></div>
<script type="module">${mapJs.outputFiles[0].text}</script></html>`);
console.log(`Native WebUI built: ${Buffer.byteLength(html)} bytes.`);
