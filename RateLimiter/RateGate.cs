﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RateLimiter
{
    /// <summary>
    /// RateGate controls access to a resource based on a specified rate limit. It uses a semaphore and a timer to manage
    /// token release times.
    /// It supports synchronous and asynchronous waiting for resource access.
    /// </summary>
    public class RateGate : IDisposable
    {
        /// <summary>
        /// Timer used to trigger exiting the semaphore.
        /// </summary>
        private Timer _exitTimer;

        /// <summary>
        /// Stores exit times in a thread-safe manner using a ConcurrentQueue. This allows multiple threads to enqueue
        /// and dequeue exit times safely.
        /// </summary>
        private readonly ConcurrentQueue<long> _exitTicks;

        /// <summary>
        /// A SemaphoreSlim instance used for controlling access to a resource across multiple threads. It allows a
        /// specified number of threads to enter concurrently.
        /// </summary>
        private SemaphoreSlim _semaphore;

        /// <summary>
        /// Tracks the disposal state of an object, where 0 indicates it is not disposed and 1 indicates it is disposed.
        /// </summary>
        private int _isDisposed; // 0 = not disposed, 1 = disposed

        /// <summary>
        /// Stores the number of ticks representing a time unit. Used for time-related calculations or conversions.
        /// </summary>
        private readonly long _timeUnitTicks;

        /// <summary>
        /// A private readonly field that holds an instance of Stopwatch. It is used to measure elapsed time.
        /// </summary>
        private readonly Stopwatch _stopwatch;

        /// <summary>
        /// Indicates whether a timer callback is currently executing. Prevents concurrent execution of multiple timer
        /// callbacks.
        /// </summary>
        private int _timerCallbackRunning;

        /// <summary>
        /// Tracks the number of pending exits in a queue using an atomic counter. This allows for size tracking without
        /// needing to enumerate the entire queue.
        /// </summary>
        private int _pendingExitCount;

        /// <summary>
        /// Represents the number of times an event or item occurs. It is a read-only property.
        /// </summary>
        public int Occurrences { get; }

        /// <summary>
        /// Represents the duration in milliseconds for a time unit. It provides a way to access the time unit's length
        /// in a precise format.
        /// </summary>
        public int TimeUnitMilliseconds { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="RateGate"/> class with the specified occurrences and time unit.
        /// </summary>
        /// <param name="occurrences">The number of occurrences allowed within the time unit.</param>
        /// <param name="timeUnit">The time unit for the rate limit.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when occurrences is less than or equal to 0, or timeUnit is less than or equal to zero or greater than int.MaxValue milliseconds.</exception>
        public RateGate(int occurrences, TimeSpan timeUnit)
        {
            // Validate arguments
            if (occurrences <= 0)
                throw new ArgumentOutOfRangeException(nameof(occurrences), "Number of occurrences must be a positive integer");
            if (timeUnit <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeUnit), "Time unit must be a positive span of time");
            if (timeUnit >= TimeSpan.FromMilliseconds(int.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(timeUnit), "Time unit must be less than int.MaxValue milliseconds");

            Occurrences = occurrences;
            TimeUnitMilliseconds = (int)timeUnit.TotalMilliseconds;

            // Use Stopwatch for more precise, monotonic timing that doesn't wrap around
            _stopwatch = Stopwatch.StartNew();
            _timeUnitTicks = timeUnit.Ticks;

            _semaphore = new SemaphoreSlim(Occurrences, Occurrences);
            _exitTicks = new ConcurrentQueue<long>();
            _pendingExitCount = 0;
            _timerCallbackRunning = 0;

            // Start the timer with an initial period, but it will be adjusted in the callback
            _exitTimer = new Timer(ExitTimerCallback, null, TimeUnitMilliseconds, Timeout.Infinite);
        }

        /// <summary>
        /// Callback method for the exit timer. Releases expired tokens and reschedules the timer.
        /// </summary>
        /// <param name="state">The state object passed to the callback.</param>
        private void ExitTimerCallback(object state)
        {
            // Use interlocked to ensure only one timer callback runs at a time
            if (Interlocked.CompareExchange(ref _timerCallbackRunning, 1, 0) != 0)
                return;

            try
            {
                if (Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0)
                    return;

                int releasedCount = 0;
                long currentTicks = _stopwatch.ElapsedTicks;
                int nextCheckDelayMs = TimeUnitMilliseconds;

                // Process expired tokens - using a limited loop count to prevent excessive CPU usage
                const int maxLoopsPerCallback = 1000;
                int loopCount = 0;

                // Local variable to track the earliest unexpired token
                long earliestRemainingTick = long.MaxValue;

                while (loopCount < maxLoopsPerCallback && _exitTicks.TryPeek(out long nextExitTick))
                {
                    if (nextExitTick > currentTicks)
                    {
                        // This is the earliest unexpired token
                        earliestRemainingTick = nextExitTick;
                        break;
                    }

                    // Remove the expired token
                    if (_exitTicks.TryDequeue(out _))
                    {
                        releasedCount++;
                        Interlocked.Decrement(ref _pendingExitCount);
                    }

                    loopCount++;
                }

                // Release the semaphore based on the count of expired tokens
                if (releasedCount > 0)
                {
                    try
                    {
                        _semaphore?.Release(releasedCount);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Semaphore was disposed
                        return;
                    }
                }

                // Calculate the next timer interval
                if (earliestRemainingTick != long.MaxValue)
                {
                    long ticksUntilNextExit = earliestRemainingTick - currentTicks;
                    long msUntilNextExit = ticksUntilNextExit / TimeSpan.TicksPerMillisecond;
                    nextCheckDelayMs = (int)Math.Max(1, Math.Min(int.MaxValue, msUntilNextExit));
                }
                else if (_pendingExitCount > 0)
                {
                    // If we couldn't check all tokens but there are still some, check again soon
                    nextCheckDelayMs = 1;
                }

                // Schedule the next callback
                try
                {
                    _exitTimer?.Change(nextCheckDelayMs, Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                    // Timer was disposed
                }
            }
            finally
            {
                // Allow the next callback to run
                Interlocked.Exchange(ref _timerCallbackRunning, 0);
            }
        }

        #region Synchronous methods

        /// <summary>
        /// Waits synchronously to proceed based on the rate limit.
        /// </summary>
        /// <param name="millisecondsTimeout">The timeout in milliseconds to wait for.</param>
        /// <returns>True if the wait succeeded, false if it timed out.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when millisecondsTimeout is less than -1.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the RateGate is already disposed.</exception>
        public bool WaitToProceed(int millisecondsTimeout)
        {
            if (millisecondsTimeout < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            CheckDisposed();

            bool entered;
            try
            {
                entered = _semaphore.Wait(millisecondsTimeout);
            }
            catch (ObjectDisposedException)
            {
                throw new ObjectDisposedException($"{nameof(RateGate)} is already disposed");
            }

            // If we entered the semaphore, compute the corresponding exit time 
            // and add it to the queue.
            if (entered)
            {
                // Calculate when this token should be released
                long exitTick = _stopwatch.ElapsedTicks + _timeUnitTicks;
                _exitTicks.Enqueue(exitTick);

                // Increment the counter atomically
                Interlocked.Increment(ref _pendingExitCount);

                // Consider triggering a timer reschedule if this is the earliest token
                // This is an optimization to release tokens sooner when rate increases suddenly
                TryRescheduleTimer(exitTick);
            }

            return entered;
        }

        /// <summary>
        /// Attempts to reschedule the timer based on the new exit tick.
        /// </summary>
        /// <param name="newExitTick">The new exit tick to consider for rescheduling.</param>
        private void TryRescheduleTimer(long newExitTick)
        {
            // Only reschedule if this is the only pending exit (i.e., it's the earliest)
            // This is a simple heuristic to avoid excessive timer rescheduling
            if (Interlocked.CompareExchange(ref _pendingExitCount, 0, 0) == 1)
            {
                long ticksUntilExit = newExitTick - _stopwatch.ElapsedTicks;
                if (ticksUntilExit > 0)
                {
                    long msUntilExit = ticksUntilExit / TimeSpan.TicksPerMillisecond;
                    int delayMs = (int)Math.Max(1, Math.Min(int.MaxValue, msUntilExit));

                    try
                    {
                        _exitTimer?.Change(delayMs, Timeout.Infinite);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Timer was disposed
                    }
                }
            }
        }

        /// <summary>
        /// Waits synchronously to proceed based on the rate limit.
        /// </summary>
        /// <param name="timeout">The timeout as a TimeSpan to wait for.</param>
        /// <returns>True if the wait succeeded, false if it timed out.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when timeout is less than -1 or greater than int.MaxValue milliseconds.</exception>
        public bool WaitToProceed(TimeSpan timeout)
        {
            long num = (long)timeout.TotalMilliseconds;
            if (num < -1 || num > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be between -1 and Int32.MaxValue milliseconds.");
            }
            return WaitToProceed((int)timeout.TotalMilliseconds);
        }

        /// <summary>
        /// Waits synchronously to proceed based on the rate limit with an infinite timeout.
        /// </summary>
        public void WaitToProceed()
        {
            _ = WaitToProceed(Timeout.Infinite);
        }

        #endregion Synchronous methods

        #region Asynchronous methods

        /// <summary>
        /// Waits asynchronously to proceed based on the rate limit.
        /// </summary>
        /// <param name="millisecondsTimeout">The timeout in milliseconds to wait for.</param>
        /// <param name="cancellationToken">The cancellation token to observe.</param>
        /// <returns>A task that represents the asynchronous wait operation. The task result is true if the wait succeeded, false if it timed out.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when millisecondsTimeout is less than -1.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the RateGate is already disposed.</exception>
        public async Task<bool> WaitToProceedAsync(int millisecondsTimeout, CancellationToken cancellationToken = default)
        {
            CheckDisposed();
            if (millisecondsTimeout < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));

            bool entered;
            try
            {
                entered = await _semaphore.WaitAsync(millisecondsTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                throw new ObjectDisposedException($"{nameof(RateGate)} is already disposed");
            }

            if (entered)
            {
                long exitTick = _stopwatch.ElapsedTicks + _timeUnitTicks;
                _exitTicks.Enqueue(exitTick);
                Interlocked.Increment(ref _pendingExitCount);
                TryRescheduleTimer(exitTick);
            }
            return entered;
        }

        /// <summary>
        /// Waits asynchronously to proceed based on the rate limit.
        /// </summary>
        /// <param name="timeout">The timeout as a TimeSpan to wait for.</param>
        /// <param name="cancellationToken">The cancellation token to observe.</param>
        /// <returns>A task that represents the asynchronous wait operation. The task result is true if the wait succeeded, false if it timed out.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when timeout is less than -1 or greater than int.MaxValue milliseconds.</exception>
        public Task<bool> WaitToProceedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            long num = (long)timeout.TotalMilliseconds;
            if (num < -1 || num > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be between -1 and Int32.MaxValue milliseconds.");

            return WaitToProceedAsync((int)timeout.TotalMilliseconds, cancellationToken);
        }

        /// <summary>
        /// Waits asynchronously to proceed based on the rate limit with an infinite timeout.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token to observe.</param>
        /// <returns>A task that represents the asynchronous wait operation.</returns>
        public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            return WaitToProceedAsync(Timeout.Infinite, cancellationToken);
        }

        #endregion Asynchronous methods

        /// <summary>
        /// Checks if this <see cref="RateGate"/> has been disposed and throws an <see cref="ObjectDisposedException"/> if it has.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown when this <see cref="RateGate"/> instance is already disposed.</exception>
        private void CheckDisposed()
        {
            if (Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0)
                throw new ObjectDisposedException($"{nameof(RateGate)} is already disposed");
        }

        /// <summary>
        /// Releases all resources used by this <see cref="RateGate"/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by this <see cref="RateGate"/> and optionally releases the managed resources.
        /// </summary>
        /// <param name="isDisposing"><c>true</c> to release both managed and unmanaged resources; <ca>false</ca> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool isDisposing)
        {
            // Use thread-safe compare-exchange to ensure Dispose is only executed once
            if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0 && isDisposing)
            {
                Timer exitTimer = Interlocked.Exchange(ref _exitTimer, null);
                if (exitTimer != null)
                {
                    exitTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    exitTimer.Dispose();
                }

                SemaphoreSlim semaphore = Interlocked.Exchange(ref _semaphore, null);
                semaphore?.Dispose();

                Interlocked.Exchange(ref _pendingExitCount, 0);
            }
        }
    }
}
