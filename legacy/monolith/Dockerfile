FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["LoginAndReg.csproj", "./"]
RUN dotnet restore "LoginAndReg.csproj"

COPY . .
RUN dotnet build "LoginAndReg.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "LoginAndReg.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "LoginAndReg.dll"]