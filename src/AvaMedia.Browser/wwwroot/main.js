import { dotnet } from './_framework/dotnet.js';
import { McpClient } from './mcp-client.js';
const client = new McpClient();
let stopVisibility = () => {};
const runtime = await dotnet.withDiagnosticTracing(false).create();
runtime.setModuleImports('console', {
  call: async (name, args) => JSON.stringify(await client.call(name, JSON.parse(args))),
  origin: () => location.origin,
  language: () => new URLSearchParams(location.search).get('lang') || localStorage.getItem('avamedia.lang') || navigator.language || 'zh-CN',
  observeVisibility: callback => {
    stopVisibility();
    const changed = () => callback(!document.hidden);
    document.addEventListener('visibilitychange', changed);
    stopVisibility = () => { document.removeEventListener('visibilitychange', changed); callback.dispose(); };
    changed();
  },
  stopObservingVisibility: () => { stopVisibility(); stopVisibility = () => {}; },
  readPending: () => localStorage.getItem('avamedia.pending') || '',
  readDraft: key => localStorage.getItem('avamedia.review.' + key) || '',
  writeDraft: (key, value) => value ? localStorage.setItem('avamedia.review.' + key, value) : localStorage.removeItem('avamedia.review.' + key),
  writePending: value => value ? localStorage.setItem('avamedia.pending', value) : localStorage.removeItem('avamedia.pending'),
  downloadText: (name, text) => {
    const url = URL.createObjectURL(new Blob([text], { type: 'application/x-subrip;charset=utf-8' }));
    const link = document.createElement('a'); link.href = url; link.download = name; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  },
  open: url => { const link = document.createElement('a'); link.href = url; link.target = '_blank'; link.rel = 'noopener'; link.click(); }
});
document.getElementById('out').replaceChildren();
try { await runtime.runMain(runtime.getConfig().mainAssemblyName, [location.href]); }
catch (error) { document.getElementById('out').textContent = `启动失败：${error.message}`; throw error; }
