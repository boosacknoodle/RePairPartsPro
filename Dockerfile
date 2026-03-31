FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY RepairPartsProWeb.csproj ./
RUN dotnet restore RepairPartsProWeb.csproj

COPY . .
RUN dotnet publish RepairPartsProWeb.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

RUN mkdir -p /app/data
VOLUME ["/app/data"]

COPY --from=build /app/publish .
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} dotnet RepairPartsProWeb.dll"]
