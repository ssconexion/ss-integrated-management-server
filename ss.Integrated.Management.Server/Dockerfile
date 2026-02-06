FROM mcr.microsoft.com/dotnet/sdk:9.0 AS builder
WORKDIR /app

COPY *.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app

COPY --from=builder /app/out .

ENTRYPOINT ["dotnet", "ss.Integrated.Management.Server.dll"]
