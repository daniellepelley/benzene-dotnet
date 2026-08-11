# Build context is the repo root (the project references src/ directly).
#   docker build -f examples/K8sTransports/Api/Dockerfile -t benzene-k8stransports-api:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish examples/K8sTransports/Api/Benzene.Examples.K8sTransports.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Benzene.Examples.K8sTransports.Api.dll"]
