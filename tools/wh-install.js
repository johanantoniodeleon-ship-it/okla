// okla · instalador de mods de Windhawk (hace lo mismo que el botón "Instalar" de la app de Windhawk).
// Uso (como administrador):  set ELECTRON_RUN_AS_NODE=1 & "C:\Program Files\Windhawk\UI\VSCodium.exe" wh-install.js <mod-id> [<mod-id> ...]
// Los mods quedan INSTALADOS PERO DESACTIVADOS: okla los enciende según el modo.

const fs = require('fs');
const path = require('path');
const https = require('https');
const { spawn, execFileSync } = require('child_process');
const yaml = require('./lib/js-yaml.js');

// VSCodium de Windhawk es de 32 bits: para él ProgramFiles es "Program Files (x86)". ProgramW6432 es la carpeta de 64 bits.
const WH = path.join(process.env.ProgramW6432 || process.env.ProgramFiles || 'C:\\Program Files', 'Windhawk');
const COMPILER = path.join(WH, 'Compiler');
const DATA = path.join(process.env.ProgramData || 'C:\\ProgramData', 'Windhawk');
// El motor está en Engine\<versión>: se usa la más nueva instalada.
const ENGINE = (() => {
  const dir = path.join(WH, 'Engine');
  const v = fs.readdirSync(dir).filter(d => /^\d+(\.\d+)*$/.test(d))
    .sort((a, b) => b.split('.').map(Number).reduce((r, x, i) => r || x - (a.split('.').map(Number)[i] || 0), 0));
  if (!v.length) throw new Error('No encuentro el motor de Windhawk en ' + dir);
  return path.join(dir, v[0]);
})();
const REG = 'HKLM\\SOFTWARE\\Windhawk\\Engine\\Mods';

function get(url) {
  return new Promise((ok, fail) => {
    https.get(url, { headers: { 'User-Agent': 'okla-mod-installer' } }, r => {
      if (r.statusCode >= 300 && r.statusCode < 400 && r.headers.location) return ok(get(r.headers.location));
      if (r.statusCode !== 200) return fail(new Error(`HTTP ${r.statusCode} ${url}`));
      const chunks = []; r.on('data', c => chunks.push(c)); r.on('end', () => ok(Buffer.concat(chunks).toString('utf8')));
    }).on('error', fail);
  });
}

function metadata(src) {
  const m = src.match(/^\/\/[ \t]+==WindhawkMod==[ \t]*$([\s\S]+?)^\/\/[ \t]+==\/WindhawkMod==[ \t]*$/m);
  if (!m) throw new Error('sin bloque ==WindhawkMod==');
  const meta = { include: [], exclude: [], architecture: [] };
  for (const line of m[1].split(/\r?\n/)) {
    const k = line.match(/^\/\/[ \t]+@([a-zA-Z]+)(:[a-zA-Z-]+)?[ \t]+(.*?)[ \t]*$/);
    if (!k || k[2]) continue; // ignoro las traducciones (@name:es ...)
    if (['include', 'exclude', 'architecture'].includes(k[1])) meta[k[1]].push(k[3]);
    else meta[k[1]] = k[3];
  }
  return meta;
}

// Convierte el YAML de ajustes de Windhawk a los valores planos que guarda en el registro.
function flatten(items, prefix, out) {
  for (const item of items || []) {
    const key = Object.keys(item).find(k => !k.startsWith('$'));
    if (key === undefined) continue;
    put(item[key], prefix + key, out);
  }
}
function put(val, name, out) {
  if (Array.isArray(val)) {
    if (val.length && val.every(v => v && typeof v === 'object' && !Array.isArray(v))) return flatten(val, name + '.', out); // grupo
    val.forEach((v, i) => {
      if (Array.isArray(v)) flatten(v, `${name}[${i}].`, out);       // lista de objetos
      else put(v, `${name}[${i}]`, out);                               // lista simple
    });
    return;
  }
  if (typeof val === 'boolean') out[name] = { t: 'REG_DWORD', v: val ? 1 : 0 };
  else if (typeof val === 'number' && Number.isInteger(val)) out[name] = { t: 'REG_DWORD', v: val >>> 0 }; // -1 -> 0xFFFFFFFF, como Windhawk
  else out[name] = { t: 'REG_SZ', v: val == null ? '' : String(val) };
}
function settings(src) {
  const m = src.match(/^\/\/[ \t]+==WindhawkModSettings==[ \t]*$\s*\/\*\s*([\s\S]+?)\s*\*\/\s*^\/\/[ \t]+==\/WindhawkModSettings==[ \t]*$/m);
  const out = {};
  if (m) flatten(yaml.load(m[1]), '', out);
  return out;
}

function compile(id, version, src, opts, target, sub, outFile) {
  const whLib = path.join(ENGINE, sub, 'windhawk.lib');
  const args = ['-std=c++23', '-O2', '-shared', '-DUNICODE', '-D_UNICODE',
    '-DWINVER=0x0A00', '-D_WIN32_WINNT=0x0A00', '-D_WIN32_IE=0x0A00', '-DNTDDI_VERSION=0x0A000008',
    '-D__USE_MINGW_ANSI_STDIO=0', '-DWH_MOD', `-DWH_MOD_ID=L"${id}"`, `-DWH_MOD_VERSION=L"${version}"`,
    whLib, '-x', 'c++', '-', '-include', 'windhawk_api.h', '-target', target, '-Wl,--export-all-symbols', '-o', outFile,
    ...opts];
  return new Promise((ok, fail) => {
    const p = spawn(path.join(COMPILER, 'bin', 'clang++.exe'), args, { cwd: COMPILER });
    let err = ''; p.stderr.on('data', d => err += d);
    p.on('close', code => code === 0 ? ok() : fail(new Error(err.split('\n').filter(l => /error/.test(l)).slice(0, 4).join('\n') || `clang salió con ${code}`)));
    p.stdin.end(src);
  });
}

// Vista de 64 bits explícita: VSCodium corre como proceso de 32 bits y si no, escribe en WOW6432Node.
const REG_EXE = fs.existsSync('C:\\Windows\\Sysnative\\reg.exe') ? 'C:\\Windows\\Sysnative\\reg.exe' : 'C:\\Windows\\System32\\reg.exe';
function reg(...a) { execFileSync(REG_EXE, [...a, '/reg:64'], { stdio: 'pipe' }); }

async function install(id) {
  const src = await get(`https://mods.windhawk.net/mods/${id}.wh.cpp`);
  const meta = metadata(src);
  const version = meta.version || '1.0';
  const archs = meta.architecture.length ? meta.architecture : ['x86', 'x86-64'];
  const targets = [];
  if (archs.includes('x86')) targets.push(['i686-w64-mingw32', '32']);
  if (archs.includes('x86-64') || archs.includes('amd64')) targets.push(['x86_64-w64-mingw32', '64']);
  const opts = (meta.compilerOptions || '').split(/\s+/).filter(Boolean);
  const lib = `${id}_${version}_${Math.floor(100000 + Math.random() * 900000)}.dll`;

  // En un Windhawk recién instalado estas carpetas todavía no existen.
  for (const [, sub] of targets) fs.mkdirSync(path.join(DATA, 'Engine', 'Mods', sub), { recursive: true });
  fs.mkdirSync(path.join(DATA, 'ModsSource'), { recursive: true });

  for (const [target, sub] of targets)
    await compile(id, version, src, opts, target, sub, path.join(DATA, 'Engine', 'Mods', sub, lib));

  fs.writeFileSync(path.join(DATA, 'ModsSource', `${id}.wh.cpp`), src);

  const key = `${REG}\\${id}`;
  const vals = settings(src);
  for (const [n, { t, v }] of Object.entries(vals)) reg('add', `${key}\\Settings`, '/v', n, '/t', t, '/d', String(v), '/f');
  reg('add', key, '/v', 'Disabled', '/t', 'REG_DWORD', '/d', '1', '/f');
  reg('add', key, '/v', 'Include', '/t', 'REG_SZ', '/d', meta.include.join('|'), '/f');
  reg('add', key, '/v', 'Exclude', '/t', 'REG_SZ', '/d', meta.exclude.join('|'), '/f');
  reg('add', key, '/v', 'Architecture', '/t', 'REG_SZ', '/d', meta.architecture.join('|'), '/f');
  reg('add', key, '/v', 'Version', '/t', 'REG_SZ', '/d', version, '/f');
  reg('add', key, '/v', 'SettingsChangeTime', '/t', 'REG_DWORD', '/d', String(Math.floor(Date.now() / 1000)), '/f');
  reg('add', key, '/v', 'LibraryFileName', '/t', 'REG_SZ', '/d', lib, '/f');
  return `${meta.name || id} ${version} · ${targets.map(t => t[1] + ' bits').join(' + ')} · ${Object.keys(vals).length} ajustes`;
}

(async () => {
  let fallos = 0;
  for (const id of process.argv.slice(2)) {
    const t0 = Date.now();
    try { console.log(`OK    ${id}: ${await install(id)} (${((Date.now() - t0) / 1000).toFixed(1)} s)`); }
    catch (e) { fallos++; console.log(`FALLO ${id}: ${e.message}`); }
  }
  process.exit(fallos ? 1 : 0);
})();
