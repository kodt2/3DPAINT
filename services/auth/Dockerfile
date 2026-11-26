FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["global.json", "./"]
COPY ["Auth.csproj", "./"]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
# The RSA signing key lives outside the image (Development: a named volume the
# service generates into; Production: an operator-provided secret file). The
# service fail-fasts in Production when the key file is absent (ADR 0018).
EXPOSE 8080
ENTRYPOINT ["dotnet", "Auth.dll"]
