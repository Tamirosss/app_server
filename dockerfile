# ============================================================
# שלב 1: Build - קומפילציה של הפרויקט
# משתמשים ב-SDK image שמכיל את כלי הבנייה
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

# תיקיית העבודה בתוך הקונטיינר
WORKDIR /app

# העתקת קבצי הפרויקט תחילה (לניצול cache של Docker)
COPY *.csproj ./

# שחזור חבילות NuGet
RUN dotnet restore

# העתקת שאר הקוד
COPY . ./

# בנייה ופרסום במצב Release
RUN dotnet publish -c Release -o /app/publish

# ============================================================
# שלב 2: Runtime - הרצת השרת
# משתמשים ב-runtime image קטן יותר (ללא כלי בנייה)
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

WORKDIR /app

# העתקת הפרויקט המקומפל משלב הבנייה
COPY --from=build /app/publish .

# פורט שהשרת מאזין עליו
EXPOSE 8080

# הגדרת ASP.NET לעבוד על פורט 8080
ENV ASPNETCORE_URLS=http://+:8080

# פקודת הרצה
ENTRYPOINT ["dotnet", "YourProjectName.dll"]








































