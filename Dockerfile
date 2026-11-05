FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/WmsCore.Api/WmsCore.Api.csproj src/WmsCore.Api/
RUN dotnet restore src/WmsCore.Api
COPY src/ src/
RUN dotnet publish src/WmsCore.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "WmsCore.Api.dll"]
