"""Include the verified embedded native dependency graph in a release SPDX SBOM."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import zipfile


def complete(sbom_path, native=None):
    native = native or Path(__file__).resolve().parent
    match = re.fullmatch(r"mt-(win-x64|win-x86|linux-x64|linux-arm64|osx-x64|osx-arm64)\.spdx\.json", sbom_path.name)
    if not match:
        raise ValueError("Expected a platform-specific release SBOM filename")
    rid = match[1]
    pin = json.loads((native / "native-stack.json").read_text())
    archive = native / "bin" / rid / "bundle.zip"
    with zipfile.ZipFile(archive) as bundle:
        manifest = json.loads(bundle.read("manifest.json"))
        if manifest["version"] != 1 or manifest["runtimeIdentifier"] != rid or manifest["versions"]["vcpkgCommit"] != pin["vcpkgCommit"]:
            raise ValueError("Native bundle provenance mismatch")
        if len(set(bundle.namelist())) != len(bundle.namelist()):
            raise ValueError("Duplicate native bundle entries")
        declared = {entry["name"] for entry in manifest["files"]}
        if declared | {"manifest.json"} != set(bundle.namelist()):
            raise ValueError("Native bundle contains undeclared files")
        for entry in manifest["files"]:
            if (not re.fullmatch(r"[A-Za-z0-9_.+-]+", entry["name"]) or
                    Path(entry["name"]).suffix.lower() in (".exe", ".com", ".bat", ".sh") or
                    hashlib.sha256(bundle.read(entry["name"])).hexdigest() != entry["sha256"]):
                raise ValueError("Native bundle file integrity mismatch")
        for name, version in pin["versions"].items():
            if manifest["versions"].get(name) != version:
                raise ValueError("Native bundle dependency version mismatch")
        graph = json.loads(bundle.read("native-dependencies.spdx.json"))
    document = json.loads(sbom_path.read_text(encoding="utf-8-sig"))
    existing_ids = {entry["SPDXID"] for key in ("packages", "files") for entry in document.get(key, [])}
    native_ids = {entry["SPDXID"] for key in ("packages", "files") for entry in graph.get(key, [])}
    if existing_ids & native_ids:
        raise ValueError("Native SBOM already merged or SPDX IDs collide")
    bundle_id = "SPDXRef-tlbx-connect-native-bundle"
    document.setdefault("packages", []).append({"name": "tlbx-connect-native", "SPDXID": bundle_id, "versionInfo": manifest["versions"]["libdatachannel"], "downloadLocation": "NOASSERTION", "filesAnalyzed": False, "licenseConcluded": "NOASSERTION", "licenseDeclared": "NOASSERTION", "copyrightText": "NOASSERTION", "sourceInfo": f"ZIP embedded in mt as manifest resource tlbx.native.connect.bundle; RID {rid}; pinned vcpkg {pin['vcpkgCommit']}", "checksums": [{"algorithm": "SHA256", "checksumValue": hashlib.sha256(archive.read_bytes()).hexdigest()}]})
    for key in ("packages", "files", "relationships", "hasExtractedLicensingInfos"):
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
