using Benzene.Clients;
using Benzene.Examples.CodeGen.Contracts.Consumer.Generated.PaymentsCapture;

// This example has no runtime message sender wired up - it exists to prove Benzene.CodeGen.Build's
// MSBuild one-liner actually regenerates and compiles a typed client from the committed
// contracts/payments.spec.json fixture on every build (see the .csproj's <BenzeneServiceContract>
// item and its header comment). Referencing the generated type by name here means a broken or
// uncompilable generated client surfaces as a real build error in Benzene.Examples.sln's
// examples-build CI job, not just "files got written to disk".
IBenzeneMessageSender sender = null!;
var client = new PaymentsCaptureServiceClient(sender);

Console.WriteLine($"Generated client: {nameof(PaymentsCaptureServiceClient)}");
Console.WriteLine($"Contract hash: {client.HashCode}");
