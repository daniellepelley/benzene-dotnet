using Benzene.Descriptor;

// benzene-descriptor --assembly <path-to-service.dll> [options]
//
// Emits a service's contract document - {name}.spec.json, the document its benzene:spec topic serves
// and the body it registers with a mesh collector (mesh.md §2) - from a built, non-running Benzene
// service. Intended to run as a post-build step (see build/Benzene.Descriptor.targets). A thin shell:
// all the real work is DescriptorEmitter's, kept callable in-process so it can be unit-tested without
// spawning this executable.

var opts = EmitOptions.Parse(args);
if (opts is null)
{
    Console.Error.WriteLine(EmitOptions.Usage);
    return 2;
}

var artifactError = opts.ValidateArtifacts();
if (artifactError is not null)
{
    Console.Error.WriteLine($"benzene-descriptor: {artifactError}");
    return 2;
}

// service-versions.md §2: a version that does not parse under its declared scheme fails the build that
// emitted it. This is the one place in the system where the error is cheap. A valid version and its
// scheme are written to the document's serviceVersion and versionScheme (#171).
var versionError = opts.ValidateVersion();
if (versionError is not null)
{
    Console.Error.WriteLine($"benzene-descriptor: {versionError}");
    return 2;
}

try
{
    var result = DescriptorEmitter.Emit(opts);
    var path = DescriptorEmitter.ResolveOutputPath(opts);

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, result.SpecJson);
    Console.WriteLine($"benzene-descriptor: wrote the contract document to {path}");

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"benzene-descriptor: {ex.Message}");
    return 1;
}
