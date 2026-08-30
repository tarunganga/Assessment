FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Ripple.Treasury.Assessment.Api/Ripple.Treasury.Assessment.Api.csproj", "Ripple.Treasury.Assessment.Api/"]
COPY ["Ripple.Treasury.Assessment.Services/Ripple.Treasury.Assessment.Services.csproj", "Ripple.Treasury.Assessment.Services/"]
COPY ["Ripple.Treasury.Assessment.Infrastructure/Ripple.Treasury.Assessment.Infrastructure.csproj", "Ripple.Treasury.Assessment.Infrastructure/"]
RUN dotnet restore "Ripple.Treasury.Assessment.Api/Ripple.Treasury.Assessment.Api.csproj"
COPY . .
WORKDIR "/src/Ripple.Treasury.Assessment.Api"
RUN dotnet build "./Ripple.Treasury.Assessment.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Ripple.Treasury.Assessment.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Ripple.Treasury.Assessment.Api.dll"]
