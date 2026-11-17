"""Post published release notes to the Stream Studio Discord webhook."""

import argparse
import json
import os
from pathlib import Path
import re
import sys
import time
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen

REPOSITORY = "CorontoSiete/Stream-Studio"
RELEASE_PREFIX = f"https://github.com/{REPOSITORY}/releases/"
WEBHOOK_PATTERN = r"https://discord\.com/api/(?:v10/)?webhooks/\d+/[A-Za-z0-9_-]+"


def request_json(url, *, payload=None, method=None, token=None):
    headers = {"User-Agent": "Stream-Studio-Release-Announcements"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
        headers["Accept"] = "application/vnd.github+json"
    data = None
    if payload is not None:
        headers["Content-Type"] = "application/json"
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    for attempt in range(4):
        try:
            with urlopen(Request(url, data=data, headers=headers, method=method), timeout=30) as response:
                body = response.read()
                return json.loads(body) if body else {}
        except HTTPError as error:
            if error.code == 429 and attempt < 3:
                try:
                    delay = float(json.loads(error.read()).get("retry_after", 1))
                except (ValueError, TypeError):
                    delay = 1
                time.sleep(min(max(delay, 1), 10))
                continue
            # Webhook URLs contain credentials; never print the request URL.
            raise RuntimeError(f"Announcement request failed (HTTP {error.code}).") from None
        except (URLError, TimeoutError):
            # An ambiguous send must not be retried automatically.
            raise RuntimeError("Announcement connection failed; inspect the channel before retrying.") from None


def split_notes(text, limit=3600):
    """Preserve text while respecting Discord's UTF-16 limits."""
    chunks = []
    while text:
        units = 0
        end = 0
        for character in text:
            size = 2 if ord(character) > 0xFFFF else 1
            if units + size > limit:
                break
            units += size
            end += 1
        if end < len(text):
            boundary = text.rfind("\n\n", 0, end)
            if boundary >= end // 2:
                end = boundary + 2
        chunks.append(text[:end])
        text = text[end:]
    return chunks


def build_payloads(release):
    url = release.get("html_url", "")
    if not url.startswith(RELEASE_PREFIX + "tag/") or release.get("draft"):
        raise ValueError("Only published Stream Studio releases can be announced.")
    tag = release["tag_name"]
    title = release.get("name") or f"Stream Studio {tag}"
    notes = re.sub(r"<!--.*?-->", "", release.get("body") or "", flags=re.S).strip()
    lines = notes.split("\n", 1)
    normalize = lambda value: re.sub(r"\bv(?=\d)", "", value.lower()).strip("# \r")
    if len(lines) > 1 and lines[0].startswith("#") and normalize(lines[0]) == normalize(title):
        notes = lines[1].strip()
    chunks = split_notes(notes or f"Read the [release details]({url}) for this update.")
    if len(chunks) > 10:
        chunks = chunks[:9] + [f"Read the remaining notes in the [full changelog]({url})."]
    installer = next(
        (asset["browser_download_url"] for asset in release.get("assets", [])
         if asset.get("name") == "StreamlinkVlcStudio-Setup.exe"
         and asset.get("browser_download_url", "").startswith(RELEASE_PREFIX + "download/")),
        url,
    )
    messages = []
    for index, chunk in enumerate(chunks):
        embed = {
            "title": title[:240] if index == 0 else f"Changelog continued ({index + 1}/{len(chunks)})",
            "url": url,
            "description": chunk,
            "color": 0x74E3BF,
            "footer": {"text": "Stream Studio • Preview release" if release.get("prerelease")
                       else "Stream Studio • Official release"},
        }
        if release.get("published_at"):
            embed["timestamp"] = release["published_at"]
        if index == 0:
            embed["fields"] = [
                {"name": "Download for Windows", "value": f"[Get the installer]({installer})", "inline": True},
                {"name": "Full changelog", "value": f"[View this release]({url})", "inline": True},
            ]
        messages.append({"allowed_mentions": {"parse": []}, "embeds": [embed]})
    return messages


def load_release(path=None):
    if path:
        return json.loads(Path(path).read_text(encoding="utf-8-sig"))
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
    if event.get("release"):
        return event["release"]
    tag = (event.get("inputs") or {}).get("release_tag", "").strip()
    suffix = "tags/" + quote(tag, safe="") if tag else "latest"
    return request_json(
        f"https://api.github.com/repos/{REPOSITORY}/releases/{suffix}",
        token=os.environ.get("GITHUB_TOKEN"),
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release-json")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--edit-message-id")
    args = parser.parse_args()
    webhook = os.environ.get("DISCORD_RELEASE_WEBHOOK_URL", "").strip()
    if not re.fullmatch(WEBHOOK_PATTERN, webhook):
        raise ValueError("DISCORD_RELEASE_WEBHOOK_URL is missing or is not a Discord webhook.")
    if args.edit_message_id and not args.edit_message_id.isdecimal():
        raise ValueError("The message ID must contain only digits.")
    release = load_release(args.release_json)
    messages = build_payloads(release)
    dry_run = args.dry_run or os.environ.get("DISCORD_DRY_RUN", "").lower() == "true"
    message_ids = []
    if not dry_run:
        for index, payload in enumerate(messages):
            edit = args.edit_message_id if index == 0 else None
            endpoint = webhook + (f"/messages/{edit}" if edit else "") + "?wait=true"
            result = request_json(endpoint, payload=payload, method="PATCH" if edit else "POST")
            message_ids.append(result.get("id"))
    print(json.dumps({"release": release["tag_name"], "messages": len(messages),
                      "dry_run": dry_run, "message_ids": message_ids}))


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, ValueError, KeyError, OSError) as error:
        print(f"Release announcement failed: {error}", file=sys.stderr)
        sys.exit(1)
