# Multi-stage build for ASP.NET Core API
# Build context: repository root
# Scoped to the API project (not the solution) so additions of tooling/test
# projects to the solution don't break or slow the image build.

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

WORKDIR /src

# Copy build configuration
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["global.json", "./"]

# Copy project files first for layer-cached restore
COPY ["src/Koinon.Domain/Koinon.Domain.csproj", "src/Koinon.Domain/"]
COPY ["src/Koinon.Application/Koinon.Application.csproj", "src/Koinon.Application/"]
COPY ["src/Koinon.Infrastructure/Koinon.Infrastructure.csproj", "src/Koinon.Infrastructure/"]
COPY ["src/Koinon.Api/Koinon.Api.csproj", "src/Koinon.Api/"]

# Restore only the API project graph
RUN dotnet restore src/Koinon.Api/Koinon.Api.csproj

# Copy source code for the API project graph
COPY ["src/Koinon.Domain/", "src/Koinon.Domain/"]
COPY ["src/Koinon.Application/", "src/Koinon.Application/"]
COPY ["src/Koinon.Infrastructure/", "src/Koinon.Infrastructure/"]
COPY ["src/Koinon.Api/", "src/Koinon.Api/"]

# Publish. Do not use --no-restore here: Docker layer restores can leave
# package assets incomplete for publish on clean builders (NETSDK1064).
RUN dotnet publish src/Koinon.Api/Koinon.Api.csproj -c Release -o /app/publish

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

WORKDIR /app

# Install curl for health checks
RUN apk add --no-cache curl

# Copy published application
COPY --from=build /app/publish .

# Expose port
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=10s --retries=3 --start-period=40s \
    CMD curl -f http://localhost:8080/health || exit 1

# Set entry point
ENTRYPOINT ["dotnet", "Koinon.Api.dll"]
