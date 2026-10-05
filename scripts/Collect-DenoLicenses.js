// Preserve license/notice texts from the exact upstream dependency archives.
// Includes all locked crates (a conservative superset of each target's build).
const [lockPath, destination] = Deno.args;
const lock = await Deno.readTextFile(lockPath);
const packages = lock.split('[[package]]').slice(1).map(block => {
  const field = name => block.match(new RegExp(`^${name} = "([^"]+)"`, 'm'))?.[1];
  return {name: field('name'), version: field('version'), source: field('source'), checksum: field('checksum')};
}).filter(p => p.source?.startsWith('registry+') && p.checksum);
const decoder = new TextDecoder();
const hex = bytes => [...new Uint8Array(bytes)].map(b => b.toString(16).padStart(2, '0')).join('');
function notices(tar) {
  const result = [];
  for (let offset = 0; offset + 512 <= tar.length;) {
    const header = tar.subarray(offset, offset + 512);
    if (header.every(b => b === 0)) break;
    const str = (from, to) => decoder.decode(header.subarray(from, to)).split('\0')[0];
    const name = str(0, 100), prefix = str(345, 500);
    const path = prefix ? prefix + '/' + name : name;
    const size = parseInt(str(124, 136).trim(), 8) || 0;
    if (size > 0 && header[156] !== 53 && /(^|\/)(licen[sc]e|copying|notice|third[-_ ]?party[-_ ]?(notices|licenses)|copyright)([._-]|$)/i.test(path)) {
      const data = tar.subarray(offset + 512, offset + 512 + size);
      if (!data.includes(0)) result.push({path, text: decoder.decode(data)});
    }
    offset += 512 + Math.ceil(size / 512) * 512;
  }
  return result;
}
const results = new Array(packages.length);let next = 0;
await Promise.all(Array.from({length: 6}, async () => {
  while (next < packages.length) {
    const index = next++, p = packages[index];
    const url = `https://static.crates.io/crates/${p.name}/${p.name}-${p.version}.crate`;
    let response;
    for (let attempt = 0; attempt < 3; attempt++) {
      try { response = await fetch(url, {signal: AbortSignal.timeout(60000)});if(response.ok) break; } catch (error) { if(attempt === 2) throw error; }
    }
    if (!response?.ok) throw new Error(`Dependency archive unavailable: ${url}`);
    const archive = await response.arrayBuffer();
    if (hex(await crypto.subtle.digest('SHA-256', archive)) !== p.checksum) throw new Error(`Dependency checksum mismatch: ${url}`);
    const tar = new Uint8Array(await new Response(new Blob([archive]).stream().pipeThrough(new DecompressionStream('gzip'))).arrayBuffer());
    const files = notices(tar);
    results[index] = `\n=== ${p.name} ${p.version} ===\nSource: ${url}\nSHA256: ${p.checksum}\n` + files.map(f => `\n--- ${f.path} ---\n${f.text}\n`).join('');
    if (files.length === 0) results[index] += 'No separate notice text in upstream archive; consult its Cargo.toml SPDX declaration and corresponding source.\n';
  }
}));
await Deno.writeTextFile(destination, 'Deno dependency notices from SHA256-verified upstream Cargo.lock archives.\n' + results.join(''));
console.log(`Collected notices from ${packages.length} verified Deno dependency archives.`);
