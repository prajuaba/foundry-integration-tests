using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Audit;
using Foundry.Core.Attributes;
using Foundry.RealTime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Foundry.IntegrationTests;

/// <summary>
/// Verifies the integration of the Foundry.RealTime module, including the 
/// DI decorator architecture, mutation broker routing, and per-entity attribute filters.
/// </summary>
public class RealTimeIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    static RealTimeIntegrationTests()
    {
        Environment.SetEnvironmentVariable("MONGODB_ENCRYPTION_KEY", "12345678901234567890123456789012");
    }

    public RealTimeIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void WebHost_WhenConfigured_RegistersRealTimeDecoratedAuditSink()
    {
        // Arrange & Act
        using var scope = _factory.Services.CreateScope();
        var auditSink = scope.ServiceProvider.GetService<IAuditSink>();
        var broker = scope.ServiceProvider.GetService<IRealTimeNotificationBroker>();

        // Assert
        Assert.NotNull(broker);
        Assert.NotNull(auditSink);
        Assert.Contains("RealTimeAuditSink", auditSink.GetType().Name);
    }

    [Fact]
    public async Task RealTimeAuditSink_WhenAuditWritten_CallsInnerSinkAndPropagatesToChannels()
    {
        // Arrange
        var services = new ServiceCollection();
        
        // Mock channel
        var mockChannel = Substitute.For<INotificationService>();
        mockChannel.ChannelName.Returns("TestChannel");
        
        services.AddSingleton(mockChannel);
        services.AddLogging();
        
        // Mock inner audit sink
        var mockInnerSink = Substitute.For<IAuditSink>();
        services.AddSingleton<IAuditSink>(mockInnerSink);

        // Register RealTime services (automatically decorates IAuditSink)
        services.AddFoundryRealTime();
        
        var serviceProvider = services.BuildServiceProvider();
        var decoratedSink = serviceProvider.GetRequiredService<IAuditSink>();
        
        // Build entry using factory method
        var entry = AuditLogEntry.ForInsert(
            operatorId: "admin",
            entityType: "IntegrationTest.Domain.Product",
            entityId: "prod-123",
            collectionName: "Products"
        );

        // Act
        await decoratedSink.WriteAsync(entry, CancellationToken.None);

        // Assert
        // 1. Verify inner audit sink was written to
        await mockInnerSink.Received(1).WriteAsync(entry, Arg.Any<CancellationToken>());
        
        // 2. Verify broker routed message to the mock notification channel
        await mockChannel.Received(1).SendMutationAsync(entry, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RealTimeAuditSink_WhenEntityHasRealTimeDisabled_DoesNotBroadcast()
    {
        // Arrange
        var services = new ServiceCollection();
        var mockChannel = Substitute.For<INotificationService>();
        mockChannel.ChannelName.Returns("TestChannel");
        
        services.AddSingleton(mockChannel);
        services.AddLogging();
        
        var mockInnerSink = Substitute.For<IAuditSink>();
        services.AddSingleton<IAuditSink>(mockInnerSink);
        
        services.AddFoundryRealTime();
        
        var serviceProvider = services.BuildServiceProvider();
        var decoratedSink = serviceProvider.GetRequiredService<IAuditSink>();

        // Type with [RealTime(false)]
        var disabledEntry = AuditLogEntry.ForUpdate(
            operatorId: "admin",
            entityType: typeof(DisabledRealTimeDummyEntity).AssemblyQualifiedName!,
            entityId: "dummy-id",
            collectionName: "Dummies",
            diffs: new List<PropertyDiff>()
        );

        // Act
        await decoratedSink.WriteAsync(disabledEntry, CancellationToken.None);

        // Assert
        // Inner sink should still be called
        await mockInnerSink.Received(1).WriteAsync(disabledEntry, Arg.Any<CancellationToken>());
        
        // But notification service should NOT receive the broadcast
        await mockChannel.DidNotReceive().SendMutationAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }
}

[RealTime(false)]
public class DisabledRealTimeDummyEntity
{
    public string Id { get; set; } = string.Empty;
}
