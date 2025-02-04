﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

#pragma warning disable

namespace System.Threading
{
    internal interface ITimer : IDisposable, IAsyncDisposable
    {
        bool Change(TimeSpan dueTime, TimeSpan period);
    }
}

namespace System
{
    // Temporary, will be removed
    // Copied from https://github.com/dotnet/runtime/blob/main/src/libraries/Common/src/System/TimeProvider.cs and trimmed some fat which is not relevant for internal stuff

    [ExcludeFromCodeCoverage]
    internal abstract class TimeProvider
    {
        public static TimeProvider System { get; } = new SystemTimeProvider();

        protected TimeProvider()
        {
        }

        public virtual DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;

        private static readonly long MinDateTicks = DateTime.MinValue.Ticks;
        private static readonly long MaxDateTicks = DateTime.MaxValue.Ticks;

        public DateTimeOffset GetLocalNow()
        {
            DateTimeOffset utcDateTime = GetUtcNow();
            TimeZoneInfo zoneInfo = LocalTimeZone;
            if (zoneInfo is null)
            {
                throw new InvalidOperationException();
            }

            TimeSpan offset = zoneInfo.GetUtcOffset(utcDateTime);

            long localTicks = utcDateTime.Ticks + offset.Ticks;
            if ((ulong)localTicks > (ulong)MaxDateTicks)
            {
                localTicks = localTicks < MinDateTicks ? MinDateTicks : MaxDateTicks;
            }

            return new DateTimeOffset(localTicks, offset);
        }

        public virtual TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;

        public virtual long TimestampFrequency => Stopwatch.Frequency;

        public virtual long GetTimestamp() => Stopwatch.GetTimestamp();

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp)
        {
            long timestampFrequency = TimestampFrequency;
            if (timestampFrequency <= 0)
            {
                throw new InvalidOperationException();
            }

            return new TimeSpan((long)((endingTimestamp - startingTimestamp) * ((double)TimeSpan.TicksPerSecond / timestampFrequency)));
        }

        public TimeSpan GetElapsedTime(long startingTimestamp) => GetElapsedTime(startingTimestamp, GetTimestamp());

        public virtual ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (callback is null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            return new SystemTimeProviderTimer(dueTime, period, callback, state);
        }

        [ExcludeFromCodeCoverage]
        private sealed class SystemTimeProviderTimer : ITimer
        {
            private readonly Timer _timer;

            public SystemTimeProviderTimer(TimeSpan dueTime, TimeSpan period, TimerCallback callback, object? state)
            {
                (uint duration, uint periodTime) = CheckAndGetValues(dueTime, period);

                // We need to ensure the timer roots itself. Timer created with a duration and period argument
                // only roots the state object, so to root the timer we need the state object to reference the
                // timer recursively.
                var timerState = new TimerState(callback, state);
                timerState.Timer = _timer = new Timer(static s =>
                {
                    TimerState ts = (TimerState)s!;
                    ts.Callback(ts.State);
                }, timerState, duration, periodTime);
            }

            private sealed class TimerState
            {
                public TimerState(TimerCallback callback, object? state)
                {
                    Callback = callback;
                    State = state;
                }

                public TimerCallback Callback { get; }

                public object? State { get; }

                public Timer? Timer { get; set; }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                (uint duration, uint periodTime) = CheckAndGetValues(dueTime, period);
                try
                {
                    return _timer.Change(duration, periodTime);
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }

            public void Dispose() => _timer.Dispose();

            public ValueTask DisposeAsync()
            {
                _timer.Dispose();
                return default;
            }

            private static (uint duration, uint periodTime) CheckAndGetValues(TimeSpan dueTime, TimeSpan periodTime)
            {
                long dueTm = (long)dueTime.TotalMilliseconds;
                long periodTm = (long)periodTime.TotalMilliseconds;

                const uint MaxSupportedTimeout = 0xfffffffe;

                if (dueTm < -1)
                {
                    throw new ArgumentOutOfRangeException(nameof(dueTime));
                }

                if (dueTm > MaxSupportedTimeout)
                {
                    throw new ArgumentOutOfRangeException(nameof(dueTime));
                }

                if (periodTm < -1)
                {
                    throw new ArgumentOutOfRangeException(nameof(periodTm));
                }

                if (periodTm > MaxSupportedTimeout)
                {
                    throw new ArgumentOutOfRangeException(nameof(periodTm));
                }

                return ((uint)dueTm, (uint)periodTm);
            }
        }

        [ExcludeFromCodeCoverage]
        private sealed class SystemTimeProvider : TimeProvider
        {
            internal SystemTimeProvider()
            {
            }
        }
    }
}

namespace System.Threading.Tasks
{
    /// <summary>
    /// Provide extensions methods for <see cref="Task"/> operations with <see cref="TimeProvider"/>.
    /// </summary>
    /// <remarks>
    /// The Microsoft.Bcl.TimeProvider library interfaces are intended solely for use in building against pre-.NET 8 surface area.
    /// If your code is being built against .NET 8 or higher, then this library should not be utilized.
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class TimeProviderTaskExtensions
    {
        private sealed class DelayState : TaskCompletionSource<bool>
        {
            public DelayState(CancellationToken cancellationToken)
                : base(TaskCreationOptions.RunContinuationsAsynchronously) => CancellationToken = cancellationToken;

            public ITimer? Timer { get; set; }
            public CancellationToken CancellationToken { get; }
            public CancellationTokenRegistration Registration { get; set; }
        }

        private sealed class WaitAsyncState : TaskCompletionSource<bool>
        {
            public WaitAsyncState(CancellationToken cancellationToken)
                : base(TaskCreationOptions.RunContinuationsAsynchronously) => CancellationToken = cancellationToken;

            public readonly CancellationTokenSource ContinuationCancellation = new();
            public CancellationToken CancellationToken { get; }
            public CancellationTokenRegistration Registration;
            public ITimer? Timer;
        }

        /// <summary>Creates a task that completes after a specified time interval.</summary>
        /// <param name="timeProvider">The <see cref="TimeProvider"/> with which to interpret <paramref name="delay"/>.</param>
        /// <param name="delay">The <see cref="TimeSpan"/> to wait before completing the returned task, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the time delay.</returns>
        /// <exception cref="System.ArgumentNullException">The <paramref name="timeProvider"/> argument is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="delay"/> represents a negative time interval other than <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
        public static Task Delay(this TimeProvider timeProvider, TimeSpan delay, CancellationToken cancellationToken = default)
        {
            if (timeProvider == TimeProvider.System)
            {
                return Task.Delay(delay, cancellationToken);
            }

            if (timeProvider is null)
            {
                throw new ArgumentNullException(nameof(timeProvider));
            }

            if (delay != Timeout.InfiniteTimeSpan && delay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(delay));
            }

            if (delay == TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            DelayState state = new(cancellationToken);

            state.Timer = timeProvider.CreateTimer(static delayState =>
            {
                DelayState s = (DelayState)delayState!;
                s.TrySetResult(true);
                s.Registration.Dispose();
                s.Timer?.Dispose();
            }, state, delay, Timeout.InfiniteTimeSpan);

            state.Registration = cancellationToken.Register(static delayState =>
            {
                DelayState s = (DelayState)delayState!;
                s.TrySetCanceled(s.CancellationToken);
                s.Registration.Dispose();
                s.Timer?.Dispose();
            }, state);

            // There are race conditions where the timer fires after we have attached the cancellation callback but before the
            // registration is stored in state.Registration, or where cancellation is requested prior to the registration being
            // stored into state.Registration, or where the timer could fire after it's been created but before it's been stored
            // in state.Timer. In such cases, the cancellation registration and/or the Timer might be stored into state after the
            // callbacks and thus left undisposed.  So, we do a subsequent check here. If the task isn't completed by this point,
            // then the callbacks won't have called TrySetResult (the callbacks invoke TrySetResult before disposing of the fields),
            // in which case it will see both the timer and registration set and be able to Dispose them. If the task is completed
            // by this point, then this is guaranteed to see s.Timer as non-null because it was deterministically set above.
            if (state.Task.IsCompleted)
            {
                state.Registration.Dispose();
                state.Timer.Dispose();
            }

            return state.Task;
        }

        /// <summary>
        /// Gets a <see cref="Task"/> that will complete when this <see cref="Task"/> completes,
        /// when the specified timeout expires, or when the specified <see cref="CancellationToken"/> has cancellation requested.
        /// </summary>
        /// <param name="task">The task for which to wait on until completion.</param>
        /// <param name="timeout">The timeout after which the <see cref="Task"/> should be faulted with a <see cref="TimeoutException"/> if it hasn't otherwise completed.</param>
        /// <param name="timeProvider">The <see cref="TimeProvider"/> with which to interpret <paramref name="timeout"/>.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for a cancellation request.</param>
        /// <returns>The <see cref="Task"/> representing the asynchronous wait.  It may or may not be the same instance as the current instance.</returns>
        /// <exception cref="System.ArgumentNullException">The <paramref name="task"/> argument is null.</exception>
        /// <exception cref="System.ArgumentNullException">The <paramref name="timeProvider"/> argument is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="timeout"/> represents a negative time interval other than <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
        public static Task WaitAsync(this Task task, TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken = default)
        {
            if (task is null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            if (timeout != Timeout.InfiniteTimeSpan && timeout < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            if (timeProvider is null)
            {
                throw new ArgumentNullException(nameof(timeProvider));
            }

            if (task.IsCompleted)
            {
                return task;
            }

            if (timeout == Timeout.InfiniteTimeSpan && !cancellationToken.CanBeCanceled)
            {
                return task;
            }

            if (timeout == TimeSpan.Zero)
            {
                Task.FromException(new TimeoutException());
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            WaitAsyncState state = new(cancellationToken);

            state.Timer = timeProvider.CreateTimer(static s =>
            {
                var state = (WaitAsyncState)s!;

                state.TrySetException(new TimeoutException());

                state.Registration.Dispose();
                state.Timer?.Dispose();
                state.ContinuationCancellation.Cancel();
            }, state, timeout, Timeout.InfiniteTimeSpan);

            _ = task.ContinueWith(static (t, s) =>
            {
                var state = (WaitAsyncState)s!;

                if (t.IsFaulted)
                {
                    state.TrySetException(t.Exception!.InnerExceptions);
                }
                else if (t.IsCanceled)
                {
                    state.TrySetCanceled();
                }
                else
                {
                    state.TrySetResult(true);
                }

                state.Registration.Dispose();
                state.Timer?.Dispose();
            }, state, state.ContinuationCancellation.Token, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            state.Registration = cancellationToken.Register(static s =>
            {
                var state = (WaitAsyncState)s!;

                state.TrySetCanceled(state.CancellationToken);

                state.Timer?.Dispose();
                state.ContinuationCancellation.Cancel();
            }, state);

            // See explanation in Delay for this final check
            if (state.Task.IsCompleted)
            {
                state.Registration.Dispose();
                state.Timer.Dispose();
            }

            return state.Task;
        }

        /// <summary>
        /// Gets a <see cref="Task"/> that will complete when this <see cref="Task"/> completes,
        /// when the specified timeout expires, or when the specified <see cref="CancellationToken"/> has cancellation requested.
        /// </summary>
        /// <param name="task">The task for which to wait on until completion.</param>
        /// <param name="timeout">The timeout after which the <see cref="Task"/> should be faulted with a <see cref="TimeoutException"/> if it hasn't otherwise completed.</param>
        /// <param name="timeProvider">The <see cref="TimeProvider"/> with which to interpret <paramref name="timeout"/>.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for a cancellation request.</param>
        /// <returns>The <see cref="Task"/> representing the asynchronous wait.  It may or may not be the same instance as the current instance.</returns>
        /// <exception cref="System.ArgumentNullException">The <paramref name="task"/> argument is null.</exception>
        /// <exception cref="System.ArgumentNullException">The <paramref name="timeProvider"/> argument is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="timeout"/> represents a negative time interval other than <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
        public static async Task<TResult> WaitAsync<TResult>(this Task<TResult> task, TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken = default)
        {
            await ((Task)task).WaitAsync(timeout, timeProvider, cancellationToken).ConfigureAwait(false);
            return task.Result;
        }

        /// <summary>Initializes a new instance of the <see cref="CancellationTokenSource"/> class that will be canceled after the specified <see cref="TimeSpan"/>. </summary>
        /// <param name="timeProvider">The <see cref="TimeProvider"/> with which to interpret the <paramref name="delay"/>. </param>
        /// <param name="delay">The time interval to wait before canceling this <see cref="CancellationTokenSource"/>. </param>
        /// <exception cref="ArgumentOutOfRangeException"> The <paramref name="delay"/> is negative and not equal to <see cref="Timeout.InfiniteTimeSpan" />
        /// or greater than maximum allowed timer duration.</exception>
        /// <returns><see cref="CancellationTokenSource"/> that will be canceled after the specified <paramref name="delay"/>.</returns>
        /// <remarks>
        /// <para>
        /// The countdown for the delay starts during the call to the constructor. When the delay expires,
        /// the constructed <see cref="CancellationTokenSource"/> is canceled if it has
        /// not been canceled already.
        /// </para>
        /// <para>
        /// If running on .NET versions earlier than .NET 8.0, there is a constraint when invoking <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> on the resultant object.
        /// This action will not terminate the initial timer indicated by <paramref name="delay"/>. However, this restriction does not apply on .NET 8.0 and later versions.
        /// </para>
        /// </remarks>
        public static CancellationTokenSource CreateCancellationTokenSource(this TimeProvider timeProvider, TimeSpan delay)
        {
            if (timeProvider is null)
            {
                throw new ArgumentNullException(nameof(timeProvider));
            }

            if (delay != Timeout.InfiniteTimeSpan && delay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(delay));
            }

            if (timeProvider == TimeProvider.System)
            {
                return new CancellationTokenSource(delay);
            }

            var cts = new CancellationTokenSource();

            ITimer timer = timeProvider.CreateTimer(static s =>
            {
                try
                {
                    ((CancellationTokenSource)s!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // ok
                }
            }, cts, delay, Timeout.InfiniteTimeSpan);

            cts.Token.Register(static t => ((ITimer)t!).Dispose(), timer);
            return cts;
        }
    }
}
