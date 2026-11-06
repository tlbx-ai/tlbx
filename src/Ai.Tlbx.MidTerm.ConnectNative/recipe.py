"""Producer-side source/archive/link proof and exact OpenVEX generation."""
import hashlib
import json
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


