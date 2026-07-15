# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Client.Demo.csproj", "./"]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Render (and most PaaS) inject PORT at runtime — bind to it, not a fixed port.
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["sh","-c","dotnet Client.Demo.dll --urls http://0.0.0.0:${PORT:-8080}"]
