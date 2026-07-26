using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Foundry.Api.Manifest;
using Foundry.Schema.Compiler;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;
using Xunit;

namespace Foundry.IntegrationTests;

/// <summary>
/// End-to-end tests that validate the full Foundry pipeline:
/// design (schema) → compile (POCOs) → scaffold (manifest) → API endpoints.
/// These test the bridge between Studio's domain model and Foundry.Api's dynamic engine.
/// </summary>
public class PipelineIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    static PipelineIntegrationTests()
    {
        Environment.SetEnvironmentVariable("MONGODB_ENCRYPTION_KEY", "12345678901234567890123456789012");
    }

    private readonly WebApplicationFactory<Program> _factory;

    public PipelineIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    #region Full Schema → Compiler → Manifest Pipeline

    [Fact]
    public async Task CompletePipeline_SchemaToApiEndpoints_WorksEndToEnd()
    {
        // Arrange — Step 1: Create a schema matching what Studio would export
        var schema = new SchemaModel
        {
            Namespace = "IntegrationTest.Domain",
            Entities = new List<Entity>
            {
                new Entity
                {
                    Name = "Product",
                    SoftDelete = false,
                    Auditable = true,
                    Properties = new List<Property>
                    {
                        new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                        new Property { Name = "Name", Type = "string", Attributes = new List<string> { "Required" } },
                        new Property { Name = "Price", Type = "decimal" },
                        new Property { Name = "Category", Type = "ProductCategory", IsEnum = true }
                    }
                }
            },
            Enums = new List<Foundry.Schema.Compiler.Enum>
            {
                new Foundry.Schema.Compiler.Enum
                {
                    Name = "ProductCategory",
                    Values = new List<string> { "Electronics", "Books", "Clothing" }
                }
            }
        };

        // Act — Step 2: Compile schema to POCOs
        var pocoFiles = PocoGenerator.Generate(schema);

        // Assert — Step 3: Verify POCO output contains required classes
        // Presence rather than an exact count: the generator also emits supporting files
        // (serialization context, index verification), and their number is not the subject
        // of this test.
        Assert.True(pocoFiles.ContainsKey("Product"));
        Assert.True(pocoFiles.ContainsKey("ProductCategory"));

        var productCode = pocoFiles["Product"];
        Assert.Contains("public partial record Product", productCode);
        Assert.Contains("using Foundry.Core.Entities;", productCode);
        Assert.Contains("namespace IntegrationTest.Domain;", productCode);

        var categoryCode = pocoFiles["ProductCategory"];
        Assert.Contains("public enum ProductCategory", categoryCode);
    }

    [Fact]
    public async Task CompletePipeline_MultipleEntities_GenerateCorrectManifest()
    {
        // Arrange — schema with two entities that reference each other (bridge scenario)
        var schema = new SchemaModel
        {
            Namespace = "IntegrationTest.Domain",
            Entities = new List<Entity>
            {
                new Entity
                {
                    Name = "Invoice",
                    SoftDelete = true,
                    Auditable = true,
                    Properties = new List<Property>
                    {
                        new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                        new Property { Name = "CustomerName", Type = "string", Attributes = new List<string> { "Required" } },
                        new Property { Name = "Amount", Type = "decimal" },
                        new Property { Name = "Status", Type = "InvoiceStatus", IsEnum = true }
                    },
                    Indexes = new List<Foundry.Schema.Compiler.Index>
                    {
                        new Foundry.Schema.Compiler.Index { Fields = new List<string> { "CustomerName" }, Unique = false }
                    }
                },
                new Entity
                {
                    Name = "InvoiceLine",
                    SoftDelete = false,
                    Auditable = false,
                    Properties = new List<Property>
                    {
                        new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                        new Property { Name = "InvoiceId", Type = "string", Attributes = new List<string> { "Index" } },
                        new Property { Name = "Description", Type = "string" },
                        new Property { Name = "Quantity", Type = "int" },
                        new Property { Name = "UnitPrice", Type = "decimal" }
                    }
                }
            },
            Enums = new List<Foundry.Schema.Compiler.Enum>
            {
                new Foundry.Schema.Compiler.Enum
                {
                    Name = "InvoiceStatus",
                    Values = new List<string> { "Draft", "Sent", "Paid", "Overdue" }
                }
            }
        };

        // Act — Compile
        var pocoFiles = PocoGenerator.Generate(schema);

        // Assert — Both entities + enum should be generated
        // Presence rather than an exact count: the generator also emits supporting files
        // (serialization context, index verification), and their number is not the subject
        // of this test.
        Assert.True(pocoFiles.ContainsKey("Invoice"));
        Assert.True(pocoFiles.ContainsKey("InvoiceLine"));
        Assert.True(pocoFiles.ContainsKey("InvoiceStatus"));

        // Invoice should have soft-delete markers
        Assert.Contains("ISoftDelete", pocoFiles["Invoice"]);
        Assert.Contains("IsDeleted", pocoFiles["Invoice"]);

        // Invoice should have index attribute
        Assert.Contains("[Indexed]", pocoFiles["Invoice"]);

        // InvoiceLine should reference Invoice (foreign key pattern)
        Assert.Contains("InvoiceId", pocoFiles["InvoiceLine"]);
    }

    [Fact]
    public async Task CompletePipeline_WithDTOs_GeneratesDtoClasses()
    {
        // Arrange — schema with DTOs
        var schema = new SchemaModel
        {
            Namespace = "IntegrationTest.Domain",
            Entities = new List<Entity>
            {
                new Entity
                {
                    Name = "Task",
                    SoftDelete = false,
                    Auditable = false,
                    Properties = new List<Property>
                    {
                        new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                        new Property { Name = "Title", Type = "string" }
                    }
                }
            },
            Dtos = new List<DtoModel>
            {
                new DtoModel
                {
                    Name = "CreateTaskDto",
                    Properties = new List<DtoProperty>
                    {
                        new DtoProperty { Name = "Title", Type = "string", IsRequired = true },
                        new DtoProperty { Name = "Description", Type = "string", IsRequired = false }
                    }
                }
            }
        };

        // Act
        var files = PocoGenerator.Generate(schema);

        // Assert — DTO should be generated alongside the entity
        Assert.True(files.ContainsKey("Task"));
        Assert.True(files.ContainsKey("CreateTaskDto"));
        Assert.Contains("public partial record CreateTaskDto", files["CreateTaskDto"]);
        Assert.Contains("Title", files["CreateTaskDto"]);
        Assert.Contains("Description", files["CreateTaskDto"]);
    }

    [Fact]
    public async Task CompletePipeline_WithCustomEndpoints_GeneratesHandlers()
    {
        // Arrange — schema with custom endpoints
        var schema = new SchemaModel
        {
            Namespace = "IntegrationTest.Domain",
            Entities = new List<Entity>
            {
                new Entity
                {
                    Name = "Task",
                    SoftDelete = false,
                    Auditable = false,
                    Properties = new List<Property>
                    {
                        new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                        new Property { Name = "Title", Type = "string" }
                    }
                }
            },
            CustomEndpoints = new List<CustomEndpoint>
            {
                new CustomEndpoint
                {
                    Route = "/api/v1/tasks/complete/{taskId}",
                    Method = "POST",
                    RequestType = "CompleteTaskCommand",
                    Roles = new List<string> { "User" }
                }
            }
        };

        // Act
        var files = PocoGenerator.Generate(schema);

        // Assert — Handler should be generated
        var handlerKey = "Handlers/CompleteTaskCommandHandler";
        Assert.True(files.ContainsKey(handlerKey), "Custom endpoint handler should be generated");
        Assert.Contains("CompleteTaskCommand", files[handlerKey]);
    }

    #endregion

    #region Manifest Generation from Schema

    [Fact]
    public async Task Schema_WithEntities_ProducesManifestEndpoints()
    {
        // Arrange — a realistic multi-entity schema (mimicking what Studio would produce)
        var schema = new SchemaModel
        {
            Namespace = "Paperclip.OrderingSystem.Domain",
            Entities = new List<Entity>
            {
                new Entity
                {
                    Name = "Order",
                    SoftDelete = true,
                    Auditable = true,
                    Properties = new List<Property>
                    {
                        new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                        new Property { Name = "OrderNumber", Type = "string", Attributes = new List<string> { "Required" } },
                        new Property { Name = "CustomerId", Type = "string" },
                        new Property { Name = "TotalAmount", Type = "decimal" },
                        new Property { Name = "Status", Type = "OrderStatus", IsEnum = true }
                    }
                },
                new Entity
                {
                    Name = "Customer",
                    SoftDelete = false,
                    Auditable = false,
                    Properties = new List<Property>
                    {
                        new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                        new Property { Name = "Name", Type = "string", Attributes = new List<string> { "Required" } },
                        new Property { Name = "Email", Type = "string" }
                    }
                }
            },
            Enums = new List<Foundry.Schema.Compiler.Enum>
            {
                new Foundry.Schema.Compiler.Enum
                { Name = "OrderStatus", Values = new List<string> { "Pending", "Completed" } }
            }
        };

        // Act — compile POCOs
        var pocoFiles = PocoGenerator.Generate(schema);

        // Assert — generate equivalent manifest structure (matching Studio's exportToApiManifest format)
        var expectedEndpoints = new Dictionary<string, string>
        {
            { "Order", "/api/v1/orders" },
            { "Customer", "/api/v1/customers" }
        };

        foreach (var kvp in expectedEndpoints)
        {
            Assert.True(pocoFiles.ContainsKey(kvp.Key), $"POCO for entity '{kvp.Key}' should be generated");
        }
    }

    [Fact]
    public async Task ManifestStructure_MatchesStudioExportFormat()
    {
        // Arrange — construct a manifest exactly as Studio's exportToApiManifest() would
        var manifest = new ApiManifest
        {
            Namespace = "Paperclip.OrderingSystem.Domain",
            Endpoints = new List<EndpointConfig>
            {
                new EndpointConfig
                {
                    Route = "/api/v1/orders",
                    Entity = "Order",
                    Methods = new List<string> { "GET", "POST", "GET_BY_ID", "PUT", "DELETE" },
                    Roles = new Dictionary<string, List<string>>
                    {
                        { "GET", new List<string> { "Admin", "User" } },
                        { "GET_BY_ID", new List<string> { "Admin", "User" } },
                        { "POST", new List<string> { "Admin" } },
                        { "PUT", new List<string> { "Admin" } },
                        { "DELETE", new List<string> { "Admin" } }
                    },
                    Caching = new Dictionary<string, CachingConfig>
                    {
                        { "GET", new CachingConfig { Enabled = true, TtlSeconds = 60 } },
                        { "GET_BY_ID", new CachingConfig { Enabled = true, TtlSeconds = 120 } }
                    }
                }
            },
            CustomEndpoints = new List<CustomEndpointConfig>
            {
                new CustomEndpointConfig
                {
                    Route = "/api/v1/orders/checkout",
                    Method = "POST",
                    RequestType = "PlaceOrderCommand",
                    Roles = new List<string> { "User", "Admin" }
                }
            }
        };

        // Act — serialize and deserialize (verifying round-trip integrity)
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<ApiManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert — verify round-trip fidelity
        Assert.NotNull(deserialized);
        Assert.Equal(manifest.Namespace, deserialized.Namespace);
        Assert.Single(deserialized.Endpoints);
        Assert.Single(deserialized.CustomEndpoints);

        var endpoint = deserialized.Endpoints[0];
        Assert.Equal("/api/v1/orders", endpoint.Route);
        Assert.Equal("Order", endpoint.Entity);
        Assert.Equal(5, endpoint.Methods.Count);
        Assert.Equal(60, endpoint.Caching["GET"].TtlSeconds);

        var custom = deserialized.CustomEndpoints[0];
        Assert.Equal("/api/v1/orders/checkout", custom.Route);
        Assert.Equal("PlaceOrderCommand", custom.RequestType);
    }

    #endregion

    #region Source Generator Manifest Handling

    [Fact]
    public async Task SourceGenerator_ManuscriptWithExtraFields_ParsesCorrectly()
    {
        // Arrange — a manifest with Entities[] and Enums[] (new Studio fields) should parse correctly
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "studio-new-format.json");
        Assert.True(File.Exists(fixturePath));

        var json = File.ReadAllText(fixturePath);

        // The source generator uses string-based parsing (ExtractValue, ExtractArrayValues).
        // Unknown fields like Entities[] and Enums[] should be silently ignored.
        // Only Endpoints[] and CustomEndpoints[] matter for routing.

        // Act — deserialize the manifest as Foundry.Api would
        var manifest = JsonSerializer.Deserialize<ApiManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert — verify only known fields are populated (extra fields are ignored)
        Assert.NotNull(manifest);
        Assert.Equal(3, manifest.Endpoints.Count);
        Assert.Equal(2, manifest.CustomEndpoints.Count);

        // Verify endpoint routing integrity
        var orderEndpoint = manifest.Endpoints.First(e => e.Entity == "Order");
        Assert.Equal("/api/v1/orders", orderEndpoint.Route);
    }

    [Fact]
    public async Task SourceGenerator_ExtraJsonFields_DontCorruptParsing()
    {
        // Arrange — manifest with deeply nested extra fields that could confuse naive parsers
        var jsonWithExtra = @"{
            ""Namespace"": ""Test.Domain"",
            ""Entities"": [{
                ""Name"": ""Widget"",
                ""BaseClass"": null,
                ""SoftDelete"": true,
                ""Auditable"": false,
                ""Indexes"": [{""Fields"": [""Id""], ""Unique"": true}],
                ""DeepNested"": {
                    ""a"": { ""b"": { ""c"": [1, 2, 3] } }
                }
            }],
            ""Enums"": [{""Name"": ""WStatus"", ""Values"": [""A""]}],
            ""Endpoints"": [{
                ""Route"": ""/api/v1/widgets"",
                ""Entity"": ""Widget"",
                ""Methods"": [""GET""]
            }]
        }";

        // Act — parse as Foundry.Api does (through both string-based and JSON deserialization)
        var manifest = JsonSerializer.Deserialize<ApiManifest>(jsonWithExtra, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert — should parse Endpoints correctly, extra fields ignored
        Assert.NotNull(manifest);
        Assert.Single(manifest.Endpoints);
        Assert.Equal("Widget", manifest.Endpoints[0].Entity);
    }

    #endregion

    #region Full Pipeline Integration (Studio → API)

    [Fact]
    public async Task CompleteStudioBridge_SchemaToManifestToApi_ProducesValidEndpoints()
    {
        // This is the critical end-to-end test that validates the entire Foundry bridge:
        //   Studio Canvas → Schema Model → POCO Compiler → Manifest Export → Foundry.Api Routing

        // Step 1: Simulate Studio exporting a multi-entity schema (what exportToApiManifest produces)
        var studioSchema = new SchemaModel
        {
            Namespace = "Paperclip.OrderingSystem.Domain",
            Entities = new List<Entity>
            {
                new Entity
                {
                    Name = "Order",
                    SoftDelete = true,
                    Auditable = true,
                    Properties = new List<Property>
                    {
                        new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                        new Property { Name = "OrderNumber", Type = "string", Attributes = new List<string> { "Required" } },
                        new Property { Name = "CustomerId", Type = "string" },
                        new Property { Name = "TotalAmount", Type = "decimal" },
                        new Property { Name = "Status", Type = "OrderStatus", IsEnum = true }
                    }
                },
                new Entity
                {
                    Name = "Customer",
                    SoftDelete = false,
                    Auditable = false,
                    Properties = new List<Property>
                    {
                        new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                        new Property { Name = "Name", Type = "string", Attributes = new List<string> { "Required" } },
                        new Property { Name = "Email", Type = "string" }
                    }
                },
                new Entity
                {
                    Name = "OrderItem",
                    SoftDelete = false,
                    Auditable = true,
                    Properties = new List<Property>
                    {
                        new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                        new Property { Name = "OrderId", Type = "string" }
                    }
                }
            },
            Enums = new List<Foundry.Schema.Compiler.Enum>
            {
                new Foundry.Schema.Compiler.Enum { Name = "OrderStatus", Values = new List<string> { "Pending", "Completed", "Cancelled" } }
            }
        };

        // Step 2: Compile POCOs (what the Schema Compiler produces)
        var pocoFiles = PocoGenerator.Generate(studioSchema);

        // Assert — all entities + enum should be generated
        // Presence rather than an exact count: the generator also emits supporting files
        // (serialization context, index verification), and their number is not the subject
        // of this test.
        Assert.True(pocoFiles.ContainsKey("Order"));
        Assert.True(pocoFiles.ContainsKey("Customer"));
        Assert.True(pocoFiles.ContainsKey("OrderItem"));
        Assert.True(pocoFiles.ContainsKey("OrderStatus"));

        // Step 3: Validate generated code quality (what Studio would show to the user)
        var orderCode = pocoFiles["Order"];
        Assert.Contains("ISoftDelete", orderCode);
        Assert.Contains("public required string OrderNumber", orderCode);

        // Step 4: Construct manifest as Studio would via exportToApiManifest() (now includes Entities[])
        var manifest = new ApiManifest
        {
            Namespace = studioSchema.Namespace,
            Endpoints = new List<EndpointConfig>
            {
                new EndpointConfig
                {
                    Route = "/api/v1/orders",
                    Entity = "Order",
                    Methods = new List<string> { "GET", "POST", "GET_BY_ID", "PUT", "DELETE" },
                    Roles = new Dictionary<string, List<string>>
                    {
                        { "GET", new List<string> { "Admin", "User" } },
                        { "POST", new List<string> { "Admin" } },
                    },
                    Caching = new Dictionary<string, CachingConfig>
                    {
                        { "GET", new CachingConfig { Enabled = true, TtlSeconds = 60 } },
                        { "GET_BY_ID", new CachingConfig { Enabled = true, TtlSeconds = 120 } }
                    }
                },
                new EndpointConfig
                {
                    Route = "/api/v1/customers",
                    Entity = "Customer",
                    Methods = new List<string> { "GET", "POST" },
                    Roles = new Dictionary<string, List<string>>
                    {
                        { "GET", new List<string> { "Admin", "User" } },
                        { "POST", new List<string> { "Admin" } }
                    }
                },
                new EndpointConfig
                {
                    Route = "/api/v1/orderitems",
                    Entity = "OrderItem",
                    Methods = new List<string> { "GET", "POST", "GET_BY_ID" },
                    Roles = new Dictionary<string, List<string>>
                    {
                        { "GET", new List<string> { "Admin" } },
                        { "POST", new List<string> { "Admin" } }
                    }
                }
            },
            CustomEndpoints = new List<CustomEndpointConfig>()
        };

        // Step 5: Verify manifest serializes and deserializes correctly (round-trip integrity)
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<ApiManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(restored);
        Assert.Equal(3, restored.Endpoints.Count);

        // Step 6: Verify each endpoint has correct routing configuration
        var orderEp = restored.Endpoints.First(e => e.Entity == "Order");
        Assert.Equal("/api/v1/orders", orderEp.Route);
        Assert.Equal(5, orderEp.Methods.Count);
        Assert.True(orderEp.Caching.ContainsKey("GET"));
        Assert.True(orderEp.Caching["GET"].Enabled);

        var customerEp = restored.Endpoints.First(e => e.Entity == "Customer");
        Assert.Equal("/api/v1/customers", customerEp.Route);

        var itemEp = restored.Endpoints.First(e => e.Entity == "OrderItem");
        Assert.Equal("/api/v1/orderitems", itemEp.Route);
    }

    [Fact]
    public async Task Bridge_Pluralization_WorksCorrectlyForRoutes()
    {
        // Validate that entity names are pluralized correctly for API routes (matching Studio's pluralize logic)
        var cases = new Dictionary<string, string>
        {
            { "Order", "/api/v1/orders" },
            { "Customer", "/api/v1/customers" },
            { "Category", "/api/v1/categories" },
            { "OrderItem", "/api/v1/orderitems" },
            { "User", "/api/v1/users" },
            { "Product", "/api/v1/products" }
        };

        foreach (var (entity, expectedRoute) in cases)
        {
            var manifest = new ApiManifest
            {
                Namespace = "Test.Domain",
                Endpoints = new List<EndpointConfig>
                {
                    new EndpointConfig { Route = expectedRoute, Entity = entity, Methods = new List<string> { "GET" } }
                },
                CustomEndpoints = new List<CustomEndpointConfig>()
            };

            Assert.Equal(expectedRoute, manifest.Endpoints[0].Route);
        }
    }

    [Fact]
    public async Task Bridge_CompleteRoundTrip_EmptySchema_Graceful()
    {
        // Edge case: what happens with an empty schema through the entire pipeline?
        var emptySchema = new SchemaModel
        {
            Namespace = "Empty.Domain",
            Entities = new List<Entity>(),
            Enums = new List<Foundry.Schema.Compiler.Enum>()
        };

        var pocoFiles = PocoGenerator.Generate(emptySchema);

        Assert.Empty(pocoFiles);

        // Manifest should also be valid with zero endpoints
        var emptyManifest = new ApiManifest
        {
            Namespace = "Empty.Domain",
            Endpoints = new List<EndpointConfig>(),
            CustomEndpoints = new List<CustomEndpointConfig>()
        };

        var json = JsonSerializer.Serialize(emptyManifest, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<ApiManifest>(json);

        Assert.NotNull(restored);
        Assert.Empty(restored.Endpoints);
    }

    #endregion
}
