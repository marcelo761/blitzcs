namespace BlitzCS.BinaryCompiler;

/// <summary>Result of an explicit, opt-in preprocessing step: a NEW file derived from the input (the input is never modified) plus its lineage.</summary>
/// <param name="DerivedPath">The derived PE32 to translate instead of the input.</param>
/// <param name="LineageToken">Stable text identifying input hash, preprocessor identity/version and options; pass it as a dependency of the translation cache key.</param>
public sealed record BinaryPreprocessResult(string DerivedPath, string LineageToken, IReadOnlyList<BinaryDiagnostic> Diagnostics);

/// <summary>
/// A build-time transformation of a binary library before translation, requested per library in the catalog (for example <c>"unpack": true</c>).
/// Implementations live in separate assemblies loaded only on request, so the translator and the CLI core never depend on them.
/// Throws <see cref="InvalidOperationException"/> with the diagnostics in the message when the input cannot be preprocessed.
/// </summary>
public interface IBinaryPreprocessor
{
    /// <summary>Catalog key that selects this preprocessor, for example "unpack".</summary>
    string Id { get; }

    BinaryPreprocessResult Preprocess(string inputPath, string workDirectory);
}
