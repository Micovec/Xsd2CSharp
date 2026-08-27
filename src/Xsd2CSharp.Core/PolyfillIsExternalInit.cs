// netstandard2.0 doesn't ship System.Runtime.CompilerServices.IsExternalInit, which the compiler
// needs to emit for any use of `init` accessors or records. This is the standard polyfill.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit;
