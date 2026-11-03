/**
 * Rewrites production dist index.html so script/link tags use CRM WebResource URLs.
 * Run after: npm run build
 *
 * Virtual folder (publisher prefix): te_/CrmAiAssistant/
 * Override: CRM_WR_PREFIX=te_/MyFolder node scripts/prepare-crm-webresources.mjs
 */
import {
  readFileSync,
  writeFileSync,
  readdirSync,
  existsSync,
  mkdirSync,
  copyFileSync,
  statSync,
  rmSync
} from 'node:fs';
import { join, basename, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = fileURLToPath(new URL('.', import.meta.url));
const root = join(__dirname, '..');
const browserDir = join(root, 'dist', 'crm-ai-assistant-ui', 'browser');
const outDir = join(root, 'dist', 'crm-webresources');
const prefix = (process.env.CRM_WR_PREFIX || 'te_/CrmAiAssistant').replace(/\/+$/, '');
const wrBase = `/WebResources/${prefix}`;

if (!existsSync(browserDir)) {
  console.error(`Missing build output: ${browserDir}`);
  console.error('Run: npm run build');
  process.exit(1);
}

if (existsSync(outDir)) {
  rmSync(outDir, { recursive: true, force: true });
}
mkdirSync(outDir, { recursive: true });

/** @type {string[]} */
const copiedFiles = [];

/**
 * @param {string} srcDir
 * @param {string} destDir
 */
function copyTree(srcDir, destDir) {
  mkdirSync(destDir, { recursive: true });
  for (const name of readdirSync(srcDir)) {
    const src = join(srcDir, name);
    const dest = join(destDir, name);
    const st = statSync(src);
    if (st.isDirectory()) {
      copyTree(src, dest);
    } else {
      copyFileSync(src, dest);
      copiedFiles.push(relative(outDir, dest).replace(/\\/g, '/'));
    }
  }
}

copyTree(browserDir, outDir);

let html = readFileSync(join(browserDir, 'index.html'), 'utf8');

// Ensure relative base for CRM iframe / virtual folder.
html = html.replace(/<base href="[^"]*">/i, '<base href="./">');

// Point relative .js / .css references at CRM web resource URLs.
html = html.replace(
  /\b(href|src)="(?!https?:|\/|data:)([^"]+\.(?:js|css))"/gi,
  (_, attr, file) => `${attr}="${wrBase}/${basename(file)}"`
);

writeFileSync(join(outDir, 'index.html'), html, 'utf8');

const mapping = copiedFiles.map((file) => {
  const type =
    file.endsWith('.html')
      ? 'Webpage (HTML)'
      : file.endsWith('.js')
        ? 'Script (JScript)'
        : file.endsWith('.css')
          ? 'Style Sheet (CSS)'
          : file.endsWith('.json')
            ? 'Data (XML)'
            : file.endsWith('.ico') || file.endsWith('.png') || file.endsWith('.svg')
              ? 'PNG/JPG/GIF/ICO format (image)'
              : 'Script (JScript)';
  return {
    distFile: file,
    crmLogicalName: `${prefix}/${file}`,
    webResourceUrl: `${wrBase}/${file}`,
    type
  };
});

writeFileSync(
  join(outDir, 'CRM-UPLOAD-MAPPING.json'),
  JSON.stringify({ prefix, wrBase, entryHtml: `${wrBase}/index.html`, mapping }, null, 2),
  'utf8'
);

console.log('CRM web resources prepared.');
console.log(`  Output:   ${outDir}`);
console.log(`  Prefix:   ${prefix}`);
console.log(`  Entry:    ${wrBase}/index.html`);
console.log(`  Mapping:  ${join(outDir, 'CRM-UPLOAD-MAPPING.json')}`);
console.log(`  Files:    ${copiedFiles.length}`);
