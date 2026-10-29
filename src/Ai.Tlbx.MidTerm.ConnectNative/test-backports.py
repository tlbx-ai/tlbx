"""Negative contracts for source/archive/link-output-bound OpenVEX. No native code."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import backports

ROOT = Path(__file__).resolve().parent


class BackportBindingContracts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tlbx-backport-contract-")
        self.addCleanup(self.temp.cleanup)
        self.prefix = Path(self.temp.name)
        self.pin = json.loads((ROOT / "native-stack.json").read_text())
        expected = self.pin["backports"]["zlib"]
        for path in ("lib", "bin", "share/zlib", "share/libdatachannel"):
            (self.prefix / path).mkdir(parents=True)
        self.archive = self.prefix / "lib/zs.lib"
        self.archive.write_bytes(b"fixture patched archive")
        self.dll = self.prefix / "bin/datachannel.dll"
        self.dll.write_bytes(b"fixture freshly linked DLL")
        self.proof = {key: expected[key] for key in ("commit", "cve", "patchSha256", "sourceSha256")}
        self.proof.update({"version": 1, "archive": "zs.lib", "archiveSha256": hashlib.sha256(self.archive.read_bytes()).hexdigest()})
        self.proof_path = self.prefix / "share/zlib/tlbx-backport.json"
        self.proof_path.write_text(json.dumps(self.proof))
        self.native_hash = hashlib.sha256(self.dll.read_bytes()).hexdigest()
        self.link = {"version": 1, "runtimeIdentifier": "win-x64", "entryPoint": "datachannel.dll", "entryPointSha256": self.native_hash, "backports": {"zlib": self.proof}}
        (self.prefix / "share/libdatachannel/tlbx-link.json").write_text(json.dumps(self.link))
        self.ports = {"zlib": "1.3.2#3"}
        self.graph = {"creationInfo": {"created": "2026-10-29T00:00:00Z"}, "packages": [{"SPDXID": "SPDXRef-native-zlib-port", "externalRefs": [{"referenceType": "purl", "referenceLocator": "pkg:vcpkg/zlib@1.3.2?port_version=3&triplet=win-x64"}]}]}
        backports.qualify(self.graph["packages"][0], self.proof)

    def test_correct_source_archive_and_frozen_link_output_pass(self):
        verified = backports.verify(self.prefix, self.pin, self.ports)
        self.assertEqual(backports.verify_link(self.prefix, verified, "win-x64"), self.link)

    def test_unpatched_port_two_is_rejected(self):
        with self.assertRaises(ValueError):
            backports.verify(self.prefix, self.pin, {"zlib": "1.3.2#2"})

    def test_wrong_patch_or_source_hash_is_rejected(self):
        for key in ("patchSha256", "sourceSha256", "commit"):
            wrong = dict(self.proof, **{key: "incorrect"})
            self.proof_path.write_text(json.dumps(wrong))
            with self.assertRaises(ValueError):
                backports.verify(self.prefix, self.pin, self.ports)

    def test_modified_static_archive_is_rejected(self):
        self.archive.write_bytes(b"fixture unpatched archive")
        with self.assertRaises(ValueError):
            backports.verify(self.prefix, self.pin, self.ports)

    def test_stale_dll_beside_correct_patched_archive_cannot_be_reattested(self):
        verified = backports.verify(self.prefix, self.pin, self.ports)
        self.dll.write_bytes(b"fixture stale unpatched DLL")
        with self.assertRaises(ValueError):
            backports.verify_link(self.prefix, verified, "win-x64")

    def test_wrong_rid_is_rejected(self):
        with self.assertRaises(ValueError):
            backports.verify_link(self.prefix, {"zlib": self.proof}, "win-x86")

    def test_copied_vex_for_different_dll_is_rejected(self):
        document = backports.vex(self.pin, {"zlib": self.proof}, self.graph, self.native_hash, "win-x64")
        backports.validate(document, self.pin, {"zlib": self.proof}, self.graph, self.native_hash, "win-x64")
        with self.assertRaises(ValueError):
            backports.validate(document, self.pin, {"zlib": self.proof}, self.graph, "0" * 64, "win-x64")

    def test_broadened_product_or_other_cve_is_rejected(self):
        for field in ("product", "cve"):
            document = backports.vex(self.pin, {"zlib": self.proof}, self.graph, self.native_hash, "win-x64")
            if field == "product":
                document["statements"][0]["products"][0]["@id"] = "pkg:vcpkg/zlib@1.3.2"
            else:
                document["statements"][0]["vulnerability"]["name"] = "CVE-2024-52533"
            with self.assertRaises(ValueError):
                backports.validate(document, self.pin, {"zlib": self.proof}, self.graph, self.native_hash, "win-x64")


if __name__ == "__main__":
    unittest.main()
