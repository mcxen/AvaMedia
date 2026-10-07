# AvaMedia contributors. SPDX-License-Identifier: AGPL-3.0-only
# Protocol references: pixeldrain.com/api and Lysagxra/BunkrDownloader.
# All network requests go through yt-dlp to retain its cookies, proxy and retries.
import json
import re
import urllib.parse
from html.parser import HTMLParser

from yt_dlp.extractor.common import InfoExtractor
from yt_dlp.utils import (
    ExtractorError, determine_ext, int_or_none, js_to_json, mimetype2ext,
    update_url_query, url_or_none,
)

_VIDEO_EXTENSIONS = {
    'mp4', 'mkv', 'webm', 'mov', 'm4v', 'avi', 'flv', 'wmv', 'ts', 'm2ts',
    'mpeg', 'mpg', 'ogv', '3gp', 'asf', 'vob',
}
_BUNKR_HOST = r'(?:www\.)?bunkr\.[a-z0-9-]+'
_PIXELDRAIN_HOST = r'(?:www\.)?pixeldrain\.com'


def _video_extension(name, mime=None):
    extension = determine_ext(name or '', default_ext=None)
    if extension in _VIDEO_EXTENSIONS:
        return extension
    if isinstance(mime, str) and mime.startswith('video/'):
        extension = mimetype2ext(mime)
        if extension in _VIDEO_EXTENSIONS:
            return extension
    return None


def _known_nonvideo(name, mime=None):
    if _video_extension(name, mime):
        return False
    extension = determine_ext(name or '', default_ext=None)
    return bool(extension or isinstance(mime, str) and mime.startswith(('image/', 'audio/')))


def _video_title(name):
    return name.rsplit('.', 1)[0] if determine_ext(name, default_ext=None) in _VIDEO_EXTENSIONS else name


def _http_url(value):
    url = url_or_none(value)
    if not url or urllib.parse.urlsplit(url).scheme not in ('http', 'https'):
        raise ExtractorError('File host returned an invalid media URL', expected=True)
    return url


class _BunkrLinks(HTMLParser):
    def __init__(self, html):
        super().__init__(convert_charrefs=True)
        self.links = []
        self.feed(html)

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'a' and attrs.get('href'):
            self.links.append(attrs)


class AvaMediaBunkrIE(InfoExtractor):
    IE_NAME = 'avamedia:bunkr'
    _VALID_URL = rf'https?://{_BUNKR_HOST}/(?:f|v|i)/(?P<id>[^/?#]+)(?:[/?#]|$)'

    def _javascript_string(self, html, variable, video_id):
        literal = self._search_regex(
            rf'''\b{variable}\s*=\s*("(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')''',
            html, variable, default=None)
        return self._parse_json(literal, video_id, transform_source=js_to_json) if literal else None

    def _real_extract(self, url):
        video_id = self._match_id(url)
        page_url = url.split('#', 1)[0]
        headers = {'Referer': page_url, 'Origin': 'https://bunkr.cr'}
        fragment = urllib.parse.parse_qs(urllib.parse.urlsplit(url).fragment)
        file_id = (fragment.get('_fid') or [None])[0]
        if file_id and not re.fullmatch(r'\d+', file_id):
            raise ExtractorError('Invalid Bunkr file ID', expected=True)

        name, mime, unsigned = None, None, None
        if not file_id:
            html = self._download_webpage(
                page_url, video_id, impersonate=True, require_impersonation=True)
            unsigned = self._javascript_string(html, 'jsCDN', video_id)
            mime = self._javascript_string(html, 'jsType', video_id)
            name = self._og_search_title(html, default=None) or self._html_extract_title(html, default=video_id)
            name = re.sub(r'\s*[|\-]\s*Bunkr\s*$', '', name or video_id, flags=re.I)
            if not unsigned:
                file_id = self._search_regex(
                    r'''\bdata-file-id\s*=\s*["'](\d+)["']''', html, 'file ID', default=None)

        if file_id:
            api_headers = {
                'Content-Type': 'application/json', 'Origin': 'https://dl.bunkr.cr',
                'Referer': f'https://dl.bunkr.cr/file/{file_id}',
            }
            metadata = self._download_json(
                'https://dl.bunkr.cr/api/_001_v2', video_id,
                data=json.dumps({'id': file_id}).encode(), headers=api_headers,
                impersonate=True, require_impersonation=True)
            if not isinstance(metadata, dict) or not metadata.get('mediafiles') or not metadata.get('path'):
                raise ExtractorError('Bunkr file is unavailable or metadata has changed', expected=True)
            unsigned = urllib.parse.urljoin(metadata['mediafiles'].rstrip('/') + '/', metadata['path'])
            name = metadata.get('original') or name
            headers = api_headers

        unsigned = _http_url(unsigned)
        parsed = urllib.parse.urlsplit(unsigned)
        name = name or urllib.parse.unquote(parsed.path.rsplit('/', 1)[-1])
        extension = _video_extension(name, mime) or _video_extension(urllib.parse.unquote(parsed.path), mime)
        if not extension:
            raise ExtractorError('This Bunkr file is not a video', expected=True)
        signature = self._download_json(
            'https://glb-apisign.cdn.cr/sign', video_id,
            query={'path': urllib.parse.unquote(parsed.path)}, headers=headers,
            impersonate=True, require_impersonation=True)
        if not isinstance(signature, dict) or not signature.get('token') or not signature.get('ex'):
            raise ExtractorError('Bunkr signing API returned no token or expiry', expected=True)
        media_url = update_url_query(unsigned, {
            'token': signature['token'], 'ex': signature['ex'], 'n': name,
        })
        return {
            'id': video_id, 'title': _video_title(name), 'webpage_url': url,
            'formats': [{
                'format_id': 'original', 'url': media_url, 'ext': extension,
                'impersonate': True,
                'http_headers': {'Referer': page_url, 'Origin': 'https://bunkr.cr'},
            }],
        }


class AvaMediaBunkrAlbumIE(InfoExtractor):
    IE_NAME = 'avamedia:bunkr:album'
    _VALID_URL = rf'https?://{_BUNKR_HOST}/a/(?P<id>[\w-]+)(?:[/?#]|$)'

    def _real_extract(self, url):
        album_id = self._match_id(url)
        page_url = update_url_query(url.split('#', 1)[0], {'advanced': '1'})
        entries, seen, visited = [], set(), set()
        title = None
        # Resolve only album metadata; members are signed individually at download time.
        while page_url and len(entries) <= 100 and len(visited) < 100:
            visited.add(page_url)
            html, response = self._download_webpage_handle(
                page_url, album_id, impersonate=True, require_impersonation=True)
            page_url = response.url
            if title is None:
                title = self._html_extract_title(html, default=album_id)
                title = re.sub(r'\s*[|\-]\s*Bunkr\s*$', '', title, flags=re.I)
            files = self._search_json(
                r'(?:window\.)?albumFiles\s*=', html, 'album files', album_id,
                contains_pattern=r'\[(?s:.+)\]', transform_source=js_to_json, default=None)
            links = _BunkrLinks(html).links
            if isinstance(files, list):
                members = []
                for file in files:
                    if not isinstance(file, dict):
                        continue
                    slug = file.get('slug') or file.get('name')
                    name = file.get('original') or file.get('name') or slug
                    if not isinstance(slug, str) or _known_nonvideo(name, file.get('mime_type')):
                        continue
                    member = urllib.parse.urljoin(page_url, '/f/' + urllib.parse.quote(slug, safe='-_.'))
                    file_id = int_or_none(file.get('id'))
                    if file_id is not None:
                        member += f'#_fid={file_id}'
                    members.append((member, slug, name, int_or_none(file.get('size'))))
            else:
                members = []
                for link in links:
                    member = urllib.parse.urljoin(page_url, link['href'])
                    if not AvaMediaBunkrIE.suitable(member) or '/i/' in urllib.parse.urlsplit(member).path:
                        continue
                    name = link.get('title') or urllib.parse.unquote(urllib.parse.urlsplit(member).path.rsplit('/', 1)[-1])
                    if not _known_nonvideo(name):
                        members.append((member, AvaMediaBunkrIE._match_id(member), name, None))
            for member, slug, name, size in members:
                # Ignore hostile/external links and deduplicate by the stable file slug.
                if not AvaMediaBunkrIE.suitable(member) or slug in seen:
                    continue
                seen.add(slug)
                entries.append(self.url_result(
                    member, ie=AvaMediaBunkrIE, video_id=slug, video_title=_video_title(name), filesize=size))
                if len(entries) > 100:
                    break
            parsed_page = urllib.parse.urlsplit(page_url)
            page_number = int_or_none((urllib.parse.parse_qs(parsed_page.query).get('page') or ['1'])[0]) or 1
            next_pages = []
            for link in links:
                candidate = update_url_query(urllib.parse.urljoin(page_url, link['href']), {'advanced': '1'})
                parsed = urllib.parse.urlsplit(candidate)
                number = int_or_none((urllib.parse.parse_qs(parsed.query).get('page') or ['0'])[0]) or 0
                if (parsed.netloc == parsed_page.netloc and parsed.path == parsed_page.path
                        and number > page_number and candidate not in visited):
                    next_pages.append((number, candidate))
            page_url = min(next_pages)[1] if next_pages else None
        if not entries:
            raise ExtractorError('Empty video collection: no Bunkr videos were found', expected=True)
        result = self.playlist_result(entries, album_id, title)
        result['playlist_count'] = max(len(entries), 101 if page_url else 0)
        return result


class _PixeldrainBaseIE(InfoExtractor):
    def _api(self, path, video_id):
        data = self._download_json(
            'https://pixeldrain.com/api/' + path, video_id,
            expected_status=(400, 401, 403, 404, 429, 451))
        if not isinstance(data, dict):
            raise ExtractorError('Invalid Pixeldrain API response', expected=True)
        if data.get('success') is False:
            raise ExtractorError(
                f"Pixeldrain API: {data.get('value', 'unknown')} - {data.get('message', '')}", expected=True)
        return data

    @staticmethod
    def _file_url(file_id):
        if not isinstance(file_id, str) or not re.fullmatch(r'[a-zA-Z0-9_-]+', file_id):
            raise ExtractorError('Invalid Pixeldrain file ID', expected=True)
        return 'https://pixeldrain.com/u/' + file_id


class AvaMediaPixeldrainIE(_PixeldrainBaseIE):
    IE_NAME = 'avamedia:pixeldrain'
    _VALID_URL = rf'https?://{_PIXELDRAIN_HOST}/(?:u|api/file)/(?P<id>[a-zA-Z0-9_-]+)(?:[/?#]|$)'

    def _real_extract(self, url):
        video_id = self._match_id(url)
        metadata = self._api(f'file/{video_id}/info', video_id)
        name = metadata.get('name') or video_id
        extension = _video_extension(name, metadata.get('mime_type'))
        if not extension:
            raise ExtractorError('This Pixeldrain file is not a video', expected=True)
        availability = metadata.get('availability')
        if availability and not metadata.get('can_download'):
            raise ExtractorError(f'Pixeldrain API: {availability}', expected=True)
        page_url = self._file_url(video_id)
        return {
            'id': video_id, 'title': _video_title(name), 'webpage_url': page_url,
            'formats': [{
                'format_id': 'original', 'url': f'https://pixeldrain.com/api/file/{video_id}?download',
                'ext': extension, 'filesize': int_or_none(metadata.get('size')),
                'http_headers': {'Referer': page_url},
            }],
        }


class AvaMediaPixeldrainListIE(_PixeldrainBaseIE):
    IE_NAME = 'avamedia:pixeldrain:list'
    _VALID_URL = rf'https?://{_PIXELDRAIN_HOST}/(?:l|api/list)/(?P<id>[a-zA-Z0-9_-]+)(?:[/?#]|$)'

    def _real_extract(self, url):
        list_id = self._match_id(url)
        metadata = self._api(f'list/{list_id}', list_id)
        files = metadata.get('files')
        if not isinstance(files, list):
            raise ExtractorError('Invalid Pixeldrain file list', expected=True)
        selection = urllib.parse.parse_qs(urllib.parse.urlsplit(url).fragment, keep_blank_values=True).get('item')
        if selection is not None:
            index = int_or_none(selection[0])
            if index is None or index < 0 or index >= len(files):
                raise ExtractorError('Invalid list item: Pixeldrain item index is out of range', expected=True)
            file = files[index]
            if not isinstance(file, dict):
                raise ExtractorError('Invalid Pixeldrain file metadata', expected=True)
            return self.url_result(self._file_url(file.get('id')), ie=AvaMediaPixeldrainIE)
        videos = [file for file in files if isinstance(file, dict)
                  and _video_extension(file.get('name'), file.get('mime_type'))]
        if not videos:
            raise ExtractorError('Empty video collection: no Pixeldrain videos were found', expected=True)
        entries = [self.url_result(
            self._file_url(file.get('id')), ie=AvaMediaPixeldrainIE,
            video_id=file['id'], video_title=_video_title(file.get('name') or file['id']), filesize=int_or_none(file.get('size')),
        ) for file in videos[:101]]
        result = self.playlist_result(entries, list_id, metadata.get('title'))
        result['playlist_count'] = len(videos)
        return result
