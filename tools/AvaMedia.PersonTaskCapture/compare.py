"""Create side-by-side person task review images without cropping either UI."""
from pathlib import Path
import sys
from PIL import Image, ImageDraw

root = Path(sys.argv[1])
out = root / 'impl'

def normalized(path, width=1000):
    image = Image.open(path).convert('RGB')
    return image.resize((width, round(image.height * width / image.width)), Image.Resampling.LANCZOS)

def comparison(left, right, filename):
    width = left.width + right.width + 36
    canvas = Image.new('RGB', (width, max(left.height, right.height) + 48), '#F2F3F7')
    draw = ImageDraw.Draw(canvas)
    draw.text((12, 10), 'Demo', fill='#1D1D1F')
    draw.text((left.width + 24, 10), 'Implementation', fill='#1D1D1F')
    canvas.paste(left, (12, 36))
    canvas.paste(right, (left.width + 24, 36))
    canvas.save(out / filename)

for theme in ('light', 'dark'):
    comparison(normalized(root / f'demo-b-{theme}.png'),
               normalized(out / f'completed-{theme}.png'), f'compare-{theme}.png')
running = normalized(out / 'running-light.png', 640)
failed = normalized(out / 'failed-light.png', 640)
states = Image.new('RGB', (1300, max(running.height, failed.height)), '#F2F3F7')
states.paste(running, (0, 0))
states.paste(failed, (660, 0))
comparison(normalized(root / 'states.png', 1300), states, 'compare-states.png')
