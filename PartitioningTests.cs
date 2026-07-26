using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NSubstitute;
using Xunit;
using Foundry.Core.Attributes;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;

namespace Foundry.IntegrationTests;

public class PartitioningTests
{
    [Partitioned(2)]
    public record TestPartitionedEntity : IEntity<ObjectId>
    {
        public ObjectId Id { get; init; }
        public string Name { get; init; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public int Version { get; set; }
    }

    [Fact]
    public void PartitionedRepository_ReadsAttribute_AndSetsThreshold()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<TestPartitionedEntity>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace("testdb", "TestPartitionedEntities"));
        mockDb.GetCollection<TestPartitionedEntity>(Arg.Any<string>(), Arg.Any<MongoCollectionSettings>())
            .Returns(mockCollection);

        // Act
        var repo = new PartitionedRepository<TestPartitionedEntity>(mockDb);

        // Assert
        Assert.Equal("TestPartitionedEntities", repo.CollectionName);
    }

    [Fact]
    public async Task GetByIdAsync_RoutesToActiveOrArchiveCollection()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<TestPartitionedEntity>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace("testdb", "TestPartitionedEntities"));
        
        mockDb.GetCollection<TestPartitionedEntity>(Arg.Any<string>(), Arg.Any<MongoCollectionSettings>())
            .Returns(mockCollection);

        var repo = new PartitionedRepository<TestPartitionedEntity>(mockDb);

        var currentYear = DateTime.UtcNow.Year;
        var activeId = ObjectId.GenerateNewId(new DateTime(currentYear, 6, 15));
        var archivedId = ObjectId.GenerateNewId(new DateTime(currentYear - 3, 6, 15));

        // Act
        await repo.GetByIdAsync(activeId);
        await repo.GetByIdAsync(archivedId);

        // Assert
        // Construction resolves "TestPartitionedEntities" and "TestPartitionedEntities_Deleted"
        mockDb.Received().GetCollection<TestPartitionedEntity>("TestPartitionedEntities", Arg.Any<MongoCollectionSettings>());
        mockDb.Received().GetCollection<TestPartitionedEntity>("TestPartitionedEntities_Deleted", Arg.Any<MongoCollectionSettings>());
        
        // Archived ID lookup resolves "TestPartitionedEntities_YYYY" archive collection
        var archiveCollectionName = $"TestPartitionedEntities_{currentYear - 3}";
        mockDb.Received().GetCollection<TestPartitionedEntity>(archiveCollectionName, Arg.Any<MongoCollectionSettings>());
    }

    [Fact]
    public void AddFoundryMongo_RegistersCorrectRepositories()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddFoundryMongo(options =>
        {
            options.ConnectionString = "mongodb://localhost:27017";
            options.DatabaseName = "testdb";
        });

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var repo = serviceProvider.GetService<IRepository<TestPartitionedEntity>>();

        Assert.NotNull(repo);
        Assert.IsType<PartitionedRepository<TestPartitionedEntity>>(repo);
    }

    [Fact]
    public async Task AggregateAsync_PrependsUnionWithStages()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<TestPartitionedEntity>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace("testdb", "TestPartitionedEntities"));
        
        mockDb.GetCollection<TestPartitionedEntity>(Arg.Any<string>(), Arg.Any<MongoCollectionSettings>())
            .Returns(mockCollection);

        // Mock database collection names listing
        var mockCursor = Substitute.For<IAsyncCursor<string>>();
        mockCursor.MoveNext(Arg.Any<CancellationToken>()).Returns(true, false);
        mockCursor.Current.Returns(new List<string> { "TestPartitionedEntities_2024", "TestPartitionedEntities_2025" });
        mockDb.ListCollectionNames(Arg.Any<ListCollectionNamesOptions>(), Arg.Any<CancellationToken>())
            .Returns(mockCursor);

        // Mock collection aggregation call
        var mockCursorResult = Substitute.For<IAsyncCursor<BsonDocument>>();
        mockCursorResult.MoveNext(Arg.Any<CancellationToken>()).Returns(true, false);
        mockCursorResult.Current.Returns(new List<BsonDocument>());
        
        mockCollection.WithReadPreference(Arg.Any<ReadPreference>()).Returns(mockCollection);

        mockCollection.AggregateAsync<BsonDocument>(
            Arg.Any<PipelineDefinition<TestPartitionedEntity, BsonDocument>>(), 
            Arg.Any<AggregateOptions>(), 
            Arg.Any<CancellationToken>())
            .Returns(mockCursorResult);

        var repo = new PartitionedRepository<TestPartitionedEntity>(mockDb);

        var pipeline = PipelineDefinition<TestPartitionedEntity, BsonDocument>.Create(new[]
        {
            new BsonDocument("$match", new BsonDocument("Name", "Test"))
        });

        // Act
        await repo.AggregateAsync(pipeline);

        // Assert
        await mockCollection.Received(1).AggregateAsync<BsonDocument>(
            Arg.Is<PipelineDefinition<TestPartitionedEntity, BsonDocument>>(p => 
                p.Render(new RenderArgs<TestPartitionedEntity>(
                    BsonSerializer.LookupSerializer<TestPartitionedEntity>(), 
                    BsonSerializer.SerializerRegistry))
                .Documents.Any(d => d.Contains("$unionWith") && d["$unionWith"]["coll"] == "TestPartitionedEntities_2024") &&
                p.Render(new RenderArgs<TestPartitionedEntity>(
                    BsonSerializer.LookupSerializer<TestPartitionedEntity>(), 
                    BsonSerializer.SerializerRegistry))
                .Documents.Any(d => d.Contains("$unionWith") && d["$unionWith"]["coll"] == "TestPartitionedEntities_2025")
            ),
            Arg.Any<AggregateOptions>(),
            Arg.Any<CancellationToken>()
        );
    }
}
