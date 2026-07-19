using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Foundry.Rules;
using Foundry.Api.MediatR.Behaviors;
using Xunit;

namespace Foundry.IntegrationTests;

/// <summary>
/// Verifies the behavior of the decoupled Foundry.Rules engine and its 
/// integration with the MediatR request pipeline.
/// </summary>
public class BusinessRulesTests
{
    public record DummyCommand : IRequest<string>;

    [Fact]
    public async Task BusinessRuleEngine_WhenAllRulesPass_ShouldNotReturnFailuresOrThrow()
    {
        // Arrange
        var services = new ServiceCollection();
        
        var passingRule = Substitute.For<IBusinessRule<DummyCommand>>();
        passingRule.ValidateAsync(Arg.Any<DummyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RuleResult.Success()));

        services.AddSingleton(passingRule);
        services.AddFoundryRules();

        var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IBusinessRuleEngine>();

        var command = new DummyCommand();

        // Act
        var results = (await engine.EvaluateAsync(command, CancellationToken.None)).ToList();
        
        // Assert
        Assert.Empty(results);
        await engine.EnsurePassedAsync(command, CancellationToken.None); // Should not throw
    }

    [Fact]
    public async Task BusinessRuleEngine_WhenARuleFails_ShouldReturnFailureAndThrowException()
    {
        // Arrange
        var services = new ServiceCollection();
        
        var failingRule = Substitute.For<IBusinessRule<DummyCommand>>();
        failingRule.ValidateAsync(Arg.Any<DummyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RuleResult.Failure("Custom validation error message.", "ERR_DUMMY")));

        services.AddSingleton(failingRule);
        services.AddFoundryRules();

        var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IBusinessRuleEngine>();

        var command = new DummyCommand();

        // Act
        var results = (await engine.EvaluateAsync(command, CancellationToken.None)).ToList();

        // Assert
        Assert.Single(results);
        Assert.False(results[0].IsPassed);
        Assert.Equal("Custom validation error message.", results[0].ErrorMessage);
        Assert.Equal("ERR_DUMMY", results[0].RuleCode);

        // Verify throwing exception
        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => 
            engine.EnsurePassedAsync(command, CancellationToken.None));
        
        Assert.Single(exception.Failures);
        Assert.Equal("ERR_DUMMY", exception.Failures[0].RuleCode);
    }

    [Fact]
    public async Task MediatRBehavior_WhenRuleFails_ShouldTranslateToFluentValidationException()
    {
        // Arrange
        var services = new ServiceCollection();
        
        var mockEngine = Substitute.For<IBusinessRuleEngine>();
        mockEngine.EvaluateAsync(Arg.Any<DummyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<RuleResult>>([RuleResult.Failure("Domain policy violation.", "POLICY_ERR")]));

        var behavior = new BusinessRuleBehavior<DummyCommand, string>(mockEngine);
        
        var request = new DummyCommand();
        var next = Substitute.For<RequestHandlerDelegate<string>>();
        next().Returns(Task.FromResult("SuccessResult"));

        // Act & Assert
        var validationEx = await Assert.ThrowsAsync<ValidationException>(() => 
            behavior.Handle(request, next, CancellationToken.None));

        Assert.Single(validationEx.Errors);
        Assert.Equal("Domain policy violation.", validationEx.Errors.First().ErrorMessage);
        Assert.Equal("POLICY_ERR", validationEx.Errors.First().ErrorCode);

        // The pipeline should halt and next delegate should NOT have been called
        await next.DidNotReceive()();
    }
}
