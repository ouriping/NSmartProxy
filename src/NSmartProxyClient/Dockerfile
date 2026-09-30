FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/NSmartProxyClient/NSmartProxyClient.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN useradd --create-home --uid 10001 nsp
WORKDIR /app
COPY --from=build /out .
USER nsp
ENTRYPOINT ["dotnet", "NSmartProxyClient.dll"]
