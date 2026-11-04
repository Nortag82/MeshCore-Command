"""Check official GitHub releases and verify a Windows installer before launch."""
import hashlib
import json
import os
import re
import tempfile
import urllib.parse
import urllib.request
from pathlib import Path

from version import VERSION

API = 'https://api.github.com/repos/Nortag82/MeshCore-Command/releases?per_page=100'
DOWNLOAD_HOSTS = {'api.github.com', 'github.com', 'release-assets.githubusercontent.com', 'github-releases.githubusercontent.com', 'objects.githubusercontent.com'}
MAX_INSTALLER = 200 * 1024 * 1024


def parse_version(value):
    match = re.fullmatch(r'v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)', value or '')
    return tuple(map(int, match.groups())) if match else None


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        parsed = urllib.parse.urlsplit(newurl)
        if parsed.scheme != 'https' or parsed.hostname not in DOWNLOAD_HOSTS or parsed.username or parsed.password or parsed.port not in (None, 443):
            raise ValueError('GitHub redirected the download to an untrusted address.')
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def select_release(releases, current=VERSION):
    current_version = parse_version(current)
    if current_version is None or not isinstance(releases, list):
        raise ValueError('Invalid release information.')
    candidates = []
    for release in releases:
        if not isinstance(release, dict) or release.get('draft') or not release.get('published_at'):
            continue
        version = parse_version(release.get('tag_name'))
        if version and version > current_version:
            candidates.append((version, release))
    if not candidates:
        return None
    version, release = max(candidates, key=lambda item: item[0])
    version_text = '.'.join(map(str, version))
    expected_name = f'MeshCore-Command-Windows-{version_text}-Setup.exe'
    assets = [asset for asset in release.get('assets', []) if isinstance(asset, dict) and asset.get('name') == expected_name and asset.get('state') == 'uploaded']
    if len(assets) != 1:
        raise ValueError('The newest release does not contain exactly one compatible Windows installer.')
    asset = assets[0]
    digest = asset.get('digest', '')
    if not re.fullmatch(r'sha256:[0-9a-f]{64}', digest):
        raise ValueError('GitHub has not provided a SHA-256 digest for this installer.')
    url = asset.get('browser_download_url', '')
    parsed = urllib.parse.urlsplit(url)
    expected_path = f'/Nortag82/MeshCore-Command/releases/download/{release["tag_name"]}/{expected_name}'
    if parsed.scheme != 'https' or parsed.hostname != 'github.com' or parsed.username or parsed.password or parsed.port not in (None, 443) or parsed.path != expected_path or parsed.query or parsed.fragment:
        raise ValueError('The installer does not have the expected GitHub address.')
    size = asset.get('size')
    if type(size) is not int or not 0 < size <= MAX_INSTALLER:
        raise ValueError('Invalid installer size.')
    return {'version': version_text, 'prerelease': bool(release.get('prerelease')), 'url': url, 'sha256': digest[7:], 'size': size, 'name': expected_name}


def check():
    request = urllib.request.Request(API, headers={'Accept': 'application/vnd.github+json', 'User-Agent': 'MeshCore-Command-Updater', 'X-GitHub-Api-Version': '2022-11-28'})
    with urllib.request.build_opener(SafeRedirect()).open(request, timeout=20) as response:
        raw = response.read(2_000_001)
    if len(raw) > 2_000_000:
        raise ValueError('GitHub release response was too large.')
    release = select_release(json.loads(raw))
    if release is None:
        return {'current': VERSION, 'available': False}
    return {'current': VERSION, 'available': True, 'version': release['version'], 'prerelease': release['prerelease']}


def download(expected_version):
    request = urllib.request.Request(API, headers={'Accept': 'application/vnd.github+json', 'User-Agent': 'MeshCore-Command-Updater', 'X-GitHub-Api-Version': '2022-11-28'})
    with urllib.request.build_opener(SafeRedirect()).open(request, timeout=20) as response:
        raw = response.read(2_000_001)
    if len(raw) > 2_000_000: raise ValueError('GitHub release response was too large.')
    release = select_release(json.loads(raw))
    if not release or release['version'] != expected_version:
        raise ValueError('The available release changed. Check again before installing.')
    folder = Path(tempfile.mkdtemp(prefix='meshcore-command-update-'))
    path = folder / release['name']
    digest = hashlib.sha256(); total = 0
    try:
        opener = urllib.request.build_opener(SafeRedirect())
        req = urllib.request.Request(release['url'], headers={'User-Agent':'MeshCore-Command-Updater'})
        with opener.open(req, timeout=30) as response, path.open('xb') as output:
            while True:
                block = response.read(1024 * 1024)
                if not block: break
                total += len(block)
                if total > release['size'] or total > MAX_INSTALLER: raise ValueError('Installer is larger than expected.')
                digest.update(block); output.write(block)
        if total != release['size'] or digest.hexdigest() != release['sha256']:
            raise ValueError('The installer failed its GitHub SHA-256 check.')
        return str(path)
    except Exception:
        try: path.unlink(missing_ok=True); folder.rmdir()
        except OSError: pass
        raise
