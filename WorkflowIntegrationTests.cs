using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using MongoDB.Bson;
using Xunit;
using Foundry.Rules;
using Foundry.Api.Manifest;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;

namespace Foundry.IntegrationTests;

/// <summary>
/// Integration tests verifying the decoupled C# Workflow Engine, UML Choice Node
/// evaluation, dynamic routing transitions, and historical activity logging.
/// </summary>
public class WorkflowIntegrationTests
{
    public record TestStateEntity : BaseEntity<ObjectId>, IWorkflowStateful
    {
        public required string EntityName { get; init; }
        public decimal TotalAmount { get; init; }
        public string CurrentState { get; set; } = string.Empty;
        public string WorkflowId { get; set; } = string.Empty;
        public string WorkflowVersion { get; set; } = string.Empty;
    }

    public record TestTransitionCommand : IRequest<Unit>, IWorkflowTransitionRequest
    {
        public string EntityId { get; init; } = string.Empty;
        public string EntityType => "TestStateEntity";
        public string TransitionId => "submit";
        public string FromState => "Draft";
        public string ToState => "route_gate";
    }

    [Fact]
    public async Task WorkflowPipeline_ShouldRouteThroughDecisionGateToCorrectState_BasedOnConditions()
    {
        // Arrange
        var services = new ServiceCollection();

        // 1. Setup API Manifest with a Decision Gate (Choice Node)
        var manifest = new ApiManifest
        {
            Workflows = new List<WorkflowConfig>
            {
                new()
                {
                    Id = "order_wf",
                    Entity = "TestStateEntity",
                    Version = "1.2.0",
                    IsActive = true,
                    States = new List<WorkflowStateConfig>
                    {
                        new() { Name = "Draft", IsInitial = true },
                        new() { Name = "Approved" },
                        new() { Name = "PendingManagerApproval" }
                    },
                    Transitions = new List<WorkflowTransitionConfig>
                    {
                        new()
                        {
                            Id = "submit",
                            FromState = "Draft",
                            ToState = "route_gate"
                        }
                    },
                    ChoiceNodes = new List<WorkflowChoiceNodeConfig>
                    {
                        new()
                        {
                            Id = "route_gate",
                            DefaultState = "Approved",
                            Branches = new List<WorkflowChoiceBranchConfig>
                            {
                                new()
                                {
                                    ToState = "PendingManagerApproval",
                                    Conditions = new List<WorkflowConditionConfig>
                                    {
                                        new() { Property = "TotalAmount", Operator = "GreaterThan", Value = "5000" }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
        services.AddSingleton(manifest);

        // 2. Setup mock Repositories
        var entityId = ObjectId.GenerateNewId();
        var entity = new TestStateEntity
        {
            Id = entityId,
            EntityName = "High Value Purchase",
            TotalAmount = 7500.00m, // Evaluates to True for GreaterThan 5000
            CurrentState = "Draft",
            WorkflowId = "order_wf",
            WorkflowVersion = "1.2.0"
        };

        var mockEntityRepo = Substitute.For<IRepository<TestStateEntity>>();
        mockEntityRepo.GetByIdAsync(entityId, Arg.Any<MongoDB.Driver.IClientSessionHandle?>(), Arg.Any<CancellationToken>())
            .Returns(entity);
        services.AddSingleton(mockEntityRepo);

        var mockLogRepo = Substitute.For<IRepository<WorkflowActivityLog>>();
        services.AddSingleton(mockLogRepo);

        // 3. Setup core dependencies
        var mockMediator = Substitute.For<IMediator>();
        services.AddSingleton(mockMediator);
        
        var mockHttpClientFactory = Substitute.For<System.Net.Http.IHttpClientFactory>();
        services.AddSingleton(mockHttpClientFactory);

        services.AddSingleton<IWorkflowEngine, WorkflowEngine>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(WorkflowTransitionBehavior<,>));

        var provider = services.BuildServiceProvider();

        // 4. Resolve behavior
        var behavior = provider.GetRequiredService<IPipelineBehavior<TestTransitionCommand, Unit>>();
        var request = new TestTransitionCommand { EntityId = entityId.ToString() };

        // Act
        var nextCalled = false;
        var result = await behavior.Handle(request, () =>
        {
            nextCalled = true;
            return Task.FromResult(Unit.Value);
        }, CancellationToken.None);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal("PendingManagerApproval", entity.CurrentState); // Dynamic gate routed to correct state!
        
        // Ensure state update saved
        await mockEntityRepo.Received(1).UpdateAsync(entity, Arg.Any<MongoDB.Driver.IClientSessionHandle?>(), Arg.Any<CancellationToken>());
        
        // Ensure activity log written
        await mockLogRepo.Received(1).InsertAsync(
            Arg.Is<WorkflowActivityLog>(log => 
                log.EntityId == entityId.ToString() && 
                log.ToState == "PendingManagerApproval" && 
                log.WorkflowId == "order_wf" &&
                log.WorkflowVersion == "1.2.0"
            ),
            Arg.Any<MongoDB.Driver.IClientSessionHandle?>(),
            Arg.Any<CancellationToken>()
        );
    }
}
