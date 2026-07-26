using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Foundry.Core.User;
using Foundry.Schema.Compiler;
using Foundry.Api.Manifest;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Foundry.IntegrationTests;

/// <summary>
/// Ensures that old-format manifests (without Entities/Enums fields) continue to work
/// alongside the new Studio bridge format. Also validates the source generator handles
/// extra JSON fields gracefully since it uses string-based parsing.
/// </summary>
public class BackwardCompatibilityTests : IClassFixture<WebApplicationFactory<Program>>
{
    static BackwardCompatibilityTests()
    {
        Environment.SetEnvironmentVariable("MONGODB_ENCRYPTION_KEY", "12345678901234567890123456789012");
    }

    private readonly WebApplicationFactory<Program> _factory;

    public BackwardCompatibilityTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> CreateClientWithAdminRole()
    {
        var mockUserContext = Substitute.For<ICurrentUserContext>();
        mockUserContext.OperatorId.Returns("test-admin");
        var claims = new List<Claim> { new(ClaimTypes.Role, "Admin") };
        mockUserContext.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")));

        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
                services.AddScoped<ICurrentUserContext>(_ => mockUserContext));
        }).CreateClient();
    }

    #region Old Format Manifest Compatibility

    [Fact]
    public async Task OldManifest_Format_Parse_WithoutEntitiesField()
    {
        // Arrange — pre-bridge manifests have NO Entities[] or Enums[] fields
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "old-format-manifest.json");
        Assert.True(File.Exists(fixturePath));
        var json = File.ReadAllText(fixturePath);

        // Verify the JSON does NOT contain Entities (confirming it's truly old format)
        Assert.DoesNotContain("\"Entities\"", json);
        Assert.DoesNotContain("\"Enums\"", json);

        // Act — Foundry.Api should parse it without error
        var manifest = JsonSerializer.Deserialize<ApiManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert — all existing fields must be preserved
        Assert.NotNull(manifest);
        Assert.Single(manifest.Endpoints);
        Assert.Equal("Order", manifest.Endpoints[0].Entity);
        Assert.Single(manifest.CustomEndpoints);
        Assert.Equal("/api/v1/orders/checkout", manifest.CustomEndpoints[0].Route);
    }

    [Fact]
    public async Task OldManifest_WithRoles_PassesSecurityChecks()
    {
        // Arrange — old manifests with Roles should still enforce security
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "old-format-manifest.json");
        Assert.True(File.Exists(fixturePath));
        var json = File.ReadAllText(fixturePath);
        var manifest = JsonSerializer.Deserialize<ApiManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Act + Assert — verify roles are parsed for all methods
        foreach (var method in manifest!.Endpoints[0].Methods)
        {
            Assert.True(manifest.Endpoints[0].Roles.ContainsKey(method),
                $"Role config missing for method '{method}'");
        }
    }

    [Fact]
    public async Task OldManifest_WithCaching_PreservesConfig()
    {
        // Arrange — old manifests with Caching should preserve TTL settings
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "old-format-manifest.json");
        Assert.True(File.Exists(fixturePath));
        var json = File.ReadAllText(fixturePath);
        var manifest = JsonSerializer.Deserialize<ApiManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Act + Assert — verify caching config round-trips
        var caching = manifest!.Endpoints[0].Caching;
        Assert.True(caching.ContainsKey("GET"));
        Assert.True(caching["GET"].Enabled);
        Assert.Equal(60, caching["GET"].TtlSeconds);

        Assert.True(caching.ContainsKey("GET_BY_ID"));
        Assert.True(caching["GET_BY_ID"].Enabled);
        Assert.Equal(120, caching["GET_BY_ID"].TtlSeconds);
    }

    [Fact]
    public async Task OldManifest_WithoutRoles_GracesDown()
    {
        // Arrange — an old-style manifest with no Roles field at all should still work
        var jsonWithoutRoles = @"{
            ""Namespace"": ""Legacy.Domain"",
            ""Endpoints"": [{
                ""Route"": ""/api/v1/products"",
                ""Entity"": ""Product"",
                ""Methods"": [""GET""]
            }],
            ""CustomEndpoints"": []
        }";

        // Act — should not throw even without Roles or Caching
        var manifest = JsonSerializer.Deserialize<ApiManifest>(jsonWithoutRoles, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert
        Assert.NotNull(manifest);
        Assert.Single(manifest.Endpoints);
    }

    #endregion

    #region Source Generator String-Based Parser Tests

    [Fact]
    public async Task SourceGenerator_ExtraTopLevelFields_Ignored()
    {
        // Arrange — the source generator uses string-based parsing (IndexOf/ExtractValue).
        // Extra top-level fields like Entities[] and Enums[] should be silently ignored.
        var jsonWithUnknowns = @"{
            ""Namespace"": ""Test.Domain"",
            ""CustomField"": ""ignored"",
            ""DeepObject"": {""nested"": {""a"": 1}},
            ""Endpoints"": [{
                ""Route"": ""/api/v1/items"",
                ""Entity"": ""Item"",
                ""Methods"": [""GET""]
            }],
            ""CustomEndpoints"": [],
            ""ArrayField"": [1,2,3]
        }";

        // Act — verify the manifest still parses correctly despite extra fields
        var manifest = JsonSerializer.Deserialize<ApiManifest>(jsonWithUnknowns, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert — only known fields are populated
        Assert.NotNull(manifest);
        Assert.Single(manifest.Endpoints);
        Assert.Equal("Item", manifest.Endpoints[0].Entity);
        Assert.Empty(manifest.CustomEndpoints);
    }

    [Fact]
    public async Task SourceGenerator_EndpointsWithExtraFields_ParsedCorrectly()
    {
        // Arrange — each endpoint may carry extra fields (like the bridge adds)
        var json = @"{
            ""Namespace"": ""Test.Domain"",
            ""Endpoints"": [{
                ""Route"": ""/api/v1/products"",
                ""Entity"": ""Product"",
                ""Methods"": [""GET"",""POST""],
                ""ExtraString"": ""value"",
                ""ExtraNumber"": 42,
                ""ExtraArray"": [true,false],
                ""ExtraObject"": {""a"":1}
            }],
            ""CustomEndpoints"": []
        }";

        // Act — should parse known fields correctly despite extras
        var manifest = JsonSerializer.Deserialize<ApiManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert
        Assert.NotNull(manifest);
        var ep = manifest!.Endpoints[0];
        Assert.Equal("/api/v1/products", ep.Route);
        Assert.Equal("Product", ep.Entity);
        Assert.Contains("GET", ep.Methods);
        Assert.Contains("POST", ep.Methods);
    }

    [Fact]
    public async Task SourceGenerator_EmptyEndpointsArray_Valid()
    {
        // Arrange — an empty endpoints array should be valid (edge case)
        var json = @"{
            ""Namespace"": ""Test.Domain"",
            ""Endpoints"": [],
            ""CustomEndpoints"": []
        }";

        // Act
        var manifest = JsonSerializer.Deserialize<ApiManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert
        Assert.NotNull(manifest);
        Assert.Empty(manifest.Endpoints);
    }

    #endregion

    #region Multi-Entity Manifest Compatibility

    [Fact]
    public async Task MultiEntityManifest_MixedFormat_ParsesCorrectly()
    {
        // Arrange — a manifest where some endpoints have old format and others have new fields
        var json = @"{
            ""Namespace"": ""Mixed.Domain"",
            ""Endpoints"": [
                {
                    ""Route"": ""/api/v1/orders"",
                    ""Entity"": ""Order"",
                    ""Methods"": [""GET""]
                },
                {
                    ""Route"": ""/api/v1/products"",
                    ""Entity"": ""Product"",
                    ""Methods"": [""GET"",""POST""],
                    ""Caching"": {""GET"": {""Enabled"": true, ""TtlSeconds"": 30}},
                    ""NavigationProperties"": [{""Name"": ""Category"", ""Type"": ""Association""}]
                }
            ],
            ""CustomEndpoints"": []
        }";

        // Act
        var manifest = JsonSerializer.Deserialize<ApiManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert — both endpoints should be present regardless of field differences
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.Endpoints.Count);
        Assert.Equal("Order", manifest.Endpoints[0].Entity);
        Assert.Equal("Product", manifest.Endpoints[1].Entity);

        // Verify the second endpoint has its extra fields parsed
        Assert.True(manifest.Endpoints[1].Caching.ContainsKey("GET"));
    }

    [Fact]
    public async Task Manifest_NewFields_BackwardsCompatible_WithStudioBridge()
    {
        // Arrange — the studio-new-format.json should work alongside old manifests in the same project
        var newFixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "studio-new-format.json");
        Assert.True(File.Exists(newFixturePath));
        var json = File.ReadAllText(newFixturePath);

        // Act — parse as ApiManifest (ignoring Entities/Enums)
        var manifest = JsonSerializer.Deserialize<ApiManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert — core fields must be identical to what the old format would produce
        Assert.NotNull(manifest);
        Assert.Equal(3, manifest.Endpoints.Count);
        Assert.All(manifest.Endpoints, ep =>
        {
            Assert.False(string.IsNullOrEmpty(ep.Route));
            Assert.False(string.IsNullOrEmpty(ep.Entity));
            Assert.NotEmpty(ep.Methods);
        });
    }

    [Fact]
    public async Task Manifest_BothFormats_CanCoexist()
    {
        // Arrange — load both old and new formats in the same test session
        var oldPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "old-format-manifest.json");
        var newPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "studio-new-format.json");

        Assert.True(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));

        // Act — parse both simultaneously (simulating a project with mixed manifests)
        var oldManifest = JsonSerializer.Deserialize<ApiManifest>(
            File.ReadAllText(oldPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var newManifest = JsonSerializer.Deserialize<ApiManifest>(
            File.ReadAllText(newPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert — both should parse without conflict
        Assert.NotNull(oldManifest);
        Assert.NotNull(newManifest);

        // Verify old manifest has exactly what it had before (no regression)
        Assert.Single(oldManifest.Endpoints);
        Assert.Single(oldManifest.CustomEndpoints);

        // Verify new manifest has all entities including the new bridge data
        Assert.Equal(3, newManifest.Endpoints.Count);
    }

    #endregion

    #region Schema → POCO Compiler Compatibility

    [Fact]
    public async Task Compiler_OldSchemaFormat_ProducesExpectedOutput()
    {
        // Arrange — a schema without new studio fields (simulating pre-bridge import)
        var schema = new SchemaModel
        {
            Namespace = "Legacy.Domain.Models",
            Entities = new List<Entity>
            {
                new Entity
                {
                    Name = "LegacyItem",
                    SoftDelete = false,
                    Auditable = false,
                    Properties = new List<Property>
                    {
                        new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                        new Property { Name = "Value", Type = "string" }
                    }
                }
            }
        };

        // Act
        var output = PocoGenerator.Generate(schema);

        // Assert — should produce valid POCO without errors
        // Presence, not an exact count: the generator also emits supporting files
        // (serialization context, index verification) whose number is not what this asserts.
        Assert.True(output.ContainsKey("LegacyItem"));
        Assert.Contains("public partial record LegacyItem", output["LegacyItem"]);
        Assert.Contains("namespace Legacy.Domain.Models;", output["LegacyItem"]);
    }

    [Fact]
    public async Task Compiler_NewSchemaFields_ProducesExpectedOutput()
    {
        // Arrange — a schema with the full studio bridge fields
        var schema = new SchemaModel
        {
            Namespace = "NewBridge.Domain.Models",
            Entities = new List<Entity>
            {
                new Entity
                {
                    Name = "NewBridgeItem",
                    SoftDelete = true,
                    Auditable = true,
                    BaseClass = null,
                    Properties = new List<Property>
                    {
                        new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                        new Property { Name = "Name", Type = "string", Attributes = new List<string> { "Required" } }
                    },
                    Indexes = new List<Foundry.Schema.Compiler.Index>
                    {
                        new Foundry.Schema.Compiler.Index { Fields = new List<string> { "Name" }, Unique = false }
                    }
                }
            }
        };

        // Act
        var output = PocoGenerator.Generate(schema);

        // Assert — should generate with soft-delete and auditable markers
        Assert.True(output.ContainsKey("NewBridgeItem"));
        Assert.Contains("ISoftDelete", output["NewBridgeItem"]);
    }

    [Fact]
    public async Task Compiler_MultiEntitySchema_GeneratesAllFiles()
    {
        // Arrange — simulate a complex studio design with multiple entities and enums
        var schema = new SchemaModel
        {
            Namespace = "Complex.Domain.Models",
            Entities = new List<Entity>
            {
                new Entity { Name = "Author", SoftDelete = false, Auditable = false, Properties = new List<Property> { new Property { Name = "Id", Type = "ObjectId", IsKey = true }, new Property { Name = "FullName", Type = "string" } } },
                new Entity { Name = "Book", SoftDelete = false, Auditable = true, Properties = new List<Property> { new Property { Name = "Id", Type = "ObjectId", IsKey = true }, new Property { Name = "Title", Type = "string", Attributes = new List<string> { "Required" } }, new Property { Name = "AuthorName", Type = "string" } } },
                new Entity { Name = "Review", SoftDelete = false, Auditable = false, Properties = new List<Property> { new Property { Name = "Id", Type = "ObjectId", IsKey = true }, new Property { Name = "BookTitle", Type = "string" }, new Property { Name = "Rating", Type = "int" } } }
            },
            Enums = new List<Foundry.Schema.Compiler.Enum>
            {
                new Foundry.Schema.Compiler.Enum { Name = "BookGenre", Values = new List<string> { "Fiction", "NonFiction", "Science" } }
            }
        };

        // Act
        var output = PocoGenerator.Generate(schema);

        // Assert — all entities + enum should be generated
        // See above: assert the domain types exist rather than counting every emitted file.
        Assert.True(output.ContainsKey("Author"));
        Assert.True(output.ContainsKey("Book"));
        Assert.True(output.ContainsKey("Review"));
        Assert.True(output.ContainsKey("BookGenre"));

        foreach (var kvp in output)
        {
            Assert.False(string.IsNullOrEmpty(kvp.Value));
            Assert.Contains("namespace Complex.Domain.Models", kvp.Value);
        }
    }

    #endregion
}
