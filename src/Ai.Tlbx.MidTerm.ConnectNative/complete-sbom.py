"""Include the verified embedded native dependency graph in a release SPDX SBOM."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import prebuilt


def complete(sbom_path, native=None):
    native = native or Path(__file__).resolve().parent
    match = re.fullmatch(r"mt-(win-x64|win-x86|linux-x64|linux-arm64|osx-x64|osx-arm64)\.spdx\.json", sbom_path.name)
    if not match:
        raise ValueError("Expected a platform-specific release SBOM filename")
    rid = match[1]
    manifest, graph, fixed = prebuilt.validate(native, rid, native / "bin" / rid)
    archive = native / "bin" / rid / "bundle.zip"
    document = json.loads(sbom_path.read_text(encoding="utf-8-sig"))
    if manifest.get("backports"):
        sbom_path.with_name(sbom_path.name.replace('.spdx.json', '.openvex.json')).write_text(json.dumps(fixed, indent=2) + "\n", encoding="utf-8")
    existing_ids = {entry["SPDXID"] for key in ("packages", "files") for entry in document.get(key, [])}
    native_ids = {entry["SPDXID"] for key in ("packages", "files") for entry in graph.get(key, [])}
    if existing_ids & native_ids:
        raise ValueError("Native SBOM already merged or SPDX IDs collide")
    bundle_id = "SPDXRef-tlbx-connect-native-bundle"
    document.setdefault("packages", []).append({"name": "tlbx-connect-native", "SPDXID": bundle_id, "versionInfo": manifest["versions"]["libdatachannel"], "downloadLocation": "NOASSERTION", "filesAnalyzed": False, "licenseConcluded": "NOASSERTION", "licenseDeclared": "NOASSERTION", "copyrightText": "NOASSERTION", "sourceInfo": f"ZIP embedded in mt as manifest resource tlbx.native.connect.bundle; RID {rid}; pinned vcpkg {manifest['versions']['vcpkgCommit']}", "checksums": [{"algorithm": "SHA256", "checksumValue": hashlib.sha256(archive.read_bytes()).hexdigest()}]})
    for key in ("packages", "files", "relationships", "hasExtractedLicensingInfos", "annotations"):
        document.setdefault(key, []).extend(graph.get(key, []))
    relationships = document.setdefault("relationships", [])
    relationships.append({"spdxElementId": "SPDXRef-DOCUMENT", "relationshipType": "DESCRIBES", "relatedSpdxElement": bundle_id})
    for package in graph["packages"]:
        if package["SPDXID"].endswith("-port"):
            relationships.append({"spdxElementId": bundle_id, "relationshipType": "CONTAINS", "relatedSpdxElement": package["SPDXID"]})
    sbom_path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    print(f"SPDX completed with verified {rid} native build graph ({len(graph['packages'])} source/build components).")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--sbom-path", type=Path, required=True)
    complete(parser.parse_args().sbom_path)
