"""Offline validation of the frozen, artifact-scoped native proof."""
import hashlib
import json
from pathlib import Path
import re
import zipfile
from recipe import verify, qualify, verify_link, vex


def validate(document, pin, verified, graph, native_hash, rid):
    if document != vex(pin, verified, graph, native_hash, rid):
        raise ValueError("OpenVEX does not match exact patched source, archive and native artifact")


def validate_frozen_bundle(native, rid, output):
    pin = json.loads((native / "native-stack.json").read_text(encoding="utf-8-sig"))
    expected_entry = "datachannel.dll" if rid.startswith("win-") else "libdatachannel.dylib" if rid.startswith("osx-") else "libdatachannel.so"
    with zipfile.ZipFile(output / "bundle.zip") as bundle:
        names = bundle.namelist()
        if len(set(names)) != len(names):
            raise ValueError("Duplicate native bundle entries")
        manifest_bytes = bundle.read("manifest.json")
        manifest = json.loads(manifest_bytes)
        if (manifest.get("version") != 1 or manifest.get("runtimeIdentifier") != rid
                or manifest.get("entryPoint") != expected_entry
                or manifest.get("versions", {}).get("vcpkgCommit") != pin["vcpkgCommit"]):
            raise ValueError("Native bundle provenance mismatch")
        for name, version in pin["versions"].items():
            if manifest["versions"].get(name) != version:
                raise ValueError("Native bundle dependency version mismatch")
        records = manifest["files"]
        declared = {record["name"] for record in records}
        if len(declared) != len(records) or declared | {"manifest.json"} != set(names):
            raise ValueError("Native bundle contains undeclared or duplicate files")
        payload = {}
        for record in records:
            name = record["name"]
            if (not re.fullmatch(r"[A-Za-z0-9_.+-]+", name)
                    or Path(name).suffix.lower() in (".exe", ".com", ".bat", ".sh")):
                raise ValueError("Native bundle file integrity mismatch")
            payload[name] = bundle.read(name)  # ZIP CRC is checked by read().
            if hashlib.sha256(payload[name]).hexdigest() != record["sha256"]:
                raise ValueError("Native bundle file integrity mismatch")
        if ((output / "manifest.json").read_bytes() != manifest_bytes
                or (output / "native-dependencies.spdx.json").read_bytes() != payload["native-dependencies.spdx.json"]):
            raise ValueError("Detached native metadata differs from embedded bundle")
        graph = json.loads(payload["native-dependencies.spdx.json"])
        native_hash = hashlib.sha256(payload[expected_entry]).hexdigest()
    verified = manifest.get("backports", {})
    if set(verified) != set(pin.get("backports", {})):
        raise ValueError("Frozen manifest is missing the reviewed backport proof")
    for name, expected in pin.get("backports", {}).items():
        evidence = verified[name]
        if (manifest.get("portVersions", {}).get(name) != expected["portVersion"]
                or evidence.get("version") != 1
                or any(evidence.get(key) != expected[key] for key in ("commit", "patchSha256", "sourceSha256", "cve"))
                or not re.fullmatch(r"[A-Za-z0-9_.+-]+", evidence.get("archive", ""))
                or not re.fullmatch(r"[0-9a-f]{64}", evidence.get("archiveSha256", ""))):
            raise ValueError("Frozen compiled backport does not match reviewed source/version pins")
    link = manifest.get("linkEvidence", {})
    if (link.get("version") != 1 or link.get("runtimeIdentifier") != rid
            or link.get("entryPoint") != expected_entry or link.get("entryPointSha256") != native_hash
            or link.get("backports") != verified):
        raise ValueError("Native bundle differs from frozen link output and patched static inputs")
    document = json.loads((output / "native-backports.vex.json").read_bytes())
    validate(document, pin, verified, graph, native_hash, rid)
    embedded = [json.loads(annotation["comment"]) for annotation in graph.get("annotations", []) if annotation.get("annotator") == "Tool: tlbx native build"]
    if embedded != [document]:
        raise ValueError("Detached OpenVEX does not match embedded hashed attestation")
    return manifest, graph, document


def validate_bundle(native, rid):
    import prebuilt
    _, _, document = prebuilt.validate(native, rid, native / "bin" / rid)
    print("PASS: frozen source/archive/link OpenVEX and committed native artifact hashes match.")
    return document


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", required=True)
    validate_bundle(Path(__file__).resolve().parent, parser.parse_args().rid)
