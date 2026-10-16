"""Regenerate public geometry; optionally import pinned public-domain UK/Kosovo/USA outlines."""

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
    parser.add_argument("--import-kosovo", action="store_true", help="Download and verify Natural Earth 5.1.2 Kosovo geometry.")
    parser.add_argument("--import-usa", action="store_true", help="Import USA as one North American country, including Alaska and Hawaii.")
    args = parser.parse_args()
    geometry_path = ROOT / "MapGame" / "Data" / "maps.json"
    data = json.loads(geometry_path.read_text(encoding="utf-8"))
    labels = json.loads((geometry_path.parent / "country-names.json").read_text(encoding="utf-8"))
    if args.import_uk or args.import_kosovo or args.import_usa:
        with urlopen(SOURCE, timeout=120) as response:
            raw = response.read()
        if hashlib.sha256(raw).hexdigest() != SOURCE_SHA256:
            raise ValueError("Natural Earth source checksum mismatch.")
        subunits = {f["properties"]["SU_A3"]: f["geometry"] for f in json.loads(raw)["features"]}
        europe = next(r for r in data["regions"] if r["id"] == "europe")
    if args.import_uk:
        ireland = next(f for f in europe["features"] if f["id"] == "IRL")
        if ireland["geometry"] != subunits["IRL"]:
            raise ValueError("Ireland no longer matches this source; review the shared border before importing.")
        for feature in europe["features"]:
            if feature["id"] in UK_IDS:
                feature["geometry"] = subunits[UK_IDS[feature["id"]]]
        if not set(UK_IDS).issubset({f["id"] for f in europe["features"]}):
            raise ValueError("The four UK feature IDs must already exist.")
        write_json(geometry_path, data)

    if args.import_kosovo:
        kosovo = next((feature for feature in europe["features"] if feature["id"] == "KOS"), None)
        if kosovo is None:
            europe["features"].append({"id": "KOS", "geometry": subunits["KOS"]})
        else:
            kosovo["geometry"] = subunits["KOS"]
        write_json(geometry_path, data)

    if args.import_usa:
        north_america = next(region for region in data["regions"] if region["id"] == "north-america")
        polygons = []
        for code in ("USB", "USK", "USH"):
            shape = subunits[code]
            polygons.extend(shape["coordinates"] if shape["type"] == "MultiPolygon" else [shape["coordinates"]])
        usa = next((feature for feature in north_america["features"] if feature["id"] == "USA"), None)
        if usa is None:
            usa = {"id": "USA"}
            north_america["features"].append(usa)
        usa["geometry"] = {"type": "MultiPolygon", "coordinates": polygons}
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
