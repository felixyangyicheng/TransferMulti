FROM mcr.microsoft.com/dotnet/sdk:10.0-preview-alpine as build
WORKDIR /app
EXPOSE 80
EXPOSE 443
COPY . .
RUN dotnet restore TransferMulti.srv.csproj
RUN dotnet publish -o /app/published-app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview-alpine as runtime
RUN apk add icu 
WORKDIR /app
COPY --from=build /app/published-app /app
ENTRYPOINT ["dotnet", "/app/TransferMulti.srv.dll"]