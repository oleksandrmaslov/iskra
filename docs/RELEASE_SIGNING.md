# Release signing setup

Iskra's tagged release paths fail closed. Engineering overrides are accepted
only for manual, explicitly labelled builds and are ignored for tagged Linux or
macOS publication.

## GitHub environment

Create a GitHub Actions environment named `release-signing`. Require an
independent reviewer, disallow self-review, and limit deployment branches/tags
to the release policy. Store the following secrets in that environment rather
than at repository scope.

Linux:

- `ISKRA_LINUX_SIGNING_KEY_ID` — fingerprint or unambiguous ID of the release
  signing subkey.
- `ISKRA_LINUX_SIGNING_PRIVATE_KEY` — ASCII-armored private signing key imported
  only into the ephemeral runner keyring.

macOS:

- `ISKRA_MACOS_SIGNING_IDENTITY` — Developer ID Application identity name.
- `ISKRA_MACOS_CERTIFICATE_P12_BASE64` — base64-encoded certificate and private
  key bundle.
- `ISKRA_MACOS_CERTIFICATE_PASSWORD` — password for the P12 bundle.
- `ISKRA_MACOS_NOTARY_APPLE_ID` — Apple account used by `notarytool`.
- `ISKRA_MACOS_NOTARY_TEAM_ID` — Apple Developer team ID.
- `ISKRA_MACOS_NOTARY_APP_PASSWORD` — app-specific password.

The workflow imports the certificate into a temporary Keychain, creates a
temporary `notarytool` profile, signs the app and CLI with hardened runtime,
verifies them, submits the DMG, waits for notarization, and staples/verifies the
result. The temporary Keychain is removed in an always-running cleanup step.

## Windows gate

Tagged Windows publication remains deliberately blocked. Before enabling it:

1. Select an organization-validated or extended-validation Authenticode
   certificate and a managed signing service/HSM that can sign on the runner
   without exporting the private key.
2. Configure a trusted RFC 3161 timestamp service.
3. Sign WPF, CLI, Avalonia, both MSIs, and both Burn setup EXEs in the correct
   order; verify every signature and timestamp before upload.
4. Add clean Windows install/upgrade/uninstall and SmartScreen evidence to the
   release gate.
5. Make tagged publication fail if any expected file is unsigned, untrusted, or
   lacks a valid timestamp.

Do not place a Windows code-signing private key, Apple P12, catalog private key,
or Linux signing key in the repository or on a factory station.

## Production catalog key

Release signing does not replace catalog signing. Rotate the embedded
development Ed25519 catalog key on a clean secured workstation, store its
private half offline/HSM/KMS-backed, ship the new public key in a new Iskra
build, and publish a catalog signed by the new key. Rehearse compromise and
revocation recovery before factory deployment.
