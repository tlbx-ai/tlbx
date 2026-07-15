# Store Setup Guide

Everything you need to create before the first release.

## Bundle ID

`ai.tlbx.midterm` (both platforms)

---

## Google Play Store

### 1. Play Console App Listing

- Go to [Google Play Console](https://play.google.com/console)
- Create app → Android → package name: `ai.tlbx.midterm`
- Fill in store listing (title, description, screenshots, icon)
- Complete the content declaration and data safety form

### 2. Upload Keystore

Generate once, keep forever:

```bash
keytool -genkeypair -v \
  -keystore upload.jks \
  -keyalg RSA -keysize 2048 \
  -validity 10000 \
  -alias upload \
  -dname "CN=tlbx, O=tlbx.ai, L=Vienna, C=AT"
```

Store `upload.jks` securely — losing it means you can't push updates.

### 3. Google Cloud Service Account

- Go to [Google Cloud Console](https://console.cloud.google.com)
- Create a service account (or reuse existing)
- In Play Console → Setup → API access → Link the Google Cloud project
- Grant the service account "Release manager" permissions
- Download the service account JSON key

### 4. GitHub Secrets

Add these to the repo's GitHub Actions secrets:

| Secret | Value |
|--------|-------|
| `ANDROID_KEYSTORE_BASE64` | `base64 -w0 upload.jks` |
| `ANDROID_KEYSTORE_PASSWORD` | Keystore password |
| `ANDROID_KEY_ALIAS` | `upload` |
| `ANDROID_KEY_PASSWORD` | Key password |
| `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON` | Full JSON contents of service account key |

---

## Apple App Store

### 1. App ID

- Go to [Apple Developer Portal](https://developer.apple.com/account/resources/identifiers/list)
- Identifiers → Register a new identifier → App IDs
- Bundle ID: `ai.tlbx.midterm` (explicit)
- Capabilities: none required (no push notifications, no special entitlements)

### 2. Distribution Certificate

- Certificates → Create → Apple Distribution
- Download and install the `.cer`
- Export as `.p12` from Keychain Access (include private key)

### 3. Provisioning Profile

- Profiles → Create → App Store Distribution
- Select App ID: `ai.tlbx.midterm`
- Select the distribution certificate
- Download `MidTerm_AppStore.mobileprovision`

### 4. App Store Connect

- Go to [App Store Connect](https://appstoreconnect.apple.com)
- My Apps → New App
- Bundle ID: `ai.tlbx.midterm`
- Fill in app metadata, screenshots, description

### 5. App Store Connect API Key

- Users and Access → Integrations → App Store Connect API
- Generate API Key (role: Admin or Developer)
- Note the Key ID and Issuer ID
- Download the `.p8` private key (one-time download)

### 6. GitHub Secrets

Add these to the repo's GitHub Actions secrets:

| Secret | Value |
|--------|-------|
| `IOS_DISTRIBUTION_CERT_P12` | `base64 -w0 distribution.p12` |
| `IOS_DISTRIBUTION_CERT_PASSWORD` | P12 export password |
| `IOS_PROVISIONING_PROFILE` | `base64 -w0 MidTerm_AppStore.mobileprovision` |
| `APPSTORE_CONNECT_API_KEY_ID` | Key ID from App Store Connect |
| `APPSTORE_CONNECT_API_ISSUER_ID` | Issuer ID from App Store Connect |
| `APPSTORE_CONNECT_API_PRIVATE_KEY` | Full contents of `.p8` file |

---

## Checklist

### Google Play
- [ ] App created in Play Console (`ai.tlbx.midterm`)
- [ ] Upload keystore generated and backed up
- [ ] Service account created and linked
- [ ] All 5 GitHub secrets added
- [ ] Store listing completed (icon, screenshots, description)
- [ ] Content rating questionnaire completed
- [ ] Data safety form completed

### Apple App Store
- [ ] App ID registered (`ai.tlbx.midterm`)
- [ ] Distribution certificate created and exported as P12
- [ ] App Store provisioning profile created
- [ ] App record created in App Store Connect
- [ ] API key generated
- [ ] All 6 GitHub secrets added
- [ ] Store listing completed (icon, screenshots, description)
- [ ] App privacy details completed
