"""在相同 1x 逻辑尺寸下并排展示 demo（左）与实现（右），不裁切组件。"""
import argparse
from pathlib import Path
from PIL import Image
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('root',type=Path,nargs='?',default=Path('/workspace/AvaMedia-sidebar-review/components'))
a=parser.parse_args()
for theme in ['light','dark']:
    pages=[]
    for i in range(1,13):
        name=f's{i:02d}-{theme}'
        left=Image.open(a.root/'sections'/f'{name}.png').convert('RGB')
        left=left.resize((left.width//2,round(left.height/2)),Image.Resampling.LANCZOS)
        right=Image.open(a.root/'impl'/f'{name}.png').convert('RGB')
        paired=Image.new('RGB',(left.width+right.width,max(left.height,right.height)), '#1C1C1E' if theme=='dark' else '#F2F3F7')
        paired.paste(left,(0,0));paired.paste(right,(left.width,0));paired.save(a.root/'impl'/f'compare-{name}.png')
        pages.append(right)
    gallery=Image.new('RGB',(pages[0].width,sum(p.height+16 for p in pages)), '#1C1C1E' if theme=='dark' else '#F2F3F7')
    y=0
    for page in pages:gallery.paste(page,(0,y));y+=page.height+16
    gallery.save(a.root/'impl'/f'gallery-{theme}.png')
