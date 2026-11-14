"""Publication checks for the buildless GitHub Pages product page.

Run with: python -m unittest discover -s tests -p test_product_page.py
These checks do not access the network or require a browser.
"""

from collections import Counter
from html.parser import HTMLParser
import json
from pathlib import Path
import re
import shutil
import subprocess
import unittest
from urllib.parse import unquote, urlsplit


ROOT = Path(__file__).resolve().parents[1]
SITE = ROOT / "site"
REPO_URL = "https://github.com/pyrrhuladev/GlimHUD"
INSTALLER_URL = f"{REPO_URL}/releases/latest/download/GlimHUD-Setup-x64.exe"
PORTABLE_URL = f"{REPO_URL}/releases/latest/download/GlimHUD-win-x64.zip"


class ProductPage(HTMLParser):
    def __init__(self, source):
        super().__init__(convert_charrefs=True)
        self.elements = []
        self.feed(source)
        self.close()

    def handle_starttag(self, tag, attrs):
        self.elements.append((tag, dict(attrs)))

    def find(self, tag):
        return [attrs for current_tag, attrs in self.elements if current_tag == tag]


class ProductPageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.html = (SITE / "index.html").read_text(encoding="utf-8")
        cls.page = ProductPage(cls.html)
        cls.script = (SITE / "app.js").read_text(encoding="utf-8")
        cls.css = (SITE / "styles.css").read_text(encoding="utf-8")

    def test_downloads_work_without_javascript_and_follow_latest_release(self):
        links = [a.get("href") for a in self.page.find("a")]
        for url in (INSTALLER_URL, PORTABLE_URL, REPO_URL, f"{REPO_URL}/issues"):
            with self.subTest(url=url):
                self.assertIn(url, links)
        self.assertIn(f"{REPO_URL}/releases/latest", links)
        self.assertIn(f"{REPO_URL}/blob/main/docs/privacy-policy.md", links)

    def test_assets_and_fragments_resolve_under_project_site(self):
        ids = [a["id"] for _, a in self.page.elements if "id" in a]
        self.assertEqual([], [key for key, count in Counter(ids).items() if count > 1])
        references = []
        for tag, attrs in self.page.elements:
            if "src" in attrs:
                references.append(attrs["src"])
            if tag in ("link", "a") and "href" in attrs:
                references.append(attrs["href"])
        references.extend(re.findall(r"url\([\"']?([^)'\"]+)", self.css))
        for reference in references:
            with self.subTest(reference=reference):
                if reference.startswith("#"):
                    self.assertTrue(reference == "#" or reference[1:] in ids)
                    continue
                parsed = urlsplit(reference)
                if parsed.scheme or parsed.netloc:
                    continue
                self.assertFalse(parsed.path.startswith("/"), "Use paths relative to /GlimHUD/.")
                target = (SITE / unquote(parsed.path)).resolve()
                self.assertTrue(target.is_relative_to(SITE.resolve()))
                self.assertTrue(target.is_file(), f"Missing local asset: {reference}")
                self.assertGreater(target.stat().st_size, 0)

    def test_russian_covers_all_visible_and_accessible_translation_keys(self):
        keys = {
            attrs[name]
            for _, attrs in self.page.elements
            for name in ("data-i18n", "data-i18n-aria", "data-i18n-alt")
            if name in attrs
        }
        match = re.search(r"\bru:\s*\{(.*?)^    \}", self.script, re.MULTILINE | re.DOTALL)
        self.assertIsNotNone(match, "Missing Russian dictionary")
        dictionary_json = re.sub(r"^(\s*)(\w+):", r'\1"\2":', match.group(1), flags=re.MULTILINE)
        russian = json.loads("{" + dictionary_json + "}")
        self.assertEqual(keys, set(russian))
        self.assertTrue(all(isinstance(value, str) and value.strip() for value in russian.values()))
        self.assertEqual(self.page.find("html")[0]["lang"], "en")
        self.assertTrue(any("\u0430" <= letter <= "\u044f" for letter in russian["heroTitle"]))

    def test_accessibility_basics(self):
        self.assertEqual(len(self.page.find("main")), 1)
        self.assertEqual(len(self.page.find("h1")), 1)
        for image in self.page.find("img"):
            self.assertIn("alt", image)
        buttons = self.page.find("button")
        self.assertTrue(all(button.get("type") == "button" for button in buttons))
        for attribute, choices in (("data-lang", {"en", "ru"}), ("data-glow", {"neon", "rainbow", "off"})):
            group = [button for button in buttons if attribute in button]
            self.assertEqual({button[attribute] for button in group}, choices)
            self.assertEqual(sum(button.get("aria-pressed") == "true" for button in group), 1)
        self.assertIn("prefers-reduced-motion", self.css)
        self.assertIn(":focus-visible", self.css)

    def test_runtime_resources_are_local_and_preview_is_labelled(self):
        for tag, attrs in self.page.elements:
            if tag in ("script", "img") and "src" in attrs:
                self.assertFalse(urlsplit(attrs["src"]).scheme)
            if tag == "link" and attrs.get("rel") == "stylesheet":
                self.assertFalse(urlsplit(attrs["href"]).scheme)
        self.assertNotRegex(self.css, r"@import\s")
        self.assertIn("SAMPLE DATA", self.html)
        self.assertIn("Sample measurements", self.html)
        self.assertIn("The preview is an animation", self.html)
        self.assertIn("currently unsigned", self.html)
        release = json.loads((SITE / "release.json").read_text(encoding="utf-8"))
        self.assertRegex(release["version"], r"^\d+\.\d+\.\d+(?:[-+][\w.-]+)?$")

    @unittest.skipUnless(shutil.which("node"), "Node.js is unavailable; static checks still run.")
    def test_javascript_syntax(self):
        result = subprocess.run(
            [shutil.which("node"), "--check", str(SITE / "app.js")],
            capture_output=True, text=True, timeout=20, check=False,
        )
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
