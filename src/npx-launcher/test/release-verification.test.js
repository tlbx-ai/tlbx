'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { resolveReleaseFromRepository, verifyExtractedRelease } = require('../bin/midterm.js');

test('selects the newest release containing the requested platform in each channel', async (t) => {
  const assetName = 'mt-osx-arm64.tar.gz';
  const releases = [
    { tag_name: 'v10.17.18-dev', prerelease: true, assets: [{ name: 'mt-win-x64.zip', browser_download_url: 'https://example.test/win.zip' }] },
    { tag_name: 'v10.17.17-dev', prerelease: true, draft: true, assets: [{ name: assetName, browser_download_url: 'https://example.test/mac.tar.gz' }] },
    { tag_name: 'v10.17.16-dev', prerelease: true, assets: [{ name: assetName, browser_download_url: 'https://example.test/mac.tar.gz' }] },
    { tag_name: 'v10.17.18', prerelease: false, assets: [] },
    { tag_name: 'v10.17.16', prerelease: false, assets: [{ name: assetName, browser_download_url: 'https://example.test/mac.tar.gz' }] }
  ];
  t.mock.method(globalThis, 'fetch', async () => ({ ok: true, json: async () => releases }));
  for (const channel of ['dev', 'stable']) {
    const release = await resolveReleaseFromRepository('tlbx-ai/tlbx', channel, {}, assetName);
    assert.equal(release.tag, channel === 'dev' ? 'v10.17.16-dev' : 'v10.17.16');
  }
  await assert.rejects(resolveReleaseFromRepository('tlbx-ai/tlbx', 'dev', {}, 'mt-linux-x64.tar.gz'), /No dev releases containing/);
});

async function createSignedFixture() {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'tlbx-npx-verify-'));
  const filePath = path.join(directory, 'mt.exe');
  await fs.writeFile(filePath, 'trusted release payload');
  const checksum = crypto.createHash('sha256').update('trusted release payload').digest('hex');
  const checksums = { 'mt.exe': checksum };
  const payload = Buffer.from(JSON.stringify({
    signatureVersion: 2,
    web: '10.4.0-dev',
    pty: '10.4.0-dev',
    protocol: 1,
    minCompatiblePty: '2.0.0',
    webOnly: false,
    platform: 'win-x64',
    channel: 'dev',
    checksums
  }));
  const { privateKey, publicKey } = crypto.generateKeyPairSync('ec', {
    namedCurve: 'secp384r1'
  });
  const signature = crypto.sign('sha256', payload, privateKey);
  const manifest = {
    web: '10.4.0-dev',
    pty: '10.4.0-dev',
    protocol: 1,
    minCompatiblePty: '2.0.0',
    webOnly: false,
    signatureVersion: 2,
    platform: 'win-x64',
    channel: 'dev',
    checksums,
    signedPayload: payload.toString('base64'),
    metadataSignature: signature.toString('base64')
  };
  await fs.writeFile(path.join(directory, 'version.json'), JSON.stringify(manifest));
  const publicKeyBase64 = publicKey.export({ type: 'spki', format: 'der' }).toString('base64');
  return { directory, filePath, manifest, publicKeyBase64 };
}

test('accepts a valid metadata-bound release', async (t) => {
  const fixture = await createSignedFixture();
  t.after(() => fs.rm(fixture.directory, { recursive: true, force: true }));

  await verifyExtractedRelease(
    fixture.directory,
    { tag: 'v10.4.0-dev' },
    { assetName: 'mt-win-x64.zip' },
    fixture.publicKeyBase64
  );
});

test('rejects a payload changed after signing', async (t) => {
  const fixture = await createSignedFixture();
  t.after(() => fs.rm(fixture.directory, { recursive: true, force: true }));
  fixture.manifest.signedPayload = Buffer.from('tampered').toString('base64');
  await fs.writeFile(path.join(fixture.directory, 'version.json'), JSON.stringify(fixture.manifest));

  await assert.rejects(
    verifyExtractedRelease(
      fixture.directory,
      { tag: 'v10.4.0-dev' },
      { assetName: 'mt-win-x64.zip' },
      fixture.publicKeyBase64
    ),
    /signature verification failed/
  );
});

test('rejects an executable changed after signing', async (t) => {
  const fixture = await createSignedFixture();
  t.after(() => fs.rm(fixture.directory, { recursive: true, force: true }));
  await fs.writeFile(fixture.filePath, 'tampered executable');

  await assert.rejects(
    verifyExtractedRelease(
      fixture.directory,
      { tag: 'v10.4.0-dev' },
      { assetName: 'mt-win-x64.zip' },
      fixture.publicKeyBase64
    ),
    /checksum mismatch/
  );
});

test('rejects the wrong platform or release tag', async (t) => {
  const fixture = await createSignedFixture();
  t.after(() => fs.rm(fixture.directory, { recursive: true, force: true }));

  await assert.rejects(
    verifyExtractedRelease(
      fixture.directory,
      { tag: 'v10.4.1-dev' },
      { assetName: 'mt-linux-x64.tar.gz' },
      fixture.publicKeyBase64
    ),
    /platform|does not match/
  );
});
