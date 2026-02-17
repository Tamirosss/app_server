# =========================
# Stage 1 - Build
# =========================
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

WORKDIR /src

# Copy csproj and restore first (better cache)
COPY *.csproj ./
RUN dotnet restore

# Copy everything else
COPY . ./

# Publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false


# =========================
# Stage 2 - Runtime
# =========================
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

WORKDIR /app

# Copy published files
COPY --from=build /app/publish .

# Create folder for SQLite
RUN mkdir -p /app/data

# Important for Coolify – bind to dynamic PORT
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}

# SQLite connection string
ENV ConnectionStrings__DefaultConnection="Data Source=/app/data/workout.db"

# Do NOT hardcode port
EXPOSE 8080

# Replace with your actual dll name if different
ENTRYPOINT ["dotnet", "JsonDemo.dll"]
