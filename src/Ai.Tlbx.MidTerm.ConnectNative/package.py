"""Package the actual RID build, its source SBOM and notices for in-process loading."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def package(rid, installed, output=None, verify_load=True):
    source = Path(__file__).resolve().parent
    pin = json.loads((source / "native-stack.json").read_text())
    prefix = installed / rid
    entry = "datachannel.dll" if rid.startswith("win-") else "libdatachannel.dylib" if rid.startswith("osx-") else "libdatachannel.so"
    candidates = list((prefix / "bin").glob("*.dll")) if rid.startswith("win-") else [p for p in (prefix / "lib").iterdir() if ".so" in p.name or p.suffix == ".dylib"]
    if not any(p.name == entry for p in candidates):
        raise ValueError(f"Missing native entry point: {entry}")
    # A vcpkg source SBOM includes the exact port files and downloaded source hashes.
    packages = []
    files = []
    relationships = []
    licenses = []
    notices = []
    versions = {"vcpkgCommit": pin["vcpkgCommit"]}
    port_versions = {}
    raw_evidence = {}
    for recipe in sorted((prefix / "share").glob("*/vcpkg.spdx.json")):
        name = recipe.parent.name
        if name.startswith("vcpkg-"):
            continue  # Build tooling is not linked into the runtime.
        doc = json.loads(recipe.read_text())
        raw_evidence[name] = recipe.read_bytes()
        port = next(p for p in doc["packages"] if p["SPDXID"] == "SPDXRef-port")
        port_versions[name] = port["versionInfo"]
        versions[name] = port["versionInfo"].split("#", 1)[0]
        metadata = source / "ports" / name / "vcpkg.json"
        if not metadata.exists():
            metadata = installed.parent / "vcpkg" / "ports" / name / "vcpkg.json"
        port_license = json.loads(metadata.read_text()).get("license") or "NOASSERTION"
        prefix_id = "SPDXRef-native-" + name + "-"
        def rewrite(value):
            if value == "SPDXRef-DOCUMENT":
                return value
            if isinstance(value, str) and value.startswith("SPDXRef-"):
                return prefix_id + value[len("SPDXRef-"):]
            if isinstance(value, list):
                return [rewrite(item) for item in value]
            if isinstance(value, dict):
                return {key: rewrite(item) for key, item in value.items()}
            return value
        for component in doc["packages"]:
            component = rewrite(component)
            component["sourceInfo"] = f"Connect {rid}; vcpkg {pin['vcpkgCommit']}; native build recipe and source inputs. " + component.get("sourceInfo", "")
            if component["SPDXID"] == prefix_id + "port":
                component["licenseDeclared"] = port_license
                if name in pin.get("cpeProducts", {}):
                    component.setdefault("externalRefs", []).append({"referenceCategory": "SECURITY", "referenceType": "cpe23Type", "referenceLocator": pin["cpeProducts"][name].format(version=versions[name])})
            packages.append(component)
        files.extend(rewrite(doc.get("files", [])))
        relationships.extend(rewrite(doc.get("relationships", [])))
        licenses.extend(doc.get("hasExtractedLicensingInfos", []))
        copyright_file = recipe.parent / "copyright"
        if not copyright_file.is_file():
            raise ValueError(f"Missing native dependency notices: {name}")
        notices.append(f"===== {name} {versions[name]} ({port_license}) =====\n" + copyright_file.read_text(errors="strict"))
    for name, version in pin["versions"].items():
        if versions.get(name) != version:
            raise ValueError(f"Native version mismatch: {name} expected {version}, got {versions.get(name)}")
    output = output or source / "bin" / rid
    output.mkdir(parents=True, exist_ok=True)
    evidence = output / "source-sboms"
    evidence.mkdir(exist_ok=True)
    for name, data in raw_evidence.items():
        (evidence / (name + ".spdx.json")).write_bytes(data)
    with tempfile.TemporaryDirectory(prefix="tlbx-connect-native-") as staging_name:
        staging = Path(staging_name)
        entry_source = next(path.resolve() for path in candidates if path.name == entry)
        for binary in candidates:
            if binary.is_symlink() and binary.name != entry:
                continue
            if binary.name != entry and binary.resolve() == entry_source:
                continue
            destination = staging / binary.name
            shutil.copyfile(binary.resolve(), destination)
            if rid.startswith("osx-") and os.environ.get("GITHUB_ACTIONS") == "true":
                subprocess.run(["codesign", "--force", "--sign", "Developer ID Application: Johannes Schmidt (FK7G5C74WH)", "--options", "runtime", "--timestamp", str(destination)], check=True)
                subprocess.run(["codesign", "--verify", "--strict", str(destination)], check=True)
        if verify_load:
            subprocess.run(["pwsh", "-NoProfile", "-File", str(source / "smoke.ps1"), "-RuntimeIdentifier", rid, "-NativeDirectory", str(staging), "-NativePrefix", str(prefix)], check=True)
        (staging / "THIRD-PARTY-NOTICES.txt").write_text("\n\n".join(notices), encoding="utf-8")
        document = {"spdxVersion": "SPDX-2.3", "dataLicense": "CC0-1.0", "SPDXID": "SPDXRef-DOCUMENT", "name": f"tlbx-connect-{rid}", "documentNamespace": f"https://tlbx.ai/spdx/connect/{rid}/{pin['vcpkgCommit']}", "creationInfo": doc["creationInfo"], "packages": packages, "files": files, "relationships": relationships}
        if licenses:
            document["hasExtractedLicensingInfos"] = licenses
        (staging / "native-dependencies.spdx.json").write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
        shutil.copyfile(staging / "native-dependencies.spdx.json", output / "native-dependencies.spdx.json")
        manifest = {"version": 1, "runtimeIdentifier": rid, "entryPoint": entry, "versions": versions, "portVersions": port_versions, "files": []}
        for path in sorted(staging.iterdir()):
            if not re.fullmatch(r"[A-Za-z0-9_.+-]+", path.name) or path.suffix.lower() in (".exe", ".com", ".bat", ".sh"):
                raise ValueError("Unexpected executable or unsafe native bundle name")
            manifest["files"].append({"name": path.name, "sha256": sha256(path.read_bytes())})
        (staging / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        shutil.copyfile(staging / "manifest.json", output / "manifest.json")
        archive = output / "bundle.zip"
        with zipfile.ZipFile(archive.with_suffix(".tmp"), "w", zipfile.ZIP_DEFLATED) as bundle:
            for path in sorted(staging.iterdir()):
                info = zipfile.ZipInfo(path.name, (1980, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                bundle.writestr(info, path.read_bytes())
        archive.with_suffix(".tmp").replace(archive)
        print(f"Native bundle: {rid}, {len(manifest['files'])} files, {len(versions)-1} dependency versions, SHA256 {sha256(archive.read_bytes())}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", required=True)
    parser.add_argument("--installed", type=Path, required=True)
    args = parser.parse_args()
    package(args.rid, args.installed.resolve())
