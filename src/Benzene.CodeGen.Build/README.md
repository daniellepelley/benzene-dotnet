# Benzene.CodeGen.Build

MSBuild one-line client generation for [Benzene](https://benzene.app): drop a committed spec JSON
file and one `<BenzeneServiceContract>` item into your project, and get a typed client SDK
regenerated incrementally and compiled straight into your build — no manual CLI invocation, no
checked-in generated `.cs` files to keep in sync by hand.

## The idea

A producer team publishes its service's contract as a `.spec.json` file (see
[`Benzene.Descriptor`](https://www.nuget.org/packages/Benzene.Descriptor), which emits one on every
build). A consumer team commits a copy of that file into their own repo — under `contracts/`, say —
the same way they'd commit a `.proto` file or an OpenAPI document. From there, this package takes
over: every time the consumer builds, it turns that committed file into a typed client and compiles
it in, regenerating only when the committed file itself changes.

## Usage

1. Reference this package (or, in-tree in the Benzene repo itself, `<Import>` its `.targets` file
   directly — see `examples/CodeGen/Benzene.Examples.CodeGen.Contracts.Consumer` for the pattern this
   repo's own examples use).
2. Commit the spec file your producer publishes (e.g. `contracts/orders.spec.json`).
3. Add one item:

   ```xml
   <ItemGroup>
     <BenzeneServiceContract Include="contracts/orders.spec.json"
                              Mode="topic-client"
                              ServiceName="Orders"
                              Namespace="Contracts.Orders"
                              Topics="order:create,order:cancel" />
   </ItemGroup>
   ```

   Every attribute but `Include` is optional:

   | Attribute     | Default                          | Maps to the `benzene build` CLI's… |
   |---------------|-----------------------------------|-------------------------------------|
   | `Mode`        | `topic-client`                    | `-output` (`client`, `topic-client`, `message-handlers`, or `readme`) |
   | `ServiceName` | the file's own stem               | `-service-name` |
   | `Namespace`   | *(the CLI's own default derivation)* | `-namespace` |
   | `Topics`      | every non-reserved topic in the document | `-topics` (comma-delimited include-list) |

4. Build. The generated client lands under
   `$(IntermediateOutputPath)benzene/{ServiceName}/` and is compiled straight into your project —
   reference its types (e.g. `{Topic}ServiceClient`, `I{Topic}ServiceClient` for `topic-client` mode)
   like any other class in your project.

Add as many `<BenzeneServiceContract>` items as you have contracts to consume; each gets its own
regeneration and its own stamp file, so touching one contract never forces the others to regenerate.

## Regeneration is incremental, generation failures are build failures

- A build with no changes to any committed contract file **skips regeneration entirely** — ordinary
  MSBuild `Inputs`/`Outputs` incrementality, not anything bespoke.
- Editing a committed contract file regenerates just that contract's client on the next build.
- A broken contract (invalid JSON, a `Mode` the CLI doesn't recognize, …) **fails the build**, with
  the CLI's own error message in the MSBuild output — a service that can't produce a valid client
  from its contract should not report a green build.

## Running the `benzene` CLI another way

By default this package invokes the `benzene` dotnet tool by its `PATH` name. Override
`$(BenzeneCliCommand)` to run it differently — for example `dotnet tool run benzene` for a local-tool
manifest install, or (as this repo's own examples do, to avoid depending on the tool already being
built when a separate CI job builds the examples) building and running it from source with a
`dotnet run` invocation. See `src/Benzene.CodeGen.Build/CLAUDE.md` for the full mechanics and the
MSBuild property-evaluation-order and item-batching traps this package's targets file works around.

## See also

- [`docs/client-sdks.md`](../../docs/client-sdks.md) — the `client`/`topic-client`/`message-handlers`
  shapes this package generates, its `-namespace`/`-topics` flags, and its "one-line MSBuild
  integration" section (a CLI reference page for `benzene build` itself, `docs/cli.md`, is a tracked
  TODO — see `docs/index.md`'s Code Generation section for the flags documented so far).
- [`Benzene.Descriptor`](../Benzene.Descriptor/README.md) — the producer-side counterpart that emits
  the `.spec.json` file this package's consumer half turns into a client.
