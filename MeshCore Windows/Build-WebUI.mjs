import { createRequire } from 'node:module';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const nativeRoot = dirname(fileURLToPath(import.meta.url));
const web = resolve(nativeRoot, '../NeonPocket/examples/companion_radio/webui');
const require = createRequire(join(web, 'package.json'));
const esbuild = require('esbuild');
const pkg = JSON.parse(await readFile(join(web, 'package.json'), 'utf8'));
const js = await esbuild.build({
  entryPoints: [join(web, 'src/app.js')], bundle: true, write: false, format: 'iife',
  platform: 'browser', target: ['safari15', 'chrome100', 'firefox100'], minify: true,
  charset: 'utf8', legalComments: 'none',
  define: { __APP_VERSION__: JSON.stringify(pkg.version), __MESHCORE_JS_VERSION__: JSON.stringify(pkg.dependencies['@liamcottle/meshcore.js']) },
});
const css = await esbuild.transform(await readFile(join(web, 'node_modules/leaflet/dist/leaflet.css'), 'utf8') + '\n' + await readFile(join(web, 'src/styles.css'), 'utf8'), { loader: 'css', minify: true });
const template = await readFile(join(web, 'src/index.html'), 'utf8');
const html = template.replace('/*__RCC6_CSS__*/', () => css.code.trim()).replace('/*__RCC6_JS__*/', () => js.outputFiles[0].text.trim());
if (html.includes('/*__RCC6_') || /<script\s+[^>]*src=|<link\s+[^>]*rel=["']stylesheet/i.test(html)) throw Error('Unresolved or external WebUI assets.');
const output = join(nativeRoot, 'artifacts');
await mkdir(output, { recursive: true });
await writeFile(join(output, 'radio.html'), html);
console.log(`Native WebUI built: ${Buffer.byteLength(html)} bytes.`);
