# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
COPY Directory.Build.props global.json NuGet.Config ./
COPY src/EmailValidation.Domain/EmailValidation.Domain.csproj src/EmailValidation.Domain/
COPY src/EmailValidation.Core/EmailValidation.Core.csproj src/EmailValidation.Core/
COPY src/EmailValidation.Application/EmailValidation.Application.csproj src/EmailValidation.Application/
COPY src/EmailValidation.Infrastructure/EmailValidation.Infrastructure.csproj src/EmailValidation.Infrastructure/
COPY src/EmailValidation.Grpc/EmailValidation.Grpc.csproj src/EmailValidation.Grpc/
COPY src/EmailValidation.Api/EmailValidation.Api.csproj src/EmailValidation.Api/
RUN dotnet restore src/EmailValidation.Api/EmailValidation.Api.csproj

FROM restore AS publish
COPY src/ src/
RUN dotnet publish src/EmailValidation.Api/EmailValidation.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    EMAILVALIDATION_HEALTHCHECK_URL=http://127.0.0.1:8080/health/live
EXPOSE 8080 8081
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=6s --start-period=30s --retries=3 \
    CMD ["dotnet", "EmailValidation.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "EmailValidation.Api.dll"]
