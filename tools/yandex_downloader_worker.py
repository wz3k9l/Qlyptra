"""Thin stdin-based adapter for llistochek/yandex-music-downloader.
The user's token is read from stdin, not placed in the Windows process command line.
"""
import json
import sys


def main() -> int:
    try:
        request = json.load(sys.stdin)
    except Exception as exc:
        print(f"Не удалось прочитать запрос Qlyptra: {exc}", file=sys.stderr)
        return 2

    token = str(request.get("token", "")).strip()
    url = str(request.get("url", "")).strip()
    output_dir = str(request.get("output_dir", "")).strip()
    if not token or not url or not output_dir:
        print("В запросе отсутствует токен, ссылка или выходная папка.", file=sys.stderr)
        return 2

    # ymd.cli exposes the same entry point as the yandex-music-downloader console command.
    sys.argv = [
        "yandex-music-downloader",
        "--token", token,
        "--url", url,
        "--quality", "2",  # Best available source quality (FLAC); Qlyptra transcodes to MP3.
        "--embed-cover",
        "--cover-resolution", "original",
        "--dir", output_dir,
        "--path-pattern", "#title",
    ]
    try:
        from ymd.cli import main as cli_main
        result = cli_main()
        return int(result) if isinstance(result, int) else 0
    except SystemExit as exc:
        return int(exc.code or 0)
    except Exception as exc:
        print(f"Загрузчик Яндекс Музыки: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
