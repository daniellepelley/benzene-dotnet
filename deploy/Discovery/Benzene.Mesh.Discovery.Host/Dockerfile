# Build from the repo root: docker build -f deploy/Discovery/Benzene.Mesh.Discovery.Host/Dockerfile .
# (this project's ProjectReferences reach up into src/, so the build context must be the repo root,
# not this folder - same reason deploy/Mesh/Benzene.Mesh.Host/Dockerfile does the same thing.)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish deploy/Discovery/Benzene.Mesh.Discovery.Host/Benzene.Mesh.Discovery.Host.csproj -c Release -o /app

# The runtime-only base image, not aspnet - this job has no HTTP surface (see ../README.md), so it
# needs none of the ASP.NET Core shared framework the mesh host's image pulls in.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Benzene.Mesh.Discovery.Host.dll"]
