# MidTerm Connector — Setup Checklist

## Google Play

- [ ] **1. Create app** in [Google Play Console](https://play.google.com/console) → package name `ai.tlbx.midterm`
- [ ] **2. Generate upload keystore** locally:
  ```
  keytool -genkeypair -v -keystore upload.jks -keyalg RSA -keysize 2048 -validity 10000 -alias upload -dname "CN=tlbx, O=tlbx.ai, L=Vienna, C=AT"
  ```
- [ ] **3. Create service account** in [Google Cloud Console](https://console.cloud.google.com) → grant "Release manager" in Play Console → download JSON key
- [ ] **4. Add GitHub secrets:**
  - `ANDROID_KEYSTORE_BASE64` — `base64 -w0 upload.jks`
  - `ANDROID_KEYSTORE_PASSWORD`
  - `ANDROID_KEY_ALIAS` — `upload`
  - `ANDROID_KEY_PASSWORD`
  - `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON` — full JSON contents

## Apple App Store

- [ ] **5. Register App ID** in [Apple Developer Portal](https://developer.apple.com/account/resources/identifiers/list) → `ai.tlbx.midterm` (no special capabilities)
- [ ] **6. Create distribution certificate** → Certificates → "Apple Distribution" → install in Keychain → export as `.p12`
- [ ] **7. Create provisioning profile** → Profiles → "App Store Distribution" → link to App ID + certificate → download `.mobileprovision`
- [ ] **8. Create app record** in [App Store Connect](https://appstoreconnect.apple.com) → bundle ID `ai.tlbx.midterm`
- [ ] **9. Generate API key** → Users and Access → Integrations → Admin role → note Key ID + Issuer ID → download `.p8`
- [ ] **10. Add GitHub secrets:**
  - `IOS_DISTRIBUTION_CERT_P12` — `base64 -w0 distribution.p12`
  - `IOS_DISTRIBUTION_CERT_PASSWORD`
  - `IOS_PROVISIONING_PROFILE` — `base64 -w0 MidTerm_AppStore.mobileprovision`
  - `APPSTORE_CONNECT_API_KEY_ID`
  - `APPSTORE_CONNECT_API_ISSUER_ID`
  - `APPSTORE_CONNECT_API_PRIVATE_KEY` — full `.p8` file contents

## Total: 11 GitHub secrets

| # | Secret | Platform |
|---|--------|----------|
| 1 | `ANDROID_KEYSTORE_BASE64` | Android |
| 2 | `ANDROID_KEYSTORE_PASSWORD` | Android |
| 3 | `ANDROID_KEY_ALIAS` | Android |
| 4 | `ANDROID_KEY_PASSWORD` | Android |
| 5 | `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON` | Android |
| 6 | `IOS_DISTRIBUTION_CERT_P12` | iOS |
| 7 | `IOS_DISTRIBUTION_CERT_PASSWORD` | iOS |
| 8 | `IOS_PROVISIONING_PROFILE` | iOS |
| 9 | `APPSTORE_CONNECT_API_KEY_ID` | iOS |
| 10 | `APPSTORE_CONNECT_API_ISSUER_ID` | iOS |
| 11 | `APPSTORE_CONNECT_API_PRIVATE_KEY` | iOS |

Store listings (screenshots, description, privacy forms) can wait until the first build uploads.
