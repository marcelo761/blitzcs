# Legacy API profiles: implementation scope

The existing per-compilation external registry remains the only source of engine symbols. Reuse `--externals` JSON catalogs: allow `profiles` to include catalogs, inherit declared defaults and contract metadata, and bind matching implementations explicitly. Profiles describe contracts; they do not implement engines or identify arbitrary BASIC commands as DLL exports.

Build order: catalog composition and provider diagnostics → generated historical declaration profile → unchanged SDK/SCP source audits → evidence-driven frontend fixes → regression and package validation.

Accept: semantic analysis can validate historical commands and report unbound calls with provider metadata; builds fail before emitting artifacts when a used contract is unbound; exact managed overload selection/defaults and existing binary `.bcx` bindings continue working; profile files are protected as build inputs. Preserve Windows x64 default output and Linux-hosted operation. Tests use xUnit projects and Python unittest for generated profiles/corpus tools, plus actual original source corpus probes. Native runtime compatibility is measured separately.

Reject conflicting signatures, duplicate bindings, include cycles and malformed metadata. Missing conventions/ABI ownership remain unknown. Do not change native translation/runtime internals, original source trees, or add engine dependencies to compiler/CLI.

Implementation conventions follow existing C# immutable signatures, per-build registries, location-rich BasicDiagnosticException errors and fail-before-write output validation. Run focused tests after each slice, then compiler/CLI suites and existing Windows x64 packaging tests before local commits.
