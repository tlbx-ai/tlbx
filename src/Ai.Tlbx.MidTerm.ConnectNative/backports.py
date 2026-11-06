"""Verify compiled upstream backports and emit artifact-scoped OpenVEX fixed evidence."""
import hashlib
import json
from pathlib import Path
import zipfile
from urllib.parse import parse_qsl, urlencode


def verify(prefix, pin, port_versions):
    verified = {}
    for name, expected in pin.get("backports", {}).items():
        if port_versions.get(name) != expected["portVersion"]:
            raise ValueError("Backport port version does not match reviewed native recipe")
        evidence = json.loads((prefix / "share" / name / "tlbx-backport.json").read_text())
        for key in ("commit", "patchSha256", "sourceSha256", "cve"):
            if evidence[key] != expected[key]:
                raise ValueError("Compiled backport source evidence does not match reviewed upstream fix")
        archive = prefix / "lib" / evidence["archive"]
        if archive.parent != prefix / "lib" or hashlib.sha256(archive.read_bytes()).hexdigest() != evidence["archiveSha256"]:
            raise ValueError("Patched static archive integrity mismatch")
        verified[name] = evidence
    return verified


def qualify(component, evidence):
    refs = [ref for ref in component.get("externalRefs", []) if ref["referenceType"] == "purl"]
    if len(refs) != 1:
        raise ValueError("Backported component needs exactly one authentic package URL")
    base, query = refs[0]["referenceLocator"].split("?", 1)
    qualifiers = dict(parse_qsl(query))
    qualifiers["checksum"] = "sha256:" + evidence["archiveSha256"]
    refs[0]["referenceLocator"] = base + "?" + urlencode(sorted(qualifiers.items()))
    return refs[0]["referenceLocator"]


def verify_link(prefix, verified, rid):
    proof = json.loads((prefix / "share/libdatachannel/tlbx-link.json").read_text())
    expected_entry = "datachannel.dll" if rid.startswith("win-") else "libdatachannel.dylib" if rid.startswith("osx-") else "libdatachannel.so"
    entry = prefix / ("bin" if rid.startswith("win-") else "lib") / expected_entry
    if (proof.get("version") != 1 or proof.get("runtimeIdentifier") != rid or proof.get("entryPoint") != expected_entry
            or proof.get("backports") != verified or hashlib.sha256(entry.read_bytes()).hexdigest() != proof.get("entryPointSha256")):
        raise ValueError("Native DLL does not match frozen link/install output and patched static inputs")
    return proof


def vex(pin, verified, graph, native_hash, rid):
    statements = []
    for name, evidence in verified.items():
        component = next(p for p in graph["packages"] if p["SPDXID"] == f"SPDXRef-native-{name}-port")
        purl = next(r["referenceLocator"] for r in component["externalRefs"] if r["referenceType"] == "purl")
        expected = pin["backports"][name]
        statements.append({"vulnerability": {"name": evidence["cve"]}, "products": [{"@id": purl, "hashes": {"sha-256": evidence["archiveSha256"]}}], "status": "fixed", "status_notes": f"Exact upstream fix {expected['upstream']}; patch SHA256 {evidence['patchSha256']}; compiled source SHA256 {evidence['sourceSha256']}; static archive SHA256 {evidence['archiveSha256']}. Native {rid} entrypoint SHA256 {native_hash}. Maintainer confirmation: {expected['maintainerConfirmation']}."})
    return {"@context": "https://openvex.dev/ns/v0.2.0", "@id": f"https://tlbx.ai/vex/connect-native/{rid}/{native_hash}", "author": "tlbx native build", "timestamp": graph["creationInfo"]["created"], "version": 1, "statements": statements}


def validate(document, pin, verified, graph, native_hash, rid):
    if document != vex(pin, verified, graph, native_hash, rid):
        raise ValueError("OpenVEX does not match exact patched source, archive and native artifact")


def validate_bundle(native, rid):
    pin = json.loads((native / "native-stack.json").read_text())
    output = native / "bin" / rid
    with zipfile.ZipFile(output / "bundle.zip") as bundle:
        manifest = json.loads(bundle.read("manifest.json"))
        for record in manifest["files"]:
            if hashlib.sha256(bundle.read(record["name"])).hexdigest() != record["sha256"]:
                raise ValueError("Backport VEX native bundle file integrity mismatch")
        graph = json.loads(bundle.read("native-dependencies.spdx.json"))
        native_hash = hashlib.sha256(bundle.read(manifest["entryPoint"])).hexdigest()
    prefix = native.parents[1] / ".artifacts/connect-dotnet-transport/installed" / rid
    verified = verify(prefix, pin, manifest["portVersions"])
    link = verify_link(prefix, verified, rid)
    if manifest.get("linkEvidence") != link or native_hash != link["entryPointSha256"]:
        raise ValueError("Native bundle DLL differs from the verified link/install output")
    if manifest.get("backports") != verified:
        raise ValueError("Native manifest does not describe the compiled backport")
    document = json.loads((output / "native-backports.vex.json").read_text())
    validate(document, pin, verified, graph, native_hash, rid)
    embedded = [json.loads(a["comment"]) for a in graph.get("annotations", []) if a.get("annotator") == "Tool: tlbx native build"]
    if embedded != [document]:
        raise ValueError("Detached OpenVEX does not match the embedded, hashed attestation")
    print("PASS: OpenVEX fixed evidence matches exact upstream source, compiled archive and native DLL hashes.")
    return document


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", required=True)
    validate_bundle(Path(__file__).resolve().parent, parser.parse_args().rid)
