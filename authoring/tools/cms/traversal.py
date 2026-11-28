"""Traversal spots (#11226): the stall records, reports and 3D dumps the live walk lane writes.

`node scripts/live.mjs cavewalk` writes each stall to a folder under `authoring/assets/traversal-spots/`: `stall.json`
(where and how the walk gave up), `why.txt` (`craft.cave.why`'s report) and `spot.json` (`craft.cave.dump`'s 3D dump).
"""

import re

from router import NotFoundError

SPOTS = "traversal-spots"
NAME = re.compile(r"^[A-Za-z0-9._-]+$")


class TraversalStore:
    def __init__(self, library, cache):
        self.library = library
        self.cache = cache

    @property
    def root(self):
        return self.library.authoring_assets / SPOTS

    def _json(self, path):
        return self.cache.json(path)

    def listing(self):
        spots = []
        if self.root.is_dir():
            for folder in self.root.iterdir():
                if not folder.is_dir() or not (folder / "spot.json").is_file():
                    continue
                stall = self._json(folder / "stall.json") or {}
                why = (folder / "why.txt").read_text(encoding="utf-8") if (folder / "why.txt").is_file() else ""
                verdict = next((line.removeprefix("verdict: ") for line in why.splitlines() if line.startswith("verdict: ")), None)
                spots.append({"name": folder.name, "modified": (folder / "spot.json").stat().st_mtime, "from": stall.get("from"),
                              "to": stall.get("to"), "along": stall.get("along"), "wayLength": stall.get("wayLength"), "verdict": verdict})
        spots.sort(key=lambda spot: spot["modified"], reverse=True)
        return {"root": self.library.rel(self.root) if self.root.exists() else f"authoring/assets/{SPOTS}", "spots": spots}

    def spot(self, name):
        folder = self.root / name
        if not NAME.match(name) or not (folder / "spot.json").is_file():
            raise NotFoundError(f"No traversal spot {name}.")
        why = folder / "why.txt"
        return {"name": name, "stall": self._json(folder / "stall.json"), "why": why.read_text(encoding="utf-8") if why.is_file() else "",
                "spot": self._json(folder / "spot.json")}


def register_routes(router, workspace):
    store = TraversalStore(workspace.library, workspace.cache)
    router.get("/api/traversal", lambda _req: store.listing())
    router.get("/api/traversal/{name}", lambda req: store.spot(req.params["name"]))
