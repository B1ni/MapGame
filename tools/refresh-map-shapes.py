"""Regenerate public geometry; optionally reimport the pinned, public-domain UK subunits."""

import argparse
import hashlib
import json
from pathlib import Path
from urllib.request import urlopen


ROOT = Path(__file__).resolve().parent.parent
SOURCE = (
    "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/"
    "v5.1.2/geojson/ne_50m_admin_0_map_subunits.geojson"
)
SOURCE_SHA256 = "b1df8433477614c89e6b724990a7ab3733ec647559863321b6255c3fa3fce3ec"
UK_IDS = {"GB-ENG": "ENG", "GB-SCT": "SCT", "GB-WLS": "WLS", "GB-NIR": "NIR"}


def write_json(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--import-uk", action="store_true", help="Download and verify Natural Earth 5.1.2 UK geometry.")
    args = parser.parse_args()
    geometry_path = ROOT / "MapGame" / "Data" / "maps.json"
    data = json.loads(geometry_path.read_text(encoding="utf-8"))
    labels = json.loads((geometry_path.parent / "country-names.json").read_text(encoding="utf-8"))
    if args.import_uk:
        with urlopen(SOURCE, timeout=120) as response:
            raw = response.read()
        if hashlib.sha256(raw).hexdigest() != SOURCE_SHA256:
            raise ValueError("Natural Earth source checksum mismatch.")
        subunits = {f["properties"]["SU_A3"]: f["geometry"] for f in json.loads(raw)["features"]}
        europe = next(r for r in data["regions"] if r["id"] == "europe")
        ireland = next(f for f in europe["features"] if f["id"] == "IRL")
        if ireland["geometry"] != subunits["IRL"]:
            raise ValueError("Ireland no longer matches this source; review the shared border before importing.")
        for feature in europe["features"]:
            if feature["id"] in UK_IDS:
                feature["geometry"] = subunits[UK_IDS[feature["id"]]]
        if not set(UK_IDS).issubset({f["id"] for f in europe["features"]}):
            raise ValueError("The four UK feature IDs must already exist.")
        write_json(geometry_path, data)

    public = {"regions": []}
    for region in data["regions"]:
        public["regions"].append({
            "id": region["id"],
            "name": labels["regions"][region["id"]]["names"]["sk"]["name"],
            "features": [{"id": f["id"], "geometry": f["geometry"]} for f in region["features"]],
        })
    write_json(ROOT / "MapGame" / "wwwroot" / "data" / "map-shapes.json", public)
    print("Public geometry refreshed; country/state answers remain server-only.")


if __name__ == "__main__":
    main()
