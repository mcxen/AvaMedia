const english = {
  skip: "Skip to content",
  brandSub: "Desktop media toolkit",
  navLabel: "Main navigation",
  navFeatures: "Features",
  navWorkspace: "Workspace",
  navDownload: "Download",
  heroEyebrow: "Windows x64 · macOS ARM64",
  heroLine1: "AvaMedia",
  heroLine2: "Media toolkit",
  heroDescription:
    "Open-source tools for video conversion, compression, editing, playback and downloads, plus audio, image and PDF processing.",
  heroDownload: "Download AvaMedia",
  heroExplore: "View screenshots",
  heroNote: "Includes FFmpeg / FFprobe, yt-dlp and QuickJS-NG.",
  heroArtLabel: "AvaMedia Quick Clip interface",
  heroWindow: "Quick Clip · AvaMedia",
  mediaLabel: "Media categories",
  stripEnd: "Batch processing",
  featuresEyebrow: "01 / FEATURES",
  featuresTitle: "Features",
  videoTitle: "Video conversion & compression",
  videoText:
    "Choose quality, bitrate or target size. Use hardware transcoding and convert iPhone HDR to SDR.",
  editTitle: "Edit & batch process",
  editText:
    "Crop, rotate, change playback speed, split videos and export multiple clips. Process multiple files in batches.",
  editTag1: "MULTIPLE CLIPS",
  editTag2: "BATCH CROP",
  audioTitle: "Audio tracks & subtitles",
  audioText:
    "Extract audio, mux video and keep multiple tracks. Adjust channels and volume. Burn in or mux subtitles.",
  audioTag1: "TRACKS",
  audioTag2: "MUX",
  audioTag3: "SUBTITLES",
  imageTitle: "Image conversion & compression",
  imageText:
    "Convert HEIC. Compress JPEG, WebP and PNG. Compare encoded sizes, save only smaller results and keep originals.",
  pdfTitle: "PDF processing",
  pdfText:
    "Preview, select, reorder, rotate, split and merge pages. Turn images and text into a PDF.",
  pdfTag1: "PAGE PREVIEWS",
  pdfTag2: "SPLIT & MERGE",
  pdfTag3: "EXTRACT TEXT",
  downloadTitle: "Video downloads",
  downloadText:
    "Paste shared text or a batch of links. Choose quality, video, audio and subtitles. Download playlists and retry failures.",
  downloadTag1: "BATCH URLS",
  downloadTag2: "PLAYLISTS",
  workspaceEyebrow: "02 / INTERFACE",
  workspaceTitle: "App screenshots",
  workspaceIntro: "Light, Dark and Mac OS 9 Platinum themes.",
  galleryLabel: "App screenshots",
  tabMain: "Main workspace",
  tabEditor: "Quick Clip",
  tabDownload: "Downloads",
  tabPlayer: "Tianchi Player",
  enlarge: "Enlarge the current screenshot",
  zoom: "View full size",
  mainAlt:
    "AvaMedia Light main workspace with tool categories and a task queue",
  editorAlt: "Quick Clip with a preview, timeline and clip editing controls",
  downloadAlt:
    "Video downloads with URL parsing, quality, subtitle and login options",
  playerAlt:
    "Tianchi Player with its video area, timeline and compact playback controls",
  classicAlt: "AvaMedia main workspace with the Mac OS 9 Platinum skin",
  mainCaption:
    "The main workspace contains tool categories, the task queue, progress and logs.",
  editorCaption:
    "Preview and edit clips, then set export options. Each clip is exported as a separate file.",
  downloadCaption:
    "Parse links, pick quality, audio and subtitles, then add downloads to the queue.",
  playerCaption:
    "A separate player with fullscreen, playback speed, frame stepping and keyboard shortcuts.",
  classicCaption: "The main workspace with the Mac OS 9 Platinum theme.",
  screenshotNote: "Actual client screenshots · Chinese interface",
  flowEyebrow: "03 / TASK QUEUE",
  flowTitle: "How to use AvaMedia",
  flowIntro:
    "The task queue supports parallel processing, progress tracking, parameter editing and retries.",
  step1Title: "Add files",
  step1Text:
    "Drop files to select tools for their media type, or select a tool and add files.",
  step2Title: "Set parameters",
  step2Text:
    "Choose format, quality and clips. Pick a destination and add your jobs to the queue.",
  step3Title: "Run tasks",
  step3Text:
    "Process jobs in parallel. Read logs, stop a job, edit its settings or retry failed items.",
  playerTitle: "Tianchi Player",
  playerText:
    "Supports fullscreen, playback speed, track selection, frame stepping and snapshots, with playlists and keyboard shortcuts.",
  wifiTitle: "WiFi file transfer",
  wifiText:
    "On the same local network, scan a QR code to upload files in your phone’s browser or download files shared by the computer.",
  wifiNote: "No phone app needed",
  downloadHeading: "Download AvaMedia",
  downloadIntro: "Select the package for your operating system.",
  releaseFallback: "View the latest release ↗",
  recommended: "For your current device",
  windowsDetails: "x64 · Installer & portable edition",
  windowsDownload: "Download for Windows",
  portableDownload: "Download portable ZIP",
  macDetails: "Apple Silicon · ARM64 · macOS 13.4+",
  macDownload: "Download macOS DMG",
  macHelp: "First-time installation",
  downloadFootnote:
    "Under active development. Checksums and media-engine sources are in the release notes.",
  faqHeading: "Frequently asked questions",
  readDocs: "Read the documentation",
  faqFreeQ: "Is AvaMedia open source?",
  faqFreeA:
    "Original code uses the AGPL-3.0-only license. The source code and issue tracker are on GitHub.",
  faqEnginesQ: "Do I need to install media engines?",
  faqEnginesA:
    "Release packages include the .NET runtime, FFmpeg / FFprobe, yt-dlp and QuickJS-NG. No separate SDK, Python or media engine installation is required.",
  faqMacQ: "What should I know about the first macOS launch?",
  faqMacA:
    "Open the DMG and drag “天池万象转换.app” into Applications. Packages target Apple Silicon ARM64 and declare macOS 13.4 as the minimum version. The app uses ad-hoc signing and has not been Developer ID signed or notarized. You may need to allow it in System Settings → Privacy & Security on first launch.",
  faqLimitsQ: "Are there practical limits?",
  faqLimitsA:
    "Fast Copy clip starts depend on keyframes. HEIC compression processes the primary still image. PDF text extraction does not reconstruct the original layout. Downloads depend on websites, accounts and network conditions. See the feature notes for scope and verification status.",
  featureNotes: "Read the feature notes ↗",
  issues: "Report an issue ↗",
  footerTagline: "Open-source media tools for Windows and macOS",
  closeScreenshot: "Close full-size screenshot",
};

const views = {
  main: {
    file: "main-light.png",
    size: [1440, 836],
    title: "tabMain",
    alt: "mainAlt",
    caption: "mainCaption",
  },
  editor: {
    file: "editor-dark.png",
    size: [1400, 836],
    title: "tabEditor",
    alt: "editorAlt",
    caption: "editorCaption",
  },
  download: {
    file: "download-light.png",
    size: [1120, 836],
    title: "tabDownload",
    alt: "downloadAlt",
    caption: "downloadCaption",
  },
  player: {
    file: "player-dark.png",
    size: [1100, 720],
    title: "tabPlayer",
    alt: "playerAlt",
    caption: "playerCaption",
  },
  classic: {
    file: "main-macos9.png",
    size: [1440, 860],
    title: "tabClassic",
    alt: "classicAlt",
    caption: "classicCaption",
  },
};
const chinese = {
  tabClassic: "Mac OS 9",
  downloadAlt: "视频下载的链接解析、画质、字幕与登录选项",
  playerAlt: "天池播放器：视频画面、时间轴与紧凑播放控制栏",
  classicAlt: "AvaMedia Mac OS 9 Platinum 皮肤的主工作区",
  editorCaption: "预览和编辑片段后设置导出参数，每个片段生成独立文件。",
  downloadCaption: "解析链接后选择画质、音视频与字幕，再加入任务队列。",
  playerCaption: "独立播放窗口。全屏、倍速、逐帧定位与常用快捷键。",
  classicCaption: "使用 Mac OS 9 Platinum 皮肤的主工作区。",
};
english.tabClassic = "Mac OS 9";

// Chinese content lives in HTML, so the page remains useful without JavaScript.
for (const [attribute, target] of [
  ["data-i18n", null],
  ["data-i18n-alt", "alt"],
  ["data-i18n-aria", "aria-label"],
]) {
  document.querySelectorAll(`[${attribute}]`).forEach((element) => {
    chinese[element.getAttribute(attribute)] ??= target
      ? element.getAttribute(target)
      : element.textContent;
  });
}
document
  .querySelectorAll("svg:not(.svg-library)")
  .forEach((icon) => icon.setAttribute("aria-hidden", "true"));

const root = document.documentElement;
const languageButton = document.querySelector("#language");
const image = document.querySelector("#gallery-image");
const panel = document.querySelector("#gallery-panel");
const description = document.querySelector("#gallery-description");
const tabs = [...document.querySelectorAll("[data-view]")];
const screenshotButton = document.querySelector("#screenshot-open");
const dialog = document.querySelector("#screenshot-dialog");
const fullImage = document.querySelector("#screenshot-full");
const screenshotTitle = document.querySelector("#screenshot-title");
const releaseLabel = document.querySelector("#release-label");
const mediaUrl = (file) => `${import.meta.env.BASE_URL}media/${file}`;
let language = "zh";
let currentView = "main";
let releaseVersion = "";

function translate(key) {
  return (language === "en" ? english : chinese)[key] ?? chinese[key] ?? key;
}

function updateReleaseLabel() {
  releaseLabel.textContent = releaseVersion
    ? `${language === "en" ? "Latest release" : "最新版本"} · ${releaseVersion} ↗`
    : translate("releaseFallback");
}

function setLanguage(next, persist = false) {
  language = next === "en" ? "en" : "zh";
  root.lang = language === "en" ? "en" : "zh-CN";
  for (const [attribute, target] of [
    ["data-i18n", null],
    ["data-i18n-alt", "alt"],
    ["data-i18n-aria", "aria-label"],
  ]) {
    document.querySelectorAll(`[${attribute}]`).forEach((element) => {
      const text = translate(element.getAttribute(attribute));
      if (target) element.setAttribute(target, text);
      else element.textContent = text;
    });
  }
  languageButton.textContent = language === "en" ? "中文" : "EN";
  languageButton.setAttribute(
    "aria-label",
    language === "en" ? "切换到简体中文" : "Switch to English",
  );
  document.title =
    language === "en"
      ? "AvaMedia · Open-source media toolkit"
      : "AvaMedia · 开源媒体工具";
  document.querySelector('meta[property="og:title"]').content = document.title;
  const summary =
    language === "en"
      ? "AvaMedia: an open-source Windows and macOS media toolkit. Convert, compress, edit, play and download media, and work with images and PDFs."
      : "AvaMedia 天池万象转换：Windows 与 macOS 开源媒体工具。视频转换、压缩、剪辑、播放、下载，以及图片和 PDF 处理。";
  document.querySelector('meta[name="description"]').content = summary;
  document.querySelector('meta[property="og:description"]').content = summary;
  updateReleaseLabel();
  if (dialog.open) fullImage.alt = translate(views[currentView].alt);
  if (persist) {
    try {
      localStorage.setItem("avamedia-language", language);
    } catch {
      /* Storage can be unavailable. */
    }
    const url = new URL(location.href);
    url.searchParams.set("lang", language);
    history.replaceState(null, "", url);
  }
}

languageButton.addEventListener("click", () =>
  setLanguage(language === "en" ? "zh" : "en", true),
);
const requestedLanguage = new URLSearchParams(location.search).get("lang");
let savedLanguage;
try {
  savedLanguage = localStorage.getItem("avamedia-language");
} catch {
  /* Use the default language. */
}
setLanguage(
  requestedLanguage === "en" || requestedLanguage === "zh"
    ? requestedLanguage
    : savedLanguage,
);

function selectView(name) {
  const view = views[name];
  if (!view) return;
  currentView = name;
  tabs.forEach((tab) => {
    const selected = tab.dataset.view === name;
    tab.setAttribute("aria-selected", String(selected));
    tab.tabIndex = selected ? 0 : -1;
  });
  panel.setAttribute("aria-labelledby", `tab-${name}`);
  image.src = mediaUrl(view.file);
  image.width = view.size[0];
  image.height = view.size[1];
  image.dataset.i18nAlt = view.alt;
  image.alt = translate(view.alt);
  description.dataset.i18n = view.caption;
  description.textContent = translate(view.caption);
  screenshotTitle.dataset.i18n = view.title;
  screenshotTitle.textContent = translate(view.title);
}
tabs.forEach((tab, index) => {
  tab.addEventListener("click", () => selectView(tab.dataset.view));
  tab.addEventListener("keydown", (event) => {
    let next;
    if (event.key === "ArrowRight") next = (index + 1) % tabs.length;
    if (event.key === "ArrowLeft")
      next = (index - 1 + tabs.length) % tabs.length;
    if (event.key === "Home") next = 0;
    if (event.key === "End") next = tabs.length - 1;
    if (next === undefined) return;
    event.preventDefault();
    selectView(tabs[next].dataset.view);
    tabs[next].focus({ preventScroll: true });
  });
});

screenshotButton.addEventListener("click", () => {
  const view = views[currentView];
  fullImage.src = mediaUrl(view.file);
  fullImage.alt = translate(view.alt);
  fullImage.width = view.size[0];
  fullImage.height = view.size[1];
  document.body.classList.add("modal-open");
  dialog.showModal();
});
document
  .querySelector("#screenshot-close")
  .addEventListener("click", () => dialog.close());
dialog.addEventListener("click", (event) => {
  if (event.target !== dialog) return;
  const box = dialog.getBoundingClientRect();
  if (
    event.clientX < box.left ||
    event.clientX > box.right ||
    event.clientY < box.top ||
    event.clientY > box.bottom
  )
    dialog.close();
});
dialog.addEventListener("close", () => {
  document.body.classList.remove("modal-open");
  screenshotButton.focus({ preventScroll: true });
});

// Reveal once; keyboard navigation and reduced motion stay immediate.
document.addEventListener(
  "keydown",
  () => root.classList.add("keyboard-navigation"),
  { capture: true },
);
document.addEventListener(
  "pointerdown",
  () => root.classList.remove("keyboard-navigation"),
  { capture: true },
);
const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
if (!reducedMotion.matches && "IntersectionObserver" in window) {
  const observer = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        entry.target.classList.remove("is-pending");
        observer.unobserve(entry.target);
      });
    },
    { rootMargin: "0px 0px -24px 0px", threshold: 0.08 },
  );
  document.querySelectorAll(".reveal").forEach((element) => {
    if (element.getBoundingClientRect().top > innerHeight) {
      element.classList.add("is-pending");
      observer.observe(element);
    }
  });
  document.addEventListener("focusin", (event) =>
    event.target.closest(".reveal")?.classList.remove("is-pending"),
  );
  reducedMotion.addEventListener("change", (event) => {
    if (!event.matches) return;
    observer.disconnect();
    document
      .querySelectorAll(".is-pending")
      .forEach((element) => element.classList.remove("is-pending"));
  });
}

// Recommendation is only a visual hint; every platform remains available.
const platform = navigator.userAgentData?.platform ?? navigator.platform;
const recommended = /Win/i.test(platform)
  ? "windows"
  : /Mac/i.test(platform) && navigator.maxTouchPoints < 2
    ? "macos"
    : null;
if (recommended) {
  const card = document.querySelector(`[data-platform="${recommended}"]`);
  card.classList.add("is-recommended");
  card.querySelector(".recommended").hidden = false;
}

async function resolveDownloads() {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 7000);
  try {
    const response = await fetch(
      "https://api.github.com/repos/mcxen/AvaMedia/releases/latest",
      { signal: controller.signal },
    );
    if (!response.ok) return;
    const release = await response.json();
    if (
      release.draft ||
      release.prerelease ||
      !/^v\d+\.\d+\.\d+$/.test(release.tag_name) ||
      !Array.isArray(release.assets)
    )
      return;
    const suffixes = {
      windows: "win-x64-setup.exe",
      portable: "win-x64-portable.zip",
      macos: "osx-arm64.dmg",
    };
    for (const [key, suffix] of Object.entries(suffixes)) {
      const name = `AvaMedia-${release.tag_name.slice(1)}-${suffix}`;
      const asset = release.assets.find(
        (item) =>
          item.name === name && item.state === "uploaded" && item.size > 0,
      );
      if (typeof asset?.browser_download_url !== "string") continue;
      const assetUrl = new URL(asset.browser_download_url);
      if (
        assetUrl.origin !== "https://github.com" ||
        !assetUrl.pathname.startsWith(
          `/mcxen/AvaMedia/releases/download/${release.tag_name}/`,
        )
      )
        continue;
      document.querySelector(`[data-asset="${key}"]`).href = assetUrl.href;
    }
    releaseVersion = release.tag_name;
    updateReleaseLabel();
  } catch {
    /* GitHub can be unavailable or rate limited; the release-page links still work. */
  } finally {
    clearTimeout(timeout);
  }
}
resolveDownloads();
