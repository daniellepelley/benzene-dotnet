using Benzene.Descriptor;

// benzene-descriptor --assembly <path-to-service.dll> [options]
//
// Emits a service's contract artifacts — {name}.spec.json (the EventServiceDocument) and/or
// {name}.service.json (the mesh §2 ServiceDescriptor) — from a built, non-running Benzene service.
// Intended to run as a post-build step (see build/Benzene.Descriptor.targets). A thin shell: all the
// real work is DescriptorEmitter's, kept callable in-process so it can be unit-tested without
// spawning this executable.

var opts = EmitOptions.Parse(args);
if (opts is null)
{
    Console.Error.WriteLine(EmitOptions.Usage);
    return 2;
}

// mesh.md §2.5: a version that does not parse under its declared scheme fails the build that emitted
// it. This is the one place in the system where the error is cheap — after here the value travels.
var versionError = opts.ValidateVersion();
if (versionError is not null)
{
    Console.Error.WriteLine($"benzene-descriptor: {versionError}");
    return 2;
}

try
{
    var result = DescriptorEmitter.Emit(opts);
    var (specPath, descriptorPath) = DescriptorEmitter.ResolveOutputPaths(opts);

    Write(result.SpecJson, specPath, "spec");
    Write(result.DescriptorJson, descriptorPath, "descriptor");

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"benzene-descriptor: {ex.Message}");
    return 1;
}

static void Write(string? json, string? path, string label)
{
    if (json is null) return;

    if (path is null)
    {
        Console.WriteLine(json);
        return;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, json);
    Console.WriteLine($"benzene-descriptor: wrote {label} to {path}");
}
