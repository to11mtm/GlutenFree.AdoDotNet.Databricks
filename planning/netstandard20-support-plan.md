# netstandard2.0 Support Plan (reference — not scheduled)

Status: **Speculative / not started.** What it would take to multi-target
`netstandard2.0` alongside the existing `net8.0` builds, so the provider works on
.NET Framework 4.6.2+ and older .NET Core. Written 2026-08 against the current
codebase; re-verify API usage before picking this up.

## 1. Why (and why not)

**For:** .NET Framework line-of-business apps are exactly the audience that can't
use the ODBC driver easily; linq2db and Dapper both run on netstandard2.0.

**Against:** meaningful engineering cost (polyfills, API-surface forks, a weaker
sync story), a permanent second CI matrix, and every future feature must be
written twice-in-one-file. Decide deliberately; don't drift into it.

## 2. Dependency readiness (verified 2026-08)

| Package | netstandard2.0? |
|---|---|
| `Apache.Arrow` 23.0.0 | ✅ (ns2.0 + net462 targets) |
| `Apache.Arrow.Compression` | ⚠️ verify (same repo/versioning; expected ✅) |
| `linq2db` 6.4.x | ✅ (ns2.0 + net462 targets) |
| `Microsoft.Extensions.Logging.Abstractions` | ✅ |
| `Apache.Arrow.Adbc.Drivers.Databricks` 0.23.0 (Thrift add-on) | ✅ (ships ns2.0 + net472 + net8.0) |
| `System.Text.Json` | ✅ via NuGet package (pin to a supported LTS line) |

No dependency blocks this; the work is all in our own code.

## 3. API gaps in our code, by remediation strategy

### 3a. Trivial polyfills (compile-time, no behavior change)

Use [PolySharp](https://github.com/Sergio0694/PolySharp) (or hand-rolled
`#if !NET8_0_OR_GREATER` shims) for:

- Throw helpers (~21 uses): `ArgumentNullException.ThrowIfNull`,
  `ArgumentException.ThrowIfNullOrEmpty`, `ArgumentOutOfRangeException` helpers.
- C# language features on old BCL: `required`/`init` members
  (`CompilerFeatureRequired`, `IsExternalInit`), index/range (`System.Index`/
  `System.Range`), `[ModuleInitializer]` (tests only today).
- `char.IsAsciiDigit`/`IsAsciiLetter[OrDigit]` (5 uses) — one-line local helper.

### 3b. Official compatibility packages

- **`IAsyncDisposable`/`await using`** → `Microsoft.Bcl.AsyncInterfaces`.
  Used by `IDatabricksTransport`, `DatabricksConnection`, readers.
- **`TimeProvider`** (15 uses: retry backoff, poll timing, test fakes) →
  `Microsoft.Bcl.TimeProvider`. Also covers
  `new CancellationTokenSource(delay, timeProvider)` via its extension methods.

### 3c. Real forks in behavior/API surface (the hard part)

1. **`DateOnly`/`TimeOnly` (biggest item — public API shape).**
   `GetDateOnly`, `DateOnly` parameter binding, linq2db mappings, type map.
   Options:
   - a) `#if` the `DateOnly` members out of ns2.0 (DATE maps to `DateTime`
     there) — simplest, but splits the public API and linq2db mapping schema.
   - b) Depend on a `DateOnly` polyfill package (e.g. `Portable.System.DateTimeOnly`)
     — keeps one API, adds a dependency that collides with the real type via
     type-forwarding subtleties on net8.0. Needs a spike.
   - Recommendation: (a), with DATE→`DateTime` (midnight) documented for ns2.0.
2. **Genuinely-synchronous pipeline.** `HttpClient.Send(...)` (sync, net5+) is
   used by `RestStatementTransport` and `OAuthM2MAuthenticator`. netstandard2.0
   has no sync HTTP send, so the sync API becomes **sync-over-async there**
   (`.GetAwaiter().GetResult()` behind `#if`). Must be prominently documented:
   the "no sync-over-async" guarantee in the README becomes net8.0-only.
   (On .NET Framework, `HttpWebRequest` *is* genuinely sync and could preserve
   the guarantee for a `net462` target — bigger job; optional stretch.)
3. **`Int128` fast path** in `DatabricksTypeMap` (Decimal128 zero-parse read,
   2 sites) — `#if` to the existing string/`BigInteger` fallback on ns2.0.
4. **`SqlDecimal`** (21 uses, public API: `GetSqlDecimal`, big-DECIMAL default).
   `System.Data.SqlTypes` is not in netstandard2.0. ⚠️ Verify packaging: on
   net462 it's in-box (`System.Data`); for ns2.0-as-consumed-by-.NET-Core there
   is no clean package. Options: `#if` those members to net8.0 + net462 targets
   only (ns2.0 falls back to `DatabricksDecimal`/string for DECIMAL(p>28)), or
   drop ns2.0 in favor of explicit `net462;net8.0` multi-targeting (see §5).
5. **Collection expressions / list patterns** (`[..]`, `is { Length: > 0 }`,
   `[]` literals, ~15 sites) — most compile fine on ns2.0 with LangVersion
   latest; a few (collection expressions targeting arrays/spans) may need
   rewrites. Mechanical cleanup during the port.

### 3d. Test projects

Tests stay `net8.0`-only (xunit + live integration don't need old TFMs), but a
CI job should build the ns2.0 target and, ideally, run the unit suite on
.NET Framework 4.8 (Windows runner) to catch behavioral drift — especially the
sync-over-async path and `DateTime` fallbacks.

## 4. Project/infra changes

- `Directory.Build.props`: `<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>`
  for `src/` (tests keep single TFM). `LangVersion` stays `latest`.
- Conditional `ItemGroup`s for the compat packages (ns2.0 only).
- `#if NET8_0_OR_GREATER` as the canonical guard (not `NETSTANDARD2_0`), so a
  future net10 bump doesn't flip logic.
- README: per-TFM feature matrix (sync pipeline, `DateOnly`, `SqlDecimal`,
  `Int128` fast path).
- CI: build matrix + net48 unit-test leg on `windows-latest`.
- Thrift add-on: same multi-target (its ADBC dependency already ships ns2.0);
  re-verify `ThriftStatementTransport`'s sync path expectations there.

## 5. Open questions

1. **ns2.0 vs `net462` (+`net8.0`) explicitly?** `net462` gives in-box
   `SqlDecimal` and a genuinely-sync `HttpWebRequest` option; ns2.0 gives
   broader reach (old .NET Core, Unity, etc.) but the weakest API surface.
   Supporting *both* (`netstandard2.0;net462;net8.0`) is common practice
   (linq2db and Arrow do exactly this) and likely the right end state.
2. `DateOnly` strategy (§3c.1) — spike the polyfill-package option before
   committing to the `#if` split.
3. Is there real user demand? This is pure cost until someone asks; consider
   waiting for an issue/request before starting.

## 6. Suggested milestones

1. **Spike:** multi-target the core project, count real compile errors, and
   test the `DateOnly` polyfill option. (1 sitting; informs everything else.)
2. **Core port:** polyfills + compat packages + `#if` forks; unit tests green
   on net8.0, core builds clean on ns2.0 (and net462 if chosen).
3. **Behavioral parity:** net48 unit-test CI leg; document per-TFM differences.
4. **linq2db + Thrift add-ons:** multi-target both; re-run integration suites
   from a net48 console harness once against a live warehouse.
