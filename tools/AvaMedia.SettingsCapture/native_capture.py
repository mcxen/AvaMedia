"""Capture the displayed settings window without Avalonia's offscreen renderer."""
import os
import sys
from PIL import ImageGrab

path, left, top, width, height = sys.argv[1:]
left, top, width, height = map(int, (left, top, width, height))
ImageGrab.grab(bbox=(left, top, left + width, top + height), xdisplay=os.environ['DISPLAY']).save(path)
