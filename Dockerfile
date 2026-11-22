# One image for every service; run.sh picks the executable.
FROM node:22 AS client
WORKDIR /client
COPY src/KatalepsisFT.Web/ClientApp/package.json src/KatalepsisFT.Web/ClientApp/package-lock.json ./
RUN npm ci
COPY src/KatalepsisFT.Web/ClientApp/ ./
RUN npx ng build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/ src/
RUN dotnet publish src/KatalepsisFT.Web    -c Release -o /app/web \
 && dotnet publish src/KatalepsisFT.Worker -c Release -o /app/worker \
 && dotnet publish src/KatalepsisFT.Jobs   -c Release -o /app/jobs

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
COPY --from=client /client/dist /app/web/wwwroot
COPY run.sh /app/run.sh
# Render routes traffic to port 10000 by default.
ENV ASPNETCORE_HTTP_PORTS=10000
USER app
CMD ["/app/run.sh", "web"]
