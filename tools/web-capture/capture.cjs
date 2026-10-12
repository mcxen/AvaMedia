#!/usr/bin/env node
// Avalonia renders to a canvas and exposes neither URL routes nor DOM controls.
// These clicks follow ConsoleView.Create/Tasks/Subtitles and FileBrowserView.
const fs = require('node:fs');
const path = require('node:path');
function playwright() {
  for (const candidate of [process.env.PLAYWRIGHT_MODULE, 'playwright', 'playwright-core', '/workspace/bp-shots/node_modules/playwright'].filter(Boolean)) {
    try { return require(candidate); } catch (e) { if (e.code !== 'MODULE_NOT_FOUND') throw e; }
  }
  throw new Error('Install Playwright or set PLAYWRIGHT_MODULE to its module directory.');
}
const output = process.env.CAPTURE_OUTPUT || '/workspace/AvaMedia-sidebar-review/ai-progress/v2/impl';
const origin = process.env.CAPTURE_URL || 'http://127.0.0.1:8765';
// CSS pixels at 1280 x 800, DPR 1. Overrides let reviewers adjust after UI edits.
const points = {
  createTab: [646, 30], addFiles: [120, 212], pickerFirstRow: [370, 264],
  tasksTab: [525, 30], runningTask: [170, 182], mobileRunningTask: [170, 246],
  ...JSON.parse(process.env.CAPTURE_POINTS || '{}'),
};
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(predicate, label, timeout = 60000) {
  const deadline = Date.now() + timeout;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error(`Timed out waiting for ${label}`);
    await delay(100);
  }
}
async function save(page, name, width, height) {
  await page.mouse.move(width - 2, height - 2); // Keep hover and tooltips out of captures.
  await page.waitForTimeout(1500);
  const png = await page.screenshot({ type: 'png', fullPage: false });
  // Decode the actual PNG, not the GPU canvas (which may clear its drawing buffer).
  const pixels = await page.evaluate(async base64 => {
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const image = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
    const canvas = new OffscreenCanvas(128, 80), ctx = canvas.getContext('2d');
    ctx.drawImage(image, 0, 0, 128, 80);
    const data = ctx.getImageData(0, 0, 128, 80).data, colors = new Set();
    let dark = 0, blue = 0;
    for (let i = 0; i < data.length; i += 4) {
      const [r, g, b] = data.slice(i, i + 3);
      colors.add(`${r >> 4},${g >> 4},${b >> 4}`);
      if (r + g + b < 510) dark++;
      if (b > r + 40 && b > g + 15) blue++;
    }
    return { width: image.width, height: image.height, colors: colors.size, dark, blue };
  }, png.toString('base64'));
  if (pixels.width !== width || pixels.height !== height || pixels.colors < 20 || pixels.dark < 25 || pixels.blue < 5)
    throw new Error(`Blank or unexpected screenshot ${name}: ${JSON.stringify(pixels)}`);
  const target = path.join(output, name + '.png');
  fs.writeFileSync(target, png);
  console.log(`${target} (${width}x${height}, ${png.length} bytes)`);
  return { name, ...pixels };
}
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await playwright().chromium.launch({ headless: true,
    executablePath: process.env.CHROME_PATH || (fs.existsSync('/usr/bin/google-chrome') ? '/usr/bin/google-chrome' : undefined),
    args: ['--no-sandbox', '--enable-unsafe-swiftshader', '--force-device-scale-factor=1'] });
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, locale: 'zh-CN', deviceScaleFactor: 1 });
    const page = await context.newPage();
    const errors = [], calls = {}, endpoints = {};
    page.on('pageerror', e => errors.push(e.stack || e.message));
    page.on('response', async response => {
      try {
        const url = new URL(response.url());
        if (url.pathname === '/mcp') {
          const body = await response.json();
          if (body.error || body.result?.isError) errors.push(JSON.stringify(body));
          const name = response.request().postDataJSON()?.params?.name;
          if (name) calls[name] = (calls[name] || 0) + 1;
        } else if (url.pathname.includes('/ai/')) {
          if (!response.ok()) errors.push(`${url.pathname}: HTTP ${response.status()}`);
          else endpoints[url.pathname.split('/').pop()] = await response.json();
        }
      } catch (e) { errors.push(e.message); }
    });
    const click = async name => { await page.mouse.click(...points[name]); await page.waitForTimeout(500); };
    await page.goto(origin + '/?lang=zh-CN');
    await until(() => calls.avamedia_list_tasks, 'initial task list');
    await page.waitForTimeout(1500);

    // The fixture exposes only auto-subtitle, so the creation tab selects it.
    await click('createTab');
    // Add each server file through the real picker. Keep demo input order and
    // avoid platform-dependent multi-selection keyboard gestures on the canvas.
    for (const [addY, rowOffset] of [[points.addFiles[1], 52], [256, 26], [312, 0]]) {
      const browseCount = calls.avamedia_browse_directory || 0;
      await page.mouse.click(points.addFiles[0], addY);
      await until(() => (calls.avamedia_browse_directory || 0) > browseCount, 'subtitle server file picker', 10000);
      await page.waitForTimeout(500);
      await page.mouse.click(points.pickerFirstRow[0], points.pickerFirstRow[1] + rowOffset);
      await page.keyboard.press('Enter');
      await page.waitForTimeout(500);
    }
    const captures = [await save(page, 'subtitle-submit-web', 1280, 800)];

    await click('tasksTab');
    await click('runningTask');
    await until(() => endpoints.progress && endpoints.results, 'subtitle progress and cues');
    if (endpoints.progress.speech.currentSegment !== 2 || endpoints.progress.speech.segments.length !== 6 ||
        endpoints.results.total !== 186 || endpoints.progress.queue.subtitleJobsWaiting !== 2)
      throw new Error('Unexpected subtitle fixture data');
    captures.push(await save(page, 'subtitle-progress-web', 1280, 800));
    // Open at the mobile viewport, then tap the task card to enter the full-page detail.
    const taskPolls = calls.avamedia_list_tasks || 0;
    await page.setViewportSize({ width: 390, height: 844 });
    await page.reload();
    await until(() => (calls.avamedia_list_tasks || 0) > taskPolls, 'mobile task list');
    await page.waitForTimeout(1500);
    const detailReads = calls.avamedia_get_task || 0;
    await page.mouse.click(...points.mobileRunningTask);
    await until(() => (calls.avamedia_get_task || 0) > detailReads, 'mobile task detail');
    captures.push(await save(page, 'subtitle-progress-mobile', 390, 844));
    if (errors.length) throw new Error(errors.join('\n'));
    fs.writeFileSync(path.join(output, 'capture-report.json'), JSON.stringify({ locale: 'zh-CN', calls,
      segment: '3/6', cues: 186, waiting: 2, captures, visualReview: 'Required: open all three PNGs to confirm content and layout.' }, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
