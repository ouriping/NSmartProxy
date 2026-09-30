FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/NSmartProxy.ServerHost/NSmartProxy.ServerHost.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN useradd --create-home --uid 10001 nsp
WORKDIR /app
COPY --from=build /out .
USER nsp
EXPOSE 7841 7842
ENTRYPOINT ["dotnet", "NSmartProxy.ServerHost.dll"]
