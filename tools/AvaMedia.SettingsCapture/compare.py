"""Place the approved demo and desktop capture side by side at their native 2x scale."""
from pathlib import Path
from PIL import Image, ImageDraw
import sys

root = Path(sys.argv[1] if len(sys.argv) > 1 else '/workspace/AvaMedia-sidebar-review/settings')
output = root / 'impl'
for implementation in sorted(output.glob('settings-v2-*.png')):
    reference = root / implementation.name
    if not reference.exists():
        continue
    demo = Image.open(reference).convert('RGB')
    actual = Image.open(implementation).convert('RGB')
    if demo.width != actual.width:
        demo = demo.resize((actual.width, round(demo.height * actual.width / demo.width)), Image.Resampling.LANCZOS)
    sheet = Image.new('RGB', (demo.width + actual.width, max(demo.height, actual.height) + 40), '#808080')
    sheet.paste(demo, (0, 40))
    sheet.paste(actual, (demo.width, 40))
    draw = ImageDraw.Draw(sheet)
    draw.text((16, 12), 'DEMO v2', fill='white')
    draw.text((demo.width + 16, 12), 'DESKTOP', fill='white')
    sheet.save(output / ('compare-' + implementation.stem.removeprefix('settings-v2-') + '.png'))
