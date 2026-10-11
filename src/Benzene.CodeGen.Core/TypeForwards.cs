using System.Runtime.CompilerServices;
using Benzene.CodeGen.Core;

// The contract hashes and their RFC 8785 canonicalizer moved into Benzene.Schema.OpenApi (the runtime
// package that produces the contract document, which carries both hashes - contract-document.md §6),
// so a deployed service no longer ships the code generator to publish its own hash. The namespace did
// not change, so source using Benzene.CodeGen.Core keeps compiling; these forwards keep assemblies
// already compiled against Benzene.CodeGen.Core working too.
[assembly: TypeForwardedTo(typeof(ContractHash))]
[assembly: TypeForwardedTo(typeof(JsonCanonicalizer))]
