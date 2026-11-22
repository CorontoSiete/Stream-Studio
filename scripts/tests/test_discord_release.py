import importlib.util
from io import BytesIO
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import call, patch
from urllib.error import HTTPError, URLError

SPEC = importlib.util.spec_from_file_location(
    "discord_release", Path(__file__).resolve().parents[1] / "announce-discord-release.py"
)
release_feed = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release_feed)


def published_release(body="## New features\n\nA useful improvement."):
    return {
        "tag_name": "v1.8.12",
        "name": "Stream Studio v1.8.12",
        "html_url": release_feed.RELEASE_PREFIX + "tag/v1.8.12",
        "published_at": "2026-11-17T20:00:24Z",
        "body": body,
        "assets": [{"name": "StreamlinkVlcStudio-Setup.exe",
                    "browser_download_url": release_feed.RELEASE_PREFIX +
                    "download/v1.8.12/StreamlinkVlcStudio-Setup.exe"}],
    }


class ReleaseFeedTests(unittest.TestCase):
    def test_first_post_pings_everyone_and_includes_notes_and_installer(self):
        messages = release_feed.build_payloads(published_release(
            "# Stream Studio 1.8.12\n\n## Fixes\n\n@everyone playback works.\n<!-- internal marker -->"
        ))
        self.assertEqual(len(messages), 1)
        self.assertEqual(messages[0]["content"], "@everyone")
        self.assertEqual(messages[0]["allowed_mentions"], {"parse": ["everyone"]})
        embed = messages[0]["embeds"][0]
        self.assertEqual(embed["description"], "## Fixes\n\n@everyone playback works.")
        self.assertIn("StreamlinkVlcStudio-Setup.exe", embed["fields"][0]["value"])

    def test_long_unicode_notes_preserve_content_and_fit_discord(self):
        notes = ("Playback 🎬 improves.\n\n" * 600).strip()
        messages = release_feed.build_payloads(published_release(notes))
        self.assertGreater(len(messages), 1)
        self.assertEqual("".join(message["embeds"][0]["description"] for message in messages), notes)
        for message in messages[1:]:
            self.assertNotIn("content", message)
            self.assertEqual(message["allowed_mentions"], {"parse": []})
        for message in messages:
            self.assertLessEqual(
                len(message["embeds"][0]["description"].encode("utf-16-le")) // 2, 3600
            )

    def test_drafts_and_foreign_repositories_are_rejected(self):
        release = published_release()
        release["draft"] = True
        with self.assertRaises(ValueError):
            release_feed.build_payloads(release)
        release["draft"] = False
        release["html_url"] = "https://example.com/release"
        with self.assertRaises(ValueError):
            release_feed.build_payloads(release)

    def test_unicode_title_fits_discord(self):
        release = published_release()
        release["name"] = "a" + "\U0001F3AC" * 239
        title = release_feed.build_payloads(release)[0]["embeds"][0]["title"]
        self.assertEqual(title, "a" + "\U0001F3AC" * 119)

    def test_empty_notes_link_to_release(self):
        message = release_feed.build_payloads(published_release(""))[0]
        self.assertIn("release details", message["embeds"][0]["description"])

    def test_editing_an_announcement_suppresses_mentions(self):
        message = release_feed.build_payloads(published_release(), ping_everyone=False)[0]
        self.assertNotIn("content", message)
        self.assertEqual(message["allowed_mentions"], {"parse": []})

    def test_stable_notes_require_a_populated_improvements_and_bug_fixes_section(self):
        valid = "## Improvements and bug fixes\n\n- Playback resumes more reliably.\n\n## Updating\n\nUse Setup."
        release_feed.validate_release_notes(valid)
        release_feed.validate_release_notes(valid.replace("## Improvements and bug fixes", "## Improvements and bug fixes ##"))
        release_feed.validate_release_notes(valid.replace("- Playback", "### Playback\n\n- Playback"))

        for notes in (
            valid.replace("Improvements and bug fixes", "New features"),
            valid.replace("- Playback resumes more reliably.", "<!-- TODO -->"),
            "````markdown\n```\n" + valid + "\n````",
            "~~~markdown\n" + valid + "\n~~~",
            "## Improvements and bug fixes\n\n##\n\nText outside the section.",
            "## Improvements and bug fixes\n\n### Playback\n\n## Updating\n\nText outside the section.",
        ):
            with self.subTest(notes=notes), self.assertRaisesRegex(ValueError, "Improvements and bug fixes"):
                release_feed.validate_release_notes(notes)

    def test_workflow_edits_the_existing_message_without_posting_or_pinging(self):
        webhook = "https://discord.com/api/webhooks/123/test-token"
        edit_id = "1573447742682833044"
        environment = {"DISCORD_RELEASE_WEBHOOK_URL": webhook, "DISCORD_EDIT_MESSAGE_ID": edit_id}
        release = published_release("## Improvements and bug fixes\n\n- Playback resumes more reliably.")
        original = {"id": edit_id, "embeds": [{"url": release["html_url"]}]}
        payload = release_feed.build_payloads(release, ping_everyone=False)[0]
        edited = {"id": edit_id, "embeds": payload["embeds"]}
        endpoint = f"{webhook}/messages/{edit_id}"
        with (patch.dict("os.environ", environment, clear=True),
              patch("sys.argv", ["announce-discord-release.py"]),
              patch.object(release_feed, "load_release", return_value=release),
              patch.object(release_feed, "request_json", side_effect=[original, edited]) as request,
              patch("builtins.print")):
            release_feed.main()
            self.assertEqual(request.call_args_list, [call(endpoint), call(endpoint, payload=payload, method="PATCH")])

    def test_workflow_rejects_an_edit_for_a_different_announcement(self):
        environment = {"DISCORD_RELEASE_WEBHOOK_URL": "https://discord.com/api/webhooks/123/test-token",
                       "DISCORD_EDIT_MESSAGE_ID": "1573447742682833044"}
        original = {"embeds": [{"url": release_feed.RELEASE_PREFIX + "tag/v1.8.11"}]}
        with (patch.dict("os.environ", environment, clear=True),
              patch("sys.argv", ["announce-discord-release.py"]),
              patch.object(release_feed, "load_release", return_value=published_release()),
              patch.object(release_feed, "request_json", return_value=original) as request):
            with self.assertRaisesRegex(ValueError, "not the announcement for this release"):
                release_feed.main()
            self.assertEqual(request.call_count, 1)

    def test_long_announcement_edit_updates_one_message_and_links_to_remaining_notes(self):
        webhook = "https://discord.com/api/webhooks/123/test-token"
        edit_id = "1573447742682833044"
        environment = {"DISCORD_RELEASE_WEBHOOK_URL": webhook, "DISCORD_EDIT_MESSAGE_ID": edit_id}
        release = published_release(("Playback 🎬 improves.\n\n" * 600).strip())
        endpoint = f"{webhook}/messages/{edit_id}"
        original = {"id": edit_id, "embeds": [{"url": release["html_url"]}]}

        def reply(url, *, payload=None, method=None):
            self.assertEqual(url, endpoint)
            if method is None:
                return original
            self.assertEqual(method, "PATCH")
            self.assertNotIn("content", payload)
            description = payload["embeds"][0]["description"]
            self.assertLessEqual(len(description.encode("utf-16-le")) // 2, 3600)
            self.assertIn(f"[full changelog]({release['html_url']})", description)
            return {"id": edit_id, "embeds": payload["embeds"]}

        with (patch.dict("os.environ", environment, clear=True),
              patch("sys.argv", ["announce-discord-release.py"]),
              patch.object(release_feed, "load_release", return_value=release),
              patch.object(release_feed, "request_json", side_effect=reply) as request,
              patch("builtins.print")):
            release_feed.main()
            self.assertEqual(request.call_count, 2)

    def test_workflow_dry_run_checks_webhook_without_posting(self):
        webhook = "https://discord.com/api/webhooks/123/test-token"
        environment = {"DISCORD_RELEASE_WEBHOOK_URL": webhook, "DISCORD_DRY_RUN": "true"}
        with (patch.dict("os.environ", environment),
              patch("sys.argv", ["announce-discord-release.py"]),
              patch.object(release_feed, "load_release", return_value=published_release()),
              patch.object(release_feed, "request_json") as request,
              patch("builtins.print")):
            release_feed.main()
            request.assert_called_once_with(webhook)

    def test_saved_release_dry_run_works_offline_without_a_webhook(self):
        with TemporaryDirectory() as directory:
            path = Path(directory) / "release.json"
            path.write_text(json.dumps(published_release()), encoding="utf-8")
            with (patch.dict("os.environ", {}, clear=True), patch("builtins.print"),
                  patch("sys.argv", ["announce-discord-release.py", "--release-json", str(path), "--dry-run"]),
                  patch.object(release_feed, "request_json") as request):
                release_feed.main()
                request.assert_not_called()

    def test_rate_limit_retries_use_provider_delay_and_tolerate_nonobject_responses(self):
        url = "https://example.com/test"
        for body, expected in [(b'{"retry_after":12.5}', 12.5), (b'{"retry_after":"NaN"}', 3), (b"null", 3), (b"", 3)]:
            with self.subTest(body=body):
                limited = HTTPError(url, 429, "", {"Retry-After": "3"}, BytesIO(body))
                with (patch.object(release_feed, "urlopen", side_effect=[limited, BytesIO(b"{}")]),
                      patch.object(release_feed.time, "sleep") as sleep):
                    self.assertEqual(release_feed.request_json(url), {})
                    sleep.assert_called_once_with(expected)

        limited = HTTPError(url, 429, "", {"Retry-After": "120"}, BytesIO(b""))
        with (patch.object(release_feed, "urlopen", side_effect=limited) as request,
              patch.object(release_feed.time, "sleep") as sleep):
            with self.assertRaisesRegex(RuntimeError, "Retry after 120 seconds"):
                release_feed.request_json(url)
            self.assertEqual(request.call_count, 1)
            sleep.assert_not_called()

    def test_workflow_call_fetches_the_published_release_by_tag(self):
        release = published_release()
        with TemporaryDirectory() as directory:
            event_path = Path(directory) / "event.json"
            event_path.write_text(json.dumps({"inputs": {"tag": "v1.8.12"}}), encoding="utf-8")
            environment = {"GITHUB_EVENT_PATH": str(event_path), "DISCORD_RELEASE_TAG": release["tag_name"],
                           "GITHUB_TOKEN": "fixture"}
            with (patch.dict("os.environ", environment),
                  patch.object(release_feed, "request_json", return_value=release) as request):
                self.assertEqual(release_feed.load_release(), release)
                request.assert_called_once_with(
                    f"https://api.github.com/repos/{release_feed.REPOSITORY}/releases/tags/v1.8.12", token="fixture"
                )

    def test_network_errors_hide_credentials_and_do_not_retry_ambiguous_sends(self):
        private_url = "https://discord.com/api/webhooks/123/private-posting-key"
        for error in [HTTPError(private_url, 401, "Unauthorized", {}, None),
                      URLError(private_url)]:
            with self.subTest(error=type(error).__name__):
                with patch.object(release_feed, "urlopen", side_effect=error) as request:
                    with self.assertRaises(RuntimeError) as raised:
                        release_feed.request_json(private_url, payload={}, method="POST")
                    self.assertNotIn(private_url, str(raised.exception))
                    self.assertEqual(request.call_count, 1)


if __name__ == "__main__":
    unittest.main()
