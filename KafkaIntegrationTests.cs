using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Foundry.Core.User;
using Foundry.Kafka.Bridge;
using Foundry.Kafka.Configuration;
using Foundry.Kafka.Consumer;
using Foundry.Kafka.Producer;
using Foundry.Mongo.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Paperclip.OrderingSystem.Domain;
using Xunit;

namespace Foundry.IntegrationTests;

public class KafkaIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    static KafkaIntegrationTests()
    {
        Environment.SetEnvironmentVariable("MONGODB_ENCRYPTION_KEY", "12345678901234567890123456789012");
    }

    public KafkaIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task KafkaProducer_InitializesWithOptionsAndCallsUnderlyingProducer()
    {
        // Arrange
        var mockProducer = Substitute.For<IProducer<string, string>>();
        var kafkaProducer = new KafkaProducer(mockProducer);

        var deliveryResult = new DeliveryResult<string, string>
        {
            Topic = "test-topic",
            Partition = 0,
            Offset = 42,
            Status = PersistenceStatus.Persisted
        };

        mockProducer.ProduceAsync(
            Arg.Any<string>(),
            Arg.Any<Message<string, string>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(deliveryResult));

        // Act
        var result = await kafkaProducer.ProduceAsync("test-topic", "test-key", "test-value");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test-topic", result.Topic);
        Assert.Equal(42, result.Offset.Value);

        await mockProducer.Received(1).ProduceAsync(
            "test-topic",
            Arg.Is<Message<string, string>>(m => m.Key == "test-key" && m.Value == "test-value"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KafkaToApiBridgeHandler_ForwardsMessagesToMappedApiUrl()
    {
        // Arrange
        var topic = "orders";
        var targetUrl = "https://api.example.com/orders";
        var payload = "{\"id\":123}";

        var options = new KafkaOptions();
        options.ConsumerOptions.TopicApiMappings.Add(topic, targetUrl);
        var mockOptions = Options.Create(options);

        var mockLogger = Substitute.For<ILogger<KafkaToApiBridgeHandler>>();
        var mockProducer = Substitute.For<IKafkaProducer>();

        // Setup mock HttpMessageHandler to capture the request and return 200 OK
        var handlerMock = new MockHttpMessageHandler(HttpStatusCode.OK, "SUCCESS");
        var httpClient = new HttpClient(handlerMock);

        var mockFactory = Substitute.For<IHttpClientFactory>();
        mockFactory.CreateClient("KafkaBridge").Returns(httpClient);

        var bridgeHandler = new KafkaToApiBridgeHandler(mockFactory, mockProducer, mockOptions, mockLogger);

        var headers = new Dictionary<string, string>();

        // Act
        await bridgeHandler.HandleAsync(topic, "key-123", payload, headers, CancellationToken.None);

        // Assert
        Assert.NotNull(handlerMock.CapturedRequest);
        Assert.Equal(HttpMethod.Post, handlerMock.CapturedRequest.Method);
        Assert.Equal(targetUrl, handlerMock.CapturedRequest.RequestUri?.ToString());
        Assert.Equal(payload, handlerMock.CapturedBody);
        
        // Assert DLQ was NOT called
        await mockProducer.DidNotReceiveWithAnyArgs().ProduceAsync(default!, default!, default!, default!, default!);
    }

    [Fact]
    public async Task KafkaToApiBridgeHandler_MapsHeadersStartingWithXKafkaToHttpRequestHeaders()
    {
        // Arrange
        var topic = "orders";
        var targetUrl = "https://api.example.com/orders";

        var options = new KafkaOptions();
        options.ConsumerOptions.TopicApiMappings.Add(topic, targetUrl);
        var mockOptions = Options.Create(options);

        var mockLogger = Substitute.For<ILogger<KafkaToApiBridgeHandler>>();
        var mockProducer = Substitute.For<IKafkaProducer>();
        var handlerMock = new MockHttpMessageHandler(HttpStatusCode.OK, "SUCCESS");
        var httpClient = new HttpClient(handlerMock);

        var mockFactory = Substitute.For<IHttpClientFactory>();
        mockFactory.CreateClient("KafkaBridge").Returns(httpClient);

        var bridgeHandler = new KafkaToApiBridgeHandler(mockFactory, mockProducer, mockOptions, mockLogger);

        var headers = new Dictionary<string, string>
        {
            { "X-Kafka-TraceId", "abc-123" },
            { "X-Kafka-Source", "test-runner" },
            { "Content-Type", "application/json" } // Standard content header
        };

        // Act
        await bridgeHandler.HandleAsync(topic, "key-123", "{}", headers, CancellationToken.None);

        // Assert
        Assert.NotNull(handlerMock.CapturedRequest);
        Assert.True(handlerMock.CapturedRequest.Headers.Contains("X-Kafka-TraceId"));
        Assert.True(handlerMock.CapturedRequest.Headers.Contains("X-Kafka-Source"));
        
        // Assert values
        var traceIdValues = handlerMock.CapturedRequest.Headers.GetValues("X-Kafka-TraceId");
        Assert.Contains("abc-123", traceIdValues);
    }

    [Fact]
    public async Task KafkaToApiBridgeHandler_RoutesToDlqOnHttpFailure()
    {
        // Arrange
        var topic = "orders";
        var targetUrl = "https://api.example.com/orders";

        var options = new KafkaOptions();
        options.ConsumerOptions.TopicApiMappings.Add(topic, targetUrl);
        var mockOptions = Options.Create(options);

        var mockLogger = Substitute.For<ILogger<KafkaToApiBridgeHandler>>();
        var mockProducer = Substitute.For<IKafkaProducer>();
        
        // Return 500 to trigger retries and eventual fallback
        var handlerMock = new MockHttpMessageHandler(HttpStatusCode.InternalServerError, "FAIL");
        var httpClient = new HttpClient(handlerMock);

        var mockFactory = Substitute.For<IHttpClientFactory>();
        mockFactory.CreateClient("KafkaBridge").Returns(httpClient);

        var bridgeHandler = new KafkaToApiBridgeHandler(mockFactory, mockProducer, mockOptions, mockLogger);

        // Act
        await bridgeHandler.HandleAsync(topic, "key-123", "{}", new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        // HTTP API should have been called 3 times (max retries = 3)
        Assert.Equal(3, handlerMock.RequestCount);

        // Should produce to DLQ topic
        await mockProducer.Received(1).ProduceAsync("orders-dlq", "key-123", "{}", Arg.Any<Confluent.Kafka.Headers>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KafkaToApiBridgeHandler_ThrowsException_IfDlqPublishingFails()
    {
        // Arrange
        var topic = "orders";
        var targetUrl = "https://api.example.com/orders";

        var options = new KafkaOptions();
        options.ConsumerOptions.TopicApiMappings.Add(topic, targetUrl);
        var mockOptions = Options.Create(options);

        var mockLogger = Substitute.For<ILogger<KafkaToApiBridgeHandler>>();
        var mockProducer = Substitute.For<IKafkaProducer>();
        
        // Mock DLQ producer to fail
        mockProducer.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Confluent.Kafka.Headers>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("Kafka write failure"));

        var handlerMock = new MockHttpMessageHandler(HttpStatusCode.InternalServerError, "FAIL");
        var httpClient = new HttpClient(handlerMock);

        var mockFactory = Substitute.For<IHttpClientFactory>();
        mockFactory.CreateClient("KafkaBridge").Returns(httpClient);

        var bridgeHandler = new KafkaToApiBridgeHandler(mockFactory, mockProducer, mockOptions, mockLogger);

        // Act & Assert
        // Since DLQ fails, it must throw so the consumer does not commit the offset
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await bridgeHandler.HandleAsync(topic, "key-123", "{}", new Dictionary<string, string>(), CancellationToken.None);
        });
    }

    [Fact]
    public async Task KafkaConsumerHostedService_SubscribesToCorrectTopicsAndDelegatesToHandler()
    {
        // Arrange
        var mockConsumer = Substitute.For<IConsumer<string, string>>();
        var mockMessageHandler = Substitute.For<IKafkaMessageHandler>();
        var mockLogger = Substitute.For<ILogger<KafkaConsumerHostedService>>();

        var options = new KafkaOptions();
        options.ConsumerOptions.GroupId = "test-group";
        options.ConsumerOptions.TopicApiMappings.Add("topic1", "http://url1");
        options.ConsumerOptions.TopicApiMappings.Add("topic2", "http://url2");
        var mockOptions = Options.Create(options);

        // Setup IServiceProvider to resolve IKafkaMessageHandler
        var mockServiceProvider = Substitute.For<IServiceProvider>();
        var mockScope = Substitute.For<IServiceScope>();
        var mockScopeFactory = Substitute.For<IServiceScopeFactory>();

        mockServiceProvider.GetService(typeof(IServiceScopeFactory)).Returns(mockScopeFactory);
        mockScopeFactory.CreateScope().Returns(mockScope);
        mockScope.ServiceProvider.GetService(typeof(IKafkaMessageHandler)).Returns(mockMessageHandler);

        var hostedService = new KafkaConsumerHostedService(mockConsumer, mockOptions, mockLogger, mockServiceProvider);

        var recordHeader = new Headers { new Header("X-Kafka-CorrelationId", System.Text.Encoding.UTF8.GetBytes("corr-id")) };
        var message = new Message<string, string> { Key = "key1", Value = "val1", Headers = recordHeader };
        var consumeResult = new ConsumeResult<string, string>
        {
            Topic = "topic1",
            Partition = 0,
            Offset = 10,
            Message = message
        };

        // We want Consume to return the message, and then throw OperationCanceledException to break the loop
        var count = 0;
        mockConsumer.Consume(Arg.Any<CancellationToken>()).Returns(x =>
        {
            count++;
            if (count == 1)
            {
                return consumeResult;
            }
            throw new OperationCanceledException();
        });

        // Act
        await hostedService.StartAsync(CancellationToken.None);

        // Allow some time for background loop
        await Task.Delay(100);

        await hostedService.StopAsync(CancellationToken.None);

        // Assert
        mockConsumer.Received(1).Subscribe(Arg.Is<IEnumerable<string>>(t => new List<string>(t).Contains("topic1") && new List<string>(t).Contains("topic2")));
        await mockMessageHandler.Received(1).HandleAsync(
            "topic1",
            "key1",
            "val1",
            Arg.Is<IDictionary<string, string>>(h => h.ContainsKey("X-Kafka-CorrelationId") && h["X-Kafka-CorrelationId"] == "corr-id"),
            Arg.Any<CancellationToken>());
        mockConsumer.Received(1).Commit(consumeResult);
    }

    [Fact]
    public async Task KafkaApiBridge_E2E_SuccessfullyForwardsToApiGateway()
    {
        // Arrange — Mock dependencies for user security and MongoDB repository
        var mockUserContext = Substitute.For<ICurrentUserContext>();
        mockUserContext.OperatorId.Returns("test-admin");
        var claims = new List<Claim> { new(ClaimTypes.Role, "Admin") };
        mockUserContext.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")));

        var mockRepository = Substitute.For<IRepository<Order>>();

        // Setup the in-memory WebHost using WebApplicationFactory
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                // Register mock security context to authorize the POST request
                services.AddScoped<ICurrentUserContext>(_ => mockUserContext);
                // Register mock database repository to bypass local MongoDB connection requirement
                services.AddScoped<IRepository<Order>>(_ => mockRepository);
                // Register mock outbox queue to bypass outbox database writes
                services.AddScoped<Foundry.Core.Outbox.IOutboxQueue>(_ => Substitute.For<Foundry.Core.Outbox.IOutboxQueue>());
            });
        }).CreateClient();

        // Create HttpClientFactory that returns the in-memory client
        var mockFactory = Substitute.For<IHttpClientFactory>();
        mockFactory.CreateClient("KafkaBridge").Returns(client);

        var options = new KafkaOptions();
        options.ConsumerOptions.TopicApiMappings.Add("orders", "http://localhost/api/v1/orders");
        var mockOptions = Options.Create(options);

        var mockLogger = Substitute.For<ILogger<KafkaToApiBridgeHandler>>();
        var mockProducer = Substitute.For<IKafkaProducer>();
        mockProducer.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Confluent.Kafka.Headers>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("DLQ was triggered. This indicates that the HTTP call to the API Gateway failed!"));

        var bridgeHandler = new KafkaToApiBridgeHandler(mockFactory, mockProducer, mockOptions, mockLogger);

        var order = new Order
        {
            Id = MongoDB.Bson.ObjectId.Parse("64b73b22e1b12b5f7cc4b21a"),
            OrderNumber = "ORD-E2E-123",
            CustomerId = "cust-e2e",
            TotalAmount = 150m
        };
        var orderPayload = System.Text.Json.JsonSerializer.Serialize(order);

        // Act
        await bridgeHandler.HandleAsync("orders", "key-123", orderPayload, new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        // Verify that the API gateway received the request, processed the command, and invoked repository insertion
        await mockRepository.Received(1).InsertAsync(
            Arg.Is<Order>(o => o.OrderNumber == "ORD-E2E-123" && o.CustomerId == "cust-e2e" && o.TotalAmount == 150m),
            Arg.Any<MongoDB.Driver.IClientSessionHandle>(),
            Arg.Any<CancellationToken>());

        // Verify DLQ was NOT triggered
        await mockProducer.DidNotReceiveWithAnyArgs().ProduceAsync(default!, default!, default!, default!, default!);
    }

    [Fact]
    public async Task KafkaHealthCheck_ReturnsHealthy_WhenMetadataQuerySucceeds()
    {
        // Arrange
        var mockAdminClient = Substitute.For<IAdminClient>();
        var options = Options.Create(new KafkaOptions { BootstrapServers = "localhost:9092", ClientId = "test" });
        var healthCheck = new Foundry.Kafka.Diagnostics.KafkaHealthCheck(options, config => mockAdminClient);
        var context = new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext
        {
            Registration = new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(
                "Kafka",
                healthCheck,
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result = await healthCheck.CheckHealthAsync(context);

        // Assert
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, result.Status);
        mockAdminClient.Received(1).GetMetadata(Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task KafkaHealthCheck_ReturnsUnhealthy_WhenMetadataQueryThrows()
    {
        // Arrange
        var mockAdminClient = Substitute.For<IAdminClient>();
        mockAdminClient.GetMetadata(Arg.Any<TimeSpan>()).Throws(new Exception("Kafka down"));
        
        var options = Options.Create(new KafkaOptions { BootstrapServers = "localhost:9092", ClientId = "test" });
        var healthCheck = new Foundry.Kafka.Diagnostics.KafkaHealthCheck(options, config => mockAdminClient);
        var context = new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext
        {
            Registration = new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(
                "Kafka",
                healthCheck,
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result = await healthCheck.CheckHealthAsync(context);

        // Assert
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
        Assert.Equal("Kafka down", result.Exception.Message);
    }
}

// A simple mock HttpMessageHandler to capture requests
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _responseContent;
    public HttpRequestMessage? CapturedRequest { get; private set; }
    public string? CapturedBody { get; private set; }
    public int RequestCount { get; private set; }

    public MockHttpMessageHandler(HttpStatusCode statusCode, string responseContent)
    {
        _statusCode = statusCode;
        _responseContent = responseContent;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        CapturedRequest = request;
        if (request.Content != null)
        {
            CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseContent)
        };
        return response;
    }
}
