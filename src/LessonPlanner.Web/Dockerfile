FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=true
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/LessonPlanner.Core/ src/LessonPlanner.Core/
COPY src/LessonPlanner.Agents/ src/LessonPlanner.Agents/
COPY src/LessonPlanner.Infrastructure/ src/LessonPlanner.Infrastructure/
COPY src/LessonPlanner.ServiceDefaults/ src/LessonPlanner.ServiceDefaults/
COPY src/LessonPlanner.Web/ src/LessonPlanner.Web/
RUN dotnet restore src/LessonPlanner.Web/LessonPlanner.Web.csproj --locked-mode -p:UseAppHost=false --source https://packagefeedproxy.microsoft.io/nuget/v3/index.json \
    && dotnet publish src/LessonPlanner.Web/LessonPlanner.Web.csproj --no-restore --configuration Release --output /out --no-self-contained -p:UseAppHost=false -p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
COPY --from=build /out/ ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "LessonPlanner.Web.dll"]
