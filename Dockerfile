# 固定 SDK 與執行階段；backend-runtime 亦供尚未整合前端時獨立驗證 API。
FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS backend-build
WORKDIR /src
COPY global.json ./
# 先複製專案與鎖檔，讓只改原始碼的建置沿用固定相依的還原快取。
COPY backend/Procurement.Api.csproj backend/packages.lock.json backend/
RUN dotnet restore backend/Procurement.Api.csproj --locked-mode
COPY backend/ backend/
RUN dotnet publish backend/Procurement.Api.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS backend-runtime
WORKDIR /app
COPY --from=backend-build /out ./
RUN mkdir -p /app/wwwroot
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "Procurement.Api.dll"]

# Node 只用於建置；最終映像由下方 ASP.NET Core 提供靜態檔。
FROM node:24.22.2 AS frontend-build
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# 單體以相同來源提供前端及 API，避免另外部署前端伺服器。
FROM backend-runtime AS final
COPY --from=frontend-build /src/frontend/dist /app/wwwroot
