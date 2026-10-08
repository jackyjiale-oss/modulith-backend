# 0011. OpenAPI snapshot test with an in-repo comparer instead of a snapshot library

- Status: Accepted
- Date: 2026-10-07
- Deciders: Lee Jia Le

## Context
The blueprint (Section 12.1) asks for a contract snapshot: an accidental change to the HTTP API must fail the pull request, and an intended one must be accepted on purpose. The plan named `Verify.XunitV3` for this. Every stable Verify version that supports xUnit v3 4.x (33.0.0 and later, released from 12 September 2026) is covered by Verify's "Open Source Maintenance Fee": organizations that use the official NuGet packages in revenue-generating work (annual gross revenue of US$10,000 or more) and government agencies pay a monthly fee, while the licence expression stays `MIT`. From v33 a build-time check (SponsorCheck) enforces it. Without a declaration every project that references Verify fails to build with `SponsorCheck error SC021: No license specified. Package 'Verify' requires a SponsorCheck license property.` The declarations it accepts are a sponsoring GitHub account, an exemption that expires at most 12 months ahead (the build fails again afterwards until it is renewed), a private licence with an end month, or `Verify_SponsorshipLicenseIgnored`, which builds with the breach-of-licence warning `SC023` (an error in our Release builds). Projects generated from this template are usually client code that earns revenue, and they inherit every test dependency. The last fee-free version, 32.0.0 (August 2026), is built for xUnit v3 3.2.x and could never be upgraded. The licence check (`nuget-license`) cannot catch this, because the licence expression is still MIT.

## Options considered
1. Verify 33 or later with a sponsorship declaration: the best tool for snapshots, but every generated project either pays the fee, renews an expiring exemption, or builds in declared breach of the licence.
2. Pin Verify.XunitV3 32.0.0: fee-free, but its compatibility with xUnit v3 4.x is unproven, it can never be upgraded (it needs a Dependabot ignore rule), and it breaks the rule to use the latest stable version.
3. Another snapshot library: the alternatives are smaller and would need the same licence review, for a test that needs about 40 lines.
4. An in-repo comparer: normalize the JSON, compare it with a committed file, write the current document next to it on a mismatch.

## Decision
We chose option 4.

- `JsonSnapshot` (`tests/TemplateName.IntegrationTests/Documentation/`) normalizes a document with `System.Text.Json`: the named top-level members are removed (`servers`, which holds the test host's address), the document's own property order is kept, and it is written indented with two spaces, LF line endings and a final newline.
- `OpenApiSnapshotTests.OpenApi_document_matches_snapshot` fetches `/openapi/v1.json` and compares it with the committed `OpenApiSnapshotTests.OpenApi_document_matches_snapshot.verified.json`. On a mismatch, or when the file is missing, it writes `OpenApiSnapshotTests.OpenApi_document_matches_snapshot.received.json` beside it and fails with a message naming both files. **To accept an intended API change, replace the `.verified.json` with the `.received.json` and commit it.** A matching run deletes a stale received file.
- `*.received.*` is git-ignored; `.gitattributes` keeps `*.verified.*` at LF so the comparison is the same on every platform.
- `JsonSnapshotTests` proves the comparer itself fails on a different or missing snapshot, so the gate cannot pass silently.

## Consequences
- Positive: no package, no licence or fee exposure for generated projects; the file names and the accept-by-renaming workflow are the ones Verify users know; the snapshot is plain, reviewable JSON.
- Negative / trade-offs accepted: about 40 lines of our own code to maintain; no diff tool integration (compare the two files in the editor or with `git diff --no-index`); only JSON is supported, and only top-level members can be ignored.
- Follow-up actions: before adding any new test or runtime dependency, read its licence terms and maintenance-fee policy, not only its licence expression (CONTRIBUTING, Section 6). Revisit if a fee-free snapshot library becomes the clear choice.
