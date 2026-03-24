FROM mcr.microsoft.com/dotnet/sdk:9.0 AS builder

WORKDIR /app

COPY ./ss-integrated-management-server/ss.Integrated.Management.Server/*.csproj ./
RUN dotnet restore

COPY ./ss-integrated-management-server/ss.Integrated.Management.Server/ ./
RUN dotnet publish -c Release -o out --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*

COPY --from=builder /app/out .

ENTRYPOINT ["dotnet", "ss.Integrated.Management.Server.dll"]