FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Ripple.Treasury.Assesment/Ripple.Treasury.Assesment.csproj", "Ripple.Treasury.Assesment/"]
RUN dotnet restore "Ripple.Treasury.Assesment/Ripple.Treasury.Assesment.csproj"
COPY . .
WORKDIR "/src/Ripple.Treasury.Assesment"
RUN dotnet build "./Ripple.Treasury.Assesment.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Ripple.Treasury.Assesment.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Ripple.Treasury.Assesment.dll"]
