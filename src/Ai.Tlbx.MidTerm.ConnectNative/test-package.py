"""Offline native archive/SBOM contracts. No compiler or native code is executed."""
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest import mock
import zipfile

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("complete_native_sbom", ROOT / "complete-sbom.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
package_spec = importlib.util.spec_from_file_location("package_connect_native", ROOT / "package.py")
package_module = importlib.util.module_from_spec(package_spec)
package_spec.loader.exec_module(package_module)


class NativePackageContracts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tlbx-native-contract-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.pin = json.loads((ROOT / "native-stack.json").read_text())
        (self.root / "native-stack.json").write_text(json.dumps(self.pin))
        self.bundle = self.root / "bin/linux-x64/bundle.zip"
        self.bundle.parent.mkdir(parents=True)
        self.graph = {"packages": [{"name": "libnice", "SPDXID": "SPDXRef-native-libnice-port", "versionInfo": "0.1.24", "checksums": [{"algorithm": "SHA512", "checksumValue": "a" * 128}]}], "relationships": [], "files": []}
        self.files = {"libdatachannel.so": b"fixture native archive bytes", "THIRD-PARTY-NOTICES.txt": b"fixture notices", "native-dependencies.spdx.json": json.dumps(self.graph).encode()}
        self.manifest = {"version": 1, "runtimeIdentifier": "linux-x64", "entryPoint": "libdatachannel.so", "versions": dict(self.pin["versions"], vcpkgCommit=self.pin["vcpkgCommit"]), "files": [{"name": name, "sha256": hashlib.sha256(data).hexdigest()} for name, data in self.files.items()]}
        self.sbom = self.root / "mt-linux-x64.spdx.json"
        self.sbom.write_text(json.dumps({"SPDXID": "SPDXRef-DOCUMENT", "packages": [], "relationships": []}))

    def write_bundle(self):
        with zipfile.ZipFile(self.bundle, "w") as bundle:
            bundle.writestr("manifest.json", json.dumps(self.manifest))
            for name, data in self.files.items():
                bundle.writestr(name, data)

    def test_source_checksums_and_native_binary_provenance_reach_release_sbom(self):
        self.write_bundle()
        module.complete(self.sbom, self.root)
        output = json.loads(self.sbom.read_text())
        dependency = next(p for p in output["packages"] if p["name"] == "libnice")
        self.assertEqual(dependency["checksums"], self.graph["packages"][0]["checksums"])
        resource = next(p for p in output["packages"] if p["name"] == "tlbx-connect-native")
        self.assertEqual(resource["checksums"][0]["checksumValue"], hashlib.sha256(self.bundle.read_bytes()).hexdigest())
        self.assertIn({"spdxElementId": resource["SPDXID"], "relationshipType": "CONTAINS", "relatedSpdxElement": dependency["SPDXID"]}, output["relationships"])
        with self.assertRaises(ValueError):
            module.complete(self.sbom, self.root)

    def test_tampered_native_file_rejected(self):
        self.files["libdatachannel.so"] = b"modified"
        self.write_bundle()
        with self.assertRaisesRegex(ValueError, "integrity"):
            module.complete(self.sbom, self.root)

    def test_undeclared_sidecar_rejected(self):
        self.files["helper.exe"] = b"unwanted child process"
        self.write_bundle()
        with self.assertRaisesRegex(ValueError, "undeclared"):
            module.complete(self.sbom, self.root)

    def test_wrong_rid_or_source_snapshot_rejected(self):
        for field in ("runtimeIdentifier", "versions"):
            old = self.manifest[field]
            self.manifest[field] = "win-x64" if field == "runtimeIdentifier" else dict(old, vcpkgCommit="bad")
            self.write_bundle()
            with self.assertRaisesRegex(ValueError, "provenance"):
                module.complete(self.sbom, self.root)
            self.manifest[field] = old

    def test_declared_child_executable_is_still_rejected(self):
        data = b"unwanted executable"
        self.files["helper.exe"] = data
        self.manifest["files"].append({"name": "helper.exe", "sha256": hashlib.sha256(data).hexdigest()})
        self.write_bundle()
        with self.assertRaisesRegex(ValueError, "integrity"):
            module.complete(self.sbom, self.root)

    def test_real_packager_preserves_complete_source_graph_without_tools_or_duplicate_sonames(self):
        installed = self.root / "installed"
        prefix = installed / "linux-x64"
        (prefix / "lib").mkdir(parents=True)
        (prefix / "lib/libdatachannel.so").write_bytes(b"fixture library")
        (prefix / "bin").mkdir()
        (prefix / "bin/build-tool.exe").write_bytes(b"must never ship")
        source_pin = json.loads((ROOT / "native-stack.json").read_text())
        fixture_versions = dict(source_pin["versions"])
        fixture_versions.update({name: value["portVersion"] for name, value in source_pin.get("backports", {}).items()})
        fixture_versions["dirent"] = "1.26"
        fixture_versions["libiconv"] = "1.19#1"
        fixture_versions["gettext-libintl"] = "0.22.5#4"
        for name, version in fixture_versions.items():
            share = prefix / "share" / name
            share.mkdir(parents=True)
            document = {"creationInfo": {"creators": ["Tool: fixture"], "created": "2026-10-29T00:00:00Z"}, "packages": [{"name": name, "SPDXID": "SPDXRef-port", "versionInfo": version, "checksums": [{"algorithm": "SHA512", "checksumValue": "b" * 128}]}], "relationships": [{"spdxElementId": "SPDXRef-DOCUMENT", "relationshipType": "DESCRIBES", "relatedSpdxElement": "SPDXRef-port"}]}
            (share / "vcpkg.spdx.json").write_text(json.dumps(document))
            if name not in ("dirent", "libiconv", "gettext-libintl"):
                (share / "copyright").write_text("fixture notices " + name)
            if name in source_pin.get("backports", {}):
                expected = source_pin["backports"][name]
                archive_data = b"fixture patched static archive"
                (prefix / "lib/libz.a").write_bytes(archive_data)
                proof = {key: expected[key] for key in ("commit", "cve", "patchSha256", "sourceSha256")}
                proof.update({"version": 1, "archive": "libz.a", "archiveSha256": hashlib.sha256(archive_data).hexdigest()})
                (share / "tlbx-backport.json").write_text(json.dumps(proof))
                document["packages"][0]["externalRefs"] = [{"referenceCategory": "PACKAGE-MANAGER", "referenceType": "purl", "referenceLocator": "pkg:vcpkg/zlib@1.3.2?port_version=3&triplet=linux-x64"}]
                (share / "vcpkg.spdx.json").write_text(json.dumps(document))
            port = self.root / "vcpkg/ports" / name
            port.mkdir(parents=True)
            (port / "vcpkg.json").write_text('{"license":"MIT"}')
        output = self.bundle.parent
        proof = json.loads((prefix / "share/zlib/tlbx-backport.json").read_text())
        (prefix / "share/libdatachannel/tlbx-link.json").write_text(json.dumps({"version": 1, "runtimeIdentifier": "linux-x64", "entryPoint": "libdatachannel.so", "entryPointSha256": hashlib.sha256(b"fixture library").hexdigest(), "backports": {"zlib": proof}}))
        package_module.package("linux-x64", installed, output, verify_load=False)
        digest = hashlib.sha256(self.bundle.read_bytes()).hexdigest()
        package_module.package("linux-x64", installed, output, verify_load=False)
        self.assertEqual(digest, hashlib.sha256(self.bundle.read_bytes()).hexdigest())
        with zipfile.ZipFile(self.bundle) as archive:
            self.assertNotIn("build-tool.exe", archive.namelist())
            graph = json.loads(archive.read("native-dependencies.spdx.json"))
            self.assertEqual(len(graph["packages"]), len(fixture_versions) - 3)
            self.assertEqual(len({p["SPDXID"] for p in graph["packages"]}), len(fixture_versions) - 3)
            self.assertFalse(any(p["name"] in ("dirent", "libiconv", "gettext-libintl") for p in graph["packages"]))
            self.assertTrue(all(r["spdxElementId"] == "SPDXRef-DOCUMENT" for r in graph["relationships"]))
        self.assertEqual((output / "source-sboms/dirent.spdx.json").read_bytes(), (prefix / "share/dirent/vcpkg.spdx.json").read_bytes())
        for rid in ("linux-arm64", "osx-x64", "osx-arm64"):
            with self.subTest(rid=rid):
                target = installed / rid
                shutil.copytree(prefix, target)
                entry = "libdatachannel.dylib" if rid.startswith("osx-") else "libdatachannel.so"
                if entry != "libdatachannel.so":
                    (target / "lib/libdatachannel.so").rename(target / "lib" / entry)
                    (target / "share/gettext-libintl/copyright").write_text("fixture macOS bundled libintl notices")
                link = json.loads((target / "share/libdatachannel/tlbx-link.json").read_text())
                link.update({"runtimeIdentifier": rid, "entryPoint": entry})
                (target / "share/libdatachannel/tlbx-link.json").write_text(json.dumps(link))
                target_output = self.root / "bin" / rid
                with mock.patch.dict("os.environ", {"GITHUB_ACTIONS": "false"}):
                    package_module.package(rid, installed, target_output, verify_load=False)
                with zipfile.ZipFile(target_output / "bundle.zip") as archive:
                    manifest = json.loads(archive.read("manifest.json"))
                    self.assertEqual(manifest["entryPoint"], entry)
                    self.assertNotIn("dirent", manifest["versions"])
                    self.assertNotIn("libiconv", manifest["versions"])
                    self.assertEqual("gettext-libintl" in manifest["versions"], rid.startswith("osx-"))
                    self.assertIn(entry, archive.namelist())
        empty_recipe = prefix / "share/dirent/vcpkg.spdx.json"
        original = empty_recipe.read_bytes()
        malformed = json.loads(original)
        malformed["files"] = [{"SPDXID": "SPDXRef-binary-file-0", "fileName": "./include/dirent.h"}]
        empty_recipe.write_text(json.dumps(malformed))
        with self.assertRaisesRegex(ValueError, "Missing native dependency notices: dirent"):
            package_module.package("linux-x64", installed, output, verify_load=False)
        empty_recipe.write_bytes(original)
        (prefix / "share/libnice/copyright").unlink()
        with self.assertRaisesRegex(ValueError, "Missing native dependency notices: libnice"):
            package_module.package("linux-x64", installed, output, verify_load=False)
        windows = installed / "win-x64"
        (windows / "bin").mkdir(parents=True)
        (windows / "bin/datachannel.dll").write_bytes(b"fixture windows library")
        (windows / "share/dirent").mkdir(parents=True)
        (windows / "share/dirent/vcpkg.spdx.json").write_bytes((prefix / "share/dirent/vcpkg.spdx.json").read_bytes())
        with self.assertRaisesRegex(ValueError, "Missing native dependency notices: dirent"):
            package_module.package("win-x64", installed, output, verify_load=False)


if __name__ == "__main__":
    unittest.main()
