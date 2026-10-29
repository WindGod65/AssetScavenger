import argparse
import os
import sys
import urllib.parse
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--referer", default="")
    args = parser.parse_args()

    parsed = urllib.parse.urlparse(args.url)
    if parsed.scheme not in ("http", "https"):
        raise ValueError("only http/https URLs are allowed")

    headers = {"User-Agent": "AssetScavenger/1.0 (local archival tool)"}
    if args.referer:
        headers["Referer"] = args.referer
    request = urllib.request.Request(args.url, headers=headers)
    temporary = args.output + ".part"
    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    try:
        with urllib.request.urlopen(request, timeout=60) as response, open(temporary, "wb") as output:
            while True:
                block = response.read(1024 * 1024)
                if not block:
                    break
                output.write(block)
        os.replace(temporary, args.output)
    finally:
        if os.path.exists(temporary):
            os.remove(temporary)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"{type(error).__name__}: {error}", file=sys.stderr)
        raise SystemExit(2)
