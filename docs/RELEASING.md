# Releasing and code signing

## Why this matters

Offender's binaries are unsigned. Windows SmartScreen warns on every download from an
unknown publisher, and for a system-monitoring tool — a category with a real history of
malware — that warning is the single biggest barrier to anyone trying it.

[SignPath Foundation](https://signpath.org/) signs open-source projects for free. This
document covers how to become eligible, how to apply, and how to wire signing into the
release pipeline.

---

## Eligibility: read this before applying

SignPath Foundation's [conditions](https://signpath.org/terms.html) require a project to
be:

| Condition | Offender's status |
|---|---|
| **No malware** — no malware or unwanted programs | ✅ |
| **OSS License** — OSI-approved, no commercial dual-licensing | ✅ MIT |
| **No proprietary code** — no closed components | ✅ |
| **Documented** — functionality described on the download page | ✅ README |
| **Maintained** — actively maintained | ⚠️ needs commit history |
| **Released** — *already released in the form to be signed* | ❌ **blocker** |

They also require that binary artifacts **"must be built from source code in a verifiable
way"**, and that every release is manually approved before signing.

**Applying before there is a published release will be declined.** The "Released"
condition is explicit, and new projects with no release history are not eligible.

### Closing the gap

1. **Push the repository to GitHub, public.**
   ```powershell
   git remote add origin https://github.com/zakijariwala/offender.git
   git push -u origin main
   ```

2. **Let CI build it.** `.github/workflows/ci.yml` builds the NativeAOT binary on a
   GitHub runner and verifies it is genuinely native and within the size budget. This is
   the "verifiable build from source" requirement — locally-built binaries do not
   satisfy it.

3. **Cut a release.**
   ```powershell
   git tag v0.1.0
   git push origin v0.1.0
   ```
   `.github/workflows/release.yml` builds, publishes SHA-256 checksums, and opens a draft
   GitHub Release. Review it and publish.

4. **Accumulate a little history.** Ship a couple of releases and keep the commit log
   alive. "Maintained" is assessed by a human.

5. **Then apply**, at <https://signpath.org/apply>.

---

## The application

Submit it yourself — it is tied to your identity and email, and approval creates an
ongoing relationship between you and the Foundation. Expect anywhere from a few days to a
few weeks for review.

Have these ready:

| Field | Value |
|---|---|
| Project name | Offender |
| Repository | `https://github.com/zakijariwala/offender` |
| License | MIT ([LICENSE](../LICENSE)) |
| Description | An ambient Windows desktop monitor that ranks the processes actually slowing the machine down. Idles as a corner notch; expands to a full panel showing CPU, memory, network, disk and GPU. |
| Language / stack | C# compiled with NativeAOT, talking directly to Win32. No UI framework. |
| Artifact to sign | `Offender.exe` — single self-contained Windows x64 executable, ~2 MB |
| Build system | GitHub Actions (`.github/workflows/release.yml`) |
| Download page | The GitHub Releases page |

Two points worth stating plainly in the application, because a reviewer will check both:

- **Why it needs elevated-looking access:** it does not. Offender runs as a normal user
  and requires no admin rights. It reads documented and semi-documented Windows
  performance APIs (`NtQuerySystemInformation`, PDH, `GetIfTable`) — the same sources
  Task Manager uses — and has no network egress of any kind.
- **Why it might look unusual to a scanner:** it enumerates all running processes and
  reads their CPU, memory and I/O counters. That is the core feature, documented in the
  README and [ARCHITECTURE.md](ARCHITECTURE.md), not incidental behaviour.

---

## Wiring up signing after approval

Approval provides three values. Add them as repository secrets/variables:

| Value | Where it goes |
|---|---|
| Organization ID | secret, e.g. `SIGNPATH_ORGANIZATION_ID` |
| API token | secret, e.g. `SIGNPATH_API_TOKEN` |
| Project + signing policy slugs | workflow inputs |

Then in `.github/workflows/release.yml`:

1. Replace the placeholder **"Submit signing request (SignPath)"** step with the official
   action, `signpath/github-action-submit-signing-request`. Take its exact input names
   from <https://docs.signpath.io/trusted-build-systems/github> — they are deliberately
   not guessed at here.
2. Remove the `if: false` guard.
3. Have the step consume the uploaded artifact and write the signed binary back into
   `dist/` before the release step runs.
4. Recompute `SHA256SUMS.txt` **after** signing — signing changes the file, so a checksum
   taken before it will not match what users download.
5. Drop the "not code-signed yet" warning from the release body.

Signing requires manual approval per release in the SignPath web UI by default. Leave
that on; it is a meaningful safeguard against a compromised pipeline signing something
you did not intend.

---

## Interim: reducing SmartScreen friction without a certificate

Until signing is in place:

- Publish SHA-256 checksums with every release (the release workflow already does).
- Say plainly in the README that the binary is unsigned and that the warning is expected
  — users who are told in advance are far less likely to bounce.
- Point users at VirusTotal results for the release binary if a false positive appears.

Note that SmartScreen reputation accrues to a *signing certificate*, not to a file, so an
unsigned binary never stops warning no matter how many people download it. Signing is the
only real fix.
