"""Use the latest official portable FFDec, falling back to bundled tools."""
import hashlib
import json
from pathlib import Path
import re
import sys
import urllib.request
import zipfile


def resolve(root):
    try:
        request = urllib.request.Request('https://api.github.com/repos/jindrapetrik/jpexs-decompiler/releases/latest', headers={'User-Agent': 'AssetScavenger'})
        with urllib.request.urlopen(request, timeout=8) as response:
            release = json.load(response)
        version = release['tag_name'].removeprefix('version')
        if not re.fullmatch(r'\d+(?:\.\d+)+', version):
            raise ValueError('Unexpected version')
        asset = next(a for a in release['assets'] if a['name'] == f'ffdec_{version}.zip')
        target = root / 'tools/FFDec/releases' / version
        launcher = target / 'ffdec-pipeline.bat'
        if not (target / 'ready.json').exists():
            target.mkdir(parents=True, exist_ok=True)
            archive = target / 'release.zip.part'
            digest = hashlib.sha256()
            with urllib.request.urlopen(asset['browser_download_url'], timeout=20) as response, archive.open('wb') as output:
                while chunk := response.read(1024 * 1024):
                    digest.update(chunk)
                    output.write(chunk)
            if asset.get('digest') and asset['digest'] != 'sha256:' + digest.hexdigest():
                raise ValueError('Checksum mismatch')
            with zipfile.ZipFile(archive) as package:
                for entry in package.infolist():
                    if not (target / entry.filename).resolve().is_relative_to(target.resolve()):
                        raise ValueError('Unsafe archive path')
                package.extractall(target)
            if not (target / 'ffdec.jar').is_file():
                raise ValueError('Missing ffdec.jar')
            launcher.write_bytes(b'@echo off\r\n"%~dp0..\\..\\..\\JavaRuntime\\bin\\java.exe" -Xmx1024m -Djna.nosys=true -jar "%~dp0ffdec.jar" %*\r\nexit /b %ERRORLEVEL%\r\n')
            (target / 'ready.json').write_text(json.dumps({'version': version, 'sha256': digest.hexdigest()}), encoding='utf-8')
        launcher.write_bytes(b'@echo off\r\n"%~dp0..\\..\\..\\JavaRuntime\\bin\\java.exe" -Xmx1024m -Dfile.encoding=UTF-8 -Dsun.stdout.encoding=UTF-8 -Dsun.stderr.encoding=UTF-8 -Djna.nosys=true -jar "%~dp0ffdec.jar" %*\r\nexit /b %ERRORLEVEL%\r\n')
        print(f'[FFDec] 官方最新版本 {version} 已就绪')
        return launcher
    except Exception as error:
        print(f'[FFDec] 无法检查或下载官方版本，使用 tools 内置版本：{error}')
        return root / 'tools/FFDec/ffdec-pipeline.bat'


if __name__ == '__main__':
    print(resolve(Path(__file__).resolve().parents[2]))
