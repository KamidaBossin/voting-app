# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build

WORKDIR /source

COPY Worker.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine

WORKDIR /app

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

RUN adduser -D -u 1000 appuser
USER appuser

COPY --from=build --chown=appuser:appuser /app/publish .

EXPOSE 8081

CMD ["dotnet", "Worker.dll"]
