# Build context is the repo root (the project references src/ directly).
#   docker build -f examples/K8sTransports/SqsWorker/Dockerfile -t benzene-k8stransports-sqs-worker:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish examples/K8sTransports/SqsWorker/Benzene.Examples.K8sTransports.SqsWorker.csproj -c Release -o /app

# A worker has no inbound listener - the runtime image is the plain (non-ASP.NET) .NET image.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Benzene.Examples.K8sTransports.SqsWorker.dll"]
