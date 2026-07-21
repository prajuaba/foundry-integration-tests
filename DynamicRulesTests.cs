using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Rules;
using Foundry.Api.MediatR;
using Paperclip.OrderingSystem.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundry.IntegrationTests;

public class DynamicRulesTests
{
    [Fact]
    public async Task DynamicRuleEvaluator_EvaluatesNumericOperators_Correctly()
    {
        var target = new Order 
        { 
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            OrderNumber = "ORD-001", 
            CustomerId = "cust-1", 
            TotalAmount = 150m 
        };

        // Test equal / ==
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "TotalAmount", "==", "150"));
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "TotalAmount", "equal", "150"));
        Assert.False(DynamicRuleEvaluator.Evaluate(target, "TotalAmount", "==", "200"));

        // Test lessthan / <
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "TotalAmount", "<", "200"));
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "TotalAmount", "lessthan", "151"));
        Assert.False(DynamicRuleEvaluator.Evaluate(target, "TotalAmount", "<", "150"));

        // Test lessthanorequal / <=
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "TotalAmount", "<=", "150"));
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "TotalAmount", "<=", "160"));

        // Test greaterthan / >
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "TotalAmount", ">", "100"));
        Assert.False(DynamicRuleEvaluator.Evaluate(target, "TotalAmount", ">", "150"));

        // Test greaterthanorequal / >=
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "TotalAmount", ">=", "150"));
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "TotalAmount", ">=", "140"));
        Assert.False(DynamicRuleEvaluator.Evaluate(target, "TotalAmount", ">=", "160"));
    }

    [Fact]
    public async Task DynamicRuleEvaluator_EvaluatesStringOperators_Correctly()
    {
        var target = new Order 
        { 
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            OrderNumber = "ORD-SPECIAL-123", 
            CustomerId = "cust-vip" 
        };

        // Test equals / ==
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "CustomerId", "==", "cust-vip"));
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "CustomerId", "equal", "CUST-VIP")); // Case-insensitive
        Assert.False(DynamicRuleEvaluator.Evaluate(target, "CustomerId", "==", "other"));

        // Test contains
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "OrderNumber", "contains", "SPECIAL"));
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "OrderNumber", "contains", "special")); // Case-insensitive
        Assert.False(DynamicRuleEvaluator.Evaluate(target, "OrderNumber", "contains", "other"));

        // Test startswith
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "OrderNumber", "startswith", "ord"));
        Assert.False(DynamicRuleEvaluator.Evaluate(target, "OrderNumber", "startswith", "special"));

        // Test endswith
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "OrderNumber", "endswith", "123"));
        Assert.False(DynamicRuleEvaluator.Evaluate(target, "OrderNumber", "endswith", "special"));
    }

    [Fact]
    public async Task DynamicRuleEvaluator_EvaluatesEnums_Correctly()
    {
        var target = new Order 
        { 
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            OrderNumber = "ORD-001", 
            Status = OrderStatus.Completed 
        };

        Assert.True(DynamicRuleEvaluator.Evaluate(target, "Status", "equal", "Completed"));
        Assert.True(DynamicRuleEvaluator.Evaluate(target, "Status", "==", "completed")); // Case-insensitive string parsing
        Assert.False(DynamicRuleEvaluator.Evaluate(target, "Status", "equal", "Pending"));
    }

    [Fact]
    public async Task DynamicRulesEngineRule_ValidatesTargetEntityInsideCommands_Correctly()
    {
        // Arrange: set up dynamic rules for Order
        var ruleList = new List<DynamicRule>
        {
            new()
            {
                RuleName = "OrderLimitRule",
                TargetEntity = "Order",
                PropertyName = "TotalAmount",
                Operator = "<",
                Value = "500",
                ErrorMessage = "Order total exceeds threshold limit.",
                ErrorCode = "LIMIT_EXCEEDED"
            }
        };

        var ruleStore = new InMemoryDynamicRuleStore(ruleList);
        var rulesEngineRule = new DynamicRulesEngineRule<InsertCommand<Order>>(ruleStore);

        // Scenario 1: Valid command (TotalAmount = 450)
        var validCommand = new InsertCommand<Order>(new Order
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            OrderNumber = "ORD-VALID",
            TotalAmount = 450m
        });
        var resultValid = await rulesEngineRule.ValidateAsync(validCommand, CancellationToken.None);
        Assert.True(resultValid.IsPassed);

        // Scenario 2: Invalid command (TotalAmount = 550)
        var invalidCommand = new InsertCommand<Order>(new Order
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            OrderNumber = "ORD-INVALID",
            TotalAmount = 550m
        });
        var resultInvalid = await rulesEngineRule.ValidateAsync(invalidCommand, CancellationToken.None);
        Assert.False(resultInvalid.IsPassed);
        Assert.Equal("Order total exceeds threshold limit.", resultInvalid.ErrorMessage);
        Assert.Equal("LIMIT_EXCEEDED", resultInvalid.RuleCode);
    }

    [Fact]
    public async Task HybridRulesEngine_AggregatesBothCompiledAndDynamicRules_Correctly()
    {
        // Arrange
        var services = new ServiceCollection();

        // 1. Add Foundry Rules core engine
        services.AddFoundryRules();

        // 2. Register mock dynamic rules: Order must have TotalAmount >= 100
        var dynamicRules = new List<DynamicRule>
        {
            new()
            {
                RuleName = "MinAmount",
                TargetEntity = "Order",
                PropertyName = "TotalAmount",
                Operator = ">=",
                Value = "100",
                ErrorMessage = "Order total must be at least 100.",
                ErrorCode = "MIN_AMOUNT_REQUIRED"
            }
        };
        services.AddSingleton<IDynamicRuleStore>(new InMemoryDynamicRuleStore(dynamicRules));

        // 3. Register compiled rule: OrderNumber must not be dummy
        services.AddTransient<IBusinessRule<InsertCommand<Order>>, CompiledDummyCheckRule>();

        var provider = services.BuildServiceProvider();
        var rulesEngine = provider.GetRequiredService<IBusinessRuleEngine>();

        // Scenario 1: Both pass
        var validOrder = new Order { Id = MongoDB.Bson.ObjectId.GenerateNewId(), OrderNumber = "ORD-123", TotalAmount = 150m };
        var validCommand = new InsertCommand<Order>(validOrder);
        var failuresValid = (await rulesEngine.EvaluateAsync(validCommand, CancellationToken.None)).ToList();
        Assert.Empty(failuresValid);

        // Scenario 2: Dynamic rule fails (TotalAmount = 50)
        var invalidOrderAmount = new Order { Id = MongoDB.Bson.ObjectId.GenerateNewId(), OrderNumber = "ORD-123", TotalAmount = 50m };
        var invalidCommandAmount = new InsertCommand<Order>(invalidOrderAmount);
        var failuresAmount = (await rulesEngine.EvaluateAsync(invalidCommandAmount, CancellationToken.None)).ToList();
        Assert.Single(failuresAmount);
        Assert.Equal("MIN_AMOUNT_REQUIRED", failuresAmount[0].RuleCode);

        // Scenario 3: Compiled rule fails (OrderNumber = "ORD-DUMMY")
        var invalidOrderDummy = new Order { Id = MongoDB.Bson.ObjectId.GenerateNewId(), OrderNumber = "ORD-DUMMY", TotalAmount = 150m };
        var invalidCommandDummy = new InsertCommand<Order>(invalidOrderDummy);
        var failuresDummy = (await rulesEngine.EvaluateAsync(invalidCommandDummy, CancellationToken.None)).ToList();
        Assert.Single(failuresDummy);
        Assert.Equal("NO_DUMMIES_ALLOWED", failuresDummy[0].RuleCode);
    }

    [Fact]
    public async Task DynamicRulesEngineRule_EvaluatesComplexLogicalExpressions_Successfully()
    {
        // Arrange: set up dynamic rule with complex expression using Microsoft.RulesEngine format
        var ruleList = new List<DynamicRule>
        {
            new()
            {
                RuleName = "ComplexVIPPromoRule",
                TargetEntity = "Order",
                Expression = "CustomerId == \"cust-vip\" ? TotalAmount >= 100 : TotalAmount >= 200",
                ErrorMessage = "Order total does not meet the required promotion minimum.",
                ErrorCode = "PROMO_LIMIT_FAIL"
            }
        };

        var ruleStore = new InMemoryDynamicRuleStore(ruleList);
        var rulesEngineRule = new DynamicRulesEngineRule<InsertCommand<Order>>(ruleStore);

        // Case 1: Non-VIP order total = 150 (fails because threshold is 200)
        var cmd1 = new InsertCommand<Order>(new Order
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            CustomerId = "cust-regular",
            OrderNumber = "ORD-REG-1",
            TotalAmount = 150m
        });
        var res1 = await rulesEngineRule.ValidateAsync(cmd1, CancellationToken.None);
        Assert.False(res1.IsPassed);
        Assert.Equal("PROMO_LIMIT_FAIL", res1.RuleCode);

        // Case 2: VIP order total = 150 (passes because threshold is 100)
        var cmd2 = new InsertCommand<Order>(new Order
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            CustomerId = "cust-vip",
            OrderNumber = "ORD-VIP-1",
            TotalAmount = 150m
        });
        var res2 = await rulesEngineRule.ValidateAsync(cmd2, CancellationToken.None);
        Assert.True(res2.IsPassed);
    }

    // A helper compiled rule class for the hybrid test
    private class CompiledDummyCheckRule : IBusinessRule<InsertCommand<Order>>
    {
        public Task<RuleResult> ValidateAsync(InsertCommand<Order> request, CancellationToken ct)
        {
            if (string.Equals(request.Entity.OrderNumber, "ORD-DUMMY", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(RuleResult.Failure("Dummy orders are not allowed.", "NO_DUMMIES_ALLOWED"));
            }
            return Task.FromResult(RuleResult.Success());
        }
    }
}
