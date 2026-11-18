import importlib.util
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch
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

    def test_empty_notes_link_to_release(self):
        message = release_feed.build_payloads(published_release(""))[0]
        self.assertIn("release details", message["embeds"][0]["description"])

    def test_editing_an_announcement_suppresses_mentions(self):
        message = release_feed.build_payloads(published_release(), ping_everyone=False)[0]
        self.assertNotIn("content", message)
        self.assertEqual(message["allowed_mentions"], {"parse": []})

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
