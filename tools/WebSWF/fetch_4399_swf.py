import argparse
import hashlib
import json
import os
import re
import sys
import ssl
import urllib.error
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
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            return response.geturl(), response.headers, response.read()
    except urllib.error.URLError as error:
        parsed = urllib.parse.urlparse(url)
        # Older public 4399 asset hosts may not support HTTPS. Retry the same
        # public resource over HTTP without disabling certificate validation.
        if (parsed.scheme == "https" and parsed.hostname
                and parsed.hostname.endswith(".4399.com")
                and parsed.path.startswith("/4399swf/")
                and isinstance(error.reason, (ssl.SSLError, ConnectionError, TimeoutError))):
            fallback = urllib.parse.urlunparse(parsed._replace(scheme="http"))
            print(f"[RETRY HTTP] {fallback}", flush=True)
            try:
                return fetch(fallback, referer)
            except Exception as fallback_error:
                raise RuntimeError(f"HTTPS 连接失败：{error}；HTTP 回退失败：{fallback_error}") from fallback_error
        raise


def decode_html(headers, data):
    meta = re.search(rb'(?i)charset\s*=\s*["\']?([\w-]+)', data[:4096])
    charset = headers.get_content_charset() or (meta.group(1).decode("ascii") if meta else "utf-8")
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


def game_references(url, content):
    # Series portals contain multiple games and a menu SWF. Select only the
    # entry the page would launch for the requested query parameter.
    entries = dict(re.findall(
        r'''game_url_arr\s*\[\s*(\d+)\s*\]\s*=\s*["']([^"']+)["']''', content))
    if entries:
        selection = urllib.parse.parse_qs(urllib.parse.urlparse(url).query).get("g", [""])[0]
        if selection not in entries:
            raise ValueError("系列专题页需要有效的 g 参数；可选值：" + ", ".join(entries))
        return [entries[selection]]
    paths = re.findall(r'''(?is)_strGamePath\s*=\s*["']([^"']+)["']''', content)
    return paths or html_references(content)


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
    parser.add_argument("--probe", action="store_true")
    args = parser.parse_args()
    parsed = urllib.parse.urlparse(args.url)
    if parsed.scheme not in ("http", "https") or not parsed.hostname or not (parsed.hostname == "4399.com" or parsed.hostname.endswith(".4399.com")):
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
            if path.endswith(".swf") and data[:3] not in SWF_SIGNATURES:
                raise RuntimeError("资源服务器未返回有效 SWF（可能返回错误页或访问提示）")
            if data[:3] in SWF_SIGNATURES:
                if args.probe:
                    print("[DETECT:DIRECT] 已验证 SWF 文件签名；不保证包含完整游戏。若只有菜单或加载画面，请使用内存采集。", flush=True)
                    return
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
                if re.search(r'game_url_arr\s*\[', content):
                    print("[DETECT:MEMORY] 检测到多阶段游戏加载入口；直取可能仅包含菜单或加载器。请打开游戏、进入关卡后确认开始内存采集。", flush=True)
                    with open(os.path.join(swf_dir, "manifest.json"), "w", encoding="utf-8") as output:
                        json.dump({"source_url": args.url, "requires_memory": True,
                                   "swf_files": [], "html_pages": html_pages, "failures": failures}, output, ensure_ascii=False, indent=2)
                    return
                for reference in game_references(final_url, content):
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
    if args.probe:
        print("[DETECT:MEMORY] 直取链路未验证；建议加载游戏后执行内存采集（不代表资源不存在）", flush=True)
        return
    if not swf_files:
        reason = failures[0]["reason"] if failures else "页面没有可识别的 Flash 入口"
        raise RuntimeError("未获取到 SWF：" + reason + "。详细地址见导出目录 manifest.json")
    print(f"[DONE] 共获取 {len(swf_files)} 个 SWF，保存到：{swf_dir}", flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"{type(error).__name__}: {error}", file=sys.stderr)
        raise SystemExit(2)
