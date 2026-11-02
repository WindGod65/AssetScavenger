import argparse
import hashlib
import json
import os
import re
import sys
import urllib.parse
import urllib.request
import zlib
from collections import deque

USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AssetScavenger/1.0"
SWF_SIGNATURES = (b"FWS", b"CWS", b"ZWS")


def fetch(url, referer=""):
    headers = {"User-Agent": USER_AGENT}
    if referer:
        headers["Referer"] = referer
    request = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.geturl(), response.headers, response.read()


def decode_html(headers, data):
    charset = headers.get_content_charset() or "utf-8"
    try:
        return data.decode(charset, errors="replace")
    except LookupError:
        return data.decode("utf-8", errors="replace")


def resolve_reference(current_url, reference):
    reference = reference.strip().replace("&amp;", "&")
    if reference.startswith("//"):
        return "https:" + reference
    if reference.startswith("/upload_swf/"):
        return "https://sda.4399.com/4399swf" + reference
    return urllib.parse.urljoin(current_url, reference)


def html_references(content):
    patterns = (
        r'''(?is)_strGamePath\s*=\s*["']([^"']+)["']''',
        r'''(?is)<(?:iframe|embed)\b[^>]*?\bsrc\s*=\s*["']([^"']+)["']''',
        r'''(?is)<param\b[^>]*?\bname\s*=\s*["']?movie["']?[^>]*?\bvalue\s*=\s*["']([^"']+)["']''',
        r'''(?is)<param\b[^>]*?\bvalue\s*=\s*["']([^"']+)["'][^>]*?\bname\s*=\s*["']?movie["']?''',
        r'''(?i)["']([^"']+\.swf(?:\?[^"']*)?)["']''',
    )
    found = []
    for pattern in patterns:
        found.extend(re.findall(pattern, content))
    return list(dict.fromkeys(found))


def swf_references(data):
    body = data
    if data.startswith(b"CWS") and len(data) > 8:
        try:
            body = data[:8] + zlib.decompress(data[8:])
        except zlib.error:
            pass
    pattern = rb'''(?i)([A-Za-z0-9_./:%?&=+~-]{1,260}\.swf(?:\?[A-Za-z0-9_./:%?&=+~-]{0,260})?)'''
    return list(dict.fromkeys(match.decode("latin-1") for match in re.findall(pattern, body)))


def safe_name(url, used_names):
    name = os.path.basename(urllib.parse.urlparse(url).path) or "game.swf"
    name = re.sub(r'''[<>:"/\\|?*\x00-\x1f]''', "_", urllib.parse.unquote(name))
    if not name.lower().endswith(".swf"):
        name += ".swf"
    stem, extension = os.path.splitext(name)
    candidate, number = name, 2
    while candidate.lower() in used_names:
        candidate = f"{stem}_{number}{extension}"
        number += 1
    used_names.add(candidate.lower())
    return candidate


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    parsed = urllib.parse.urlparse(args.url)
    if parsed.scheme not in ("http", "https") or not parsed.hostname or not parsed.hostname.endswith("4399.com"):
        raise ValueError("需要输入公开的 4399 游戏页面地址")

    swf_dir = os.path.abspath(args.output)
    os.makedirs(swf_dir, exist_ok=True)
    queue = deque([(args.url, args.url)])
    seen, used_names = set(), set()
    swf_files, failures, html_pages = [], [], []

    while queue and len(seen) < 80:
        requested_url, referer = queue.popleft()
        normalized = requested_url.split("#", 1)[0]
        if normalized in seen:
            continue
        seen.add(normalized)
        try:
            final_url, headers, data = fetch(normalized, referer)
            path = urllib.parse.urlparse(final_url).path.lower()
            if data[:3] in SWF_SIGNATURES:
                filename = safe_name(final_url, used_names)
                target = os.path.join(swf_dir, filename)
                with open(target, "wb") as output_file:
                    output_file.write(data)
                swf_files.append({"file": filename, "url": final_url, "bytes": len(data),
                                  "sha256": hashlib.sha256(data).hexdigest()})
                print(f"[SWF] {filename} ({len(data)} bytes)", flush=True)
                for reference in swf_references(data):
                    child = resolve_reference(final_url, reference)
                    if child.split("#", 1)[0] not in seen:
                        queue.append((child, final_url))
                continue
            if path.endswith((".htm", ".html")) or not html_pages:
                content = decode_html(headers, data)
                html_pages.append(final_url)
                print(f"[PAGE] {final_url}", flush=True)
                for reference in html_references(content):
                    child = resolve_reference(final_url, reference)
                    child_path = urllib.parse.urlparse(child).path.lower()
                    if child_path.endswith((".swf", ".htm", ".html")) and child.split("#", 1)[0] not in seen:
                        queue.append((child, final_url))
        except Exception as error:
            failures.append({"url": normalized, "referer": referer,
                             "reason": f"{type(error).__name__}: {error}"})
            print(f"[FAILED] {normalized}: {error}", flush=True)

    manifest = {"source_url": args.url, "html_pages": html_pages,
                "swf_files": swf_files, "failures": failures}
    with open(os.path.join(swf_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as output:
        json.dump(manifest, output, ensure_ascii=False, indent=2)
        output.write("\n")
    if not swf_files:
        raise RuntimeError("没有找到 SWF；页面可能不是 Flash 游戏，或真实入口需要登录/动态脚本")
    print(f"[DONE] 共获取 {len(swf_files)} 个 SWF，保存到：{swf_dir}", flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"{type(error).__name__}: {error}", file=sys.stderr)
        raise SystemExit(2)
