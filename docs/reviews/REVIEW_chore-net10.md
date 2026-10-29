# Review: chore/net10 (42e3723) vs main (a769aae)

Reviewer: ReviewerBot. Date: 2026-10-29 09:22 Europe/Warsaw (UTC+1).
Method: private clone `/tmp/rr6-acs` @ 42e3723, SDK 10.0.403 / runtime 10.0.14, ephemeral ports only, no source changes.
Throwaway probes were deleted afterwards (`git status` clean). A net8 comparison used a scratch console app (SDK 8.0.427), which was also deleted.

## Verdict: **Approve**

There are no blocking or major issues. The X509 loader change behaves the same as the old constructor for every format I tried on net8.
The minor findings below are hardening items, not regressions. They can go in this PR or a follow-up.

## Focus-area status

| # | Area | Status |
|---|------|--------|
| 1 | DPAPI without the ProtectedData package | ✅ OK |
| 2 | X509 loader change | ✅ OK, no regression; 2 Minor + 2 Nit hardening items |
| 3 | Packages | ✅ OK; 1 Nit (xunit v2 flagged "Legacy") |
| 4 | .NET 10 breaking changes | ✅ No impact found |

### 1. DPAPI (ProtectedData) on net10.0-windows
- **In-box:** `System.Security.Cryptography.ProtectedData.dll` is in `Microsoft.WindowsDesktop.App.Ref` 10.0.14 (`ref/net10.0`) and listed in its `FrameworkList.xml`.
  The NU1510 docs say a direct reference is redundant ("will not be pruned"/unnecessary) when the package is supplied by the shared framework, under PrunePackageReference, which is on by default for net10 targets. So removing it from `AcsTool.App.csproj` is correct.
- **Usage:** it's only used in `src/AcsTool.App/Services/PlatformServices.cs:69,78` (`DpapiSecretProtector`), in the App project with TFM `net10.0-windows`.
  - No cross-platform project (Core/Proxy/Simulator/Presentation, all `net10.0`) references it.
  - No CA1416 warnings appear under `AnalysisLevel 10.0-recommended`.
- **Build:** compiles with 0 warnings and 0 errors (CI=true locally, and in the CI Windows job).
- **m8 path:** `catch (… when ex is CryptographicException or FormatException)` → `SecretUnprotectException` is unchanged.

### 2. X509 loader (`src/AcsTool.Core/Net/Tls.cs:99-128`)
- **Semantics:**
  - Files containing `-----BEGIN CERTIFICATE-----` go to `X509Certificate2.CreateFromPem` (:103-105).
  - Otherwise the loader tries `X509CertificateLoader.LoadCertificate` (:109). It accepts a single X.509 cert as DER or PEM, never PKCS#12/PKCS#7.
  - If that fails, it tries `LoadPkcs12Collection(data, password: null)` (:114). Defaults apply: `X509KeyStorageFlags.DefaultKeySet` and `Pkcs12LoaderLimits.Defaults`, whose exceptions derive from CryptographicException.
  - It then picks the first cert with BasicConstraints CA=true, or else `[0]`, and disposes the others.
- **Tests:** the new `TlsCertificateLoadTests` cover PEM, DER, a password-less PFX and garbage input. There is no test for a BOM, CRLF or a bundle, and no assertion on HasPrivateKey.
- **Probe results on net10:**

  | Input | Result |
  |---|---|
  | PEM with CRLF | OK |
  | PEM bundle | only the first cert is used |
  | PEM with a UTF-8 BOM | rejected |
  | PFX, null or "" password | loads, **HasPrivateKey=True** |
  | PFX with a password | fails ("password may be incorrect") |
  | PKCS#7 (.p7b) | rejected |
  | Truncated DER | "ASN1 corrupted data" (from the PKCS#12 fallback) |

- **net8 comparison (old `new X509Certificate2(byte[])`):**
  - BOM PEM: also rejected.
  - PKCS#7: also rejected ("Cannot find the original signer").
  - PFX: also HasPrivateKey=True.
  - So none of the above is a regression.
- **Validation callback:** unchanged. It uses `CustomRootTrust`, `X509RevocationMode.NoCheck` (as before, which is acceptable for a private ACS CA in a test tool) and ExtraStore. There's no security regression.
- **Error redaction:** the only new message is "No certificate found in {path}". It contains the path, but no secrets. Fine.

### 3. Packages
- **Vulnerabilities:** `dotnet list package --vulnerable --include-transitive` reports no vulnerable packages in all 10 projects.
- **Prerelease:** no prerelease versions in `Directory.Packages.props`.
- **Outdated:** `--outdated` reports none.
- **Deprecated:** `--deprecated` reports only `xunit 2.9.3` as "Legacy", with alternative `xunit.v3` (in the 4 test projects). See N3.
- **Test tooling on net10:**
  - Test.Sdk 18.11.0, xunit.runner.visualstudio 4.0.1 and coverlet.collector 10.1.0 run fine on net10.0. All 4 test assemblies executed, and coverlet produced 4 cobertura reports.
  - Xunit.SkippableFact 1.5.85 works (its skip is honoured: 1 skipped locally).

### 4. .NET 10 breaking changes (official "Breaking changes in .NET 10" list)
- **WPF:**
  - Empty `Grid.RowDefinitions`/`ColumnDefinitions` are now disallowed: none found in `src/AcsTool.App/**/*.xaml`.
  - Incorrect `DynamicResource` usage now crashes: there are no `DynamicResource` usages.
  - The smoke start (`--smoke-exit`) passed on Windows CI.
- **Kestrel / ASP.NET Core 10:**
  - WebHostBuilder/IWebHost and IPNetwork/KnownNetworks are obsolete: none are used.
  - `AddServerHeader = false` is still honoured (`ProxyServer.cs:179`).
- **HttpClient / SocketsHttpHandler / SslStream:**
  - Uri length limits were removed: harmless.
  - X500DistinguishedName validation is stricter, and OpenSSL ≥1.1.1 is required: no impact, and all TLS tests pass.
  - Handler config (`UpstreamHandlerFactory.cs:30-46`, EnabledSslProtocols) is unchanged.
- **Extensions:**
  - BackgroundService and configuration changes: not used.
- **SDK:** restore now audits transitive packages. This is clean, with no NU190x findings.
- **Fidelity probe on net10:** see the Results section below. Header handling, byte-exact bodies, 204, cookies and Date/Server are all unaffected.

## Findings

| ID | Severity | Location | Issue | Fix |
|----|----------|----------|-------|-----|
| M1 | Minor | `src/AcsTool.Core/Net/Tls.cs:114` | The PFX fallback imports the private key (DefaultKeySet). On Windows that can persist key material in the user key store for a file used only as a trust anchor. Because the simulator loads the CA per transport (`HttpCwmpTransport.cs:73`), this repeats per CPE. Same as the old constructor, so not a regression. | `X509CertificateLoader.LoadPkcs12Collection(data, null, X509KeyStorageFlags.EphemeralKeySet, new Pkcs12LoaderLimits(Pkcs12LoaderLimits.Defaults) { IgnorePrivateKeys = true })`, or re-load the picked cert from `RawData`. Add `Assert.False(cert.HasPrivateKey)`. |
| M2 | Minor | `Tls.cs:102-105` | PEM saved with a UTF-8 BOM (Notepad's "UTF-8 with BOM") is rejected with a confusing error. This is pre-existing (net8 also fails), but cheap to fix now that the loader is being touched. | Skip a leading `EF BB BF` before decoding, or decode as UTF-8 and `TrimStart('\uFEFF')`. Add BOM and CRLF tests. |
| N1 | Nit | `Tls.cs:105` | A PEM bundle (intermediate + root) silently uses only the first cert. | `X509Certificate2Collection.ImportFromPem` → add all to `CustomTrustStore`/ExtraStore, or document "single cert only". |
| N2 | Nit | `Tls.cs:111-117` | Non-PFX garbage or truncated DER surfaces the PKCS#12 fallback's "ASN1 corrupted data". PKCS#7 isn't supported (same as before). | Wrap it: `throw new CryptographicException($"{path}: not a PEM/DER certificate or password-less PFX", ex)`. |
| N3 | Nit | `Directory.Packages.props` | xunit 2.9.3 is flagged deprecated ("Legacy") in favour of xunit.v3. Xunit.SkippableFact ties the suite to v2. | Track a follow-up migration to xunit.v3, which has native `Assert.Skip`. No action needed for this PR. |
| N4 | Nit (pre-existing) | `src/AcsTool.Simulator/Transport/HttpCwmpTransport.cs:73` | `handlers.Create` is called without a preloaded CA, so the CA is loaded per transport and never disposed. | Load once and pass `PreloadedCustomCa`, or dispose it with the transport. |

## Results
- **Local build:** `CI=true` (TreatWarningsAsErrors) gives **0 warnings, 0 errors**. That includes AcsTool.App (net10.0-windows) via EnableWindowsTargeting.
- **Local tests: 530 passed, 1 skipped**, up from 526+1. The 4 new TLS tests account for the difference.
  - Core: 312 passed + 1 skipped (the DNS test, skippable on the box).
  - Proxy: 97. Simulator: 67. Presentation: 54.
- **Coverage (line):**

  | Project | net10 | Previous |
  |---|---|---|
  | Core | 86.1 | 87.0 |
  | Proxy | 89.7 | 89.7 |
  | Simulator | 89.0 | 89.7 |
  | Presentation | 85.2 | 85.4 |

  The small drops are consistent with coverlet 10 / C# 14 codegen differences, not with removed tests. All projects are still above 80.
- **Response-fidelity probe (net10, ephemeral ports, deleted afterwards):**
  - The 35-byte body with BOM, CRLF and non-ASCII was byte-identical.
  - Set-Cookie ×2 and WWW-Authenticate ×2 were preserved.
  - Date ×1 and Server ×1 (the upstream's own; Kestrel adds none).
  - Empty POST → 204 with no body; the upstream saw `Content-Length: 0` and no Transfer-Encoding.
  - Header values have OWS trimmed (RFC 9110 compliant).
- **CI run [40840664514](https://github.com/vrmfariborz/acs-tr069-tool/actions/runs/40840664514):** push on `chore/net10`, head_sha `42e3723effe9…`, **success**.
  - Created 09:13:59, finished 09:16:08 (Europe/Warsaw, UTC+1).
  - **linux:** success, 09:14:02–09:14:47. SDK 10.0.403, 0 warnings / 0 errors. Core 312, Sim 67, Pres 54, Proxy 97, with 0 skipped (the network-skip guard passed).
  - **windows:** success, 09:14:02–09:16:08. SDK 10.0.403, 0 warnings / 0 errors. The same 312/97/67/54 with 0 skipped. Publish and smoke start succeeded.
  - **Annotations:** windows 0; linux 1 notice ("ubuntu-latest label will migrate to Ubuntu 26 beginning October 19, 2026"). That's informational. Consider pinning `ubuntu-24.04` if you want reproducibility.
