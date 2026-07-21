using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using MediatR;
using Xunit;
using Foundry.Api.MediatR.Behaviors;
using Foundry.Api.Middleware;
using Foundry.Api.MediatR;
using Paperclip.OrderingSystem.Domain;

namespace Foundry.IntegrationTests;

/// <summary>
/// Verifies that the IdempotencyBehavior blocks duplicate requests using headers and allows retries after transient failures.
/// </summary>
public class IdempotencyTests
{
    [Fact]
    public async Task IdempotencyBehavior_AllowsFirstCall_And_BlocksDuplicateCall()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMemoryCache();
        
        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);
        
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<IdempotencyBehavior<InsertCommand<Order>, Order>>.Instance;

        var behavior = new IdempotencyBehavior<InsertCommand<Order>, Order>(cache, httpContextAccessor, logger);

        var order = new Order { Id = MongoDB.Bson.ObjectId.GenerateNewId(), OrderNumber = "ORD-1" };
        var command = new InsertCommand<Order>(order);

        // 1. Without header: should bypass behavior
        int executionCount = 0;
        RequestHandlerDelegate<Order> nextDelegate = () =>
        {
            executionCount++;
            return Task.FromResult(order);
        };

        var result1 = await behavior.Handle(command, nextDelegate, CancellationToken.None);
        Assert.Equal(order, result1);
        Assert.Equal(1, executionCount);

        // 2. With header, first attempt: should succeed and mark key as completed
        httpContext.Request.Headers["X-Idempotency-Key"] = "test-key-123";
        
        var result2 = await behavior.Handle(command, nextDelegate, CancellationToken.None);
        Assert.Equal(order, result2);
        Assert.Equal(2, executionCount);

        // 3. With header, duplicate attempt: should throw IdempotencyException
        var ex = await Assert.ThrowsAsync<IdempotencyException>(() =>
            behavior.Handle(command, nextDelegate, CancellationToken.None)
        );
        Assert.Equal("test-key-123", ex.IdempotencyKey);
        Assert.Equal(2, executionCount); // Handler must not run again!
    }

    [Fact]
    public async Task IdempotencyBehavior_RemovesKeyOnFailure_ToAllowRetry()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<IdempotencyBehavior<InsertCommand<Order>, Order>>.Instance;

        var behavior = new IdempotencyBehavior<InsertCommand<Order>, Order>(cache, httpContextAccessor, logger);

        var order = new Order { Id = MongoDB.Bson.ObjectId.GenerateNewId(), OrderNumber = "ORD-2" };
        var command = new InsertCommand<Order>(order);

        httpContext.Request.Headers["X-Idempotency-Key"] = "test-key-fail";

        int attempts = 0;
        RequestHandlerDelegate<Order> failingDelegate = () =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("Simulated transient database exception");
            }
            return Task.FromResult(order);
        };

        // Act & Assert: First attempt fails
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            behavior.Handle(command, failingDelegate, CancellationToken.None)
        );
        Assert.Equal(1, attempts);

        // Second attempt with the SAME key should work because the key was evicted on failure!
        var result = await behavior.Handle(command, failingDelegate, CancellationToken.None);
        Assert.Equal(order, result);
        Assert.Equal(2, attempts);
    }
}
