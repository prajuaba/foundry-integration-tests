using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Xunit;
using Foundry.Core.Outbox;
using Foundry.Core.Entities;
using Foundry.Mongo.Services;
using Foundry.Mongo.Repositories;
using Foundry.Api.MediatR;
using Foundry.Api.MediatR.Behaviors;
using Paperclip.OrderingSystem.Domain;
using MediatR;

namespace Foundry.IntegrationTests;

public class OutboxTests
{
    public class SampleDomainEvent
    {
        public string EventId { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    [Fact]
    public async Task OutboxQueue_InsertsMessage_Successfully()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<OutboxMessage>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "OutboxMessages"));
        mockCollection.Database.Returns(mockDb);
        mockDb.GetCollection<OutboxMessage>(Arg.Any<string>()).Returns(mockCollection);

        var repository = new Repository<OutboxMessage>(mockDb, null, null, null);
        var queue = new MongoOutboxQueue(repository);

        OutboxMessage? capturedMessage = null;
        await mockCollection.InsertOneAsync(
            Arg.Do<OutboxMessage>(m => capturedMessage = m),
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>()
        );

        var sampleEvent = new SampleDomainEvent { EventId = "evt-1", Content = "Order Created" };

        // Act
        await queue.EnqueueAsync(sampleEvent, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedMessage);
        Assert.Contains("SampleDomainEvent", capturedMessage.EventType);
        Assert.Contains("evt-1", capturedMessage.Payload);
        Assert.Null(capturedMessage.ProcessedAt);
        Assert.Equal(0, capturedMessage.RetryCount);
    }

    [Fact]
    public async Task OutboxPublisherWorker_DispatchesAndMarksAsProcessed()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<OutboxMessage>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "OutboxMessages"));
        mockCollection.Database.Returns(mockDb);
        mockDb.GetCollection<OutboxMessage>(Arg.Any<string>()).Returns(mockCollection);

        // Set up mock database to return our outbox message
        var outboxMessage = new OutboxMessage
        {
            Id = ObjectId.GenerateNewId(),
            EventType = typeof(SampleDomainEvent).AssemblyQualifiedName ?? nameof(SampleDomainEvent),
            Payload = "{\"EventId\":\"evt-2\",\"Content\":\"Dispatched\"}",
            CreatedAt = DateTime.UtcNow,
            RetryCount = 0
        };

        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<OutboxMessage>>(),
            Arg.Any<FindOptions<OutboxMessage, OutboxMessage>>(),
            Arg.Any<CancellationToken>()
        ).Returns(x => 
        {
            var freshCursor = Substitute.For<IAsyncCursor<OutboxMessage>>();
            freshCursor.Current.Returns(new List<OutboxMessage> { outboxMessage });
            int internalCount = 0;
            freshCursor.MoveNextAsync(Arg.Any<CancellationToken>()).Returns(_ => 
            {
                internalCount++;
                return Task.FromResult(internalCount == 1);
            });
            return Task.FromResult(freshCursor);
        });

        var repository = new Repository<OutboxMessage>(mockDb, null, null, null);
        var dispatcher = Substitute.For<IOutboxDispatcher>();

        var services = new ServiceCollection();
        services.AddSingleton<IRepository<OutboxMessage>>(repository);
        services.AddSingleton<IOutboxDispatcher>(dispatcher);
        var serviceProvider = services.BuildServiceProvider();

        var worker = new OutboxPublisherWorker(serviceProvider, NullLogger<OutboxPublisherWorker>.Instance);

        // Capture update call to verify the message is updated to ProcessedAt
        OutboxMessage? updatedMessage = null;
        var replaceResult = Substitute.For<ReplaceOneResult>();
        replaceResult.MatchedCount.Returns(1);
        replaceResult.ModifiedCount.Returns(1);

        mockCollection.ReplaceOneAsync(
            Arg.Any<FilterDefinition<OutboxMessage>>(),
            Arg.Any<OutboxMessage>(),
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>()
        ).Returns(x => 
        {
            updatedMessage = x.ArgAt<OutboxMessage>(1);
            return Task.FromResult(replaceResult);
        });

        // Act
        // Invoke protected ProcessOutboxMessagesAsync via reflection to run a single sweep
        var method = typeof(OutboxPublisherWorker).GetMethod("ProcessOutboxMessagesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        
        var task = (Task)method.Invoke(worker, new object[] { CancellationToken.None })!;
        await task;

        // Assert
        await dispatcher.Received(1).DispatchAsync(
            outboxMessage.EventType,
            outboxMessage.Payload,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>()
        );

        Assert.NotNull(updatedMessage);
        Assert.NotNull(updatedMessage.ProcessedAt);
        Assert.Equal(0, updatedMessage.RetryCount);
    }

    [Fact]
    public async Task OutboxDomainEventBehavior_EnqueuesMutationEvent_OnSuccess()
    {
        // Arrange
        var mockOutboxQueue = Substitute.For<IOutboxQueue>();
        var behavior = new OutboxDomainEventBehavior<InsertCommand<Order>, Order>(mockOutboxQueue);

        var order = new Order { Id = MongoDB.Bson.ObjectId.GenerateNewId(), OrderNumber = "ORD-OUTBOX-1" };
        var command = new InsertCommand<Order>(order);

        RequestHandlerDelegate<Order> nextDelegate = () => Task.FromResult(order);

        // Act
        var result = await behavior.Handle(command, nextDelegate, CancellationToken.None);

        // Assert
        Assert.Equal(order, result);
        await mockOutboxQueue.Received(1).EnqueueAsync(
            Arg.Is<EntityMutationEvent<Order>>(e => 
                e.MutationType == "Insert" && 
                e.Entity == order
            ),
            Arg.Any<CancellationToken>()
        );
    }
}
