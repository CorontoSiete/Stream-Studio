"""Post published release notes to the Stream Studio Discord webhook."""

import argparse
import json
import math
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


def retry_delay(error):
    try:
        body = json.load(error)
    except ValueError:
        body = None
    for value in (body.get("retry_after") if isinstance(body, dict) else None, (error.headers or {}).get("Retry-After")):
        try:
            seconds = float(value)
        except (TypeError, ValueError, OverflowError):
            continue
        if math.isfinite(seconds) and seconds >= 0:
            if seconds > 60:
                raise RuntimeError(f"Announcement was rate limited. Retry after {seconds:g} seconds.") from None
            return max(1, seconds)
    return 1


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
            with error:
                if error.code != 429 or attempt == 3:
                    # Webhook URLs contain credentials; never print the request URL.
                    raise RuntimeError(f"Announcement request failed (HTTP {error.code}).") from None
                delay = retry_delay(error)
            time.sleep(delay)
        except (URLError, TimeoutError):
            # An ambiguous send must not be retried automatically.
            raise RuntimeError("Announcement connection failed; inspect the channel before retrying.") from None


def utf16_prefix_length(text, limit):
    remaining = limit
    for index, character in enumerate(text):
        remaining -= 2 if ord(character) > 0xFFFF else 1
        if remaining < 0:
            return index
    return len(text)


def split_notes(text, limit=3600):
    """Preserve text while respecting Discord's UTF-16 limits."""
    if limit < 2:
        raise ValueError("The note limit must accommodate any Unicode character.")
    chunks = []
    while text:
        end = utf16_prefix_length(text, limit)
        if end < len(text):
            boundary = text.rfind("\n\n", 0, end)
            if boundary >= end // 2:
                end = boundary + 2
        chunks.append(text[:end])
        text = text[end:]
    return chunks


def public_notes(notes):
    return re.sub(r"<!--.*?-->", "", notes, flags=re.S).strip()


def validate_release_notes(notes):
    improvement_section = re.search(
        r"^##[ \t]+Improvements and bug fixes[ \t]*\n(.*?)(?=^#{1,2}[ \t]|\Z)",
        public_notes(notes), flags=re.M | re.S,
    )
    if not improvement_section or not improvement_section[1].strip():
        raise ValueError("Add a nonempty '## Improvements and bug fixes' section to the release notes.")


def build_payloads(release, *, ping_everyone=True):
    url = release.get("html_url", "")
    if not url.startswith(RELEASE_PREFIX + "tag/") or release.get("draft"):
        raise ValueError("Only published Stream Studio releases can be announced.")
    tag = release["tag_name"]
    title = release.get("name") or f"Stream Studio {tag}"
    notes = public_notes(release.get("body") or "")
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
            "title": title[:utf16_prefix_length(title, 240)] if index == 0 else f"Changelog continued ({index + 1}/{len(chunks)})",
            "url": url,
            "description": chunk,
            "color": 0x74E3BF,
            "footer": {"text": "Stream Studio • Preview release" if release.get("prerelease")
                       else "Stream Studio • Official release"},
        }
        if release.get("published_at"):
            embed["timestamp"] = release["published_at"]
        message = {"allowed_mentions": {"parse": []}, "embeds": [embed]}
        if index == 0:
            embed["fields"] = [
                {"name": "Download for Windows", "value": f"[Get the installer]({installer})", "inline": True},
                {"name": "Full changelog", "value": f"[View this release]({url})", "inline": True},
            ]
            if ping_everyone:
                message["content"] = "@everyone"
                message["allowed_mentions"]["parse"].append("everyone")
        messages.append(message)
    return messages


def load_release(path=None):
    if path:
        return json.loads(Path(path).read_text(encoding="utf-8-sig"))
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
    if event.get("release"):
        return event["release"]
    tag = (os.environ.get("DISCORD_RELEASE_TAG") or (event.get("inputs") or {}).get("release_tag", "")).strip()
    suffix = "tags/" + quote(tag, safe="") if tag else "latest"
    return request_json(
        f"https://api.github.com/repos/{REPOSITORY}/releases/{suffix}",
        token=os.environ.get("GITHUB_TOKEN"),
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release-json")
    parser.add_argument("--validate-notes", type=Path)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--edit-message-id")
    args = parser.parse_args()
    if args.validate_notes is not None:
        validate_release_notes(args.validate_notes.read_text(encoding="utf-8-sig"))
        return

    dry_run = args.dry_run or os.environ.get("DISCORD_DRY_RUN", "").lower() == "true"
    webhook = os.environ.get("DISCORD_RELEASE_WEBHOOK_URL", "").strip()
    if not (dry_run and args.release_json) and not re.fullmatch(WEBHOOK_PATTERN, webhook):
        raise ValueError("DISCORD_RELEASE_WEBHOOK_URL is missing or is not a Discord webhook.")
    edit_id = args.edit_message_id
    if edit_id is None:
        edit_id = os.environ.get("DISCORD_EDIT_MESSAGE_ID", "").strip() or None
    if edit_id is not None and not re.fullmatch(r"[0-9]+", edit_id):
        raise ValueError("The message ID must contain only digits.")
    release = load_release(args.release_json)
    messages = build_payloads(release, ping_everyone=not edit_id)
    edit_endpoint = f"{webhook}/messages/{edit_id}" if edit_id else None

    if edit_endpoint and not (dry_run and args.release_json):
        original = request_json(edit_endpoint)
        if not any(embed.get("url") == release["html_url"] for embed in original.get("embeds", [])):
            raise ValueError("The Discord message is not the announcement for this release.")

    message_ids = []
    if dry_run:
        if not args.release_json and not edit_endpoint:
            request_json(webhook)
    else:
        for index, payload in enumerate(messages):
            if edit_endpoint and index == 0:
                result = request_json(edit_endpoint, payload=payload, method="PATCH")
                if result.get("id") != edit_id or not any(
                    embed.get("description") == payload["embeds"][0]["description"]
                    for embed in result.get("embeds", [])
                ):
                    raise RuntimeError("Discord did not confirm the edited announcement.")
            else:
                result = request_json(webhook + "?wait=true", payload=payload, method="POST")
            message_ids.append(result.get("id"))
    print(json.dumps({"release": release["tag_name"], "messages": len(messages),
                      "dry_run": dry_run, "message_ids": message_ids}))


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, ValueError, KeyError, OSError) as error:
        print(f"Release announcement failed: {error}", file=sys.stderr)
        sys.exit(1)
