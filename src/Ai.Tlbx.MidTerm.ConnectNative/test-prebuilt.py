"""Pinned native artifact contracts: no compiler, network or native code execution."""
import hashlib
import json
from pathlib import Path
import shutil
import tempfile
import unittest
import zipfile
import backports
import prebuilt

ROOT = Path(__file__).resolve().parent


def seed_recipe(destination):
    for name in ("native-stack.json", "vcpkg.json", "build.ps1", "package.py", "recipe.py"):
        shutil.copyfile(ROOT / name, destination / name)
    for name in ("ports", "triplets", "source-metadata"):
        shutil.copytree(ROOT / name, destination / name)


def fixture_payload(native, rid="linux-x64"):
    pin = json.loads((native / "native-stack.json").read_text())
    entry = "datachannel.dll" if rid.startswith("win-") else "libdatachannel.dylib" if rid.startswith("osx-") else "libdatachannel.so"
    content = b"fixture native archive bytes"
    expected = pin["backports"]["zlib"]
    proof = {key: expected[key] for key in ("commit", "cve", "patchSha256", "sourceSha256")}
    proof.update({"version": 1, "archive": "libz.a", "archiveSha256": hashlib.sha256(b"fixture patched static archive").hexdigest()})
    graph = {"spdxVersion": "SPDX-2.3", "creationInfo": {"created": "2026-10-29T00:00:00Z"}, "packages": [
        {"name": "libnice", "SPDXID": "SPDXRef-native-libnice-port", "versionInfo": "0.1.24", "checksums": [{"algorithm": "SHA512", "checksumValue": "a" * 128}]},
        {"name": "zlib", "SPDXID": "SPDXRef-native-zlib-port", "versionInfo": expected["portVersion"], "externalRefs": [{"referenceType": "purl", "referenceLocator": f"pkg:vcpkg/zlib@{expected['version']}?port_version=3&triplet={rid}"}]}
    ], "relationships": [], "files": []}
    backports.qualify(graph["packages"][1], proof)
    native_hash = hashlib.sha256(content).hexdigest()
    document = backports.vex(pin, {"zlib": proof}, graph, native_hash, rid)
    graph["annotations"] = [{"annotator": "Tool: tlbx native build", "comment": json.dumps(document, sort_keys=True)}]
    files = {entry: content, "THIRD-PARTY-NOTICES.txt": b"fixture notices", "native-dependencies.spdx.json": json.dumps(graph).encode()}
    manifest = {"version": 1, "runtimeIdentifier": rid, "entryPoint": entry,
        "versions": dict(pin["versions"], vcpkgCommit=pin["vcpkgCommit"]), "portVersions": {"zlib": expected["portVersion"]},
        "backports": {"zlib": proof}, "linkEvidence": {"version": 1, "runtimeIdentifier": rid, "entryPoint": entry, "entryPointSha256": native_hash, "backports": {"zlib": proof}},
        "files": [{"name": name, "sha256": hashlib.sha256(data).hexdigest()} for name, data in files.items()]}
    return manifest, graph, document, files


def write_payload(directory, manifest, files, document):
    directory.mkdir(parents=True, exist_ok=True)
    manifest_bytes = json.dumps(manifest).encode()
    with zipfile.ZipFile(directory / "bundle.zip", "w") as archive:
        for name, data in dict(files, **{"manifest.json": manifest_bytes}).items():
            archive.writestr(zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0)), data)
    (directory / "manifest.json").write_bytes(manifest_bytes)
    (directory / "native-dependencies.spdx.json").write_bytes(files["native-dependencies.spdx.json"])
    (directory / "native-backports.vex.json").write_text(json.dumps(document))


class PinnedNativeContracts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tlbx-native-prebuilt-")
        self.addCleanup(self.temp.cleanup)
        self.native = Path(self.temp.name) / "native"
        self.native.mkdir()
        seed_recipe(self.native)
        self.rid = "linux-x64"
        self.directory = self.native / "prebuilt" / self.rid
        self.manifest, self.graph, self.vex, self.files = fixture_payload(self.native)
        self.write()
        prebuilt.seal(self.native, self.rid)

    def write(self):
        write_payload(self.directory, self.manifest, self.files, self.vex)

    def test_prepare_succeeds_without_source_build_tree_and_is_incremental(self):
        prebuilt.prepare(self.native, self.rid)
        bundle = self.native / "bin" / self.rid / "bundle.zip"
        modified = bundle.stat().st_mtime_ns
        prebuilt.prepare(self.native, self.rid)
        self.assertEqual(modified, bundle.stat().st_mtime_ns)
        self.assertFalse((self.native.parent / ".artifacts").exists())
        self.assertFalse(any(path.name in ("installed", "vcpkg", "downloads") for path in self.native.rglob("*")))
        backports.validate_bundle(self.native, self.rid)

    def symlink_or_skip(self, link, target, directory=False):
        try:
            link.symlink_to(target, target_is_directory=directory)
        except OSError as error:
            self.skipTest(f"This host cannot create the required symlink: {error}")

    def test_prepare_ignores_predictable_temp_symlink_and_preserves_external_file(self):
        destination = self.native / "bin" / self.rid
        destination.mkdir(parents=True)
        sentinel = self.native.parent / "external-sentinel"
        sentinel.write_bytes(b"must remain untouched")
        link = destination / "bundle.zip.prebuilt-tmp"
        self.symlink_or_skip(link, sentinel)
        prebuilt.prepare(self.native, self.rid)
        self.assertEqual(b"must remain untouched", sentinel.read_bytes())
        self.assertTrue(link.is_symlink())
        self.assertFalse(list(destination.glob(".*.tmp")))
        prebuilt.validate(self.native, self.rid, destination)

    def test_prepare_rejects_redirected_destination_before_external_write(self):
        external = self.native.parent / "external-directory"
        external.mkdir()
        sentinel = external / "sentinel"
        sentinel.write_bytes(b"must remain untouched")
        (self.native / "bin").mkdir()
        self.symlink_or_skip(self.native / "bin" / self.rid, external, directory=True)
        with self.assertRaisesRegex(ValueError, "symlink or junction"):
            prebuilt.prepare(self.native, self.rid)
        self.assertEqual(b"must remain untouched", sentinel.read_bytes())
        self.assertEqual(["sentinel"], sorted(path.name for path in external.iterdir()))

    def test_missing_prebuilt_never_reuses_stale_bin_or_compiles(self):
        prebuilt.prepare(self.native, self.rid)
        (self.directory / "bundle.zip").unlink()
        with self.assertRaises(ValueError):
            prebuilt.prepare(self.native, self.rid)

    def test_missing_index_fails_closed(self):
        (self.native / "prebuilt/index.json").unlink()
        with self.assertRaises(FileNotFoundError):
            prebuilt.prepare(self.native, self.rid)

    def test_tampered_committed_bundle_or_sidecar_is_rejected(self):
        for name in prebuilt.FILES:
            with self.subTest(name=name):
                path = self.directory / name
                original = path.read_bytes()
                path.write_bytes(original + b"tampered")
                with self.assertRaises(ValueError):
                    prebuilt.prepare(self.native, self.rid)
                path.write_bytes(original)

    def test_changed_dependency_recipe_requires_deliberate_regeneration(self):
        patch = next((self.native / "ports").rglob("*.patch"))
        patch.write_bytes(patch.read_bytes() + b"changed recipe")
        with self.assertRaisesRegex(ValueError, "recipe mismatch"):
            prebuilt.prepare(self.native, self.rid)

    def test_audit_tools_documentary_links_and_validator_do_not_change_recipe(self):
        original = prebuilt.recipe_digest(self.native, self.rid)
        path = self.native / "native-stack.json"
        pin = json.loads(path.read_text())
        pin["grype"]["version"] = "future scanner"
        pin["cpeSources"]["glib"] = "https://example.test/review"
        path.write_text(json.dumps(pin))
        (self.native / "prebuilt.py").write_text("validator-only change")
        (self.native / "test-prebuilt.py").write_text("test-only change")
        (self.native / "triplets/win-x64.cmake").write_text("unrelated RID")
        self.assertEqual(original, prebuilt.recipe_digest(self.native, self.rid))
        pin["cpeProducts"]["glib"] = "changed emitted SPDX identity"
        path.write_text(json.dumps(pin))
        self.assertNotEqual(original, prebuilt.recipe_digest(self.native, self.rid))

    def test_seal_rejects_wrong_rid_or_pinned_version(self):
        for key in ("runtimeIdentifier", "versions"):
            original = self.manifest[key]
            self.manifest[key] = "win-x64" if key == "runtimeIdentifier" else dict(original, libnice="old")
            self.write()
            with self.assertRaises(ValueError):
                prebuilt.seal(self.native, self.rid)
            self.manifest[key] = original

    def test_seal_rejects_crc_corruption_without_replacing_index(self):
        path = self.directory / "bundle.zip"
        original_index = (self.native / "prebuilt/index.json").read_bytes()
        path.write_bytes(path.read_bytes().replace(b"fixture native archive bytes", b"altered native archive bytes", 1))
        with self.assertRaises(zipfile.BadZipFile):
            prebuilt.seal(self.native, self.rid)
        self.assertEqual(original_index, (self.native / "prebuilt/index.json").read_bytes())

    def test_seal_rejects_wrong_backport_source_even_with_updated_hashes(self):
        self.manifest["backports"]["zlib"]["sourceSha256"] = "0" * 64
        self.write()
        with self.assertRaisesRegex(ValueError, "backport"):
            prebuilt.seal(self.native, self.rid)

    def test_seal_rejects_stale_link_output_even_after_rehashing_payload(self):
        self.files["libdatachannel.so"] = b"another native output"
        self.manifest["files"][0]["sha256"] = hashlib.sha256(self.files["libdatachannel.so"]).hexdigest()
        self.write()
        with self.assertRaisesRegex(ValueError, "link"):
            prebuilt.seal(self.native, self.rid)

    def test_seal_rejects_missing_backport_or_broad_vex(self):
        self.manifest["backports"] = {}
        self.write()
        with self.assertRaisesRegex(ValueError, "backport"):
            prebuilt.seal(self.native, self.rid)
        self.manifest, _, _, self.files = fixture_payload(self.native)
        self.vex["statements"][0]["products"][0]["@id"] = "pkg:vcpkg/zlib@1.3.2"
        self.write()
        with self.assertRaisesRegex(ValueError, "OpenVEX"):
            prebuilt.seal(self.native, self.rid)

    def test_seal_rejects_child_executable_or_undeclared_zip_entry(self):
        self.files["helper.exe"] = b"must not ship"
        self.manifest["files"].append({"name": "helper.exe", "sha256": hashlib.sha256(self.files["helper.exe"]).hexdigest()})
        self.write()
        with self.assertRaises(ValueError):
            prebuilt.seal(self.native, self.rid)


if __name__ == "__main__":
    unittest.main()