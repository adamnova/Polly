﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Polly.Bulkhead;
using Polly.Specs.Helpers;
using Polly.Specs.Helpers.Bulkhead;
using FluentAssertions;
using Polly.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Polly.Specs.Bulkhead;

[Collection(Constants.ParallelThreadDependentTestCollection)]
public class BulkheadTResultAsyncSpecs : BulkheadSpecsBase
{
    public BulkheadTResultAsyncSpecs(ITestOutputHelper testOutputHelper) : base(testOutputHelper) { }

    #region Configuration

    [Fact]
    public void Should_throw_when_maxparallelization_less_or_equal_to_zero()
    {
        Action policy = () => Policy
            .BulkheadAsync<int>(0, 1);

        policy.Should().Throw<ArgumentOutOfRangeException>().And
            .ParamName.Should().Be("maxParallelization");
    }

    [Fact]
    public void Should_throw_when_maxQueuingActions_less_than_zero()
    {
        Action policy = () => Policy
            .BulkheadAsync<int>(1, -1);

        policy.Should().Throw<ArgumentOutOfRangeException>().And
            .ParamName.Should().Be("maxQueuingActions");
    }

    [Fact]
    public void Should_throw_when_onBulkheadRejected_is_null()
    {
        Action policy = () => Policy
            .BulkheadAsync<int>(1, 0, null);

        policy.Should().Throw<ArgumentNullException>().And
            .ParamName.Should().Be("onBulkheadRejectedAsync");
    }

    #endregion

    #region onBulkheadRejected delegate

    [Fact]
    public void Should_call_onBulkheadRejected_with_passed_context()
    {
        var operationKey = "SomeKey";
        var contextPassedToExecute = new Context(operationKey);

        Context contextPassedToOnRejected = null;
        Func<Context, Task> onRejectedAsync = async ctx => { contextPassedToOnRejected = ctx; await TaskHelper.EmptyTask; };

        using (var bulkhead = Policy.BulkheadAsync<int>(1, onRejectedAsync))
        { 
            var tcs = new TaskCompletionSource<object>();
            using (var cancellationSource = new CancellationTokenSource())
            {
                Task.Run(() => {
                    bulkhead.ExecuteAsync(async () =>
                    {
                        await tcs.Task;
                        return 0;
                    });
                });

                Within(CohesionTimeLimit, () => Expect(0, () => bulkhead.BulkheadAvailableCount, nameof(bulkhead.BulkheadAvailableCount)));

                bulkhead.Awaiting(b => b.ExecuteAsync(_ => Task.FromResult(1), contextPassedToExecute)).Should().Throw<BulkheadRejectedException>();

                cancellationSource.Cancel();
                tcs.SetCanceled();
            }

            contextPassedToOnRejected.Should().NotBeNull();
            contextPassedToOnRejected.OperationKey.Should().Be(operationKey);
            contextPassedToOnRejected.Should().BeSameAs(contextPassedToExecute);

        }
    }

    #endregion


    #region Bulkhead behaviour

    protected override IBulkheadPolicy GetBulkhead(int maxParallelization, int maxQueuingActions)
    {
        return Policy.BulkheadAsync<ResultPrimitive>(maxParallelization, maxQueuingActions);
    }

    protected override Task ExecuteOnBulkhead(IBulkheadPolicy bulkhead, TraceableAction action)
    {
        return action.ExecuteOnBulkheadAsync<ResultPrimitive>((AsyncBulkheadPolicy<ResultPrimitive>)bulkhead);
    }

    #endregion

}