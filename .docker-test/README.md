# Jellyfin 10.11.11 isolated test server

This compose project binds Jellyfin only to `127.0.0.1:8097`. It does not use
Tailscale and does not share configuration, cache, users, or plugins with the
production server on port 8096.

Build the plugin before starting the container:

```powershell
dotnet build --configuration Release
New-Item -ItemType Directory -Force .docker-test/config/plugins/Bangumi_1.7.6.100
Copy-Item Jellyfin.Plugin.Bangumi/bin/Release/net9.0/Jellyfin.Plugin.Bangumi.dll `
  .docker-test/config/plugins/Bangumi_1.7.6.100/
docker compose -f .docker-test/compose.yml up -d
```

Place only disposable test media under `.docker-test/media`. The directory is
mounted read-only. In the test server, create an animation library at `/media`
and a separate test user, then authorize that user with its own Bangumi OAuth
binding.

Stop the test server with:

```powershell
docker compose -f .docker-test/compose.yml down
```

The `down` command does not delete `.docker-test/config` or
`.docker-test/cache`, so test state remains available for diagnosis.
