import { copyFile, mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";

const destination = new URL("../public/media/", import.meta.url);
const repository = new URL("../../", import.meta.url);
const assets = {
  "main-light.png": "docs/assets/screenshots/main-light.png",
  "editor-dark.png": "docs/assets/screenshots/editor-dark.png",
  "download-light.png": "docs/assets/screenshots/download-light.png",
  "main-macos9.png": "docs/assets/screenshots/main-macos9.png",
  "player-dark.png": "docs/assets/player-dark.png",
  "app-64.png": "src/AvaMedia.Desktop/Assets/AppIcon/v2/app-64.png",
  "app-128.png": "src/AvaMedia.Desktop/Assets/AppIcon/v2/app-128.png",
  "app-32.png": "src/AvaMedia.Desktop/Assets/AppIcon/v2/app-32.png",
};

await mkdir(destination, { recursive: true });
await Promise.all(
  Object.entries(assets).map(([name, source]) =>
    copyFile(new URL(source, repository), new URL(name, destination)),
  ),
);
console.log(
  `Prepared ${Object.keys(assets).length} existing AvaMedia assets in ${fileURLToPath(destination)}`,
);
