# ============================================================================
# Multi-stage build: the SDK image (~800 MB, has the compiler) builds the
# app; the final image only contains the slim ASP.NET runtime + our output.
# Result: a much smaller, safer production image.
# ============================================================================

# ---- Stage 1: build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy everything (a .dockerignore keeps bin/obj out). For bigger repos you'd
# copy csproj files first and `dotnet restore` separately to maximize Docker
# layer caching — kept simple here on purpose.
COPY . .

RUN dotnet publish src/OrderService/OrderService.Api/OrderService.Api.csproj \
    -c Release -o /app/publish

# ---- Stage 2: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# .NET 8 images listen on 8080 by default (non-root friendly).
EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderService.Api.dll"]
