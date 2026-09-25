"""Check built documentation for missing local pages, assets and anchors.

Usage: python Tools/Documentation/check_site.py <site directory>
This offline check does not request external URLs.
"""
from html.parser import HTMLParser
from pathlib import Path
import json
import sys
from urllib.parse import unquote, urlsplit


class Page(HTMLParser):
    def __init__(self, text):
        super().__init__(convert_charrefs=True)
        self.ids = set()
        self.links = []
        self.feed(text)

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if attrs.get("id"):
            self.ids.add(attrs["id"])
        if tag == "a" and attrs.get("name"):
            self.ids.add(attrs["name"])
        attribute = "href" if tag in ("a", "link") else "src"
        if tag in ("a", "link", "img", "script", "source") and attrs.get(attribute):
            self.links.append(attrs[attribute])


def check(root):
    root = root.resolve()
    if not (root / "index.html").is_file():
        raise ValueError("Expected a built site directory containing index.html")
    pages = {p.resolve(): Page(p.read_text(encoding="utf-8")) for p in root.rglob("*.html")}
    errors = []
    checked = 0
    for source, page in pages.items():
        # The error page is served at unknown URLs, so relative navigation has no fixed base.
        if source == root / "404.html":
            continue
        for link in page.links:
            parsed = urlsplit(link)
            if parsed.scheme or parsed.netloc:
                continue
            path = unquote(parsed.path)
            if path.startswith("/Batcomputer/"):
                target = root / path[len("/Batcomputer/"):]
            elif path.startswith("/"):
                target = root / path.lstrip("/")
            else:
                target = source.parent / path if path else source
            target = target.resolve()
            if target.is_dir():
                target /= "index.html"
            checked += 1
            if not target.is_relative_to(root) or not target.is_file():
                errors.append(f"{source.relative_to(root)}: missing target {link}")
            elif parsed.fragment and target.suffix == ".html":
                fragment = unquote(parsed.fragment)
                if fragment not in pages[target].ids:
                    errors.append(f"{source.relative_to(root)}: missing anchor {link}")
    excluded = ("research/", "guides/custom-equipment-proof/", "guides/equipment-inventory/",
                "guides/development-roadmap/", "releases/1.0.0-beta.3/")
    search = root / "search" / "search_index.json"
    if not search.is_file():
        errors.append("Search index was not generated")
    else:
        for entry in json.loads(search.read_text(encoding="utf-8"))["docs"]:
            if entry["location"].startswith(excluded):
                errors.append("Historical research leaked into search: " + entry["location"])
    for error in errors:
        print(error)
    print(f"Checked {len(pages)} HTML pages and {checked} local references; {len(errors)} errors.")
    return bool(errors)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    raise SystemExit(check(Path(sys.argv[1])))
