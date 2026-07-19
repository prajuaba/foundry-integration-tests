using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NSubstitute;
using Xunit;
using Foundry.Core.Attributes;
using Foundry.Core.Audit;
using Foundry.Core.Entities;
using Foundry.Core.User;
using FoundryMongo.Repositories;
using FoundryMongo.UnitOfWork;
using Foundry.Api.MediatR;
using Foundry.Api.MediatR.Behaviors;
using Foundry.Rules;
using MediatR;

namespace Foundry.IntegrationTests;

public class ComplianceAndAuditingTests
{
    [ReadAudited]
    public record AuditedEntity : IEntity<ObjectId>
    {
        public ObjectId Id { get; init; } = ObjectId.GenerateNewId();
        public string Name { get; init; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public int Version { get; set; }
    }

    public record NonAuditedEntity : IEntity<ObjectId>
    {
        public ObjectId Id { get; init; } = ObjectId.GenerateNewId();
        public string Name { get; init; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public int Version { get; set; }
    }

    [Fact]
    public async Task ReadAuditing_LogsToAuditSink_WhenEntityIsMarkedReadAudited()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<AuditedEntity>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace("testdb", "AuditedEntities"));
        mockDb.GetCollection<AuditedEntity>(Arg.Any<string>(), Arg.Any<MongoCollectionSettings>()).Returns(mockCollection);

        var mockCursor = Substitute.For<IAsyncCursor<AuditedEntity>>();
        var entity = new AuditedEntity { Name = "Sensitive Record" };
        mockCursor.MoveNextAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true), Task.FromResult(false));
        mockCursor.Current.Returns(new[] { entity });
        mockCursor.MoveNext(Arg.Any<CancellationToken>()).Returns(true, false);
        mockCursor.Current.Returns(new[] { entity });

        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<AuditedEntity>>(), 
            Arg.Any<FindOptions<AuditedEntity, AuditedEntity>>(), 
            Arg.Any<CancellationToken>())
            .Returns(mockCursor);

        var mockAuditSink = Substitute.For<IAuditSink>();
        var mockUserContext = Substitute.For<ICurrentUserContext>();
        mockUserContext.OperatorId.Returns("user-99");

        var repo = new Repository<AuditedEntity>(mockDb, mockAuditSink, mockUserContext);

        // Act
        var result = await repo.GetByIdAsync(entity.Id);

        // Assert
        Assert.NotNull(result);
        await mockAuditSink.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.OperatorId == "user-99" && e.Action == AuditAction.Read && e.EntityId == entity.Id.ToString()),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task ReadAuditing_DoesNotLogToAuditSink_WhenEntityIsNotMarkedReadAudited()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<NonAuditedEntity>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace("testdb", "NonAuditedEntities"));
        mockDb.GetCollection<NonAuditedEntity>(Arg.Any<string>(), Arg.Any<MongoCollectionSettings>()).Returns(mockCollection);

        var mockCursor = Substitute.For<IAsyncCursor<NonAuditedEntity>>();
        var entity = new NonAuditedEntity { Name = "Normal Record" };
        mockCursor.MoveNextAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true), Task.FromResult(false));
        mockCursor.Current.Returns(new[] { entity });
        mockCursor.MoveNext(Arg.Any<CancellationToken>()).Returns(true, false);
        mockCursor.Current.Returns(new[] { entity });

        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<NonAuditedEntity>>(), 
            Arg.Any<FindOptions<NonAuditedEntity, NonAuditedEntity>>(), 
            Arg.Any<CancellationToken>())
            .Returns(mockCursor);

        var mockAuditSink = Substitute.For<IAuditSink>();
        var mockUserContext = Substitute.For<ICurrentUserContext>();

        var repo = new Repository<NonAuditedEntity>(mockDb, mockAuditSink, mockUserContext);

        // Act
        var result = await repo.GetByIdAsync(entity.Id);

        // Assert
        Assert.NotNull(result);
        await mockAuditSink.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void MaskSensitiveFields_BypassesMasking_WhenUserHasViewPiiClaim()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockAuditSink = Substitute.For<IAuditSink>();
        var mockUserContext = Substitute.For<ICurrentUserContext>();

        var claims = new List<Claim> { new Claim("scope", "view:pii") };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        mockUserContext.User.Returns(principal);

        var repo = new Repository<AuditedEntity>(mockDb, mockAuditSink, mockUserContext);
        var entity = new AuditedEntity { Name = "Super Secret Name" };

        // Act
        var masked = repo.MaskSensitiveFields(entity);

        // Assert
        // Since the user has "view:pii" scope, we expect the original entity back without masking/encryption modifications
        Assert.Equal("Super Secret Name", masked.Name);
    }

    [Fact]
    public async Task UnitOfWork_CoordinatesSessionAndTransaction()
    {
        // Arrange
        var mockClient = Substitute.For<IMongoClient>();
        var mockSession = Substitute.For<IClientSessionHandle>();
        mockClient.StartSession(Arg.Any<ClientSessionOptions>(), Arg.Any<CancellationToken>()).Returns(mockSession);

        // Act
        using (var uow = new UnitOfWork(mockClient))
        {
            Assert.Same(mockSession, uow.Session);
            await uow.CommitAsync();
        }

        // Assert
        mockSession.Received(1).StartTransaction(Arg.Any<TransactionOptions>());
        await mockSession.Received(1).CommitTransactionAsync(Arg.Any<CancellationToken>());
    }

    public record TestCommand : IRequest<string>;

    [Fact]
    public async Task BusinessRuleBehavior_ThrowsValidationException_WhenRuleFails()
    {
        // Arrange
        var services = new ServiceCollection();
        var mockRule = Substitute.For<IBusinessRule<TestCommand>>();
        mockRule.ValidateAsync(Arg.Any<TestCommand>(), Arg.Any<CancellationToken>())
            .Returns(RuleResult.Failure("Invalid order status change."));

        services.AddSingleton(mockRule);
        services.AddFoundryRules();
        var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IBusinessRuleEngine>();

        var behavior = new BusinessRuleBehavior<TestCommand, string>(engine);
        var request = new TestCommand();
        
        // Act & Assert
        var ex = await Assert.ThrowsAsync<FluentValidation.ValidationException>(async () =>
        {
            await behavior.Handle(request, () => Task.FromResult("Success"), CancellationToken.None);
        });

        Assert.Contains("Invalid order status change.", ex.Errors.First().ErrorMessage);
    }

    [Fact]
    public async Task BusinessRuleBehavior_ContinuesToNext_WhenAllRulesPass()
    {
        // Arrange
        var services = new ServiceCollection();
        var mockRule = Substitute.For<IBusinessRule<TestCommand>>();
        mockRule.ValidateAsync(Arg.Any<TestCommand>(), Arg.Any<CancellationToken>())
            .Returns(RuleResult.Success());

        services.AddSingleton(mockRule);
        services.AddFoundryRules();
        var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IBusinessRuleEngine>();

        var behavior = new BusinessRuleBehavior<TestCommand, string>(engine);
        var request = new TestCommand();

        // Act
        var result = await behavior.Handle(request, () => Task.FromResult("HandlerResponse"), CancellationToken.None);

        // Assert
        Assert.Equal("HandlerResponse", result);
    }
}
