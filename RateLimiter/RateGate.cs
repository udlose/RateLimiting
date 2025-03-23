using System;
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
        /// <param name="occurrences">The number of occurrences allowed per unit of time.</param>
        /// <param name="timeUnit">The length of the time unit.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        ///     Thrown when <paramref name="occurrences"/> is less than or equal to zero, or when <paramref name="timeUnit"/>
        ///     is not a positive span of time, or when <paramref name="timeUnit"/> is greater than or equal to 2^32 milliseconds.
        /// </exception>
        public RateGate(int occurrences, TimeSpan timeUnit)
        {
            // Check the arguments.
            if (occurrences <= 0)
                throw new ArgumentOutOfRangeException(nameof(occurrences), "Number of occurrences must be a positive integer");
            if (timeUnit <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeUnit), "Time unit must be a positive span of time");
            if (timeUnit >= TimeSpan.FromMilliseconds(uint.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(timeUnit), "Time unit must be less than 2^32 milliseconds");

            Occurrences = occurrences;
            TimeUnitMilliseconds = (int)timeUnit.TotalMilliseconds;

            // Create the semaphore, with the number of occurrences as the maximum count.
            _semaphore = new SemaphoreSlim(Occurrences, Occurrences);

            // Create a queue to hold the semaphore exit times.
            _exitTimes = new ConcurrentQueue<int>();

            // Create a timer to exit the semaphore. Use the time unit as the original
            // interval length because that's the earliest we will need to exit the semaphore.
            _exitTimer = new Timer(ExitTimerCallback, null, TimeUnitMilliseconds, -1);
        }

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
        ///     Releases unmanaged resources held by an instance of this class.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Callback method for the exit timer. Releases expired tokens and reschedules the timer.
        /// </summary>
        /// <param name="state">The state object passed to the callback.</param>
        private void ExitTimerCallback(object state)
        {
            // While there are exit times that are passed due still in the queue,
            // exit the semaphore and dequeue the exit time.
            int exitTime;
            while (_exitTimes.TryPeek(out exitTime)
                   && unchecked(exitTime - Environment.TickCount) <= 0)
            {
                _semaphore.Release();
                _exitTimes.TryDequeue(out _);
            }

            // Try to get the next exit time from the queue and compute
            // the time until the next check should take place. If the 
            // queue is empty, then no exit times will occur until at least
            // one time unit has passed.
            int timeUntilNextCheck;
            if (_exitTimes.TryPeek(out exitTime))
                timeUntilNextCheck = unchecked(exitTime - Environment.TickCount);
            else
                timeUntilNextCheck = TimeUnitMilliseconds;

            // Set the timer.
            _exitTimer.Change(timeUntilNextCheck, -1);
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

            // Block until we can enter the semaphore or until the timeout expires.
            bool entered = _semaphore.Wait(millisecondsTimeout);

            // If we entered the semaphore, compute the corresponding exit time 
            // and add it to the queue.
            if (entered)
            {
                int timeToExit = unchecked(Environment.TickCount + TimeUnitMilliseconds);
                _exitTimes.Enqueue(timeToExit);
            }

            return entered;
        }

        /// <summary>
        ///     Blocks the current thread until allowed to proceed or until the
        ///     specified timeout elapses.
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

            bool entered = await _semaphore.WaitAsync(millisecondsTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (entered)
            {
                int timeToExit = unchecked(Environment.TickCount + TimeUnitMilliseconds);
                _exitTimes.Enqueue(timeToExit);
            }
            return entered;
        }

        public async Task<bool> WaitToProceedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            long num = (long)timeout.TotalMilliseconds;
            if (num < -1 || num > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be between -1 and Int32.MaxValue milliseconds.");

            return await WaitToProceedAsync((int)timeout.TotalMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            await WaitToProceedAsync(Timeout.Infinite, cancellationToken)
                .ConfigureAwait(false);
        }

        #endregion Asynchronous methods

        /// <summary>
        /// Checks if the RateGate has been disposed and throws an <see cref="ObjectDisposedException"/> if it has.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown when this <see cref="RateGate"/> instance is already disposed.</exception>
        private void CheckDisposed()
        {
            if (Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0)
                throw new ObjectDisposedException($"{nameof(RateGate)} is already disposed");
        }

        /// <summary>
        ///     Releases unmanaged resources held by an instance of this class.
        /// </summary>
        /// <param name="isDisposing">Whether this object is being disposed.</param>
        protected virtual void Dispose(bool isDisposing)
        {
            if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0 && isDisposing)
            {
                // Cancel the timer first to prevent new callbacks
                _exitTimer.Change(Timeout.Infinite, Timeout.Infinite);

                // The semaphore and timer both implement IDisposable and 
                // therefore must be disposed.
                // Explicitly set the members to null to cause NullReferenceException
                // if someone tries to use this object after it's disposed.
                _semaphore.Dispose();
                _semaphore = null;
                _exitTimer.Dispose();
                _exitTimer = null;
            }
        }
    }
}
