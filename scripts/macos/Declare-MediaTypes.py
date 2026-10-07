#!/usr/bin/env python3
"""Declare the existing macOS application as a viewer for supported media."""
import argparse
import ctypes
import plistlib
import re
from pathlib import Path


def media_types(extensions):
    core = ctypes.CDLL('/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation')
    types = ctypes.CDLL('/System/Library/Frameworks/CoreServices.framework/CoreServices')
    core.CFStringCreateWithCString.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_uint32]
    core.CFStringCreateWithCString.restype = ctypes.c_void_p
    core.CFStringGetCString.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_long, ctypes.c_uint32]
    core.CFStringGetCString.restype = ctypes.c_bool
    core.CFRelease.argtypes = [ctypes.c_void_p]
    types.UTTypeCreatePreferredIdentifierForTag.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    types.UTTypeCreatePreferredIdentifierForTag.restype = ctypes.c_void_p
    utf8 = 0x08000100
    tag = core.CFStringCreateWithCString(None, b'public.filename-extension', utf8)
    identifiers, imported = [], {}
    try:
        for extension, parent in extensions:
            value = core.CFStringCreateWithCString(None, extension.encode(), utf8)
            conforming = core.CFStringCreateWithCString(None, parent.encode(), utf8)
            identifier = types.UTTypeCreatePreferredIdentifierForTag(tag, value, conforming)
            try:
                buffer = ctypes.create_string_buffer(1024)
                if not identifier or not core.CFStringGetCString(identifier, buffer, len(buffer), utf8):
                    raise RuntimeError(f'Cannot resolve the content type for .{extension}')
                content_type = buffer.value.decode()
                if content_type.startswith('dyn.'):
                    content_type = f'app.avamedia.player.{extension}'
                # Import known identifiers too: the build host may know a third-party
                # type that is absent on the user's Mac.
                declaration = imported.setdefault(content_type, {
                    'UTTypeIdentifier': content_type,
                    'UTTypeConformsTo': [parent],
                    'UTTypeDescription': f'{extension.upper()} media',
                    'UTTypeTagSpecification': {'public.filename-extension': []},
                })
                declaration['UTTypeTagSpecification']['public.filename-extension'].append(extension)
                identifiers.append(content_type)
            finally:
                if identifier:
                    core.CFRelease(identifier)
                core.CFRelease(value)
                core.CFRelease(conforming)
    finally:
        core.CFRelease(tag)
    return sorted(set(identifiers)), list(imported.values())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('application', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    # Use the same input lists as Windows registration and the player.
    video_source = (root / 'src/AvaMedia.Core/VideoFormats.cs').read_text()
    video = re.search(r'InputExtensions.*?new\[\]\s*\{(.*?)\}', video_source, re.S)
    audio_source = (root / 'src/AvaMedia.Desktop/SystemPlayerIntegration.cs').read_text()
    audio = re.search(r'Concat\(new\[\]\s*\{(.*?)\}', audio_source, re.S)
    if not video or not audio:
        raise RuntimeError('Cannot read the shared player input extensions')
    extensions = [(ext, 'public.movie') for ext in re.findall(r'"([a-z0-9]+)"', video[1])]
    extensions += [(ext, 'public.audio') for ext in re.findall(r'"\.([a-z0-9]+)"', audio[1])]
    identifiers, imported = media_types(extensions)
    plist_path = args.application / 'Contents/Info.plist'
    with plist_path.open('rb') as stream:
        plist = plistlib.load(stream)
    plist.update({
        'CFBundleDocumentTypes': [{
            'CFBundleTypeName': 'Video and audio',
            'CFBundleTypeRole': 'Viewer',
            'LSHandlerRank': 'Alternate',
            'LSItemContentTypes': identifiers,
        }],
        'UTImportedTypeDeclarations': imported,
    })
    with plist_path.open('wb') as stream:
        plistlib.dump(plist, stream, sort_keys=False)


if __name__ == '__main__':
    main()
