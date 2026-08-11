# Build context is the repo root (the project references src/ directly).
#   docker build -f examples/K8sTransports/KafkaWorker/Dockerfile -t benzene-k8stransports-kafka-worker:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish examples/K8sTransports/KafkaWorker/Benzene.Examples.K8sTransports.KafkaWorker.csproj -c Release -o /app

# A worker has no inbound listener - the runtime image is the plain (non-ASP.NET) .NET image.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Benzene.Examples.K8sTransports.KafkaWorker.dll"]
