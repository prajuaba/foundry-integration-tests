# Foundry.IntegrationTests

Integration and end-to-end tests that validate the complete Foundry pipeline — bridging Schema Studio's domain model export to Foundry.Api's dynamic engine.

## What These Tests Cover

| Category | Files | Purpose |
|----------|-------|---------|
| **Manifest Contracts** | `ManifestContractTests.cs` | Verify Studio's export format matches Foundry.Api expectations |
| **Pipeline E2E** | `PipelineIntegrationTests.cs` | Validate schema → compiler → manifest → API routing end-to-end |
| **Backward Compat** | `BackwardCompatibilityTests.cs` | Ensure old manifests work alongside new bridge fields |

## Fixtures

| File | Format | Description |
|------|--------|-------------|
| `fixtures/old-format-manifest.json` | Pre-bridge | Manifest without `Entities[]` or `Enums[]` fields |
| `fixtures/studio-new-format.json` | Post-bridge | Full Studio export with all bridge fields |

## Running the Tests

```bash
dotnet test foundry-integration-tests/Foundry.IntegrationTests.csproj
```
